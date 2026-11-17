using static CastlingRights;
using static MoveGenerator;
using static Square;

// =============================================================================
//  AnkaX Board Representation & State
//
//  Maintains static global bitboard state, piece mappings, FEN parsing,
//  and lightweight state snapshotting for move make/takeback.
// =============================================================================

// =============================================================================
//  SECTION 1 — Bitboard Helper Operations
// =============================================================================

public static class BitboardOperations
{
    public static void SetBit(ref ulong bitboard, int square) => bitboard |= 1UL << square;
    public static bool GetBit(ulong bitboard, int square) => (bitboard & (1UL << square)) != 0;
}

// =============================================================================
//  SECTION 2 — Board State & Constants
// =============================================================================

public static class Board
{
    // Piece codes
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
        ' ', 'n', 'b', 'r', 'q', ' ',   // White P..K
        ' ', 'n', 'b', 'r', 'q', ' '    // Black p..k
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

    // Global board state
    public static ulong[] bitboards = new ulong[12];
    public static ulong[] occupancies = new ulong[3];
    public static int side;
    public static int enPassant = (int)noSquare;
    public static int castle;
    public static int halfmoveClock;

    // =========================================================================
    //  SECTION 3 — FEN Parsing
    // =========================================================================

    /// <summary>
    /// Parses a FEN string and initializes all board bitboards, occupancy layers,
    /// game state flags, and the Zobrist hash key.
    /// </summary>
    public static void ParseFEN(string fen)
    {
        // Step 1. Reset current state
        Array.Clear(bitboards);
        Array.Clear(occupancies);
        side = White;
        enPassant = (int)noSquare;
        castle = 0;
        halfmoveClock = 0;

        string[] parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return;

        // Step 2. Parse piece placement
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

        // Step 3. Parse active color
        side = parts[1] == "w" ? White : Black;

        // Step 4. Parse castling rights
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

        // Step 5. Parse en passant target square
        if (parts[3] != "-")
            enPassant = ((8 - (parts[3][1] - '0')) << 3) | (parts[3][0] - 'a');

        // Step 6. Parse halfmove clock
        if (parts.Length > 4 && int.TryParse(parts[4], out int hm))
            halfmoveClock = hm;

        // Step 7. Rebuild occupancy bitboards and initial Zobrist hash
        UpdateOccupancies();
        Zobrist.hashKey = Zobrist.GenerateHashKey();
    }

    // =========================================================================
    //  SECTION 4 — Occupancy Layer Rebuild
    // =========================================================================

    /// <summary>
    /// Recomputes White, Black, and Combined occupancy bitboards from piece bitboards.
    /// </summary>
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
    //  SECTION 5 — State Snapshot (Copy & Takeback)
    // =========================================================================

    /// <summary>
    /// Creates a value snapshot of the current board state for move execution.
    /// </summary>
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

    /// <summary>
    /// Restores the board state from a snapshot. The <c>in</c> modifier avoids
    /// copying the ~144-byte struct by passing a readonly reference.
    /// </summary>
    public static void TakeBack(in BoardState state)
    {
        bitboards[0] = state.bb0; bitboards[1] = state.bb1; bitboards[2] = state.bb2;
        bitboards[3] = state.bb3; bitboards[4] = state.bb4; bitboards[5] = state.bb5;
        bitboards[6] = state.bb6; bitboards[7] = state.bb7; bitboards[8] = state.bb8;
        bitboards[9] = state.bb9; bitboards[10] = state.bb10; bitboards[11] = state.bb11;

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