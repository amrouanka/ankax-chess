using static MoveFlag;
using static MoveGenerator;

/// <summary>
/// Entry point. By default runs the UCI loop. Command-line arguments can
/// trigger perft tests or other developer utilities.
///
/// Usage:
///   AnkaX.exe                 → UCI mode (default)
///   AnkaX.exe perft N         → run perft to depth N on starting position
///   AnkaX.exe perft N "FEN"   → run perft to depth N on a given FEN
///   AnkaX.exe perft suite     → run the built-in perft test suite
/// </summary>
internal static class Program
{
    // =========================================================================
    //  Well-known FEN positions
    // =========================================================================

    public const string StartPosition = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    public const string KiwipetePosition = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    public const string PinPosition = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    public const string Position4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
    public const string Position5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";
    public const string Position6 = "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10";

    /// <summary>
    /// Global debug flag. When true, Search may suppress UCI output and instead
    /// expose results via static fields for test harnesses. Off in production.
    /// </summary>
    public static bool Debug = false;

    // =========================================================================
    //  Entry Point
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
            return args[0].ToLowerInvariant() switch
            {
                "perft" => HandlePerftCommand(args),
                "bench" => HandleBenchCommand(args),
                _ => PrintUsageAndExit()
            };
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
        Console.WriteLine("  AnkaX                    Run UCI mode");
        Console.WriteLine("  AnkaX perft <depth>      Run perft on starting position");
        Console.WriteLine("  AnkaX perft <depth> <fen>  Run perft on given FEN");
        Console.WriteLine("  AnkaX perft suite        Run built-in perft test suite");
        return 1;
    }

    /// <summary>Returns the current time in milliseconds (64-bit, no wraparound).</summary>
    public static long GetTimeMs() => Environment.TickCount64;

    // =========================================================================
    //  Perft Command Dispatch
    // =========================================================================

    private static int HandlePerftCommand(string[] args)
    {
        // "perft suite"
        if (args.Length >= 2 && args[1].Equals("suite", StringComparison.OrdinalIgnoreCase))
        {
            RunPerftSuite();
            return 0;
        }

        // "perft <depth> [fen]"
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

    private static int HandleBenchCommand(string[] args)
    {
        Console.WriteLine("bench: not implemented yet.");
        return 0;
    }

    // =========================================================================
    //  Perft Core
    // =========================================================================

    /// <summary>
    /// Recursive perft: counts leaf nodes reachable from the current position
    /// at the given depth. Used to verify move generation correctness.
    /// </summary>
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

    /// <summary>
    /// Runs perft and prints per-move node counts in the standard format
    /// used by Stockfish (`go perft N`), plus total nodes, time, and NPS.
    /// Output can be diff'd directly against other engines to locate bugs.
    /// </summary>
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

            if (MakeMove(moveList.moves[i], (int)allMoves) == 0)
            {
                Board.TakeBack(state);
                continue;
            }

            long nodesForMove = depth == 1 ? 1 : PerftDriver(depth - 1);
            totalNodes += nodesForMove;
            Board.TakeBack(state);

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
    //  Perft Test Suite
    // =========================================================================

    /// <summary>
    /// One row of the perft suite: a FEN, a depth, and the expected node count.
    /// Expected counts come from the Chess Programming Wiki perft results page.
    /// </summary>
    private readonly record struct PerftTest(string Fen, int Depth, long Expected, string Name);

    /// <summary>
    /// The six classical perft positions. Between them they cover:
    /// standard play, promotions, en passant, castling, pins, and discovered checks.
    /// If MoveGenerator passes all six, it's almost certainly correct.
    /// </summary>
    private static readonly PerftTest[] PerftSuite =
    [
        new(StartPosition,    5, 4_865_609,    "Startpos"),
        new(KiwipetePosition, 4, 4_085_603,    "Kiwipete"),
        new(PinPosition,      6, 11_030_083,   "Pin position"),
        new(Position4,        5, 15_833_292,   "Position 4"),
        new(Position5,        3, 62_379,       "Position 5"),
        new(Position6,        4, 3_894_594,    "Position 6"),
    ];

    /// <summary>
    /// Runs all perft tests silently (no per-move breakdown), then prints
    /// a PASS/FAIL summary.
    /// </summary>
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
}