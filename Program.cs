using static MoveFlag;
using static MoveGenerator;

class Program
{
    public const string StartPosition = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    public const string TrickyPosition = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    public const string PinPosition = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    public const string Position5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";

    // Debug mode variable
    public static bool debug = false; // [11/24] 14.9 <--> [12/24] 15.0

    static void Main()
    {
        if (debug)
        {
            RunTacticalTest(); // [12/24] 13.1 ->  [12/24] 13.5 -> [11/24] 14.8
        }
        else
        {
            // Connect to the GUI
            Uci.UciLoop();
        }
    }

    // ─────────────────────────────────────────────
    //  Tactical test suite
    //  Each entry: (FEN, bestMove in coordinate notation, description)
    //  All positions verified for FEN validity.
    // ─────────────────────────────────────────────
    private static readonly (string fen, string bestMove, string name)[] TacticalTests =
    [
        ("8/8/5p2/PR3pk1/8/1P4K1/8/5r2 w - - 3 42",
        "b5b4", "Rook Endgame — b4"),

        ("8/5p2/8/p4k2/Pp6/1P4KP/8/8 b - - 1 45",
        "f5e4", "Pawn Endgame — Ke4"),

        ("3q1rk1/1b1n1p1p/p2p3Q/3Np1Pp/2B1P3/P1N5/5r2/2KR4 w - - 0 25",
        "g5g6", "Attack — g6"),

        ("8/5N2/2p5/7p/1k1pp1pP/3P2P1/6K1/8 w - - 0 59",
        "d3e4", "Pawn Endgame — dxe4"),

        ("3r2k1/6P1/2p3rp/3qp3/p1p2Q2/2Pp4/PP1R2PP/5R1K w - - 0 31",
        "f4f8", "Queen Invasion — Qf8+"),

        ("r3k2r/1pB2pp1/p6p/2p3n1/4P3/P1PQ1b2/BP3q2/2K3RR w kq - 2 23",
        "g1g5", "Win Knight — Rxg5"),

        ("r3kb1r/ppp2ppp/2n5/2q2P2/4N3/2p2P2/P5PP/R1BQKB1R b KQkq - 1 12",
        "c3c2", "Promotion Threat — c2"),

        ("r3kb1r/ppp2ppp/2n5/2q2P2/4N3/5P2/P1p1Q1PP/R1B1KB1R b KQkq - 1 13",
        "c5b4", "King Check — Qb4+"),

        ("8/1p3r1p/6pk/p3Q3/6P1/PP5P/2r2q1N/R4B1K w - - 3 29",
        "h3h4", "King Hunt — h4"),

        ("8/3r4/8/8/2PK4/5k2/8/8 w - - 4 61",
        "d4e5", "Pawn Endgame — Ke5"),

        ("2r4k/Q7/P1p1q1p1/1pR4p/1P1b4/4PP2/3BKP2/8 b - - 0 31",
        "h8g8", "Quiet King Defensive move — Kg8"),

        ("6k1/R4rp1/2p2p2/Bp1bp2P/3p4/R5P1/1PP2P1K/r7 w - - 0 1",
        "a7a8", "Zwischenzug (In-Between Move) Simple Example 1 — Ra8"),

        ("r1b1r1k1/1ppn1p1p/3pnqp1/8/p1P1P3/5P2/PbNQNBPP/1R2RB1K w - - 0 1",
        "b1b2", "Rook sac to trap queen — Rxb2"),

        ("r3kb1r/3n1pp1/p6p/2pPp2q/Pp2N3/3B2PP/1PQ2P2/R3K2R w KQkq - 0 1",
        "d5d6", "Positional pawn move to suffocate the bishop — d6"),

        ("3r1bk1/p4ppp/Qp2p3/8/1P1B4/Pq2P1P1/2r2P1P/R3R1K1 b - - 0 1",
        "e6e5", "Pawn move to trap the bishop in the middle of the board — e5"),

        ("r4rk1/pp1n1p1p/1nqP2p1/2b1P1B1/4NQ2/1B3P2/PP2K2P/2R5 w - - 0 1",
        "c1c5", "Nolot #2 — Rxc5"),

        ("r2qk2r/ppp1b1pp/2n1p3/3pP1n1/3P2b1/2PB1NN1/PP4PP/R1BQK2R w KQkq - 0 1",
        "f3g5", "Nolot #3 — Nxg5"),

        ("r1b1kb1r/1p1n1ppp/p2ppn2/6BB/2qNP3/2N5/PPP2PPP/R2Q1RK1 w kq - 0 1",
        "d4e6", "Nolot #4 — Nxe6"),

        ("r2qrb1k/1p1b2p1/p2ppn1p/8/3NP3/1BN5/PPP3QP/1K3RR1 w - - 0 1",
        "e4e5", "Nolot #5 — e5"),

        ("r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
        "c3d5", "Knight pressure pinned knight and attack the queen — Nd5"),

        ("1r1bk2r/2R2ppp/p3p3/1b2P2q/4QP2/4N3/1B4PP/3R2K1 w k - 0 1",
        "d1d8", "Nolot #7 — Rxd8"),

        ("r1b2rk1/1p1nbppp/pq1p4/3B4/P2NP3/2N1p3/1PP3PP/R2Q1R1K w - - 0 1",
        "f1f7", "Nolot #10 — Rxf7"),

        ("r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
        "g6h6", "Nolot #11 — Rxh6"),

        ("r1bn2k1/p2p2P1/2p1r2P/1p2p3/2BqPn2/1P6/P1PP4/1K4R1 w - - 1 5",
        "h6h7", "Nolot #11 (follow-up) — h7+"),
    ];

    public static int GetTimeMs() => Environment.TickCount;

    public static void RunTacticalTest()
    {
        int passed = 0;
        int total = TacticalTests.Length;
        long totalDepth = 0;

        Console.WriteLine("═══════════════════════════════════════════════");
        Console.WriteLine("  Tactical Test Suite — 1 second per position");
        Console.WriteLine("═══════════════════════════════════════════════");
        Console.WriteLine();

        for (int t = 0; t < total; t++)
        {
            var (fen, expectedMove, name) = TacticalTests[t];

            // ── Setup position ────────────────────
            Board.ParseFEN(fen);
            TranspositionTable.Clear();
            Search.repetitionIndex = 0;
            Search.AddToRepetitionHistory(Zobrist.hashKey);

            // ── Setup time: 1 second ──────────────
            // Use the proper method — it sets BOTH softStopTime and stoptime
            TimeManagement.ResetForGo();
            TimeManagement.StartMoveTimeSearch(50);

            // ── Run search ────────────────────────
            Search.SearchPosition(64);

            // ── Compare result ────────────────────
            string engineMove = MoveEncoding.GetMove(Search.lastBestMove).Trim();
            bool correct = engineMove == expectedMove;
            if (correct) passed++;

            totalDepth += Search.lastDepthReached;

            string status = correct ? "✓ PASS" : "✗ FAIL";

            Console.WriteLine($"  [{t + 1,2}/{total}] {status}  {name}");
            Console.WriteLine($"         Expected: {expectedMove}  Got: {engineMove}  Depth: {Search.lastDepthReached}");
            Console.WriteLine();
        }

        Console.WriteLine("═══════════════════════════════════════════════");
        Console.WriteLine($"  Result: {passed}/{total} positions solved");

        double pct = (double)passed / total * 100.0;
        double avgDepth = (double)totalDepth / total;
        Console.WriteLine($"  Accuracy: {pct:F1}%");
        Console.WriteLine($"  Avg Depth: {avgDepth:F2}");
        Console.WriteLine("═══════════════════════════════════════════════");
    }

    static long PerftDriver(int depth)
    {
        long nodes = 0;
        if (depth == 0)
        {
            return 1;
        }

        MoveList moveList = new();
        GenerateMoves(ref moveList);

        for (int i = 0; i < moveList.count; i++)
        {
            BoardState state = Board.CopyBoard();

            if (MakeMove(moveList.moves[i], (int)allMoves) == 0)
            {
                Board.TakeBack(state);
                continue;
            }

            nodes += PerftDriver(depth - 1);

            Board.TakeBack(state);
        }
        return nodes;
    }

    static void PerftTest(int depth)
    {
        Console.WriteLine("\n     Performance test\n");

        MoveList moveList = new();
        GenerateMoves(ref moveList);

        long totalNodes = 0;
        int start = GetTimeMs();

        for (int i = 0; i < moveList.count; i++)
        {
            BoardState state = Board.CopyBoard();

            if (MakeMove(moveList.moves[i], (int)allMoves) == 0)
            {
                Board.TakeBack(state);
                continue;
            }

            long nodesForMove = PerftDriver(depth - 1);
            totalNodes += nodesForMove;

            Board.TakeBack(state);

            Console.WriteLine($"     move: {MoveEncoding.GetMove(moveList.moves[i])}  nodes: {nodesForMove}");
        }

        Console.WriteLine($"\n    Depth: {depth}");
        Console.WriteLine($"    Nodes: {totalNodes}");
        Console.WriteLine($"     Time: {GetTimeMs() - start}ms\n");
    }
}
