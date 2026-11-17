using System.Runtime.CompilerServices;
using System.Text;
using static Board;
using static MoveEncoding;
using static MoveGenerator;
using static System.Numerics.BitOperations;

// =============================================================================
//  Score convention:
//      Positive  = side to move is better
//      Negative  = opponent is better
//      All scores are from the side-to-move's perspective (negamax).
//      Mate scores encode distance: ±(MateScore − ply).
//
//  Move ordering (highest → lowest priority):
//
//      Tier 1: TT move          10,000,000
//      Tier 2: Q-promotion       9,000,000
//      Tier 3: PV move           8,000,000
//      Tier 4: Captures          1,000,100 .. 1,000,605   (MVV-LVA)
//              ─── gap: 83,716 ───
//      Tier 5: Killer 1            900,000
//              Killer 2            800,000
//      Tier 6: Counter-move        700,000
//      Tier 7: Castling            600,000
//              Underpromotion      500,000
//              ─── gap: 483,616 ───
//      Tier 8: History            -16,384 .. +16,384      (gravity-bounded)
//
//  Performance techniques:
//      1. Flat 1D arrays with bit-shift indexing (avoids multi-dim bounds checks)
//      2. Pre-expanded LMR and MVV-LVA tables (eliminates log/modulo from hot paths)
//      3. Hoisted loop invariants (depth-dependent values computed once, not per-move)
//      4. Pre-allocated MoveList pool (zero heap allocation during search)
//      5. BoardState by 'in' reference (avoids ~144-byte struct copy per takeback)
//      6. Insertion sort with early-exit (exploits partial ordering from TT/captures)
//      7. Static fields cached into locals (keeps bitboards/occupancies in registers)
// =============================================================================

public static class Search
{
    // =========================================================================
    //  SECTION 1 — Search parameters
    //
    //  All tunable constants live here. Changing a number is the only way to
    //  retune the search — no structural code needs to move. Grouped by the
    //  feature they control.
    // =========================================================================

    #region Core

    private const int MaxPly = 64;
    private const int Infinity = 50000;
    private const int MateScore = 49000;
    private const int MateThreshold = MateScore - MaxPly;

    #endregion

    #region Late Move Reductions

    private const int FullDepthMoves = 4;
    private const int ReductionLimit = 3;
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

    // Gravity formula: history += bonus − history × |bonus| / MaxHistory.
    // Smoothly bounds scores without hard clamping.
    private const int MaxHistory = 16384;

    #endregion

    #region Internal

    private const int TimeCheckMask = 16383;
    private const int RepetitionTableSize = 1024;
    private const int NoSquare = (int)Square.noSquare;

    #endregion

    // =========================================================================
    //  SECTION 2 — State and flat-array tables
    //
    //  Every table uses flat 1D indexing with bit-shift strides. Row width
    //  is always a power of two so the index computation is a shift + OR
    //  instead of a multiply + add. Example:
    //
    //      lmrTable[(depth << 6) | moveIndex]     // 64 entries per depth
    //      historyMoves[(piece << 6) | square]     // 64 squares per piece
    //
    //  All arrays are pre-allocated once and reused across searches.
    //  Array.Clear resets them at the start of each SearchPosition call.
    // =========================================================================

    private const int LmrRowShift = 6;      // 64 move indices per depth
    private const int PvRowShift = 6;       // 64 PV entries per ply
    private const int PieceShift = 4;       // 16 entries per piece (12 used)
    private const int HistoryShift = 6;     // 64 target squares per piece
    private const int KillerShift = 1;      // 2 killer slots per ply

    private static int ply;
    private static long nodes;

    public static long LastNodeCount => nodes;
    public static int lastBestMove = 0;
    public static int lastDepthReached = 0;

    // LMR reduction table: lmrTable[(depth << 6) | moveIndex]
    private static readonly int[] lmrTable = new int[(MaxPly + 1) << LmrRowShift];

    // Killer moves: killerMoves[(ply << 1)] and [(ply << 1) + 1].
    // Adjacent in memory → single cache line for both killers at the same ply.
    private static readonly int[] killerMoves = new int[MaxPly << KillerShift];

    // History heuristic: historyMoves[(piece << 6) | targetSquare]
    private static readonly int[] historyMoves = new int[12 << HistoryShift];

    // Counter-move table: counterMoves[(piece << 6) | targetSquare] = best reply.
    // Indexed by the opponent's previous move. When a quiet move causes a beta
    // cutoff, it's stored here as the refutation of whatever the opponent played.
    // 768 ints = 3 KB, always in L1.
    private static readonly int[] counterMoves = new int[12 << HistoryShift];

    // Continuation history: contHistory[prevIndex × 768 + currIndex] = score.
    // For each opponent move, stores how well each of our replies performed.
    // Uses the same gravity formula as regular history.
    // 768 × 768 = 589,824 ints ≈ 2.3 MB, fits in L2.
    private static readonly int[] contHistory = new int[768 * 768];

    // Move stack: moveStack[ply] = move played at this ply. Child nodes look
    // up their parent's move to index counterMoves and contHistory.
    private static readonly int[] moveStack = new int[MaxPly + 2];

    // One MoveList per ply, pre-allocated to avoid heap pressure during search.
    private static readonly MoveList[] moveLists = new MoveList[MaxPly + 2];

    // MVV-LVA source table (used only during initialization).
    private static readonly int[,] mvvLva =
    {
        { 105, 205, 305, 405, 505, 605 },
        { 104, 204, 304, 404, 504, 604 },
        { 103, 203, 303, 403, 503, 603 },
        { 102, 202, 302, 402, 502, 602 },
        { 101, 201, 301, 401, 501, 601 },
        { 100, 200, 300, 400, 500, 600 },
    };

    // Pre-expanded MVV-LVA: mvvLvaTable[(attacker << 4) | victim].
    // Eliminates modulo from the hot path.
    private static readonly int[] mvvLvaTable = new int[12 << PieceShift];

    // PV table: pvTable[(ply << 6) | index]
    private static readonly int[] pvTable = new int[MaxPly << PvRowShift];
    private static readonly int[] pvLength = new int[MaxPly];

    // Repetition detection
    private static readonly ulong[] repetitionTable = new ulong[RepetitionTableSize];
    public static int repetitionIndex = 0;

    // =========================================================================
    //  SECTION 3 — One-time initialization
    //
    //  Runs on first access. Builds the LMR reduction table (log formula)
    //  and expands MVV-LVA into a flat lookup so the hot path never computes
    //  modulo or logarithms.
    // =========================================================================

    static Search()
    {
        for (int i = 0; i < moveLists.Length; i++)
            moveLists[i] = new MoveList();

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

        for (int attacker = 0; attacker < 12; attacker++)
        {
            int rowBase = attacker << PieceShift;
            for (int victim = 0; victim < 12; victim++)
                mvvLvaTable[rowBase + victim] = mvvLva[attacker % 6, victim % 6];
        }
    }

    // =========================================================================
    //  SECTION 4 — Iterative deepening entry point
    //
    //  Searches from depth 1 upward, using the previous iteration's score
    //  to set aspiration windows (from depth 4 onward). Outputs UCI info
    //  lines per iteration and bestmove at the end. Time management can
    //  abort between iterations or mid-search via TimeManagement.stopped.
    // =========================================================================

    public static void SearchPosition(int depth)
    {
        nodes = 0;
        ply = 0;
        TimeManagement.stopped = false;

        Array.Clear(pvTable);
        Array.Clear(pvLength);
        Array.Clear(killerMoves);
        Array.Clear(historyMoves);
        Array.Clear(counterMoves);
        Array.Clear(contHistory);
        Array.Clear(moveStack);

        int alpha = -Infinity;
        int beta = Infinity;
        int bestMove = 0;
        int completedDepth = 0;

        StringBuilder? infoBuilder = Program.Debug ? null : new StringBuilder(256);

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

        // Fallback: if search was stopped before finding a move, pick any legal move
        if (bestMove == 0)
        {
            ref MoveList moveList = ref moveLists[0];
            GenerateMoves(ref moveList);

            for (int i = 0; i < moveList.count; i++)
            {
                int move = moveList.moves[i];
                BoardState state = CopyBoard();

                if (MakeMove(move) == 0)
                {
                    TakeBack(in state);
                    continue;
                }

                TakeBack(in state);
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
    //  SECTION 5 — Alpha-beta search (PVS)
    //
    //  The hot function. Called recursively for every node in the search tree.
    //  Uses fail-soft scoring: the returned value is the actual best score
    //  found, not clamped to the [alpha, beta] window.
    //
    //  Node types:
    //      PV node  — beta − alpha > 1 (full window, expecting exact score)
    //      Cut node — zero-window, expected to fail high
    //      All node — zero-window, expected to fail low
    //
    //  The order of pruning techniques follows a standard pattern:
    //      1. Repetition / 50-move draws (cheapest check)
    //      2. Check extension (before horizon test)
    //      3. Mate distance pruning
    //      4. TT probe (may return immediately in non-PV nodes)
    //      5. Static eval (needed by all pruning below)
    //      6. Reverse futility pruning (eval >> beta → prune)
    //      7. Null move pruning (skip our turn → still >= beta?)
    //      8. IIR (no TT move → reduce depth by 1)
    //      9. Futility pruning flag (applied per-move in the loop)
    //     10. Move loop with LMR
    // =========================================================================

    private static int AlphaBeta(int alpha, int beta, int depth, bool allowNullMove = true)
    {
        if ((nodes & TimeCheckMask) == 0)
            TimeManagement.Communicate();

        if (TimeManagement.stopped)
            return 0;

        int currentPly = ply;
        pvLength[currentPly] = currentPly;

        // --- Draws ---
        if (currentPly > 0 && IsRepetition())
            return 0;

        if (halfmoveClock >= 100)
            return 0;

        // --- Check extension ---
        bool inCheck = IsInCheck();
        if (inCheck && currentPly < MaxPly - 10)
            depth++;

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

        // --- TT probe ---
        ulong hashKey = Zobrist.hashKey;

        int ttMove = 0;
        int ttScore = TranspositionTable.Probe(
            hashKey, depth, alpha, beta, currentPly, out ttMove);

        if (ttScore != TranspositionTable.NoScore && !pvNode)
            return ttScore;

        // --- Static eval ---
        int staticEval = inCheck ? -MateScore : Evaluation.Evaluate();

        // & (not &&): branchless — both sides always evaluated
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
            BoardState nullState = CopyBoard();

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
            int nullScore = -AlphaBeta(-beta, -beta + 1, depth - 1 - reduction, false);
            ply--;
            TakeBack(in nullState);

            if (TimeManagement.stopped)
                return 0;

            if (nullScore >= beta)
                return beta;
        }

        // --- IIR: reduce depth when no TT move is available ---
        // The !inCheck guard prevents canceling the check extension above.
        if (depth >= 4 && ttMove == 0 && !inCheck)
            depth--;

        // --- Futility flag (applied per-move in the loop) ---
        bool canPrune = false;
        if (quietNode && depth <= FutilityMaxDepth)
            canPrune = staticEval + FutilityMarginPerDepth * depth <= alpha;

        // --- Parent move lookup for counter-move + continuation history ---
        int prevMove = currentPly > 0 ? moveStack[currentPly - 1] : 0;
        int counterMove = 0;
        int prevContIndex = -1;
        int prevContRowBase = -1;
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
            ttMove, pvMove,
            killerMoves[killerIndex], killerMoves[killerIndex + 1],
            counterMove, prevContRowBase);

        int[] moves = moveList.moves;
        int moveCount = moveList.count;

        // Hoisted loop invariants
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

            // Futility: skip quiet moves when static eval is far below alpha
            if (canPrune && movesSearched > 0 && isQuiet && move != ttMove)
            {
                anyMovePruned = true;
                continue;
            }

            BoardState state = CopyBoard();
            if (MakeMove(move) == 0)
            {
                TakeBack(in state);
                continue;
            }

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
                // LMR for quiet moves searched after the first few
                if (movesSearched >= FullDepthMoves &&
                    depth >= ReductionLimit &&
                    isQuiet && !inCheck)
                {
                    int moveIndex = movesSearched < 64 ? movesSearched : 63;
                    int reduction = lmrTable[lmrRowBase + moveIndex];

                    if (pvNode && reduction > 1)
                        reduction--;

                    if (move == counterMove && reduction > 0)
                        reduction--;

                    score = -AlphaBeta(-alpha - 1, -alpha, newDepth - reduction);
                }
                else
                {
                    score = alpha + 1;  // Force re-search below
                }

                // PVS re-search: reduced/null-window beat alpha → verify
                if (score > alpha)
                {
                    score = -AlphaBeta(-alpha - 1, -alpha, newDepth);

                    if (score > alpha && score < beta)
                        score = -AlphaBeta(-beta, -alpha, newDepth);
                }
            }

            ply--;
            repetitionIndex--;
            TakeBack(in state);

            if (TimeManagement.stopped)
                return 0;

            movesSearched++;

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

                pvTable[pvRowBase + currentPly] = move;

                int childPvLength = pvLength[nextPly];
                int copyCount = childPvLength - nextPly;

                if (copyCount > 0)
                    Array.Copy(pvTable, childRowBase + nextPly,
                               pvTable, pvRowBase + nextPly, copyCount);

                pvLength[currentPly] = childPvLength;
            }
        }

        // --- Terminal node ---
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
    //  SECTION 6 — Quiescence search
    //
    //  Captures-only search at the horizon to avoid the horizon effect.
    //  Stand-pat evaluation provides a lower bound. Delta pruning skips
    //  captures whose material gain cannot possibly raise alpha.
    //  When in check, all moves are generated (evasion search).
    // =========================================================================

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

        // Stand pat: the side to move can usually choose not to capture
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
            GenerateMoves(ref moveList, capturesOnly: true);

        int killerIndex = currentPly << KillerShift;

        SortMoves(
            ref moveList,
            0, 0,
            killerMoves[killerIndex], killerMoves[killerIndex + 1],
            0, -1);

        int[] moves = moveList.moves;
        int moveCount = moveList.count;
        int legalMoves = 0;
        int deltaBase = eval + QsDeltaMargin;

        for (int i = 0; i < moveCount; i++)
        {
            int move = moves[i];

            // Delta pruning: skip captures that can't raise alpha
            if (!inCheck)
            {
                int promoted = GetMovePromoted(move);
                int capturedValue = GetPieceValue(GetPieceAtSquare(GetMoveTarget(move)));
                int promotionDelta = promoted != 0 ? GetPieceValue(promoted) - 89 : 0;

                if (deltaBase + capturedValue + promotionDelta < alpha)
                    continue;
            }

            BoardState state = CopyBoard();
            if (MakeMove(move) == 0)
            {
                TakeBack(in state);
                continue;
            }

            legalMoves++;
            ply++;
            int score = -Quiescence(-beta, -alpha);
            ply--;
            TakeBack(in state);

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

    // =========================================================================
    //  SECTION 7 — Move ordering
    //
    //  Scores every move in the list then sorts descending with insertion
    //  sort. Insertion sort outperforms quicksort on small, partially-ordered
    //  lists — TT/captures cluster at the front naturally, so the early-exit
    //  skip fires frequently.
    //
    //  The tier gaps (see header) prevent history scores from contaminating
    //  heuristic ordering. MVV-LVA ensures PxQ > QxP (capturing with the
    //  least valuable piece is preferred).
    // =========================================================================

    private static void SortMoves(
        ref MoveList moveList,
        int ttMove, int pvMove,
        int killer1, int killer2,
        int counterMove, int prevContRowBase)
    {
        int count = moveList.count;
        if (count < 2)
        {
            if (count == 1)
                moveList.scores[0] = ScoreMove(
                    moveList.moves[0], ttMove, pvMove, killer1, killer2,
                    counterMove, prevContRowBase);
            return;
        }

        Span<int> moves = moveList.moves.AsSpan(0, count);
        Span<int> scores = moveList.scores.AsSpan(0, count);

        for (int i = 0; i < count; i++)
            scores[i] = ScoreMove(moves[i], ttMove, pvMove, killer1, killer2,
                                  counterMove, prevContRowBase);

        // Insertion sort with early-exit: skip elements already in order
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
        int move, int ttMove, int pvMove,
        int killer1, int killer2,
        int counterMove, int prevContRowBase)
    {
        if (move == ttMove) return 10_000_000;

        int promoted = GetMovePromoted(move);

        if (promoted == Q || promoted == q) return 9_000_000;
        if (move == pvMove) return 8_000_000;

        int target = GetMoveTarget(move);

        if (GetMoveCapture(move) != 0)
        {
            int piece = GetMovePiece(move);
            int victim = GetPieceAtSquare(target);
            return mvvLvaTable[(piece << PieceShift) | victim] + 1_000_000;
        }

        if (move == killer1) return 900_000;
        if (move == killer2) return 800_000;
        if (move == counterMove) return 700_000;
        if (GetMoveCastling(move) != 0) return 600_000;
        if (promoted != 0) return 500_000;

        // History + continuation history, gravity-bounded
        int currIndex = (GetMovePiece(move) << HistoryShift) | target;
        int history = historyMoves[currIndex];
        if (prevContRowBase >= 0)
            history += contHistory[prevContRowBase + currIndex];
        return Math.Clamp(history, -MaxHistory, MaxHistory);
    }

    // =========================================================================
    //  SECTION 8 — Helpers
    //
    //  Small utility methods used by the search. All are marked for
    //  aggressive inlining since they're called millions of times per second
    //  and consist of a few instructions each.
    // =========================================================================

    // --- Repetition detection ------------------------------------------------

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
        ulong[] table = repetitionTable;

        for (int i = index - 3; i >= earliest; i -= 2)
        {
            if (table[i] == key)
                return true;
        }

        return false;
    }

    // --- UCI score formatting ------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsMateScore(int score) =>
        score >= MateThreshold || score <= -MateThreshold;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScoreToMate(int score) =>
        score > 0 ? (MateScore - score + 1) / 2 : -(MateScore + score) / 2;

    private static string FormatUciScore(int score) =>
        IsMateScore(score) ? $"mate {ScoreToMate(score)}" : $"cp {score}";

    // --- Board queries -------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInCheck()
    {
        int sideToMove = side;
        int kSq = sideToMove == White
            ? TrailingZeroCount(bitboards[K])
            : TrailingZeroCount(bitboards[k]);
        return PieceAttacks.IsSquareAttacked(kSq, sideToMove ^ 1);
    }

    /// <summary>
    /// Finds which piece occupies a square by scanning all 12 bitboards.
    /// Ordered by frequency: pawns first, kings last.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPieceAtSquare(int square)
    {
        ulong mask = 1UL << square;
        ulong[] bb = bitboards;

        if ((bb[P] & mask) != 0) return P;
        if ((bb[p] & mask) != 0) return p;
        if ((bb[N] & mask) != 0) return N;
        if ((bb[n] & mask) != 0) return n;
        if ((bb[B] & mask) != 0) return B;
        if ((bb[b] & mask) != 0) return b;
        if ((bb[R] & mask) != 0) return R;
        if ((bb[r] & mask) != 0) return r;
        if ((bb[Q] & mask) != 0) return Q;
        if ((bb[q] & mask) != 0) return q;
        if ((bb[K] & mask) != 0) return K;
        if ((bb[k] & mask) != 0) return k;

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasNonPawnMaterial(int sideToCheck)
    {
        ulong[] bb = bitboards;
        return sideToCheck == White
            ? (bb[N] | bb[B] | bb[R] | bb[Q]) != 0
            : (bb[n] | bb[b] | bb[r] | bb[q]) != 0;
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

    // --- History updates -----------------------------------------------------

    // Gravity formula: score drifts toward zero over time, preventing any
    // single position from permanently dominating the history table.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateHistory(int move, int bonus)
    {
        int index = (GetMovePiece(move) << HistoryShift) | GetMoveTarget(move);
        historyMoves[index] += bonus - historyMoves[index] * Math.Abs(bonus) / MaxHistory;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateContHistory(int prevRowBase, int move, int bonus)
    {
        int idx = prevRowBase + ((GetMovePiece(move) << HistoryShift) | GetMoveTarget(move));
        contHistory[idx] += bonus - contHistory[idx] * Math.Abs(bonus) / MaxHistory;
    }
}