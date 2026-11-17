using static System.Numerics.BitOperations;
using static Board;
using static MoveGenerator;

// =============================================================================
//  AnkaX Zobrist Hashing
//
//  Incremental position fingerprint for TT and repetition detection.
//
//  Each piece-square, en passant square, castling mask, and side-to-move is
//  assigned a random 64-bit key. The position hash is the XOR of all active
//  keys. XOR is reversible, so MakeMove can update the hash in O(1) without
//  rebuilding it from scratch.
// =============================================================================

public static class Zobrist
{
    // =========================================================================
    //  SECTION 1 — Key tables
    // =========================================================================

    // Indexed by: (piece << 6) | square
    public static readonly ulong[] pieceKeys = new ulong[12 << 6];
    public static readonly ulong[] enpassantKeys = new ulong[64];
    public static readonly ulong[] castleKeys = new ulong[16];

    public static ulong sideKey;

    // Incremental hash for the current position
    public static ulong hashKey;

    // =========================================================================
    //  SECTION 2 — Pseudo-random generator (xorshift32)
    // =========================================================================

    private static uint _rngState;

    private static uint NextU32()
    {
        uint x = _rngState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rngState = x;
        return x;
    }

    private static ulong NextU64()
    {
        // Combine 4 x 16-bit slices to populate all 64 bits
        ulong n1 = NextU32() & 0xFFFF;
        ulong n2 = NextU32() & 0xFFFF;
        ulong n3 = NextU32() & 0xFFFF;
        ulong n4 = NextU32() & 0xFFFF;
        return n1 | (n2 << 16) | (n3 << 32) | (n4 << 48);
    }

    // =========================================================================
    //  SECTION 3 — Initialization
    // =========================================================================

    static Zobrist() => InitRandomKeys();

    /// <summary>
    /// Generates all Zobrist keys using a fixed seed for reproducibility.
    /// </summary>
    public static void InitRandomKeys()
    {
        // Step 1. Seed with Stockfish-compatible value
        _rngState = 1804289383;

        // Step 2. Piece-square keys
        for (int piece = P; piece <= k; piece++)
            for (int sq = 0; sq < 64; sq++)
                pieceKeys[(piece << 6) | sq] = NextU64();

        // Step 3. En passant keys
        for (int sq = 0; sq < 64; sq++)
            enpassantKeys[sq] = NextU64();

        // Step 4. Castling rights keys (16 combinations of KQkq)
        for (int i = 0; i < 16; i++)
            castleKeys[i] = NextU64();

        // Step 5. Side-to-move key (XORed only when White to move)
        sideKey = NextU64();
    }

    // =========================================================================
    //  SECTION 4 — Hash generation (full rebuild)
    // =========================================================================

    /// <summary>
    /// Rebuilds the hash from scratch by XORing all active position features.
    /// Used only on FEN load; search uses incremental updates.
    /// </summary>
    public static ulong GenerateHashKey()
    {
        ulong key = 0;

        // Step 1. Piece-square contributions
        for (int piece = P; piece <= k; piece++)
        {
            ulong bb = bitboards[piece];
            while (bb != 0)
            {
                int sq = TrailingZeroCount(bb);
                key ^= pieceKeys[(piece << 6) | sq];
                bb &= bb - 1;
            }
        }

        // Step 2. Side to move
        if (side == White)
            key ^= sideKey;

        // Step 3. En passant target square
        if (enPassant != (int)Square.noSquare)
            key ^= enpassantKeys[enPassant];

        // Step 4. Castling rights
        key ^= castleKeys[castle];

        return key;
    }
}