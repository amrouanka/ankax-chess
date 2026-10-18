# Anka

Anka is a C# chess engine designed to play chess through the UCI (Universal Chess Interface) protocol. It implements core engine functionality such as move generation, board state handling, evaluation, alpha-beta search, and time management, making it suitable for use with UCI-compatible chess GUIs and engine testing tools.

## Overview

This project is a custom chess engine rather than a simple chess game. It parses FEN positions, generates legal moves, searches for the best move using alpha-beta pruning, and evaluates positions based on material, mobility, king safety, passed pawns, and piece-square tables.

The engine is built for experimentation and learning as well as practical use in environments that support UCI engines.

## Features

- Bitboard-based board representation
- Legal move generation for standard chess pieces
- FEN parsing and move validation
- UCI command parsing and engine loop
- Alpha-beta search with move ordering and pruning
- Transposition table support
- Repetition detection and draw handling
- Evaluation using tactical and positional heuristics
- Built-in tactical test positions for validation
- Time-control support for move-based and clock-based searches

## Installation

Clone the repository and restore dependencies:

```bash
git clone <repo-url>
cd Anka
dotnet restore
```

## Build

```bash
dotnet build
```

## Run

```bash
dotnet run
```

The engine starts in UCI mode and waits for commands from standard input.

## Example UCI session

```text
uci
isready
position startpos
go depth 8
bestmove e2e4
```

Common commands include:

- `uci`
- `isready`
- `ucinewgame`
- `position startpos`
- `position fen <fen-string>`
- `go depth <n>`
- `go movetime <ms>`
- `quit`

## Project structure

```text
.
├── BitboardOperations.cs      # Bitboard helpers and low-level bit operations
├── Board.cs                   # Board state, FEN parsing, occupancy and take-back logic
├── Enums.cs                   # Chess constants and enum definitions
├── Evaluation.cs              # Positional and material evaluation
├── Move.cs                    # Move encoding and related helpers
├── MoveGenerator.cs           # Legal move generation
├── PieceAttacks.cs            # Attack detection and piece attack maps
├── Program.cs                 # Entry point and tactical test runner
├── Search.cs                  # Alpha-beta search, pruning, TT, repetition logic
├── TimeManagement.cs          # Search timing and clock management
├── TranspositionTable.cs      # TT implementation
├── Uci.cs                     # UCI parser and engine loop
├── Zobrist.cs                 # Zobrist hashing support
├── v2.7.1-full-retune.csproj # .NET project file
├── README.md                 # Project documentation
├── .gitignore                # Git ignore rules
└── obj/ / bin/               # Build artifacts
```

## Development notes

This project is optimized for performance and engine design. It uses techniques commonly found in strong chess engines, including:

- bitboards for fast board operations
- move ordering for search efficiency
- pruning heuristics for reduced search cost
- transposition tables for repeated positions
- flat-array storage for hot-path performance tuning

## Testing

The project includes a tactical test suite that runs known chess positions to validate move selection quality and engine behavior. You can trigger the tactical test mode from the program configuration or by modifying the debug switch in the entry point.

## License

This project does not currently declare a license. If you plan to distribute or reuse it publicly, add an explicit license file and choose a license before publishing.

## Contributing

Contributions are welcome if you want to improve the engine, add features, fix bugs, or refine the evaluation and search logic. Pull requests should include a clear description of the change and any relevant validation steps.
