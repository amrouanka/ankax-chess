using static Board;
using static MoveEncoding;
using static MoveGenerator;

// =============================================================================
// AnkaX Chess Engine - Universal Chess Interface (UCI) Protocol Handler
//
// Full implementation of the UCI protocol for communication with chess GUIs.
// Supports position loading, move execution, time management initialization,
// transposition table resizing, and debug visual displays.
//
// Protocol Reference: https://www.chessprogramming.org/UCI
// =============================================================================

public static class Uci
{
    // =========================================================================
    //  SECTION 1 — Engine Metadata & Configuration
    // =========================================================================

    private const string EngineName = "AnkaX v1.1.1";
    private const string EngineAuthor = "Amrou Louai Anka";
    private const int MaxSearchDepth = 64;

    // Reusable move list for move parsing (avoids allocation during input processing).
    private static MoveList parseMoveList;

    static Uci()
    {
        parseMoveList = new MoveList();
    }

    // =========================================================================
    //  SECTION 2 — Main Control Loop
    // =========================================================================

    // Reads commands from standard input and dispatches to command handlers.
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

            // First-character dispatch switch for quick command routing
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
    //  SECTION 3 — UCI Command Handlers
    // =========================================================================

    // Responds to the initial "uci" command with engine identification and options.
    private static void HandleUci()
    {
        Console.WriteLine($"id name {EngineName}");
        Console.WriteLine($"id author {EngineAuthor}");
        Console.WriteLine($"option name Hash type spin default {TranspositionTable.DefaultHashMb} " +
                          $"min {TranspositionTable.MinHashMb} max {TranspositionTable.MaxHashMb}");
        Console.WriteLine("uciok");
    }

    // Responds to "ucinewgame" by resetting board state and flushing the TT.
    private static void HandleUciNewGame()
    {
        ParsePosition("position startpos");
        TranspositionTable.Clear();
    }

    // Handles "setoption name Hash value <N>" to dynamically resize the TT.
    private static void HandleSetOption(string command)
    {
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

        if (string.Equals(name, "Hash", StringComparison.OrdinalIgnoreCase) && value != null)
        {
            if (int.TryParse(value, out int hashMb))
                TranspositionTable.Resize(hashMb);
        }
    }

    // Responds to "d" command: displays ascii board representation and position info.
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
    //  SECTION 4 — Position & Search Command Parsers
    // =========================================================================

    // Parses "position [startpos | fen <FEN>] [moves <m1> <m2> ...]" command.
    public static void ParsePosition(string command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        var parts = command.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != "position")
            return;

        int idx = 1;

        // ---------------------------------------------------------------------
        //  Step 1: Set up base board state (startpos or custom FEN)
        // ---------------------------------------------------------------------
        if (idx < parts.Length && parts[idx] == "startpos")
        {
            ParseFEN(Program.StartPosition);
            idx++;
        }
        else if (idx < parts.Length && parts[idx] == "fen")
        {
            idx++;
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

        // ---------------------------------------------------------------------
        //  Step 2: Reset repetition history and execute move sequence
        // ---------------------------------------------------------------------
        Search.repetitionIndex = 0;

        if (idx < parts.Length && parts[idx] == "moves")
        {
            idx++;
            while (idx < parts.Length)
            {
                int move = ParseMove(parts[idx]);
                if (move == 0)
                    break;

                Search.AddToRepetitionHistory(Zobrist.hashKey);
                MakeMove(move);
                idx++;
            }
        }

        Search.AddToRepetitionHistory(Zobrist.hashKey);
    }

    // Parses "go [depth N] [movetime N] [wtime N btime N ...] [infinite]" command.
    public static void ParseGo(string command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        var parts = command.Split([' '], StringSplitOptions.RemoveEmptyEntries);

        int depth = -1;
        bool infinite = false;

        TimeManagement.ResetForGo();

        // ---------------------------------------------------------------------
        //  Step 1: Parse parameter tokens
        // ---------------------------------------------------------------------
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

                case "ponder":
                case "searchmoves":
                case "nodes":
                case "mate":
                    if (parts[i] != "ponder" && i + 1 < parts.Length)
                        i++;
                    break;
            }
        }

        // ---------------------------------------------------------------------
        //  Step 2: Initialize time limits and trigger search thread
        // ---------------------------------------------------------------------
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

        if (Program.Debug)
        {
            Console.WriteLine(
                $"info string start:{TimeManagement.starttime} " +
                $"soft:{TimeManagement.softStopTime} " +
                $"hard:{TimeManagement.stoptime} " +
                $"time:{TimeManagement.time} inc:{TimeManagement.inc} depth:{depth}");
        }

        Search.SearchPosition(depth);
    }

    // =========================================================================
    //  SECTION 5 — Move String Conversion
    // =========================================================================

    // Converts long algebraic notation (e.g. "e2e4", "a7a8q") to integer encoded move.
    // Returns 0 if move string is invalid or illegal in current position.
    public static int ParseMove(string moveString)
    {
        if (string.IsNullOrEmpty(moveString) || moveString.Length < 4)
            return 0;

        // Algebraic notation mapping (a8=0 .. h1=63)
        int sourceSquare = (moveString[0] - 'a') + (8 - (moveString[1] - '0')) * 8;
        int targetSquare = (moveString[2] - 'a') + (8 - (moveString[3] - '0')) * 8;

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

            // Non-promotion move match
            if (promotedPiece == 0)
                return move;

            // Promotion move match against target character
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
    //  SECTION 6 — Board Display & FEN Serialization
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

        bool anyCastle = false;
        if ((castle & (int)CastlingRights.wk) != 0) { fen.Append('K'); anyCastle = true; }
        if ((castle & (int)CastlingRights.wq) != 0) { fen.Append('Q'); anyCastle = true; }
        if ((castle & (int)CastlingRights.bk) != 0) { fen.Append('k'); anyCastle = true; }
        if ((castle & (int)CastlingRights.bq) != 0) { fen.Append('q'); anyCastle = true; }
        if (!anyCastle) fen.Append('-');

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