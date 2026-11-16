using static CastlingRights;
using static Square;
using static MoveGenerator;

// =============================================================================
//  Bitboard helpers — inlined here since they're tiny and used everywhere.
//  All methods delegate to hardware intrinsics (POPCNT, TZCNT) via BitOperations.
// =============================================================================

public static class BitboardOperations
{
    public static void SetBit(ref ulong bitboard, int square) => bitboard |= 1UL << square;
    public static bool GetBit(ulong bitboard, int square) => (bitboard & (1UL << square)) != 0;
    public static void PopBit(ref ulong bitboard, int square) => bitboard &= ~(1UL << square);
}

// =============================================================================
//  Board state, FEN parsing, copy/takeback, debug printing
// =============================================================================

public static class Board
{
    public const int P = 0, N = 1, B = 2, R = 3, Q = 4, K = 5;
    public const int p = 6, n = 7, b = 8, r = 9, q = 10, k = 11;

    public static readonly char[] asciiPieces = "PNBRQKpnbrqk".ToCharArray();

    public static readonly Dictionary<char, int> charPieces = new()
    {
        ['P'] = P,
        ['N'] = N,
        ['B'] = B,
        ['R'] = R,
        ['Q'] = Q,
        ['K'] = K,
        ['p'] = p,
        ['n'] = n,
        ['b'] = b,
        ['r'] = r,
        ['q'] = q,
        ['k'] = k
    };

    public static readonly char[] promotedPieces =
    [
        ' ', 'n', 'b', 'r', 'q', ' ',   // white pieces P..K
        ' ', 'n', 'b', 'r', 'q', ' '    // black pieces p..k
    ];

    public static readonly string[] squareToCoordinates =
    [
        "a8","b8","c8","d8","e8","f8","g8","h8",
        "a7","b7","c7","d7","e7","f7","g7","h7",
        "a6","b6","c6","d6","e6","f6","g6","h6",
        "a5","b5","c5","d5","e5","f5","g5","h5",
        "a4","b4","c4","d4","e4","f4","g4","h4",
        "a3","b3","c3","d3","e3","f3","g3","h3",
        "a2","b2","c2","d2","e2","f2","g2","h2",
        "a1","b1","c1","d1","e1","f1","g1","h1"
    ];

    public static ulong[] bitboards = new ulong[12];
    public static ulong[] occupancies = new ulong[3];
    public static int side;
    public static int enPassant = (int)noSquare;
    public static int castle;
    public static int halfmoveClock = 0;

    // =========================================================================
    //  FEN Parsing
    // =========================================================================

    public static void ParseFEN(string fen)
    {
        Array.Fill(bitboards, 0UL);
        Array.Fill(occupancies, 0UL);

        side = 0;
        enPassant = (int)noSquare;
        castle = 0;
        halfmoveClock = 0;

        int rank = 0, file = 0;

        for (int i = 0; i < fen.Length && rank < 8; i++)
        {
            char c = fen[i];

            if (char.IsLetter(c))
            {
                int piece = charPieces[c];
                BitboardOperations.SetBit(ref bitboards[piece], rank * 8 + file);
                file++;
            }
            else if (char.IsDigit(c))
            {
                file += c - '0';
            }
            else if (c == '/')
            {
                rank++;
                file = 0;
            }
            else if (c == ' ')
            {
                i++;
                side = (fen[i] == 'w') ? White : Black;
                i += 2;

                while (fen[i] != ' ')
                {
                    if (fen[i] == 'K') castle |= (int)wk;
                    else if (fen[i] == 'Q') castle |= (int)wq;
                    else if (fen[i] == 'k') castle |= (int)bk;
                    else if (fen[i] == 'q') castle |= (int)bq;
                    i++;
                }

                i++;
                if (fen[i] != '-')
                {
                    int epFile = fen[i] - 'a';
                    int epRank = 8 - (fen[i + 1] - '0');
                    enPassant = epRank * 8 + epFile;
                }
                else
                    enPassant = (int)noSquare;

                break;
            }
        }

        string[] parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 4 && int.TryParse(parts[4], out int hm))
            halfmoveClock = hm;
        else
            halfmoveClock = 0;

        UpdateOccupancies();

        Zobrist.hashKey = Zobrist.GenerateHashKey();
    }

    // =========================================================================
    //  Occupancy
    // =========================================================================

    public static void UpdateOccupancies()
    {
        occupancies[White] =
            bitboards[P] | bitboards[N] | bitboards[B] |
            bitboards[R] | bitboards[Q] | bitboards[K];

        occupancies[Black] =
            bitboards[p] | bitboards[n] | bitboards[b] |
            bitboards[r] | bitboards[q] | bitboards[k];

        occupancies[Both] = occupancies[White] | occupancies[Black];
    }

    // =========================================================================
    //  Copy / TakeBack
    // =========================================================================

    public static BoardState CopyBoard()
    {
        return new BoardState
        {
            bb0 = bitboards[0],
            bb1 = bitboards[1],
            bb2 = bitboards[2],
            bb3 = bitboards[3],
            bb4 = bitboards[4],
            bb5 = bitboards[5],
            bb6 = bitboards[6],
            bb7 = bitboards[7],
            bb8 = bitboards[8],
            bb9 = bitboards[9],
            bb10 = bitboards[10],
            bb11 = bitboards[11],

            occ0 = occupancies[0],
            occ1 = occupancies[1],
            occ2 = occupancies[2],

            side = side,
            enPassant = enPassant,
            castle = castle,
            halfmoveClock = halfmoveClock,

            hashKey = Zobrist.hashKey
        };
    }

    public static void TakeBack(BoardState state)
    {
        bitboards[0] = state.bb0; bitboards[1] = state.bb1;
        bitboards[2] = state.bb2; bitboards[3] = state.bb3;
        bitboards[4] = state.bb4; bitboards[5] = state.bb5;
        bitboards[6] = state.bb6; bitboards[7] = state.bb7;
        bitboards[8] = state.bb8; bitboards[9] = state.bb9;
        bitboards[10] = state.bb10; bitboards[11] = state.bb11;

        occupancies[0] = state.occ0;
        occupancies[1] = state.occ1;
        occupancies[2] = state.occ2;

        side = state.side;
        enPassant = state.enPassant;
        castle = state.castle;
        halfmoveClock = state.halfmoveClock;

        Zobrist.hashKey = state.hashKey;
    }

    // =========================================================================
    //  Debug Printing
    // =========================================================================

    public static void PrintBoard()
    {
        Console.WriteLine();

        for (int rank = 0; rank < 8; rank++)
        {
            for (int file = 0; file < 8; file++)
            {
                int square = rank * 8 + file;

                if (file == 0)
                    Console.Write($"  {8 - rank} ");

                int piece = -1;
                for (int bbPiece = P; bbPiece <= k; bbPiece++)
                    if (BitboardOperations.GetBit(bitboards[bbPiece], square))
                        piece = bbPiece;

                Console.Write($" {(piece == -1 ? '.' : asciiPieces[piece])}");
            }
            Console.WriteLine();
        }

        Console.WriteLine("\n     a b c d e f g h\n");
        Console.WriteLine($"     Side:      {(side == 0 ? "white" : "black")}");
        Console.WriteLine($"     Enpassant: {(enPassant != (int)noSquare ? squareToCoordinates[enPassant] : "no")}");
        Console.WriteLine($"     Castling:  {((castle & (int)wk) != 0 ? 'K' : '-')}{((castle & (int)wq) != 0 ? 'Q' : '-')}{((castle & (int)bk) != 0 ? 'k' : '-')}{((castle & (int)bq) != 0 ? 'q' : '-')}\n");
        Console.WriteLine($"     Hash key:  {Zobrist.hashKey}\n\n");
    }

    public static void PrintBitboard(ulong bitboard)
    {
        Console.WriteLine();

        for (int rank = 0; rank < 8; rank++)
        {
            for (int file = 0; file < 8; file++)
            {
                int square = rank * 8 + file;
                if (file == 0) Console.Write($"  {8 - rank} ");
                Console.Write($" {(BitboardOperations.GetBit(bitboard, square) ? 1 : 0)}");
            }
            Console.WriteLine();
        }

        Console.WriteLine("\n     a b c d e f g h\n");
        Console.WriteLine($"     Bitboard: {bitboard}\n");
    }

    public static void PrintMoveList(ref MoveList moveList)
    {
        Console.WriteLine();

        for (int i = 0; i < moveList.count; i++)
        {
            int move = moveList.moves[i];
            Console.WriteLine(
                $"{squareToCoordinates[MoveEncoding.GetMoveSource(move)]}" +
                $"{squareToCoordinates[MoveEncoding.GetMoveTarget(move)]}" +
                $"{(MoveEncoding.GetMovePromoted(move) != 0 ? promotedPieces[MoveEncoding.GetMovePromoted(move)] : ' ')}");
        }

        Console.WriteLine($"\n      Total number of moves: {moveList.count}\n");
    }
}