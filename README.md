# AnkaX

A bitboard chess engine written in C#. Speaks UCI.

## Overview

AnkaX is a custom chess engine that parses FEN positions, generates legal moves,
searches using alpha-beta pruning with advanced heuristics, and evaluates
positions using tapered material, piece-square tables, mobility, king safety,
passed pawns, and more. It communicates via the UCI protocol and is compatible
with all major chess GUIs.

## Engine Strength

- **CCRL Blitz:** 2469 Elo (v1.0.2, [CCRL Blitz Rating List](https://www.computerchess.org.uk/ccrl/404/))
- **v1.0.3:** +46 Elo over v1.0.2 (self-play, performance optimizations)
- **v1.0.4:** +7 Elo over v1.0.3 (self-play, history heuristic improvements)
- **v1.0.5:** +7 Elo over v1.0.4 (self-play, fail-soft search & IIR)
- **v1.0.6:** +65 to +93 Elo over v1.0.5 (self-play, time management overhaul; TC-dependent)

> Self-play results measured at 3+0.05 using
> [cutechess-cli](https://github.com/cutechess/cutechess) with the
> [UHO 4060 v4](https://www.sp-ohl.de/books/books.html) opening book
> (12 plies, randomized).

## Features

- Bitboard-based board representation with 12 piece bitboards and 3 occupancy bitboards
- Legal move generation for all standard chess pieces
- FEN parsing and move validation
- Full UCI protocol support (`uci`, `isready`, `ucinewgame`, `position`, `go`, `stop`, `quit`)
- Iterative deepening with aspiration windows
- Principal Variation Search (PVS) with fail-soft return values
- Internal Iterative Reductions (IIR)
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

    git clone https://github.com/amrouanka/ankax-chess.git
    cd ankax-chess
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
    id name AnkaX v1.0.6
    id author Amrou Louai Anka
    uciok
    isready
    readyok
    position startpos moves e2e4 e7e5
    go depth 10
    info score cp 37 depth 9 nodes 69791 pv e2e4 e7e5 g1f3 g8f6 b1c3 b8c6 d2d4 e5d4 f3d4
    bestmove g1f3

## Testing & Self-Play

This repository includes scripts for automated self-play testing using
[cutechess-cli](https://github.com/cutechess/cutechess).

### Prerequisites

1. Install [cutechess-cli](https://github.com/cutechess/cutechess/releases)
   and update the `CUTECHESS` path in `scripts/run_match.bat` if needed.
2. Place an opening book in the `books/` directory. This project uses
   [UHO 4060 v4](https://www.sp-ohl.de/books/books.html) by Stefan Pohl.

### Running a Match Locally

    scripts/run_match.bat

This runs 10,000 games between the current dev build and the release build
in `releases/AnkaX.exe`, using 3+0.05 time control with the UHO book.
Results are saved to `dev_vs_release.pgn`.

### Running a Match via GitHub Actions

Trigger the **Engine Test** workflow from the Actions tab (or VSCode).
It runs 20 parallel shards of 250 games each (5,000 total) at 10+0.1 on
`ubuntu-24.04` runners, building both engines from source.

After the run completes, download the PGN artifacts and combine them:

```powershell
Get-ChildItem -Recurse -Filter "result_*.pgn" | Get-Content | Set-Content scripts\dev_vs_release.pgn; & .\scripts\check_los.bat
```

### Checking Results

    scripts/check_los.bat

Parses the PGN and displays Elo difference, error margin, and Likelihood of
Superiority (LOS).

## Project Structure

| File / Directory       | Description                                               |
|------------------------|-----------------------------------------------------------|
| `Program.cs`           | Entry point, tactical test suite, perft driver            |
| `Uci.cs`               | UCI protocol parser and engine loop                       |
| `Board.cs`             | Board state, FEN parsing, copy/takeback                   |
| `MoveGenerator.cs`     | Legal move generation and make-move                       |
| `PieceAttacks.cs`      | Attack tables (leapers, magic bitboards for sliders)      |
| `BitboardOperations.cs`| Bitboard helpers (popcount, LSB, set/pop bit)             |
| `Search.cs`            | PVS alpha-beta search, quiescence, pruning, move ordering |
| `Evaluation.cs`        | Tapered evaluation with PeSTO PSTs and positional features|
| `TranspositionTable.cs`| Hash table with Zobrist keying                            |
| `Zobrist.cs`           | Zobrist hash key generation                               |
| `Move.cs`              | Move encoding/decoding, move list, board state snapshot   |
| `TimeManagement.cs`    | Search timing and clock management                        |
| `Enums.cs`             | Piece, square, castling, and flag constants               |
| `AnkaX.csproj`         | .NET 10 project file                                      |
| `.github/workflows/`   | GitHub Actions CI for automated self-play testing         |
| `books/`               | Opening books for self-play testing                       |
| `releases/`            | Published executables for version comparison              |
| `scripts/run_match.bat`| Automated self-play match script (cutechess-cli)          |
| `scripts/check_los.bat`| PGN result analyzer (Elo, LOS)                            |
| `SEARCH.md`            | Technical documentation of the search                     |

## Development Notes

AnkaX uses techniques common in strong chess engines:

- Bitboards for fast board operations
- Flat-array storage for hot-path performance (no multi-dimensional arrays on the search path)
- Pre-allocated move lists to eliminate GC pressure during search
- Aggressive move ordering for search efficiency
- Multiple pruning heuristics for reduced search cost
- Transposition tables for repeated positions
- Hardware-accelerated bit counting (`POPCNT`)

## Credits

- Opening book: [UHO 4060 v4](https://www.sp-ohl.de/books/books.html) by **Stefan Pohl**
- Testing framework: [cutechess-cli](https://github.com/cutechess/cutechess) by Ilari Pihlajisto, Arto Jonsson, and contributors
- Engine architecture inspired by the [Chess Programming Wiki](https://www.chessprogramming.org/) and the BBC/Code Monkey King tutorial series

## License

MIT — see [LICENSE](LICENSE).

## Contributing

Contributions are welcome. Pull requests should include a clear description of
the change and any relevant validation steps (perft results, self-play Elo).
