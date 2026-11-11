using static Board;
using static MoveEncoding;
using static MoveGenerator;

// =============================================================================
//  AnkaX UCI Protocol Handler
// =============================================================================
//
//  Implements the Universal Chess Interface (UCI) protocol.
//  Reference: https://www.chessprogramming.org/UCI
//
//  Supported commands:
//    uci          — Identify engine and report options
//    isready      — Synchronization ping
//    ucinewgame   — Reset for a new game (clears TT and board)
//    position     — Set board position (startpos or FEN + moves)
//    go           — Start searching (depth, movetime, clock, infinite)
//    stop         — Abort current search
//    quit         — Exit the engine
//    setoption    — Set engine options (Hash)
//    d            — Display current board (debug, non-standard)
//
//  Go sub-commands:
//    depth <n>       — Search to fixed depth
//    movetime <ms>   — Search for exactly N milliseconds
//    wtime <ms>      — White's remaining clock time
//    btime <ms>      — Black's remaining clock time
//    winc <ms>       — White's increment per move
//    binc <ms>       — Black's increment per move
//    movestogo <n>   — Moves until next time control
//    infinite        — Search until "stop" is received
//
// =============================================================================

public static class Uci
{
    // =========================================================================
    //  Constants
    // =========================================================================

    private const string EngineName = "AnkaX v1.0.5";
    private const string EngineAuthor = "Amrou Louai Anka";

    // Reusable move list for ParseMove (avoids heap allocation per call).
    // Safe because UCI parsing is single-threaded and never overlaps with search.
    private static MoveList parseMoveList;

    static Uci()
    {
        parseMoveList = new MoveList();
    }

    // =========================================================================
    //  Main UCI Loop
    // =========================================================================

    /// <summary>
    /// Reads commands from stdin and dispatches them. Runs until "quit".
    /// </summary>
    public static void UciLoop()
    {
        while (true)
        {
            if (TimeManagement.quit)
                break;

            string? input = Console.ReadLine();
            if (string.IsNullOrEmpty(input))
                continue;

            input = input.Trim();
            if (input.Length == 0)
                continue;

            // Fast first-character dispatch before full string comparison
            switch (input[0])
            {
                case 'u':
                    if (input == "uci")
                        HandleUci();
                    else if (input.StartsWith("ucinewgame"))
                        HandleUciNewGame();
                    break;

                case 'i':
                    if (input.StartsWith("isready"))
                        Console.WriteLine("readyok");
                    break;

                case 'p':
                    if (input.StartsWith("position"))
                        ParsePosition(input);
                    break;

                case 'g':
                    if (input.StartsWith("go"))
                    {
                        ParseGo(input);
                        if (TimeManagement.quit)
                            return;
                    }
                    break;

                case 's':
                    if (input.StartsWith("stop"))
                        TimeManagement.stopped = true;
                    else if (input.StartsWith("setoption"))
                        HandleSetOption(input);
                    break;

                case 'q':
                    if (input.StartsWith("quit"))
                    {
                        TimeManagement.stopped = true;
                        return;
                    }
                    break;

                case 'd':
                    if (input == "d")
                        HandleDisplay();
                    break;
            }
        }
    }

    // =========================================================================
    //  Command Handlers
    // =========================================================================

    /// <summary>
    /// Responds to "uci" with engine identification and available options.
    /// </summary>
    private static void HandleUci()
    {
        Console.WriteLine($"id name {EngineName}");
        Console.WriteLine($"id author {EngineAuthor}");
        Console.WriteLine("option name Hash type spin default 16 min 1 max 128");
        Console.WriteLine("uciok");
    }

    /// <summary>
    /// Responds to "ucinewgame" by resetting the board and clearing the TT.
    /// </summary>
    private static void HandleUciNewGame()
    {
        ParsePosition("position startpos");
        TranspositionTable.Clear();
    }

    /// <summary>
    /// Responds to "setoption name X value Y".
    /// Currently supports: Hash (stub — TT size is fixed at compile time).
    /// </summary>
    private static void HandleSetOption(string command)
    {
        // Parse: "setoption name <name> value <value>"
        var parts = command.Split([' '], StringSplitOptions.RemoveEmptyEntries);

        string? name = null;
        string? value = null;

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "name" && i + 1 < parts.Length)
                name = parts[i + 1];
            else if (parts[i] == "value" && i + 1 < parts.Length)
                value = parts[i + 1];
        }

        if (name == null)
            return;

        // Hash option is accepted but currently ignored (TT size is compile-time constant).
        // When dynamic TT sizing is added, this is where it goes.
        if (string.Equals(name, "Hash", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            // int.TryParse(value, out int hashMb);
            // TODO: Resize TT to hashMb megabytes
        }
    }

    /// <summary>
    /// Responds to "d" by printing the current board state (debug aid).
    /// </summary>
    private static void HandleDisplay()
    {
        Console.WriteLine();
        Console.WriteLine("  +---+---+---+---+---+---+---+---+");

        for (int rank = 0; rank < 8; rank++)
        {
            Console.Write($"  |");
            for (int file = 0; file < 8; file++)
            {
                int sq = rank * 8 + file;
                int piece = GetPieceOnSquare(sq);
                char ch = piece == 0 ? ' ' : PieceToChar(piece);
                Console.Write($" {ch} |");
            }
            Console.WriteLine($" {8 - rank}");
            Console.WriteLine("  +---+---+---+---+---+---+---+---+");
        }

        Console.WriteLine("    a   b   c   d   e   f   g   h");
        Console.WriteLine();
        Console.WriteLine($"Fen: {GetFEN()}");
        Console.WriteLine($"Key: {Zobrist.hashKey:X16}");
        Console.WriteLine($"Side: {(side == White ? "White" : "Black")}");
        Console.WriteLine();
    }

    // =========================================================================
    //  Position Parsing
    // =========================================================================

    /// <summary>
    /// Parses "position startpos [moves ...]" or "position fen FEN [moves ...]".
    /// Sets up the board and plays the listed moves to reach the target position.
    /// </summary>
    public static void ParsePosition(string command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        var parts = command.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != "position")
            return;

        int idx = 1;

        // --- Set up the starting position ---
        if (idx < parts.Length && parts[idx] == "startpos")
        {
            ParseFEN(Program.StartPosition);
            idx++;
        }
        else if (idx < parts.Length && parts[idx] == "fen")
        {
            idx++;

            // Collect FEN tokens (up to 6 fields) until "moves" or end
            var fenTokens = new List<string>(6);
            while (idx < parts.Length && parts[idx] != "moves")
            {
                fenTokens.Add(parts[idx]);
                idx++;
            }

            if (fenTokens.Count > 0)
                ParseFEN(string.Join(" ", fenTokens));
            else
                ParseFEN(Program.StartPosition);
        }
        else
        {
            ParseFEN(Program.StartPosition);
        }

        // --- Reset repetition history ---
        Search.repetitionIndex = 0;

        // --- Play the move sequence ---
        if (idx < parts.Length && parts[idx] == "moves")
        {
            idx++;
            while (idx < parts.Length)
            {
                int move = ParseMove(parts[idx]);
                if (move == 0)
                    break;

                Search.AddToRepetitionHistory(Zobrist.hashKey);
                MakeMove(move, (int)MoveFlag.allMoves);
                idx++;
            }
        }

        // Record the final position for repetition detection
        Search.AddToRepetitionHistory(Zobrist.hashKey);
    }

    // =========================================================================
    //  Go Parsing
    // =========================================================================

    /// <summary>
    /// Parses "go [depth N] [movetime N] [wtime N btime N ...] [infinite]"
    /// and starts the search with the appropriate time control.
    /// </summary>
    public static void ParseGo(string command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        var parts = command.Split([' '], StringSplitOptions.RemoveEmptyEntries);

        int depth = -1;
        bool infinite = false;

        TimeManagement.ResetForGo();

        for (int i = 1; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case "infinite":
                    infinite = true;
                    break;

                case "depth" when i + 1 < parts.Length:
                    int.TryParse(parts[++i], out depth);
                    break;

                case "movetime" when i + 1 < parts.Length:
                    int.TryParse(parts[++i], out TimeManagement.movetime);
                    break;

                case "wtime" when i + 1 < parts.Length && side == White:
                    int.TryParse(parts[++i], out TimeManagement.time);
                    break;

                case "btime" when i + 1 < parts.Length && side == Black:
                    int.TryParse(parts[++i], out TimeManagement.time);
                    break;

                case "winc" when i + 1 < parts.Length && side == White:
                    int.TryParse(parts[++i], out TimeManagement.inc);
                    break;

                case "binc" when i + 1 < parts.Length && side == Black:
                    int.TryParse(parts[++i], out TimeManagement.inc);
                    break;

                case "movestogo" when i + 1 < parts.Length:
                    int.TryParse(parts[++i], out TimeManagement.movestogo);
                    break;

                // Skip tokens we don't use yet (ponder, searchmoves, nodes, mate)
                case "ponder":
                case "searchmoves":
                case "nodes":
                case "mate":
                    // Consume the keyword; if it takes a value, skip that too
                    if (parts[i] != "ponder" && i + 1 < parts.Length)
                        i++;
                    break;
            }
        }

        // --- Start the appropriate time control ---
        if (infinite)
        {
            TimeManagement.StartInfiniteSearch();
        }
        else if (TimeManagement.movetime != -1)
        {
            TimeManagement.StartMoveTimeSearch(TimeManagement.movetime);
        }
        else if (TimeManagement.time != -1)
        {
            TimeManagement.StartClockSearch(TimeManagement.time, TimeManagement.inc);
        }

        if (depth == -1)
            depth = MaxSearchDepth;

        if (Program.debug)
        {
            Console.WriteLine(
                $"info string start:{TimeManagement.starttime} " +
                $"soft:{TimeManagement.softStopTime} " +
                $"hard:{TimeManagement.stoptime} " +
                $"time:{TimeManagement.time} inc:{TimeManagement.inc} depth:{depth}");
        }

        Search.SearchPosition(depth);
    }

    private const int MaxSearchDepth = 64;

    // =========================================================================
    //  Move Parsing
    // =========================================================================

    /// <summary>
    /// Converts a UCI move string (e.g., "e2e4", "a7a8q") to an encoded move.
    /// Returns 0 if the string doesn't match any legal move in the current position.
    /// </summary>
    public static int ParseMove(string moveString)
    {
        if (string.IsNullOrEmpty(moveString) || moveString.Length < 4)
            return 0;

        // Parse source and target squares from algebraic notation
        // UCI: 'a'-'h' for file, '1'-'8' for rank
        // Engine: a8=0, h1=63 → rank = 8 - digit, file = char - 'a'
        int sourceSquare = (moveString[0] - 'a') + (8 - (moveString[1] - '0')) * 8;
        int targetSquare = (moveString[2] - 'a') + (8 - (moveString[3] - '0')) * 8;

        // Generate all legal moves and find the matching one
        ref MoveList moveList = ref parseMoveList;
        GenerateMoves(ref moveList);

        int[] moves = moveList.moves;
        int count = moveList.count;

        for (int i = 0; i < count; i++)
        {
            int move = moves[i];

            if (GetMoveSource(move) != sourceSquare || GetMoveTarget(move) != targetSquare)
                continue;

            int promotedPiece = GetMovePromoted(move);

            // Non-promotion: source and target match is sufficient
            if (promotedPiece == 0)
                return move;

            // Promotion: verify the promotion piece matches the 5th character
            if (moveString.Length < 5)
                continue;

            bool match = moveString[4] switch
            {
                'q' => promotedPiece == Q || promotedPiece == q,
                'r' => promotedPiece == R || promotedPiece == r,
                'b' => promotedPiece == B || promotedPiece == b,
                'n' => promotedPiece == N || promotedPiece == n,
                _ => false
            };

            if (match)
                return move;
        }

        return 0;
    }

    // =========================================================================
    //  Display Helpers
    // =========================================================================

    private static int GetPieceOnSquare(int square)
    {
        ulong mask = 1UL << square;
        ulong[] boards = bitboards;

        for (int piece = P; piece <= k; piece++)
        {
            if ((boards[piece] & mask) != 0)
                return piece;
        }

        return 0;
    }

    private static char PieceToChar(int piece) => piece switch
    {
        P => 'P',
        N => 'N',
        B => 'B',
        R => 'R',
        Q => 'Q',
        K => 'K',
        p => 'p',
        n => 'n',
        b => 'b',
        r => 'r',
        q => 'q',
        k => 'k',
        _ => '.'
    };

    private static string GetFEN()
    {
        // Minimal FEN reconstruction for debug display
        var fen = new System.Text.StringBuilder(90);

        for (int rank = 0; rank < 8; rank++)
        {
            int empty = 0;
            for (int file = 0; file < 8; file++)
            {
                int piece = GetPieceOnSquare(rank * 8 + file);
                if (piece == 0)
                {
                    empty++;
                }
                else
                {
                    if (empty > 0) { fen.Append(empty); empty = 0; }
                    fen.Append(PieceToChar(piece));
                }
            }
            if (empty > 0) fen.Append(empty);
            if (rank < 7) fen.Append('/');
        }

        fen.Append(side == White ? " w " : " b ");

        // Castling rights
        bool anyCastle = false;
        if ((castle & (int)CastlingRights.wk) != 0) { fen.Append('K'); anyCastle = true; }
        if ((castle & (int)CastlingRights.wq) != 0) { fen.Append('Q'); anyCastle = true; }
        if ((castle & (int)CastlingRights.bk) != 0) { fen.Append('k'); anyCastle = true; }
        if ((castle & (int)CastlingRights.bq) != 0) { fen.Append('q'); anyCastle = true; }
        if (!anyCastle) fen.Append('-');

        // En passant
        if (enPassant != (int)Square.noSquare)
        {
            int epFile = enPassant % 8;
            int epRank = 8 - enPassant / 8;
            fen.Append($" {(char)('a' + epFile)}{epRank}");
        }
        else
        {
            fen.Append(" -");
        }

        fen.Append($" {halfmoveClock}");

        return fen.ToString();
    }
}