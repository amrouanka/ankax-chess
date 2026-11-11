using System.Runtime.CompilerServices;
using System.Text;
using static Board;
using static MoveEncoding;
using static MoveGenerator;

// ============================================================================
// DOCUMENTATION FOR INTERMEDIATE PROGRAMMERS
// ============================================================================
//
// KEY CONCEPTS USED IN THIS OPTIMIZATION PASS:
//
// 1. FLAT ARRAYS vs MULTI-DIMENSIONAL ARRAYS (int[] vs int[,])
// ------------------------------------------------------------
// In C#, a multi-dimensional array like int[,] is NOT the same as int[][] (jagged).
// When you write: myArray[x, y], the CLR must:
//   a) Check that x is within bounds of dimension 0
//   b) Check that y is within bounds of dimension 1
//   c) Compute the memory offset: (x * rowWidth + y) * elementSize
// That's 2 bounds checks + 1 multiply PER ACCESS.
//
// A flat int[] only does:
//   a) Check that index is within bounds (1 check)
//   b) Load from that offset directly
//
// We can manually compute the index using bit-shifts instead of multiplies.
// Example: instead of lmrTable[depth, moveIndex], we use:
//   lmrTable[(depth << 6) + moveIndex]
// The "<<" (left shift) is the same as multiplying by 64, but it takes 1 CPU
// cycle instead of ~3-4 for a multiply. We choose row widths that are powers
// of 2 (64, 16, etc.) so shifts work perfectly.
//
// WHY THIS MATTERS: AlphaBeta runs millions of times per second. Saving even
// 2-3 nanoseconds per node adds up to real ELO gains.
//
//
// 2. CACHING STATIC FIELDS INTO LOCAL VARIABLES
// ----------------------------------------------
// A "static field" like Search.ply lives in a fixed memory location. Every
// time you write "ply" in the code, the CPU must read from that memory
// address. If you read it 15 times in one function, that's 15 memory reads.
//
// By writing: int currentPly = ply;
// We read it ONCE into a CPU register (extremely fast), then use the local
// variable (which the JIT keeps in a register) for all subsequent reads.
//
// Same idea applies to: side -> sideToMove, Zobrist.hashKey -> hashKey,
// bitboards -> boards (a local reference to the array).
//
//
// 3. Span<int>
// ------------
// A Span<int> is a lightweight "view" into an array (or part of an array).
// It does NOT copy data and does NOT allocate heap memory.
//
// When you write: moveList.moves.AsSpan(0, count)
// You get a Span that "sees" only the first 'count' elements of the array.
//
// WHY USE IT: The JIT compiler can sometimes eliminate bounds checks when it
// knows the Span's length. In a tight sorting loop that runs thousands of
// times, removing those bounds checks adds up.
//
// Think of Span like a "window" that looks at a portion of an array:
//   Array:  [a, b, c, d, e, f, g, h]
//   Span:   [a, b, c, d, e]  <-- only sees first 5 elements
//
// Span lives entirely on the STACK (not the heap), so it costs nothing to
// create and nothing for the garbage collector to clean up.
//
//
// 4. StringBuilder FOR UCI OUTPUT
// --------------------------------
// When you write: Console.Write($"info score {x} depth {y}")
// C# creates a NEW string object on the heap every time. In a loop printing
// PV moves, you create many small strings, each one causing a tiny heap
// allocation that the garbage collector must eventually clean up.
//
// StringBuilder is a reusable buffer. You .Clear() it and reuse the same
// memory. One allocation at the start, zero allocations per iteration.
//
// This is NOT on the hot search path (it only runs once per depth), but
// it's good practice and prevents GC pauses during search.
//
//
// 5. PRE-EXPANDED LOOKUP TABLES
// ------------------------------
// The original code computes: mvvLva[piece % 6, victim % 6]
// The "% 6" (modulo) operation costs ~20-40 CPU cycles because integer
// division is one of the slowest operations a CPU can do.
//
// Instead, we pre-compute a 12x16 table in the static constructor where
// the index is: (attacker << 4) | victim
// We use 16-wide rows (power of 2) so the shift is free.
// The modulo is done ONCE at startup, never during search.
//
//
// 6. HOISTING LOOP INVARIANTS
// ----------------------------
// A "loop invariant" is a value computed inside a loop that never changes
// between iterations. Moving it ABOVE the loop means it's computed once
// instead of potentially thousands of times.
//
// Example: depth - 1 never changes inside the move loop. We compute it
// once as "newDepth" before the loop starts. Same for depth * depth,
// the LMR table row base address, etc.
//
//
// 7. BRANCHLESS BOOLEAN COMPUTATION
// ----------------------------------
// When you write: !pvNode && !inCheck
// The CPU must evaluate each condition and BRANCH (jump) based on the
// result. Modern CPUs predict branches, but mispredictions cost ~15 cycles.
//
// When you write: !pvNode & !inCheck (single & instead of &&)
// The CPU computes BOTH sides and combines them with a bitwise AND.
// No branch, no prediction, no misprediction penalty.
//
// IMPORTANT: Only safe when both sides have no side effects and are cheap
// to compute. Never do this with method calls that might be expensive.
//
// ============================================================================

public static class Search
{
    private const int MaxPly = 64;
    private const int Infinity = 50000;
    private const int MateScore = 49000;
    private const int MateThreshold = MateScore - MaxPly;

    // LMR
    private const int FullDepthMoves = 4;
    private const int ReductionLimit = 3;
    private const int LmrBase = 1;
    private const int LmrDivisor = 2;

    // Aspiration
    private const int AspirationWindow = 50;
    private const int AspirationMinDepth = 4;
    private const int AspirationRetryLimit = 3;

    // Reverse futility pruning
    private const int ReverseFutilityMaxDepth = 4;
    private const int ReverseFutilityMarginPerDepth = 110;

    // Futility pruning
    private const int FutilityMaxDepth = 4;
    private const int FutilityMarginPerDepth = 120;

    // Null move pruning
    private const int NullMoveMinDepth = 3;
    private const int NullMoveBaseReduction = 3;
    private const int NullMoveDepthDivisor = 4;
    private const int NullMoveEvalDivisor = 200;
    private const int NullMoveEvalBonusCap = 3;

    // Quiescence
    private const int QsDeltaMargin = 200;

    // History gravity: smoothly bounds scores between -MaxHistory and +MaxHistory
    // without hard clamping. Formula: history += bonus - history * |bonus| / MaxHistory
    private const int MaxHistory = 16384;

    // Internal
    private const int TimeCheckMask = 16383;
    private const int RepetitionTableSize = 1024;
    private const int AllMoves = (int)MoveFlag.allMoves;
    private const int NoSquare = (int)Square.noSquare;

    // ========================================================================
    // FLAT-ARRAY STRIDE CONSTANTS
    // ========================================================================
    // These define how many bits to shift when computing a flat array index.
    // Each one corresponds to a dimension that is a power of 2.
    //
    // Example: LmrRowShift = 6 means each row is 64 elements wide (2^6 = 64).
    // To get to row 'depth', we shift: depth << 6, which equals depth * 64.
    // ========================================================================
    private const int LmrRowShift = 6;     // lmrTable: 64 entries per depth
    private const int PvRowShift = 6;      // pvTable: 64 entries per ply
    private const int PieceShift = 4;      // mvvLvaTable: 16-wide rows (12 used, padded)
    private const int HistoryShift = 6;    // historyMoves: 64 targets per piece
    private const int KillerShift = 1;     // killerMoves: 2 entries per ply

    // ========================================================================
    // FLAT ARRAYS (replacing int[,] multi-dimensional arrays)
    // ========================================================================
    // See documentation section 1 above for why flat arrays are faster.
    // ========================================================================

    // Was: int[MaxPly + 1, 64] -- indexed as lmrTable[depth, moveIndex]
    // Now: int[(MaxPly+1) * 64] -- indexed as lmrTable[(depth << 6) + moveIndex]
    private static readonly int[] lmrTable = new int[(MaxPly + 1) << LmrRowShift];

    private static int ply;
    private static long nodes;

    public static long LastNodeCount => nodes;
    public static int lastBestMove = 0;
    public static int lastDepthReached = 0;

    // Was: killerMove1[MaxPly] + killerMove2[MaxPly] (two separate arrays)
    // Now: killerMoves[MaxPly * 2] -- killer1 and killer2 for the same ply are
    // ADJACENT in memory, so reading both touches one cache line instead of two.
    // Index: killerMoves[ply << 1] = killer1, killerMoves[(ply << 1) + 1] = killer2
    private static readonly int[] killerMoves = new int[MaxPly << KillerShift];

    // Was: int[12, 64] -- indexed as historyMoves[piece, target]
    // Now: int[12 * 64] -- indexed as historyMoves[(piece << 6) | target]
    private static readonly int[] historyMoves = new int[12 << HistoryShift];

    // Pre-allocated move lists, one per ply. Eliminates ~2KB heap allocation per node.
    // Safe because only one ply generates moves at a time.
    private static readonly MoveList[] moveLists = new MoveList[MaxPly + 2];

    // Original mvvLva kept as the source-of-truth for initialization
    private static readonly int[,] mvvLva =
    {
        { 105, 205, 305, 405, 505, 605 },
        { 104, 204, 304, 404, 504, 604 },
        { 103, 203, 303, 403, 503, 603 },
        { 102, 202, 302, 402, 502, 602 },
        { 101, 201, 301, 401, 501, 601 },
        { 100, 200, 300, 400, 500, 600 },
    };

    // Pre-expanded to 12x16 so we never need "% 6" during search.
    // See documentation section 5 above.
    private static readonly int[] mvvLvaTable = new int[12 << PieceShift];

    // Was: int[MaxPly, MaxPly]
    // Now: int[MaxPly * MaxPly] -- row base computed once per node
    private static readonly int[] pvTable = new int[MaxPly << PvRowShift];
    private static readonly int[] pvLength = new int[MaxPly];

    private static readonly ulong[] repetitionTable = new ulong[RepetitionTableSize];
    public static int repetitionIndex = 0;

    static Search()
    {
        // Pre-allocate move lists once. Each MoveList constructor creates two int[256] arrays.
        // After this, zero allocations happen during search.
        for (int i = 0; i < moveLists.Length; i++)
            moveLists[i] = new MoveList();

        // Initialize LMR table (flat version)
        for (int depth = 0; depth <= MaxPly; depth++)
        {
            int rowBase = depth << LmrRowShift;

            for (int moves = 0; moves < 64; moves++)
            {
                if (depth < 2 || moves < 1)
                {
                    lmrTable[rowBase + moves] = 1;
                    continue;
                }

                int reduction = (int)(LmrBase + Math.Log(depth) * Math.Log(moves) / LmrDivisor);
                if (reduction < 1) reduction = 1;

                int maxReduction = depth - 2;
                if (reduction > maxReduction) reduction = maxReduction;

                lmrTable[rowBase + moves] = reduction;
            }
        }

        // Pre-expand MVV/LVA so "% 6" is done here once, never during search
        for (int attacker = 0; attacker < 12; attacker++)
        {
            int rowBase = attacker << PieceShift;

            for (int victim = 0; victim < 12; victim++)
                mvvLvaTable[rowBase + victim] = mvvLva[attacker % 6, victim % 6];
        }
    }

    public static void AddToRepetitionHistory(ulong hashKey)
    {
        if (repetitionIndex < repetitionTable.Length)
            repetitionTable[repetitionIndex++] = hashKey;
    }

    public static void RemoveFromRepetitionHistory()
    {
        if (repetitionIndex > 0)
            repetitionIndex--;
    }

    private static bool IsRepetition()
    {
        int index = repetitionIndex;
        if (index < 3)
            return false;

        int earliest = index - 1 - halfmoveClock;
        if (earliest < 0)
            earliest = 0;

        ulong key = Zobrist.hashKey;

        // Cache the array reference so the loop doesn't re-read the static
        // field address on every iteration (see documentation section 2)
        ulong[] table = repetitionTable;

        for (int i = index - 3; i >= earliest; i -= 2)
        {
            if (table[i] == key)
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsMateScore(int score)
    {
        return score >= MateThreshold || score <= -MateThreshold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScoreToMate(int score)
    {
        if (score > 0)
            return (MateScore - score + 1) / 2;

        return -(MateScore + score) / 2;
    }

    private static string FormatUciScore(int score)
    {
        return IsMateScore(score)
            ? $"mate {ScoreToMate(score)}"
            : $"cp {score}";
    }

    public static void SearchPosition(int depth)
    {
        nodes = 0;
        ply = 0;
        TimeManagement.stopped = false;

        Array.Clear(pvTable, 0, pvTable.Length);
        Array.Clear(pvLength, 0, pvLength.Length);
        Array.Clear(killerMoves, 0, killerMoves.Length);
        Array.Clear(historyMoves, 0, historyMoves.Length);

        int alpha = -Infinity;
        int beta = Infinity;
        int bestMove = 0;
        int completedDepth = 0;

        // Reusable string builder to avoid heap allocations per iteration.
        // See documentation section 4 above.
        // The "?" means this variable is allowed to be null (nullable type).
        // We set it to null in debug mode because we never print UCI info then.
        StringBuilder? infoBuilder = Program.debug ? null : new StringBuilder(256);

        for (int currentDepth = 1; currentDepth <= depth; currentDepth++)
        {
            int score;

            if (currentDepth < AspirationMinDepth)
            {
                score = AlphaBeta(-Infinity, Infinity, currentDepth);
            }
            else
            {
                score = AlphaBeta(alpha, beta, currentDepth);

                int window = AspirationWindow;
                int failCount = 0;

                while ((score <= alpha || score >= beta) && !TimeManagement.stopped)
                {
                    failCount++;

                    if (failCount >= AspirationRetryLimit)
                    {
                        alpha = -Infinity;
                        beta = Infinity;
                    }
                    else
                    {
                        if (score <= alpha) alpha -= window;
                        if (score >= beta) beta += window;
                        window += window / 2;
                    }

                    score = AlphaBeta(alpha, beta, currentDepth);
                }
            }

            if (TimeManagement.stopped)
                break;

            alpha = score - AspirationWindow;
            beta = score + AspirationWindow;
            completedDepth = currentDepth;

            // pvTable is flat now; row 0 starts at index 0
            int rootMove = pvTable[0];
            if (rootMove != 0)
                bestMove = rootMove;

            if (infoBuilder != null)
            {
                int rootPvLength = pvLength[0];

                infoBuilder.Clear();
                infoBuilder.Append("info score ").Append(FormatUciScore(score))
                           .Append(" depth ").Append(currentDepth)
                           .Append(" nodes ").Append(nodes)
                           .Append(" pv ");

                for (int i = 0; i < rootPvLength; i++)
                    infoBuilder.Append(GetMove(pvTable[i])).Append(' ');

                Console.WriteLine(infoBuilder.ToString());
            }

            if (TimeManagement.ShouldStopAfterIteration())
                break;
        }

        if (bestMove == 0)
        {
            ref MoveList moveList = ref moveLists[0];
            GenerateMoves(ref moveList);

            int[] moves = moveList.moves;
            int moveCount = moveList.count;

            for (int i = 0; i < moveCount; i++)
            {
                int move = moves[i];
                BoardState state = CopyBoard();

                if (MakeMove(move, AllMoves) == 0)
                {
                    TakeBack(state);
                    continue;
                }

                TakeBack(state);
                bestMove = move;
                break;
            }

            if (bestMove == 0)
            {
                Console.WriteLine("bestmove 0000");
                return;
            }
        }

        if (Program.debug)
        {
            lastBestMove = bestMove;
            lastDepthReached = completedDepth;
        }
        else
        {
            Console.WriteLine($"bestmove {GetMove(bestMove)}");
        }
    }

    private static int AlphaBeta(int alpha, int beta, int depth, bool allowNullMove = true)
    {
        if ((nodes & TimeCheckMask) == 0)
            TimeManagement.Communicate();

        if (TimeManagement.stopped)
            return 0;

        // Cache the static field into a local variable.
        // This single read replaces ~15+ reads of the static field throughout
        // this function. See documentation section 2.
        int currentPly = ply;
        pvLength[currentPly] = currentPly;

        if (currentPly > 0 && IsRepetition())
            return 0;

        if (halfmoveClock >= 100)
            return 0;

        bool inCheck = IsInCheck();

        if (inCheck && currentPly < MaxPly - 10)
            depth++;

        if (depth <= 0)
            return Quiescence(alpha, beta);

        if (currentPly >= MaxPly - 1)
            return Evaluation.Evaluate();

        nodes++;

        // Mate distance pruning
        int matingScore = MateScore - currentPly;
        if (matingScore < beta)
        {
            beta = matingScore;
            if (alpha >= matingScore)
                return matingScore;
        }

        int matedScore = -MateScore + currentPly;
        if (matedScore > alpha)
        {
            alpha = matedScore;
            if (beta <= matedScore)
                return matedScore;
        }

        bool pvNode = (beta - alpha) > 1;

        // Cache static fields that we read multiple times below
        ulong hashKey = Zobrist.hashKey;
        int sideToMove = side;

        int ttMove = 0;
        int ttScore = TranspositionTable.Probe(
            hashKey,
            depth,
            alpha,
            beta,
            currentPly,
            out ttMove);

        if (ttScore != TranspositionTable.NoScore && !pvNode)
            return ttScore;

        int staticEval = inCheck ? -MateScore : Evaluation.Evaluate();

        // Branchless combined predicate. See documentation section 7.
        // Both sides are already-computed bools, so using & instead of &&
        // lets the CPU compute both without branching.
        bool quietNode = !pvNode & !inCheck;
        bool prunableNode = quietNode & (currentPly > 0);

        // Reverse futility pruning
        if (prunableNode &&
            depth <= ReverseFutilityMaxDepth &&
            staticEval > -MateThreshold &&
            staticEval < MateThreshold)
        {
            if (staticEval - ReverseFutilityMarginPerDepth * depth >= beta)
                return beta;
        }

        // Null move pruning
        if (prunableNode &&
            depth >= NullMoveMinDepth &&
            allowNullMove &&
            staticEval >= beta &&
            HasNonPawnMaterial(sideToMove))
        {
            BoardState nullMoveState = CopyBoard();

            Zobrist.hashKey ^= Zobrist.sideKey;
            side ^= 1;
            halfmoveClock++;

            if (enPassant != NoSquare)
            {
                Zobrist.hashKey ^= Zobrist.enpassantKeys[enPassant];
                enPassant = NoSquare;
            }

            int evalBonus = (staticEval - beta) / NullMoveEvalDivisor;
            if (evalBonus > NullMoveEvalBonusCap)
                evalBonus = NullMoveEvalBonusCap;

            int reduction = NullMoveBaseReduction + depth / NullMoveDepthDivisor + evalBonus;
            if (reduction > depth - 1)
                reduction = depth - 1;

            ply++;
            int nullMoveScore = -AlphaBeta(-beta, -beta + 1, depth - 1 - reduction, false);
            ply--;
            TakeBack(nullMoveState);

            if (TimeManagement.stopped)
                return 0;

            if (nullMoveScore >= beta)
                return beta;
        }

        if (depth >= 4 && ttMove == 0 && !inCheck)
            depth--;

        // Futility pruning 
        bool canPrune = false;
        if (quietNode && depth <= FutilityMaxDepth)
            canPrune = staticEval + FutilityMarginPerDepth * depth <= alpha;

        ref MoveList moveList = ref moveLists[currentPly];
        GenerateMoves(ref moveList);

        // Compute killer index once. killerMoves[(ply*2)] = killer1,
        // killerMoves[(ply*2)+1] = killer2. Both are adjacent in memory.
        int killerIndex = currentPly << KillerShift;
        int pvMove = currentPly == 0 ? pvTable[0] : 0;

        SortMoves(
            ref moveList,
            ttMove,
            pvMove,
            killerMoves[killerIndex],
            killerMoves[killerIndex + 1],
            sideToMove);

        int[] moves = moveList.moves;
        int moveCount = moveList.count;

        // ====================================================================
        // HOISTED LOOP INVARIANTS (see documentation section 6)
        // These values never change inside the move loop, so we compute them
        // once here instead of recomputing on every iteration.
        // ====================================================================
        int newDepth = depth - 1;
        int depthSquared = depth * depth;
        int lmrRowBase = depth << LmrRowShift;
        int pvRowBase = currentPly << PvRowShift;
        int nextPly = currentPly + 1;
        int childRowBase = nextPly << PvRowShift;

        int movesSearched = 0;
        int bestScore = -Infinity;
        int bestMove = 0;
        int originalAlpha = alpha;
        int legalMoves = 0;
        bool anyMovePruned = false;

        for (int i = 0; i < moveCount; i++)
        {
            int move = moves[i];
            int promoted = GetMovePromoted(move);

            // Branchless: both operands are trivially cheap, so & avoids
            // a branch. See documentation section 7.
            bool isQuiet = (GetMoveCapture(move) == 0) & (promoted == 0);

            if (canPrune &&
                movesSearched > 0 &&
                isQuiet &&
                move != ttMove)
            {
                anyMovePruned = true;
                continue;
            }

            BoardState state = CopyBoard();
            if (MakeMove(move, AllMoves) == 0)
            {
                TakeBack(state);
                continue;
            }

            repetitionTable[repetitionIndex++] = Zobrist.hashKey;
            ply++;
            legalMoves++;

            int score;

            if (movesSearched == 0)
            {
                score = -AlphaBeta(-beta, -alpha, newDepth);
            }
            else
            {
                if (movesSearched >= FullDepthMoves &&
                    depth >= ReductionLimit &&
                    isQuiet &&
                    !inCheck)
                {
                    int moveIndex = movesSearched < 64 ? movesSearched : 63;

                    // Flat LMR table: row base was hoisted above the loop
                    int reduction = lmrTable[lmrRowBase + moveIndex];

                    if (pvNode && reduction > 1)
                        reduction--;

                    score = -AlphaBeta(-alpha - 1, -alpha, newDepth - reduction);
                }
                else
                {
                    score = alpha + 1;
                }

                if (score > alpha)
                {
                    score = -AlphaBeta(-alpha - 1, -alpha, newDepth);

                    if (score > alpha && score < beta)
                        score = -AlphaBeta(-beta, -alpha, newDepth);
                }
            }

            ply--;
            repetitionIndex--;
            TakeBack(state);

            if (TimeManagement.stopped)
                return 0;

            movesSearched++;

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
            }

            if (score >= beta)
            {
                if (isQuiet)
                {
                    UpdateHistory(move, depthSquared);

                    if (killerMoves[killerIndex] != move)
                    {
                        killerMoves[killerIndex + 1] = killerMoves[killerIndex];
                        killerMoves[killerIndex] = move;
                    }
                }

                TranspositionTable.Store(
                    hashKey,
                    depth,
                    bestScore,
                    bestMove,
                    TTFlag.Beta,
                    currentPly);

                return bestScore;
            }

            if (score > alpha)
            {
                if (isQuiet)
                {
                    UpdateHistory(move, depthSquared);
                }

                alpha = score;

                // Flat PV table: copy child PV into current ply's row
                pvTable[pvRowBase + currentPly] = move;

                int childPvLength = pvLength[nextPly];
                int copyCount = childPvLength - nextPly;

                // Array.Copy is a single vectorized block copy, much faster
                // than a manual element-by-element loop for contiguous data
                if (copyCount > 0)
                    Array.Copy(pvTable, childRowBase + nextPly, pvTable, pvRowBase + nextPly, copyCount);

                pvLength[currentPly] = childPvLength;
            }
        }

        if (legalMoves == 0)
        {
            if (anyMovePruned)
                return staticEval;

            return inCheck ? -MateScore + currentPly : 0;
        }

        TTFlag flag = alpha <= originalAlpha ? TTFlag.Alpha : TTFlag.Exact;
        TranspositionTable.Store(hashKey, depth, bestScore, bestMove, flag, currentPly);

        return bestScore;
    }

    public static int Quiescence(int alpha, int beta)
    {
        if ((nodes & TimeCheckMask) == 0)
            TimeManagement.Communicate();

        if (TimeManagement.stopped)
            return 0;

        int currentPly = ply;

        if (currentPly >= MaxPly - 1)
            return Evaluation.Evaluate();

        if (halfmoveClock >= 100)
            return 0;

        nodes++;

        bool inCheck = IsInCheck();

        int eval = 0;
        if (!inCheck)
        {
            eval = Evaluation.Evaluate();

            if (eval >= beta)
                return eval;

            if (eval > alpha)
                alpha = eval;
        }

        ref MoveList moveList = ref moveLists[currentPly];
        if (inCheck)
            GenerateMoves(ref moveList);
        else
            GenerateCaptureMoves(ref moveList);

        int killerIndex = currentPly << KillerShift;

        SortMoves(
            ref moveList,
            0,
            0,
            killerMoves[killerIndex],
            killerMoves[killerIndex + 1],
            side);

        int[] moves = moveList.moves;
        int moveCount = moveList.count;
        int legalMoves = 0;

        // eval + QsDeltaMargin is the same for every move in this node.
        // Computing it once avoids redundant addition per move.
        int deltaBase = eval + QsDeltaMargin;

        for (int i = 0; i < moveCount; i++)
        {
            int move = moves[i];

            if (!inCheck)
            {
                int promoted = GetMovePromoted(move);
                int capturedValue = GetPieceValue(GetPieceAtSquare(GetMoveTarget(move)));
                int promotionValueDelta = promoted != 0 ? GetPieceValue(promoted) - 89 : 0;

                if (deltaBase + capturedValue + promotionValueDelta < alpha)
                    continue;
            }

            BoardState state = CopyBoard();
            if (MakeMove(move, AllMoves) == 0)
            {
                TakeBack(state);
                continue;
            }

            legalMoves++;
            ply++;
            int score = -Quiescence(-beta, -alpha);
            ply--;
            TakeBack(state);

            if (TimeManagement.stopped)
                return 0;

            if (score >= beta)
                return score;

            if (score > alpha)
                alpha = score;
        }

        if (inCheck && legalMoves == 0)
            return -MateScore + currentPly;

        return alpha;
    }

    private static void SortMoves(
        ref MoveList moveList,
        int ttMove,
        int pvMove,
        int killer1,
        int killer2,
        int sideToMove)
    {
        int count = moveList.count;
        if (count < 2)
        {
            if (count == 1)
                moveList.scores[0] = ScoreMove(moveList.moves[0], ttMove, pvMove, killer1, killer2, sideToMove);
            return;
        }

        // Span: a lightweight "window" into the array.
        // See documentation section 3 above.
        // Slicing to 'count' helps the JIT eliminate bounds checks.
        Span<int> moves = moveList.moves.AsSpan(0, count);
        Span<int> scores = moveList.scores.AsSpan(0, count);

        for (int i = 0; i < count; i++)
            scores[i] = ScoreMove(moves[i], ttMove, pvMove, killer1, killer2, sideToMove);

        // Insertion sort with early-exit optimization:
        // If the current element is already >= the previous one, it's already
        // in the right place. Skip it entirely. Move lists tend to be partially
        // ordered after scoring (TT/PV/captures score high and cluster at front),
        // so this skip fires frequently and saves many inner-loop iterations.
        for (int i = 1; i < count; i++)
        {
            int score = scores[i];
            if (score <= scores[i - 1])
                continue;

            int move = moves[i];
            int j = i - 1;

            do
            {
                moves[j + 1] = moves[j];
                scores[j + 1] = scores[j];
                j--;
            }
            while (j >= 0 && scores[j] < score);

            moves[j + 1] = move;
            scores[j + 1] = score;
        }
    }

    private static int ScoreMove(
        int move,
        int ttMove,
        int pvMove,
        int killer1,
        int killer2,
        int sideToMove)
    {
        if (move == ttMove) return 30000;

        int promoted = GetMovePromoted(move);
        if (promoted == Q || promoted == q) return 29000;
        if (move == pvMove) return 20000;

        int target = GetMoveTarget(move);

        if (GetMoveCapture(move) != 0)
        {
            // Flat mvvLvaTable: no "% 6" needed. See documentation section 5.
            int victim = GetPieceAtSquare(target);
            return mvvLvaTable[(GetMovePiece(move) << PieceShift) | victim] + 10000;
        }

        // Killer moves passed in as parameters instead of reading
        // killerMove1[ply] and killerMove2[ply] from static arrays.
        // This avoids re-reading the static 'ply' field plus two array
        // accesses on every single move scored.
        if (move == killer1) return 9000;
        if (move == killer2) return 8000;
        if (GetMoveCastling(move) != 0) return 7500;
        if (promoted != 0) return 7200;

        // Flat history: (piece << 6) | target
        int history = historyMoves[(GetMovePiece(move) << HistoryShift) | target];

        // Clamp to safe range: below killers (8000-9000) but allows negative values
        // to push bad quiet moves to the bottom of the list.
        return Math.Clamp(history, -7000, 7000);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPieceValue(int piece) => piece switch
    {
        P or p => 88,
        N or n => 309,
        B or b => 331,
        R or r => 494,
        Q or q => 981,
        K or k => 20000,
        _ => 0
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateHistory(int move, int bonus)
    {
        int index = (GetMovePiece(move) << HistoryShift) | GetMoveTarget(move);
        historyMoves[index] += bonus - historyMoves[index] * Math.Abs(bonus) / MaxHistory;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInCheck()
    {
        // Cache static field once instead of reading 'side' twice
        int sideToMove = side;

        int kSq = sideToMove == White
            ? BitboardOperations.GetLs1bIndex(bitboards[K])
            : BitboardOperations.GetLs1bIndex(bitboards[k]);

        return PieceAttacks.IsSquareAttacked(kSq, sideToMove ^ 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPieceAtSquare(int square)
    {
        ulong mask = 1UL << square;

        // Cache the array reference so 12 array accesses use a register-held
        // pointer instead of re-reading the static field each time
        ulong[] boards = bitboards;

        if ((boards[P] & mask) != 0) return P;
        if ((boards[p] & mask) != 0) return p;
        if ((boards[N] & mask) != 0) return N;
        if ((boards[n] & mask) != 0) return n;
        if ((boards[B] & mask) != 0) return B;
        if ((boards[b] & mask) != 0) return b;
        if ((boards[R] & mask) != 0) return R;
        if ((boards[r] & mask) != 0) return r;
        if ((boards[Q] & mask) != 0) return Q;
        if ((boards[q] & mask) != 0) return q;
        if ((boards[K] & mask) != 0) return K;
        if ((boards[k] & mask) != 0) return k;

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasNonPawnMaterial(int sideToCheck)
    {
        ulong[] boards = bitboards;

        return sideToCheck == White
            ? (boards[N] | boards[B] | boards[R] | boards[Q]) != 0
            : (boards[n] | boards[b] | boards[r] | boards[q]) != 0;
    }
}