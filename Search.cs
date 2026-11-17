using System.Runtime.CompilerServices;
using System.Text;
using static Board;
using static MoveEncoding;
using static MoveGenerator;
using static System.Numerics.BitOperations;

// =============================================================================
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
// =============================================================================

public static class Search
{
    // =========================================================================
    //  SECTION 1 — Search parameters
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

    private const int MaxHistory = 16384;

    #endregion

    #region Internal

    private const int TimeCheckMask = 16383;
    private const int RepetitionTableSize = 1024;
    private const int NoSquare = (int)Square.noSquare;

    #endregion

    // =========================================================================
    //  SECTION 2 — State and flat-array tables
    // =========================================================================

    // Power-of-two row strides allow indexing via (row << shift) | col instead of multiplication.
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

    // Indexed by: (depth << 6) | moveIndex
    private static readonly int[] lmrTable = new int[(MaxPly + 1) << LmrRowShift];

    // Indexed by: (ply << 1) for slot 0, (ply << 1) + 1 for slot 1
    private static readonly int[] killerMoves = new int[MaxPly << KillerShift];

    // Indexed by: (piece << 6) | targetSquare
    private static readonly int[] historyMoves = new int[12 << HistoryShift];

    // Indexed by opponent's previous move: (piece << 6) | targetSquare -> best quiet refutation
    private static readonly int[] counterMoves = new int[12 << HistoryShift];

    // Indexed by: prevMoveIndex * 768 + currMoveIndex, where moveIndex = (piece << 6) | targetSquare
    private static readonly int[] contHistory = new int[768 * 768];

    // Tracks the move played at each ply so child nodes can look up the parent move
    private static readonly int[] moveStack = new int[MaxPly + 2];

    private static readonly MoveList[] moveLists = new MoveList[MaxPly + 2];

    private static readonly int[,] mvvLva =
    {
        { 105, 205, 305, 405, 505, 605 },
        { 104, 204, 304, 404, 504, 604 },
        { 103, 203, 303, 403, 503, 603 },
        { 102, 202, 302, 402, 502, 602 },
        { 101, 201, 301, 401, 501, 601 },
        { 100, 200, 300, 400, 500, 600 },
    };

    // Indexed by: (attacker << 4) | victim
    private static readonly int[] mvvLvaTable = new int[12 << PieceShift];

    // Triangular PV table indexed by: (ply << 6) | index
    private static readonly int[] pvTable = new int[MaxPly << PvRowShift];
    private static readonly int[] pvLength = new int[MaxPly];

    private static readonly ulong[] repetitionTable = new ulong[RepetitionTableSize];
    public static int repetitionIndex = 0;

    // =========================================================================
    //  SECTION 3 — One-time initialization
    // =========================================================================

    static Search()
    {
        for (int i = 0; i < moveLists.Length; i++)
            moveLists[i] = new MoveList();

        // Precompute logarithmic Late Move Reductions table
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

        // Flatten 6x6 MVV-LVA table across all 12 piece codes to avoid modulo in ScoreMove
        for (int attacker = 0; attacker < 12; attacker++)
        {
            int rowBase = attacker << PieceShift;
            for (int victim = 0; victim < 12; victim++)
                mvvLvaTable[rowBase + victim] = mvvLva[attacker % 6, victim % 6];
        }
    }

    // =========================================================================
    //  SECTION 4 — Iterative deepening entry point
    // =========================================================================

    /// <summary>
    /// Searches the current position up to <paramref name="depth"/> using iterative
    /// deepening with aspiration windows, and prints UCI info and bestmove output.
    /// </summary>
    public static void SearchPosition(int depth)
    {
        // Step 1. Reset search state and heuristic tables
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

        // Step 2. Iterative deepening loop
        for (int currentDepth = 1; currentDepth <= depth; currentDepth++)
        {
            int score;

            // Step 3. Search with a full window at shallow depths, then narrow aspiration windows.
            // If the score falls outside [alpha, beta], widen the failed bound and re-search.
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

            // Center next iteration's aspiration window around the current score
            alpha = score - AspirationWindow;
            beta = score + AspirationWindow;
            completedDepth = currentDepth;

            // Step 4. Record best root move and report UCI info for the completed iteration
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

        // Step 5. Fallback: if time expired before depth 1 finished, pick the first legal move
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
    // =========================================================================

    /// <summary>
    /// Fail-soft Principal Variation Search (negamax). Returns the score from
    /// the side-to-move's perspective.
    /// </summary>
    private static int AlphaBeta(int alpha, int beta, int depth, bool allowNullMove = true)
    {
        // Step 1. Periodic time check and draw detection (repetition & 50-move rule)
        if ((nodes & TimeCheckMask) == 0)
            TimeManagement.Communicate();

        if (TimeManagement.stopped)
            return 0;

        int currentPly = ply;
        pvLength[currentPly] = currentPly;

        if (currentPly > 0 && IsRepetition())
            return 0;

        if (halfmoveClock >= 100)
            return 0;

        // Step 2. Check extension and horizon drop into Quiescence search
        bool inCheck = IsInCheck();
        if (inCheck && currentPly < MaxPly - 10)
            depth++;

        if (depth <= 0)
            return Quiescence(alpha, beta);

        if (currentPly >= MaxPly - 1)
            return Evaluation.Evaluate();

        nodes++;

        // Step 3. Mate distance pruning: narrow window if a shorter mate was already found
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

        // Step 4. Transposition table probe (cutoffs allowed only in non-PV nodes)
        ulong hashKey = Zobrist.hashKey;

        int ttMove = 0;
        int ttScore = TranspositionTable.Probe(
            hashKey, depth, alpha, beta, currentPly, out ttMove);

        if (ttScore != TranspositionTable.NoScore && !pvNode)
            return ttScore;

        // Step 5. Static evaluation
        int staticEval = inCheck ? -MateScore : Evaluation.Evaluate();

        bool quietNode = !pvNode & !inCheck;
        bool prunableNode = quietNode & (currentPly > 0);

        // Step 6. Reverse futility pruning: if static eval is well above beta at shallow
        // depth, assume the position will still fail high even if we lose a margin per ply.
        if (prunableNode &&
            depth <= ReverseFutilityMaxDepth &&
            staticEval > -MateThreshold &&
            staticEval < MateThreshold)
        {
            if (staticEval - ReverseFutilityMarginPerDepth * depth >= beta)
                return beta;
        }

        // Step 7. Null move pruning: pass our turn and search with reduced depth.
        // Disabled in pawn-only endgames to avoid missing zugzwang.
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

        // Step 8. Internal iterative reduction (IIR): reduce depth when no TT move exists to guide ordering
        if (depth >= 4 && ttMove == 0 && !inCheck)
            depth--;

        // Step 9. Prepare futility flag, parent move context, and sorted move list
        bool canPrune = false;
        if (quietNode && depth <= FutilityMaxDepth)
            canPrune = staticEval + FutilityMarginPerDepth * depth <= alpha;

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

        int newDepth = depth - 1;
        int depthSquared = depth * depth;
        int lmrRowBase = depth << LmrRowShift;
        int pvRowBase = currentPly << PvRowShift;
        int nextPly = currentPly + 1;
        int childRowBase = nextPly << PvRowShift;

        // Step 10. Iterate through moves
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

            // Step 10a. Futility pruning: skip late quiet moves if static eval is far below alpha
            if (canPrune && movesSearched > 0 && isQuiet && move != ttMove)
            {
                anyMovePruned = true;
                continue;
            }

            // Step 10b. Make move and filter out illegal moves that leave the king in check
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

            // Step 10c. Principal Variation Search (PVS) with Late Move Reductions (LMR)
            if (movesSearched == 0)
            {
                score = -AlphaBeta(-beta, -alpha, newDepth);
            }
            else
            {
                // Reduce depth for late quiet moves
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
                    score = alpha + 1;  // Force unreduced zero-window search below
                }

                // If reduced search beat alpha (or LMR was skipped), verify at full depth with zero window,
                // then re-search with full window if it still improves alpha inside a PV node.
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

            // Step 10d. Beta cutoff (fail-high): update quiet move heuristics and store lower bound in TT
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

            // Step 10e. Alpha improvement: update history and propagate child PV line
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

        // Step 11. Checkmate / stalemate check and TT storage
        if (legalMoves == 0)
        {
            // If we skipped moves via futility pruning, we cannot prove stalemate
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
    // =========================================================================

    /// <summary>
    /// Searches tactical sequences (captures and promotions, or all evasions
    /// when in check) until a quiet position is reached.
    /// </summary>
    public static int Quiescence(int alpha, int beta)
    {
        // Step 1. Time check and draw/ply limits
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

        // Step 2. Stand-pat evaluation: when not in check, the side to move can decline
        // all captures, so the static evaluation acts as a lower bound on the score.
        int eval = 0;
        if (!inCheck)
        {
            eval = Evaluation.Evaluate();

            if (eval >= beta)
                return eval;

            if (eval > alpha)
                alpha = eval;
        }

        // Step 3. Generate moves: all evasions if in check, captures only otherwise
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

        // Step 4. Iterate through tactical moves (or check evasions)
        for (int i = 0; i < moveCount; i++)
        {
            int move = moves[i];

            // Delta pruning: skip captures whose material gain + margin still cannot reach alpha
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

        // Step 5. If in check and no legal evasion exists, return checkmate score
        if (inCheck && legalMoves == 0)
            return -MateScore + currentPly;

        return alpha;
    }

    // =========================================================================
    //  SECTION 7 — Move ordering
    // =========================================================================

    /// <summary>
    /// Scores all moves in <paramref name="moveList"/> and sorts them descending
    /// using insertion sort.
    /// </summary>
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

        int currIndex = (GetMovePiece(move) << HistoryShift) | target;
        int history = historyMoves[currIndex];
        if (prevContRowBase >= 0)
            history += contHistory[prevContRowBase + currIndex];
        return Math.Clamp(history, -MaxHistory, MaxHistory);
    }

    // =========================================================================
    //  SECTION 8 — Helpers
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

    /// <summary>
    /// Checks if the current position hash has appeared earlier in the game or search.
    /// Steps back by 2 plies (same side to move) and stops at the last irreversible
    /// move (bounded by <see cref="Board.halfmoveClock"/>).
    /// </summary>
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsMateScore(int score) =>
        score >= MateThreshold || score <= -MateThreshold;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScoreToMate(int score) =>
        score > 0 ? (MateScore - score + 1) / 2 : -(MateScore + score) / 2;

    private static string FormatUciScore(int score) =>
        IsMateScore(score) ? $"mate {ScoreToMate(score)}" : $"cp {score}";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInCheck()
    {
        int sideToMove = side;
        int kSq = sideToMove == White
            ? TrailingZeroCount(bitboards[K])
            : TrailingZeroCount(bitboards[k]);
        return PieceAttacks.IsSquareAttacked(kSq, sideToMove ^ 1);
    }

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

    /// <summary>
    /// Updates history using the gravity formula: <c>h += bonus - h * |bonus| / MaxHistory</c>,
    /// which smoothly bounds values within [-MaxHistory, +MaxHistory] and decays old scores.
    /// </summary>
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