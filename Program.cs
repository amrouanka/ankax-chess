using static MoveGenerator;

// =============================================================================
// AnkaX Chess Engine - Program Entry Point & Testing Suite
//
// Command-line entry point supporting standard UCI mode, perft performance
// verification, and fixed-depth search benchmarks.
//
// Commands:
//   AnkaX.exe                 -> UCI mode (default)
//   AnkaX.exe perft <N>       -> Run perft to depth N on startpos
//   AnkaX.exe perft <N> <FEN> -> Run perft to depth N on custom FEN
//   AnkaX.exe perft suite     -> Run full built-in perft test suite
//   AnkaX.exe bench [depth]   -> Run fixed-depth benchmark suite (default depth 12)
// =============================================================================

internal static class Program
{
    // =========================================================================
    //  SECTION 1 — Known FEN Positions & Global Configuration
    // =========================================================================

    public const string StartPosition = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    public const string KiwipetePosition = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    public const string PinPosition = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    public const string Position4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
    public const string Position5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";
    public const string Position6 = "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10";

    // Suppresses standard UCI output during benchmarks and test harnesses.
    public static bool Debug = false;

    // =========================================================================
    //  SECTION 2 — Entry Point & Argument Dispatch
    // =========================================================================

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Uci.UciLoop();
                return 0;
            }

            if (string.Equals(args[0], "perft", StringComparison.OrdinalIgnoreCase))
                return HandlePerftCommand(args);

            if (string.Equals(args[0], "bench", StringComparison.OrdinalIgnoreCase))
                return HandleBenchCommand(args);

            if (string.Equals(args[0], "tune", StringComparison.OrdinalIgnoreCase))
                return Tuner.Run(args);

            return PrintUsageAndExit();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex}");
            return 1;
        }
    }

    private static int PrintUsageAndExit()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  AnkaX                          Run UCI mode");
        Console.WriteLine("  AnkaX perft <depth>            Run perft on starting position");
        Console.WriteLine("  AnkaX perft <depth> <fen>      Run perft on given FEN");
        Console.WriteLine("  AnkaX perft suite              Run built-in perft test suite");
        Console.WriteLine("  AnkaX bench [depth]            Run benchmark (default depth 12)");
        Console.WriteLine("  AnkaX tune [dataset] [epochs]  Run Texel evaluation tuner");
        return 1;
    }

    // =========================================================================
    //  SECTION 3 — Benchmark Suite
    // =========================================================================

    private static readonly string[] BenchPositions =
    [
        // Opening
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "rnbqkbnr/pppppppp/8/8/2P5/8/PP1PPPPP/RNBQKBNR b KQkq - 0 1",
        "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R b KQkq - 3 3",
        "r1bqkbnr/1ppp1ppp/p1n5/1B2p3/4P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 4",

        // Middlegame — tactical / open
        "rnbqkb1r/1p2pppp/p2p1n2/8/3NP3/2N5/PPP2PPP/R1BQKB1R w KQkq - 0 6",
        "r1bqkb1r/pppp1ppp/2n2n2/4p2Q/2B1P3/8/PPPP1PPP/RNB1K1NR w KQkq - 4 4",
        "r1bq1rk1/pp2ppbp/2np1np1/8/3NP3/2N1BP2/PPPQ2PP/R3KB1R w KQ - 0 9",

        // Middlegame — positional / closed
        "rnbq1rk1/ppp1ppbp/3p1np1/8/2PPP3/2N2N2/PP3PPP/R1BQKB1R w KQ - 0 7",
        "rnbqkb1r/ppp1pppp/5n2/3p4/2PP4/2N5/PP2PPPP/R1BQKBNR w KQkq - 2 3",
        "rnbqk1nr/pppp1ppp/4p3/8/1bPP4/2N5/PP2PPPP/R1BQKBNR w KQkq - 2 3",

        // Complex / sharp positions
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",

        // Endgame positions
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "8/8/4k3/8/4P3/4K3/8/4R3 w - - 0 1",
        "8/5pk1/6p1/3P4/8/6K1/5P2/8 w - - 0 1",
    ];

    // Runs fixed-depth search benchmark over standard position set.
    private static int HandleBenchCommand(string[] args)
    {
        int depth = 12;
        if (args.Length >= 2 && int.TryParse(args[1], out int d) && d > 0)
            depth = d;

        bool prevDebug = Debug;
        Debug = true;

        long totalNodes = 0;
        long totalTime = 0;

        Console.WriteLine($"Benchmark: depth {depth}, {BenchPositions.Length} positions");
        Console.WriteLine();

        // ---------------------------------------------------------------------
        //  Benchmark Execution Loop
        // ---------------------------------------------------------------------
        for (int i = 0; i < BenchPositions.Length; i++)
        {
            Board.ParseFEN(BenchPositions[i]);

            Search.repetitionIndex = 0;
            Search.AddToRepetitionHistory(Zobrist.hashKey);
            TranspositionTable.Clear();

            long start = GetTimeMs();
            Search.SearchPosition(depth);
            long elapsed = GetTimeMs() - start;

            long nodes = Search.LastNodeCount;
            totalNodes += nodes;
            totalTime += elapsed;

            Console.WriteLine($"  [{i + 1,2}/{BenchPositions.Length}]  " +
                              $"{nodes,12:N0} nodes  {elapsed,6} ms");
        }

        long nps = totalTime > 0 ? totalNodes * 1000 / totalTime : 0;

        Console.WriteLine();
        Console.WriteLine($"  Total nodes: {totalNodes:N0}");
        Console.WriteLine($"  Total time:  {totalTime:N0} ms");
        Console.WriteLine($"  NPS:         {nps:N0}");
        Console.WriteLine();

        Debug = prevDebug;
        return 0;
    }

    // =========================================================================
    //  SECTION 4 — Performance Test (Perft) Core
    // =========================================================================

    private static int HandlePerftCommand(string[] args)
    {
        if (args.Length >= 2 && string.Equals(args[1], "suite", StringComparison.OrdinalIgnoreCase))
        {
            RunPerftSuite();
            return 0;
        }

        if (args.Length < 2 || !int.TryParse(args[1], out int depth) || depth < 1)
        {
            Console.WriteLine("Error: perft requires a depth >= 1, or the keyword 'suite'.");
            return 1;
        }

        string fen = args.Length >= 3 ? args[2] : StartPosition;
        Board.ParseFEN(fen);
        RunPerft(depth);
        return 0;
    }

    // Recursive leaf node counter for move generator verification.
    private static long PerftDriver(int depth)
    {
        if (depth == 0)
            return 1;

        MoveList moveList = new();
        GenerateMoves(ref moveList);

        long nodes = 0;
        for (int i = 0; i < moveList.count; i++)
        {
            BoardState state = Board.CopyBoard();

            if (MakeMove(moveList.moves[i]) == 0)
            {
                Board.TakeBack(in state);
                continue;
            }

            nodes += PerftDriver(depth - 1);
            Board.TakeBack(in state);
        }

        return nodes;
    }

    // Root perft executor: outputs move-by-move move counts (Stockfish style).
    private static long RunPerft(int depth)
    {
        Console.WriteLine();
        Console.WriteLine($"     Performance test — depth {depth}");
        Console.WriteLine();

        MoveList moveList = new();
        GenerateMoves(ref moveList);

        long totalNodes = 0;
        long startTime = GetTimeMs();

        for (int i = 0; i < moveList.count; i++)
        {
            BoardState state = Board.CopyBoard();

            if (MakeMove(moveList.moves[i]) == 0)
            {
                Board.TakeBack(in state);
                continue;
            }

            long nodesForMove = depth == 1 ? 1 : PerftDriver(depth - 1);
            totalNodes += nodesForMove;
            Board.TakeBack(in state);

            Console.WriteLine($"     {MoveEncoding.GetMove(moveList.moves[i]),-6} : {nodesForMove}");
        }

        long elapsed = GetTimeMs() - startTime;
        long nps = elapsed > 0 ? totalNodes * 1000 / elapsed : 0;

        Console.WriteLine();
        Console.WriteLine($"    Depth: {depth}");
        Console.WriteLine($"    Nodes: {totalNodes:N0}");
        Console.WriteLine($"     Time: {elapsed} ms");
        Console.WriteLine($"      NPS: {nps:N0}");
        Console.WriteLine();

        return totalNodes;
    }

    // =========================================================================
    //  SECTION 5 — Perft Automated Test Suite
    // =========================================================================

    private readonly record struct PerftTest(string Fen, int Depth, long Expected, string Name);

    // Standard perft test positions verified against standard reference values.
    private static readonly PerftTest[] PerftSuite =
    [
        new(StartPosition,    5, 4_865_609,  "Startpos"),
        new(KiwipetePosition, 4, 4_085_603,  "Kiwipete"),
        new(PinPosition,      6, 11_030_083, "Pin position"),
        new(Position4,        5, 15_833_292, "Position 4"),
        new(Position5,        3, 62_379,     "Position 5"),
        new(Position6,        4, 3_894_594,  "Position 6"),
    ];

    private static void RunPerftSuite()
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  Perft Test Suite");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine();

        int passed = 0;
        long totalNodes = 0;
        long totalTime = 0;

        for (int i = 0; i < PerftSuite.Length; i++)
        {
            var test = PerftSuite[i];
            Board.ParseFEN(test.Fen);

            long startTime = GetTimeMs();
            long actual = PerftDriver(test.Depth);
            long elapsed = GetTimeMs() - startTime;

            totalNodes += actual;
            totalTime += elapsed;

            bool ok = actual == test.Expected;
            if (ok) passed++;

            string status = ok ? "✓ PASS" : "✗ FAIL";
            long nps = elapsed > 0 ? actual * 1000 / elapsed : 0;

            Console.WriteLine($"  [{i + 1}/{PerftSuite.Length}] {status}  {test.Name}  (depth {test.Depth})");
            Console.WriteLine($"         Expected: {test.Expected,15:N0}");
            Console.WriteLine($"         Actual:   {actual,15:N0}");
            Console.WriteLine($"         Time:     {elapsed,15:N0} ms   NPS: {nps:N0}");
            Console.WriteLine();
        }

        long totalNps = totalTime > 0 ? totalNodes * 1000 / totalTime : 0;

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Result:      {passed}/{PerftSuite.Length} positions passed");
        Console.WriteLine($"  Total nodes: {totalNodes:N0}");
        Console.WriteLine($"  Total time:  {totalTime:N0} ms");
        Console.WriteLine($"  Overall NPS: {totalNps:N0}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    // =========================================================================
    //  SECTION 6 — Utilities & Helpers
    // =========================================================================

    public static long GetTimeMs() => Environment.TickCount64;
}