using System.Runtime.CompilerServices;
using System.Text;
using static Board;
using static MoveEncoding;
using static MoveGenerator;

// =============================================================================
//  AnkaX Search — Principal Variation Search with PVS, NMP, LMR, and pruning
// =============================================================================
//
//  Search features (all tested and verified):
//    ✓ Iterative deepening with aspiration windows
//    ✓ Principal Variation Search (PVS)
//    ✓ Null Move Pruning (NMP) with adaptive reduction
//    ✓ Late Move Reductions (LMR) with PV + counter-move adjustment
//    ✓ Reverse futility pruning (static eval well above beta)
//    ✓ Futility pruning (static eval well below alpha)
//    ✓ Check extensions
//    ✓ Internal Iterative Reduction (IIR) when no TT move
//    ✓ Counter-move history (best response to opponent's previous move)
//    ✓ Continuation history (score-based follow-up to opponent's move)
//    ✓ Mate distance pruning
//    ✓ Quiescence search with delta pruning
//    ✓ Fail-soft return values
//
//  Move ordering (highest to lowest priority):
//    TT move (30000) → Q-promotion (29000) → PV move (20000) →
//    MVV-LVA (10000+) → Killer 1 (9000) → Killer 2 (8000) →
//    Counter-move (7800) → Castling (7500) → Other promotions (7200) →
//    History + Continuation (-7000..+7000)
//
//  Performance techniques used:
//    1. Flat arrays with bit-shift indexing (avoids multi-dim bounds checks)
//    2. Static field caching into locals (reduces memory reads)
//    3. Span<T> for sort slices (helps JIT eliminate bounds checks)
//    4. StringBuilder for UCI output (avoids per-iteration heap allocs)
//    5. Pre-expanded lookup tables (avoids modulo in hot paths)
//    6. Hoisted loop invariants (computed once, not per-iteration)
//    7. Branchless boolean operators (& instead of &&)
//    8. Pre-allocated MoveList pool (zero allocs during search)
//
// =============================================================================

public static class Search
{
    // =========================================================================
    //  Constants
    // =========================================================================

    #region Core

    private const int MaxPly = 64;
    private const int Infinity = 50000;
    private const int MateScore = 49000;
    private const int MateThreshold = MateScore - MaxPly;

    #endregion

    #region Late Move Reductions

    private const int FullDepthMoves = 4;   // Moves searched at full depth before LMR kicks in
    private const int ReductionLimit = 3;   // Minimum depth for LMR
    private const int LmrBase = 1;
    private const int LmrDivisor = 2;

    #endregion

    #region Aspiration Windows

    private const int AspirationWindow = 50;
    private const int AspirationMinDepth = 4;
    private const int AspirationRetryLimit = 3;

    #endregion

    #region Pruning

    private const int ReverseFutilityMaxDepth = 4;
    private const int ReverseFutilityMarginPerDepth = 110;

    private const int FutilityMaxDepth = 4;
    private const int FutilityMarginPerDepth = 120;

    #endregion

    #region Null Move Pruning

    private const int NullMoveMinDepth = 3;
    private const int NullMoveBaseReduction = 3;
    private const int NullMoveDepthDivisor = 4;
    private const int NullMoveEvalDivisor = 200;
    private const int NullMoveEvalBonusCap = 3;

    #endregion

    #region Quiescence

    private const int QsDeltaMargin = 200;

    #endregion

    #region History

    // Gravity formula: history += bonus - history * |bonus| / MaxHistory
    // Smoothly bounds scores without hard clamping.
    private const int MaxHistory = 16384;

    #endregion

    #region Internal

    private const int TimeCheckMask = 16383;       // Check time every 16384 nodes
    private const int RepetitionTableSize = 1024;
    private const int AllMoves = (int)MoveFlag.allMoves;
    private const int NoSquare = (int)Square.noSquare;

    #endregion

    // =========================================================================
    //  Flat-Array Stride Constants
    // =========================================================================
    //
    //  Each constant defines the bit-shift for a flat array dimension.
    //  Row width = 2^shift. Shifts replace multiplication (1 cycle vs ~3-4).
    //
    //  Example: LmrRowShift = 6 → row width 64 → index = (depth << 6) + move
    //

    private const int LmrRowShift = 6;     // 64 move indices per depth
    private const int PvRowShift = 6;      // 64 PV entries per ply
    private const int PieceShift = 4;      // 16 entries per piece (12 used)
    private const int HistoryShift = 6;    // 64 target squares per piece
    private const int KillerShift = 1;     // 2 killer slots per ply

    // =========================================================================
    //  State
    // =========================================================================

    private static int ply;
    private static long nodes;

    public static long LastNodeCount => nodes;
    public static int lastBestMove = 0;
    public static int lastDepthReached = 0;

    // =========================================================================
    //  Data Arrays (all flat for cache-friendly access)
    // =========================================================================

    // LMR reduction table: lmrTable[(depth << 6) + moveIndex]
    private static readonly int[] lmrTable = new int[(MaxPly + 1) << LmrRowShift];

    // Killer moves: killerMoves[ply << 1] = killer1, [(ply << 1) + 1] = killer2
    // Adjacent in memory → single cache line for both killers at same ply.
    private static readonly int[] killerMoves = new int[MaxPly << KillerShift];

    // History heuristic: historyMoves[(piece << 6) | targetSquare]
    private static readonly int[] historyMoves = new int[12 << HistoryShift];

    // Counter-move table: counterMoves[(piece << 6) | targetSquare] = best reply move.
    // Indexed by the opponent's PREVIOUS move (piece + target square). When a quiet
    // move causes a beta cutoff, we store it here as the refutation of whatever
    // the opponent just played. Small (768 ints = 3 KB) so always in L1.
    private static readonly int[] counterMoves = new int[12 << HistoryShift];

    // Continuation history: contHistory[prevIndex * 768 + currIndex] = score.
    // 2D table: for each opponent move (prevIndex), stores how well each of our
    // replies (currIndex) performed. Uses same gravity formula as regular history.
    // 768 × 768 = 589,824 ints ≈ 2.3 MB, fits in L2 cache.
    private static readonly int[] contHistory = new int[768 * 768];

    // Move stack: moveStack[ply] = move played at this ply. Needed so child nodes
    // can look up their parent's move to index counterMoves and contHistory.
    // Cleared per search.
    private static readonly int[] moveStack = new int[MaxPly + 2];

    // Pre-allocated move lists (one per ply). Eliminates heap allocation in search.
    private static readonly MoveList[] moveLists = new MoveList[MaxPly + 2];

    // MVV-LVA source table (used only during initialization)
    private static readonly int[,] mvvLva =
    {
        { 105, 205, 305, 405, 505, 605 },
        { 104, 204, 304, 404, 504, 604 },
        { 103, 203, 303, 403, 503, 603 },
        { 102, 202, 302, 402, 502, 602 },
        { 101, 201, 301, 401, 501, 601 },
        { 100, 200, 300, 400, 500, 600 },
    };

    // Pre-expanded MVV-LVA: mvvLvaTable[(attacker << 4) | victim]
    // Eliminates "% 6" modulo from the hot path.
    private static readonly int[] mvvLvaTable = new int[12 << PieceShift];

    // PV table: pvTable[(ply << 6) + index]
    private static readonly int[] pvTable = new int[MaxPly << PvRowShift];
    private static readonly int[] pvLength = new int[MaxPly];

    // Repetition detection
    private static readonly ulong[] repetitionTable = new ulong[RepetitionTableSize];
    public static int repetitionIndex = 0;

    // =========================================================================
    //  Static Constructor — one-time initialization
    // =========================================================================

    static Search()
    {
        // Pre-allocate move lists (each contains two int[256] arrays)
        for (int i = 0; i < moveLists.Length; i++)
            moveLists[i] = new MoveList();

        // Build LMR reduction table using log formula
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

        // Expand MVV-LVA table: pre-compute modulo so search never needs it
        for (int attacker = 0; attacker < 12; attacker++)
        {
            int rowBase = attacker << PieceShift;
            for (int victim = 0; victim < 12; victim++)
                mvvLvaTable[rowBase + victim] = mvvLva[attacker % 6, victim % 6];
        }
    }

    // =========================================================================
    //  Repetition Detection
    // =========================================================================

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
        ulong[] table = repetitionTable;  // Cache reference to avoid repeated static reads

        for (int i = index - 3; i >= earliest; i -= 2)
        {
            if (table[i] == key)
                return true;
        }

        return false;
    }

    // =========================================================================
    //  UCI Score Formatting
    // =========================================================================

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

    // =========================================================================
    //  Iterative Deepening Entry Point
    // =========================================================================

    /// <summary>
    /// Searches the current position to the given depth using iterative deepening
    /// with aspiration windows. Outputs UCI info lines and a bestmove at the end.
    /// </summary>
    public static void SearchPosition(int depth)
    {
        nodes = 0;
        ply = 0;
        TimeManagement.stopped = false;

        Array.Clear(pvTable, 0, pvTable.Length);
        Array.Clear(pvLength, 0, pvLength.Length);
        Array.Clear(killerMoves, 0, killerMoves.Length);
        Array.Clear(historyMoves, 0, historyMoves.Length);
        Array.Clear(counterMoves, 0, counterMoves.Length);
        Array.Clear(contHistory, 0, contHistory.Length);
        Array.Clear(moveStack, 0, moveStack.Length);

        int alpha = -Infinity;
        int beta = Infinity;
        int bestMove = 0;
        int completedDepth = 0;

        // Reusable buffer for UCI output (avoids per-iteration string allocations).
        // Null in debug mode since we never print UCI info then.
        StringBuilder? infoBuilder = Program.Debug ? null : new StringBuilder(256);

        for (int currentDepth = 1; currentDepth <= depth; currentDepth++)
        {
            int score;

            if (currentDepth < AspirationMinDepth)
            {
                // Full window for shallow depths
                score = AlphaBeta(-Infinity, Infinity, currentDepth);
            }
            else
            {
                // Aspiration window around previous iteration's score
                score = AlphaBeta(alpha, beta, currentDepth);

                int window = AspirationWindow;
                int failCount = 0;

                while ((score <= alpha || score >= beta) && !TimeManagement.stopped)
                {
                    failCount++;

                    if (failCount >= AspirationRetryLimit)
                    {
                        // Window too tight — fall back to full window
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

        // Fallback: if search was stopped before any move was found, pick the first legal move
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

        if (Program.Debug)
        {
            lastBestMove = bestMove;
            lastDepthReached = completedDepth;
        }
        else
        {
            Console.WriteLine($"bestmove {GetMove(bestMove)}");
        }
    }

    // =========================================================================
    //  Alpha-Beta Search (PVS)
    // =========================================================================

    /// <summary>
    /// Principal Variation Search with null move pruning, LMR, futility pruning,
    /// reverse futility pruning, check extensions, IIR, counter-move history,
    /// and continuation history.
    /// Returns fail-soft scores (actual best, not clamped to alpha/beta).
    /// </summary>
    private static int AlphaBeta(int alpha, int beta, int depth, bool allowNullMove = true)
    {
        // --- Time management ---
        if ((nodes & TimeCheckMask) == 0)
            TimeManagement.Communicate();

        if (TimeManagement.stopped)
            return 0;

        // --- Initialize node ---
        int currentPly = ply;  // Cache static field into local register
        pvLength[currentPly] = currentPly;

        if (currentPly > 0 && IsRepetition())
            return 0;

        if (halfmoveClock >= 100)
            return 0;

        bool inCheck = IsInCheck();

        // Check extension: search one ply deeper when in check
        if (inCheck && currentPly < MaxPly - 10)
            depth++;

        // Drop into quiescence at horizon
        if (depth <= 0)
            return Quiescence(alpha, beta);

        if (currentPly >= MaxPly - 1)
            return Evaluation.Evaluate();

        nodes++;

        // --- Mate distance pruning ---
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

        // --- Transposition table probe ---
        ulong hashKey = Zobrist.hashKey;

        int ttMove = 0;
        int ttScore = TranspositionTable.Probe(
            hashKey, depth, alpha, beta, currentPly, out ttMove);

        if (ttScore != TranspositionTable.NoScore && !pvNode)
            return ttScore;

        // --- Static evaluation ---
        int staticEval = inCheck ? -MateScore : Evaluation.Evaluate();

        // Branchless combined predicates (& instead of && avoids branch misprediction)
        bool quietNode = !pvNode & !inCheck;
        bool prunableNode = quietNode & (currentPly > 0);

        // --- Reverse futility pruning ---
        if (prunableNode &&
            depth <= ReverseFutilityMaxDepth &&
            staticEval > -MateThreshold &&
            staticEval < MateThreshold)
        {
            if (staticEval - ReverseFutilityMarginPerDepth * depth >= beta)
                return beta;
        }

        // --- Null move pruning ---
        if (prunableNode &&
            depth >= NullMoveMinDepth &&
            allowNullMove &&
            staticEval >= beta &&
            HasNonPawnMaterial(side))
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

        // --- Internal Iterative Reduction (IIR) ---
        // Reduce depth when no TT move is available and not in check.
        // The !inCheck guard prevents canceling the check extension above.
        if (depth >= 4 && ttMove == 0 && !inCheck)
            depth--;

        // --- Futility pruning ---
        bool canPrune = false;
        if (quietNode && depth <= FutilityMaxDepth)
            canPrune = staticEval + FutilityMarginPerDepth * depth <= alpha;

        // --- Parent move lookup (counter-move + continuation history) ---
        // The parent's move is at moveStack[currentPly - 1]. From it we derive:
        //   counterMove    — our stored refutation (one specific move)
        //   prevContIndex    — key for counter-move table lookup
        //   prevContRowBase  — pre-multiplied base for continuation history indexing
        // Both are used in move ordering and LMR. Skipped at root (no parent).
        int prevMove = currentPly > 0 ? moveStack[currentPly - 1] : 0;
        int counterMove = 0;
        int prevContIndex = -1;        // Index into counterMoves table
        int prevContRowBase = -1;      // Pre-multiplied row base for contHistory
        if (prevMove != 0)
        {
            prevContIndex = (GetMovePiece(prevMove) << HistoryShift) | GetMoveTarget(prevMove);
            prevContRowBase = prevContIndex * 768;
            counterMove = counterMoves[prevContIndex];
        }

        // --- Generate and sort moves ---
        ref MoveList moveList = ref moveLists[currentPly];
        GenerateMoves(ref moveList);

        int killerIndex = currentPly << KillerShift;
        int pvMove = currentPly == 0 ? pvTable[0] : 0;

        SortMoves(
            ref moveList,
            ttMove,
            pvMove,
            killerMoves[killerIndex],
            killerMoves[killerIndex + 1],
            counterMove,
            prevContRowBase);

        int[] moves = moveList.moves;
        int moveCount = moveList.count;

        // --- Hoisted loop invariants (computed once, not per-iteration) ---
        int newDepth = depth - 1;
        int depthSquared = depth * depth;
        int lmrRowBase = depth << LmrRowShift;
        int pvRowBase = currentPly << PvRowShift;
        int nextPly = currentPly + 1;
        int childRowBase = nextPly << PvRowShift;

        // --- Move loop ---
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

            bool isQuiet = (GetMoveCapture(move) == 0) & (promoted == 0);

            // Futility pruning: skip quiet moves when static eval is far below alpha
            if (canPrune &&
                movesSearched > 0 &&
                isQuiet &&
                move != ttMove)
            {
                anyMovePruned = true;
                continue;
            }

            // Late Move Pruning: skip quiet moves after enough moves searched.
            // At shallow depth, if our ordering is good, moves beyond 3+d² are noise.
            // Guard: non-PV, not in check, non-mate score (don't prune when losing).
            if (depth <= 4 &&
                !pvNode &&
                !inCheck &&
                isQuiet &&
                movesSearched >= 3 + depth * depth &&
                bestScore > -MateThreshold)
            {
                anyMovePruned = true;
                continue;
            }

            // Make move and check legality
            BoardState state = CopyBoard();
            if (MakeMove(move, AllMoves) == 0)
            {
                TakeBack(state);
                continue;
            }

            // Record move so child nodes can index counterMoves + contHistory
            moveStack[currentPly] = move;
            repetitionTable[repetitionIndex++] = Zobrist.hashKey;
            ply++;
            legalMoves++;

            int score;

            if (movesSearched == 0)
            {
                // First move: full-window PVS search
                score = -AlphaBeta(-beta, -alpha, newDepth);
            }
            else
            {
                // Late Move Reductions for quiet moves
                if (movesSearched >= FullDepthMoves &&
                    depth >= ReductionLimit &&
                    isQuiet &&
                    !inCheck)
                {
                    int moveIndex = movesSearched < 64 ? movesSearched : 63;
                    int reduction = lmrTable[lmrRowBase + moveIndex];

                    if (pvNode && reduction > 1)
                        reduction--;

                    // Reduce less for counter-move (it refuted this move before)
                    if (move == counterMove && reduction > 0)
                        reduction--;

                    score = -AlphaBeta(-alpha - 1, -alpha, newDepth - reduction);
                }
                else
                {
                    score = alpha + 1;  // Force re-search below
                }

                // PVS re-search: if reduced search beats alpha, search at full depth
                if (score > alpha)
                {
                    score = -AlphaBeta(-alpha - 1, -alpha, newDepth);

                    // If still within the window, do a full re-search
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

            // --- Update best ---
            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
            }

            // --- Beta cutoff ---
            if (score >= beta)
            {
                if (isQuiet)
                {
                    UpdateHistory(move, depthSquared);

                    if (prevContRowBase >= 0)
                        UpdateContHistory(prevContRowBase, move, depthSquared);

                    if (killerMoves[killerIndex] != move)
                    {
                        killerMoves[killerIndex + 1] = killerMoves[killerIndex];
                        killerMoves[killerIndex] = move;
                    }

                    // Store this quiet move as the refutation of the parent's move
                    if (prevContIndex >= 0)
                        counterMoves[prevContIndex] = move;
                }

                TranspositionTable.Store(
                    hashKey, depth, bestScore, bestMove, TTFlag.Beta, currentPly);

                return bestScore;
            }

            // --- Alpha improvement ---
            if (score > alpha)
            {
                if (isQuiet)
                {
                    UpdateHistory(move, depthSquared);

                    if (prevContRowBase >= 0)
                        UpdateContHistory(prevContRowBase, move, depthSquared);
                }

                alpha = score;

                // Update PV: copy child PV into current ply's row
                pvTable[pvRowBase + currentPly] = move;

                int childPvLength = pvLength[nextPly];
                int copyCount = childPvLength - nextPly;

                if (copyCount > 0)
                    Array.Copy(pvTable, childRowBase + nextPly,
                               pvTable, pvRowBase + nextPly, copyCount);

                pvLength[currentPly] = childPvLength;
            }
        }

        // --- End of node ---
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

    // =========================================================================
    //  Quiescence Search
    // =========================================================================

    /// <summary>
    /// Captures-only search at the horizon to avoid the horizon effect.
    /// Uses delta pruning to skip captures that cannot raise alpha.
    /// In check, generates all moves (evasion).
    /// Returns fail-soft scores.
    /// </summary>
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

        // Stand pat: evaluate the position without making any capture
        int eval = 0;
        if (!inCheck)
        {
            eval = Evaluation.Evaluate();

            if (eval >= beta)
                return eval;

            if (eval > alpha)
                alpha = eval;
        }

        // In check: generate all moves (evasion). Otherwise: captures only.
        ref MoveList moveList = ref moveLists[currentPly];
        if (inCheck)
            GenerateMoves(ref moveList);
        else
            GenerateCaptureMoves(ref moveList);

        int killerIndex = currentPly << KillerShift;

        // No counter-move or continuation history in quiescence
        // (captures only, ordered by MVV-LVA)
        SortMoves(
            ref moveList,
            0, 0,
            killerMoves[killerIndex],
            killerMoves[killerIndex + 1],
            0,
            -1);

        int[] moves = moveList.moves;
        int moveCount = moveList.count;
        int legalMoves = 0;

        // Delta pruning threshold (same for all moves in this node)
        int deltaBase = eval + QsDeltaMargin;

        for (int i = 0; i < moveCount; i++)
        {
            int move = moves[i];

            // Delta pruning: skip captures that can't possibly raise alpha
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

        // Checkmate detection
        if (inCheck && legalMoves == 0)
            return -MateScore + currentPly;

        return alpha;
    }

    // =========================================================================
    //  Move Ordering
    // =========================================================================

    /// <summary>
    /// Scores all moves in the list and sorts them in descending order
    /// using insertion sort with early-exit optimization.
    /// </summary>
    private static void SortMoves(
        ref MoveList moveList,
        int ttMove,
        int pvMove,
        int killer1,
        int killer2,
        int counterMove,
        int prevContRowBase)
    {
        int count = moveList.count;
        if (count < 2)
        {
            if (count == 1)
                moveList.scores[0] = ScoreMove(
                    moveList.moves[0], ttMove, pvMove, killer1, killer2, counterMove, prevContRowBase);
            return;
        }

        // Span slices help the JIT eliminate bounds checks in the sort loop
        Span<int> moves = moveList.moves.AsSpan(0, count);
        Span<int> scores = moveList.scores.AsSpan(0, count);

        for (int i = 0; i < count; i++)
            scores[i] = ScoreMove(moves[i], ttMove, pvMove, killer1, killer2, counterMove, prevContRowBase);

        // Insertion sort with early-exit: skip elements already in order.
        // Move lists are partially ordered (TT/captures cluster at front),
        // so this skip fires frequently.
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

    /// <summary>
    /// Assigns a priority score to a single move for ordering.
    /// Higher score = searched first.
    /// </summary>
    private static int ScoreMove(
        int move,
        int ttMove,
        int pvMove,
        int killer1,
        int killer2,
        int counterMove,
        int prevContRowBase)
    {
        if (move == ttMove) return 30000;

        int promoted = GetMovePromoted(move);
        if (promoted == Q || promoted == q) return 29000;
        if (move == pvMove) return 20000;

        int target = GetMoveTarget(move);

        if (GetMoveCapture(move) != 0)
        {
            int piece = GetMovePiece(move);
            int victim = GetPieceAtSquare(target);
            return mvvLvaTable[(piece << PieceShift) | victim] + 10000;
        }

        if (move == killer1) return 9000;
        if (move == killer2) return 8000;
        if (move == counterMove) return 7800;
        if (GetMoveCastling(move) != 0) return 7500;
        if (promoted != 0) return 7200;

        int currIndex = (GetMovePiece(move) << HistoryShift) | target;
        int history = historyMoves[currIndex];
        if (prevContRowBase >= 0)
            history += contHistory[prevContRowBase + currIndex];
        return Math.Clamp(history, -7000, 7000);
    }

    // =========================================================================
    //  Utility Methods
    // =========================================================================

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
    private static void UpdateContHistory(int prevRowBase, int move, int bonus)
    {
        int currIndex = (GetMovePiece(move) << HistoryShift) | GetMoveTarget(move);
        int idx = prevRowBase + currIndex;
        contHistory[idx] += bonus - contHistory[idx] * Math.Abs(bonus) / MaxHistory;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInCheck()
    {
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
        ulong[] boards = bitboards;  // Cache reference for 12 sequential reads

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