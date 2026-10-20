using static Board;
using static MoveEncoding;
using static MoveGenerator;

public static class Uci
{
    // Parse a move string (e.g., "e2e4", "a7a8q") and return the encoded move, or 0 if illegal/not found.
    public static int ParseMove(string moveString)
    {
        if (string.IsNullOrEmpty(moveString) || moveString.Length < 4)
            return 0;

        // Generate all legal moves for the current position
        var moveList = new MoveList();
        GenerateMoves(ref moveList);

        // Parse source square
        int sourceSquare = moveString[0] - 'a' + (8 - (moveString[1] - '0')) * 8;

        // Parse target square
        int targetSquare = moveString[2] - 'a' + (8 - (moveString[3] - '0')) * 8;

        // Loop over the generated moves
        for (int i = 0; i < moveList.count; i++)
        {
            int move = moveList.moves[i];

            // Match source and target squares
            if (sourceSquare == GetMoveSource(move) && targetSquare == GetMoveTarget(move))
            {
                int promotedPiece = GetMovePromoted(move);

                // If there is a promotion, verify it matches the move string
                if (promotedPiece != 0)
                {
                    if (moveString.Length < 5)
                        continue; // promotion character missing

                    char promoChar = moveString[4];
                    switch (promoChar)
                    {
                        case 'q' when promotedPiece == Q || promotedPiece == q:
                            return move;
                        case 'r' when promotedPiece == R || promotedPiece == r:
                            return move;
                        case 'b' when promotedPiece == B || promotedPiece == b:
                            return move;
                        case 'n' when promotedPiece == N || promotedPiece == n:
                            return move;
                        default:
                            continue; // mismatched promotion character
                    }
                }

                // Non-promotion move matches
                return move;
            }
        }

        // No matching legal move found
        return 0;
    }

    // Parse UCI "position" command (e.g., "position startpos moves e2e4 e7e5")
    public static void ParsePosition(string command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        // Trim leading whitespace and skip "position"
        var parts = command.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != "position")
            return;

        int idx = 1;

        // Handle "startpos"
        if (idx < parts.Length && parts[idx] == "startpos")
        {
            ParseFEN(Program.StartPosition);
            idx++;
        }
        // Handle "fen ..."
        else if (idx < parts.Length && parts[idx] == "fen")
        {
            idx++;
            if (idx < parts.Length)
            {
                // Reconstruct FEN from remaining tokens until "moves" or end
                var fenTokens = new List<string>();
                while (idx < parts.Length && parts[idx] != "moves")
                {
                    fenTokens.Add(parts[idx]);
                    idx++;
                }
                if (fenTokens.Count > 0)
                {
                    var fen = string.Join(" ", fenTokens);
                    ParseFEN(fen);
                }
                else
                {
                    // FEN missing; fall back to start position
                    ParseFEN(Program.StartPosition);
                }
            }
            else
            {
                // FEN missing; fall back to start position
                ParseFEN(Program.StartPosition);
            }
        }
        else
        {
            // Unknown or missing position specifier; default to start position
            ParseFEN(Program.StartPosition);
        }

        // ✅ Reset repetition history
        Search.repetitionIndex = 0;

        // Parse "moves" section if present
        if (idx < parts.Length && parts[idx] == "moves")
        {
            idx++;
            while (idx < parts.Length)
            {
                int move = ParseMove(parts[idx]);
                if (move == 0)
                    break; // illegal or unparsable move

                // ✅ Record position BEFORE the move
                Search.AddToRepetitionHistory(Zobrist.hashKey);

                MakeMove(move, (int)MoveFlag.allMoves);
                idx++;
            }
        }

        // ✅ Record the final current position
        Search.AddToRepetitionHistory(Zobrist.hashKey);
    }

    // Parse UCI "go" command (e.g., "go depth 5")
    public static void ParseGo(string command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        int depth = -1;
        bool infinite = false;
        var parts = command.Split([' '], StringSplitOptions.RemoveEmptyEntries);

        TimeManagement.ResetForGo();

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "go")
                continue;

            if (parts[i] == "infinite")
            {
                infinite = true;
            }
            else if (parts[i] == "depth" && i + 1 < parts.Length)
            {
                int.TryParse(parts[i + 1], out depth);
            }
            else if (parts[i] == "movetime" && i + 1 < parts.Length)
            {
                int.TryParse(parts[i + 1], out TimeManagement.movetime);
            }
            else if (parts[i] == "wtime" && i + 1 < parts.Length && side == White)
            {
                int.TryParse(parts[i + 1], out TimeManagement.time);
            }
            else if (parts[i] == "btime" && i + 1 < parts.Length && side == Black)
            {
                int.TryParse(parts[i + 1], out TimeManagement.time);
            }
            else if (parts[i] == "winc" && i + 1 < parts.Length && side == White)
            {
                int.TryParse(parts[i + 1], out TimeManagement.inc);
            }
            else if (parts[i] == "binc" && i + 1 < parts.Length && side == Black)
            {
                int.TryParse(parts[i + 1], out TimeManagement.inc);
            }
            else if (parts[i] == "movestogo" && i + 1 < parts.Length)
            {
                int.TryParse(parts[i + 1], out TimeManagement.movestogo);
            }
        }

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
            TimeManagement.StartClockSearch(
                TimeManagement.time,
                TimeManagement.inc);
        }

        if (depth == -1)
            depth = 64;

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
    // Main UCI loop
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

            if (input == "uci")
            {
                Console.WriteLine("id name AnkaX v1.0.2");
                Console.WriteLine("id author Amrou Louai Anka");
                Console.WriteLine("uciok");
            }
            else if (input.StartsWith("isready"))
            {
                Console.WriteLine("readyok");
            }
            else if (input.StartsWith("ucinewgame"))
            {
                ParsePosition("position startpos");
                TranspositionTable.Clear();
            }
            else if (input.StartsWith("position"))
            {
                ParsePosition(input);
            }
            else if (input.StartsWith("go"))
            {
                ParseGo(input);

                if (TimeManagement.quit)
                    break;
            }
            else if (input.StartsWith("stop"))
            {
                TimeManagement.stopped = true;
            }
            else if (input.StartsWith("quit"))
            {
                TimeManagement.stopped = true;
                break;
            }
        }
    }
}
