# AnkaX

A bitboard chess engine written in C#. Speaks UCI.

## Overview

AnkaX is a custom chess engine that parses FEN positions, generates legal moves, searches using alpha-beta pruning with advanced heuristics, and evaluates positions using tapered material, piece-square tables, mobility, king safety, passed pawns, and more. It communicates via the UCI protocol and is compatible with all major chess GUIs.

## Estimated Strength

~2150–2250 Elo (according to tests against rated engines)

## Features

- Bitboard-based board representation with 12 piece bitboards and 3 occupancy bitboards
- Legal move generation for all standard chess pieces
- FEN parsing and move validation
- Full UCI protocol support (`uci`, `isready`, `ucinewgame`, `position`, `go`, `stop`, `quit`)
- Iterative deepening with aspiration windows
- Principal Variation Search (PVS)
- Null move pruning with adaptive reduction
- Late Move Reductions (LMR)
- Reverse futility and futility pruning
- Check extensions and mate distance pruning
- Quiescence search with delta pruning
- Move ordering: TT move, MVV-LVA, killer moves, history heuristic, PV move
- Transposition table with Zobrist hashing
- Repetition detection and fifty-move rule
- Tapered evaluation (MG/EG): material, PSTs, bishop pair, mobility, rook open files, passed pawns, isolated pawns, king safety, knight outposts, queenless king centralization
- Time management for clock-based and move-time searches

## Installation

    git clone <repo-url>
    cd AnkaX
    dotnet restore

## Build

    dotnet build -c Release

Or publish a self-contained single-file executable (no .NET runtime required):

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishAot=false

The output will be in `bin/Release/net10.0/win-x64/publish/AnkaX.exe`.

## Run

    dotnet run

Or run the published executable directly:

    AnkaX.exe

The engine starts in UCI mode and waits for commands from standard input.

## Example UCI Session

    > AnkaX.exe
    uci
    id name AnkaX v1.0.2
    id author Amrou
    uciok
    isready
    readyok
    position startpos moves e2e4 e7e5
    go depth 10
    info score cp 37 depth 9 nodes 69791 pv e2e4 e7e5 g1f3 g8f6 b1c3 b8c6 d2d4 e5d4 f3d4
    bestmove g1f3

## Project Structure

| File | Description |
|---|---|
| `Program.cs` | Entry point |
| `Uci.cs` | UCI protocol parser and engine loop |
| `Board.cs` | Board state, FEN parsing, make/unmake |
| `MoveGenerator.cs` | Legal move generation |
| `PieceAttacks.cs` | Attack tables (knight, king, pawn, sliding) |
| `BitboardOperations.cs` | Bitboard helpers (popcount, LSB, etc.) |
| `Search.cs` | Alpha-beta search, quiescence, pruning |
| `Evaluation.cs` | Tapered evaluation function |
| `TranspositionTable.cs` | Hash table |
| `Zobrist.cs` | Zobrist key generation |
| `Move.cs` | Move encoding and decoding |
| `TimeManagement.cs` | Search timing and clock management |
| `Enums.cs` | Piece, square, and flag constants |
| `AnkaX.csproj` | .NET project file |

## Development Notes

AnkaX uses techniques common in strong chess engines:

- Bitboards for fast board operations
- Flat-array storage for hot-path performance
- Aggressive move ordering for search efficiency
- Multiple pruning heuristics for reduced search cost
- Transposition tables for repeated positions

## License

MIT — see [LICENSE](LICENSE).

## Contributing

Contributions are welcome. Pull requests should include a clear description of the change and any relevant validation steps.
