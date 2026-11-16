using static System.Numerics.BitOperations;
using static Board;
using static MoveGenerator;

// =============================================================================
//  Zobrist hashing — incremental position fingerprinting for the transposition
//  table and repetition detection.
//
//  Each piece on each square, each castling-right combination, each en-passant
//  file, and the side-to-move flag gets a unique random 64-bit key. The
//  position hash is the XOR of all active keys. Because XOR is its own inverse,
//  MakeMove/UnmakeMove can update the hash incrementally in O(1) by XORing
//  out the old keys and XORing in the new ones — no full rebuild needed.
//
//  The random keys are generated once at startup with a xorshift32 PRNG seeded
//  with the conventional value 1804289383 (same seed used by Stockfish and
//  most Code-Monkey-King–lineage engines, so positions are cross-comparable
//  during debugging).
// =============================================================================

public static class Zobrist
{
    // =========================================================================
    //  SECTION 1 — Key tables
    //
    //  All tables are filled once in the static constructor and never modified
    //  afterwards. `pieceKeys` is a flat 1D array indexed as
    //  `(piece << 6) | square` — the same layout used in Evaluation.cs — to
    //  avoid the extra multiply that C# 2D arrays require on every access.
    // =========================================================================

    // One random key per (piece, square) pair. 12 pieces × 64 squares = 768.
    public static readonly ulong[] pieceKeys = new ulong[12 << 6];

    // One random key per en-passant target square. Only squares on ranks 3
    // and 6 are ever used, but 64 entries keeps the indexing trivial.
    public static readonly ulong[] enpassantKeys = new ulong[64];

    // One random key per castling-rights combination. Castling is encoded as
    // a 4-bit mask (KQkq), giving 16 possible values.
    public static readonly ulong[] castleKeys = new ulong[16];

    // XORed into the hash when it is White's turn. Black-to-move positions
    // omit this key, so the same piece layout with opposite sides to move
    // produces a different hash.
    public static ulong sideKey;

    // The running hash for the current board position. Updated incrementally
    // by MakeMove/UnmakeMove; rebuilt from scratch only when loading a FEN.
    public static ulong hashKey;

    // =========================================================================
    //  SECTION 2 — Pseudo-random number generator (init-only)
    //
    //  xorshift32 by George Marsaglia. Period 2^32 − 1, passes most
    //  statistical tests, and is more than adequate for generating hash keys
    //  (we need ~900 random 64-bit values, not cryptographic security).
    //  The four 16-bit slices are combined into a 64-bit key to ensure all
    //  bit positions are well-populated.
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
        ulong n1 = NextU32() & 0xFFFF;
        ulong n2 = NextU32() & 0xFFFF;
        ulong n3 = NextU32() & 0xFFFF;
        ulong n4 = NextU32() & 0xFFFF;
        return n1 | (n2 << 16) | (n3 << 32) | (n4 << 48);
    }

    // =========================================================================
    //  SECTION 3 — Key initialization
    //
    //  Called once from the static constructor. Public so that test harnesses
    //  can re-seed if needed, but normal play never calls it a second time.
    // =========================================================================

    static Zobrist() => InitRandomKeys();

    public static void InitRandomKeys()
    {
        _rngState = 1804289383;

        for (int piece = P; piece <= k; piece++)
            for (int sq = 0; sq < 64; sq++)
                pieceKeys[(piece << 6) | sq] = NextU64();

        for (int sq = 0; sq < 64; sq++)
            enpassantKeys[sq] = NextU64();

        for (int i = 0; i < 16; i++)
            castleKeys[i] = NextU64();

        sideKey = NextU64();
    }

    // =========================================================================
    //  SECTION 4 — Full hash rebuild
    //
    //  Walks every piece on the board and XORs its key into a fresh hash.
    //  Used only when loading a new position (FEN/setboard). During search,
    //  the hash is maintained incrementally — this function is never called.
    // =========================================================================

    public static ulong GenerateHashKey()
    {
        ulong key = 0;

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

        if (side == White)
            key ^= sideKey;

        if (enPassant != (int)Square.noSquare)
            key ^= enpassantKeys[enPassant];

        key ^= castleKeys[castle];

        return key;
    }
}