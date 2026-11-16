using static CastlingRights;
using static Square;
using static MoveGenerator;

// =============================================================================
//  Bitboard helpers — inlined here since they're tiny and used everywhere.
// =============================================================================

public static class BitboardOperations
{
    public static void SetBit(ref ulong bitboard, int square) => bitboard |= 1UL << square;
    public static bool GetBit(ulong bitboard, int square) => (bitboard & (1UL << square)) != 0;
}

// =============================================================================
//  Board state, FEN parsing, copy/takeback
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
        Array.Clear(bitboards);
        Array.Clear(occupancies);
        side = White;
        enPassant = (int)noSquare;
        castle = 0;
        halfmoveClock = 0;

        string[] parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return;

        // Piece placement
        int rank = 0, file = 0;
        foreach (char c in parts[0])
        {
            if (c == '/') { rank++; file = 0; }
            else if (char.IsDigit(c)) { file += c - '0'; }
            else if (charPieces.TryGetValue(c, out int piece))
            {
                bitboards[piece] |= 1UL << ((rank << 3) | file);
                file++;
            }
        }

        // Active color
        side = parts[1] == "w" ? White : Black;

        // Castling rights
        foreach (char c in parts[2])
        {
            switch (c)
            {
                case 'K': castle |= (int)wk; break;
                case 'Q': castle |= (int)wq; break;
                case 'k': castle |= (int)bk; break;
                case 'q': castle |= (int)bq; break;
            }
        }

        // En passant target
        if (parts[3] != "-")
            enPassant = ((8 - (parts[3][1] - '0')) << 3) | (parts[3][0] - 'a');

        // Halfmove clock
        if (parts.Length > 4 && int.TryParse(parts[4], out int hm))
            halfmoveClock = hm;

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

    // 'in' passes a readonly reference instead of copying ~144 bytes per call.
    // At 119M perft nodes this avoids ~16 GB of redundant struct copies.
    public static void TakeBack(in BoardState state)
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
}