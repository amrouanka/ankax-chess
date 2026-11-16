using static System.Numerics.BitOperations;
using static Board;
using static MoveGenerator;

// =============================================================================
//  Piece attack tables — precomputed at startup, read-only during search.
//
//  Leapers (pawns, knights, kings):
//      Simple bit-shift masks. One 64-entry table per piece type per side.
//
//  Sliders (bishops, rooks, queens):
//      Magic bitboards. For each square, a "mask" identifies the relevant
//      blocker squares (edges excluded, since a piece on the edge can't block
//      beyond the board). All 2^n subsets of those blockers are enumerated,
//      multiplied by a pre-found "magic number", and shifted to produce a
//      compact index into a per-square attack table.
//
//      Bishop: up to 9 relevant bits → 512 entries per square → 32 K entries.
//      Rook:   up to 12 relevant bits → 4096 entries per square → 256 K entries.
//      Queen = bishop | rook (no separate table needed).
//
//  The "on-the-fly" attack generators are used only during table initialization
//  to fill the magic tables. They are never called during search.
// =============================================================================

public static class PieceAttacks
{
    // =========================================================================
    //  SECTION 1 — File-mask constants
    //
    //  Used to prevent bit-shifts from wrapping around the board edges.
    //  For example, a pawn on the a-file shifting left would land on the
    //  h-file of the rank below without the NotAFile guard.
    // =========================================================================

    // All squares except the a-file (bit 0 of each rank cleared).
    public const ulong NotAFile = 0xFEFEFEFEFEFEFEFE;
    // All squares except the h-file (bit 7 of each rank cleared).
    public const ulong NotHFile = 0x7F7F7F7F7F7F7F7F;
    // All squares except the g- and h-files.
    public const ulong NotHGFile = 0x3F3F3F3F3F3F3F3F;
    // All squares except the a- and b-files.
    public const ulong NotABFile = 0xFCFCFCFCFCFCFCFC;

    // =========================================================================
    //  SECTION 2 — Attack table storage
    //
    //  Leaper tables are small and stay 2D/1D as appropriate. Slider tables
    //  are flattened to 1D for the same reason as Zobrist.pieceKeys and
    //  Evaluation.MgTable: shift+OR indexing is cheaper than the multiply
    //  that C# 2D arrays emit.
    // =========================================================================

    // Pawn attacks: [side, square]. 2 × 64 = 128 entries.
    public static readonly ulong[,] pawnAttacks = new ulong[2, 64];

    // Knight and king attacks: [square]. 64 entries each.
    public static readonly ulong[] knightAttacks = new ulong[64];
    public static readonly ulong[] kingAttacks = new ulong[64];

    // Slider blocker masks: the subset of squares that can block a slider
    // on the given square (board edges excluded).
    public static readonly ulong[] bishopMasks = new ulong[64];
    public static readonly ulong[] rookMasks = new ulong[64];

    // Magic attack tables, flattened:
    //   bishopAttacks[(square << 9) | magicIndex]   — 64 × 512  = 32,768 entries
    //   rookAttacks[(square << 12) | magicIndex]    — 64 × 4096 = 262,144 entries
    public static readonly ulong[] bishopAttacks = new ulong[64 << 9];
    public static readonly ulong[] rookAttacks = new ulong[64 << 12];

    // =========================================================================
    //  SECTION 3 — Magic bitboard data
    //
    //  `relevantBits` = number of blocker bits for each square (determines
    //  the table size: 2^bits entries). `magicNumbers` = pre-found multipliers
    //  that hash each blocker configuration into a unique index with no
    //  collisions. These are standard values shared across many engines.
    // =========================================================================

    public static readonly int[] bishopRelevantBits =
    [
        6, 5, 5, 5, 5, 5, 5, 6,
        5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 7, 7, 7, 7, 5, 5,
        5, 5, 7, 9, 9, 7, 5, 5,
        5, 5, 7, 9, 9, 7, 5, 5,
        5, 5, 7, 7, 7, 7, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5,
        6, 5, 5, 5, 5, 5, 5, 6
    ];

    public static readonly int[] rookRelevantBits =
    [
        12, 11, 11, 11, 11, 11, 11, 12,
        11, 10, 10, 10, 10, 10, 10, 11,
        11, 10, 10, 10, 10, 10, 10, 11,
        11, 10, 10, 10, 10, 10, 10, 11,
        11, 10, 10, 10, 10, 10, 10, 11,
        11, 10, 10, 10, 10, 10, 10, 11,
        11, 10, 10, 10, 10, 10, 10, 11,
        12, 11, 11, 11, 11, 11, 11, 12
    ];

    public static readonly ulong[] rookMagicNumbers =
    [
        0x8a80104000800020UL, 0x140002000100040UL, 0x2801880a0017001UL, 0x100081001000420UL,
        0x200020010080420UL, 0x3001c0002010008UL, 0x8480008002000100UL, 0x2080088004402900UL,
        0x800098204000UL, 0x2024401000200040UL, 0x100802000801000UL, 0x120800800801000UL,
        0x208808088000400UL, 0x2802200800400UL, 0x2200800100020080UL, 0x801000060821100UL,
        0x80044006422000UL, 0x100808020004000UL, 0x12108a0010204200UL, 0x140848010000802UL,
        0x481828014002800UL, 0x8094004002004100UL, 0x4010040010010802UL, 0x20008806104UL,
        0x100400080208000UL, 0x2040002120081000UL, 0x21200680100081UL, 0x20100080080080UL,
        0x2000a00200410UL, 0x20080800400UL, 0x80088400100102UL, 0x80004600042881UL,
        0x4040008040800020UL, 0x440003000200801UL, 0x4200011004500UL, 0x188020010100100UL,
        0x14800401802800UL, 0x2080040080800200UL, 0x124080204001001UL, 0x200046502000484UL,
        0x480400080088020UL, 0x1000422010034000UL, 0x30200100110040UL, 0x100021010009UL,
        0x2002080100110004UL, 0x202008004008002UL, 0x20020004010100UL, 0x2048440040820001UL,
        0x101002200408200UL, 0x40802000401080UL, 0x4008142004410100UL, 0x2060820c0120200UL,
        0x1001004080100UL, 0x20c020080040080UL, 0x2935610830022400UL, 0x44440041009200UL,
        0x280001040802101UL, 0x2100190040002085UL, 0x80c0084100102001UL, 0x4024081001000421UL,
        0x20030a0244872UL, 0x12001008414402UL, 0x2006104900a0804UL, 0x1004081002402UL
    ];

    public static readonly ulong[] bishopMagicNumbers =
    [
        0x40040844404084UL, 0x2004208a004208UL, 0x10190041080202UL, 0x108060845042010UL,
        0x581104180800210UL, 0x2112080446200010UL, 0x1080820820060210UL, 0x3c0808410220200UL,
        0x4050404440404UL, 0x21001420088UL, 0x24d0080801082102UL, 0x1020a0a020400UL,
        0x40308200402UL, 0x4011002100800UL, 0x401484104104005UL, 0x801010402020200UL,
        0x400210c3880100UL, 0x404022024108200UL, 0x810018200204102UL, 0x4002801a02003UL,
        0x85040820080400UL, 0x810102c808880400UL, 0xe900410884800UL, 0x8002020480840102UL,
        0x220200865090201UL, 0x2010100a02021202UL, 0x152048408022401UL, 0x20080002081110UL,
        0x4001001021004000UL, 0x800040400a011002UL, 0xe4004081011002UL, 0x1c004001012080UL,
        0x8004200962a00220UL, 0x8422100208500202UL, 0x2000402200300c08UL, 0x8646020080080080UL,
        0x80020a0200100808UL, 0x2010004880111000UL, 0x623000a080011400UL, 0x42008c0340209202UL,
        0x209188240001000UL, 0x400408a884001800UL, 0x110400a6080400UL, 0x1840060a44020800UL,
        0x90080104000041UL, 0x201011000808101UL, 0x1a2208080504f080UL, 0x8012020600211212UL,
        0x500861011240000UL, 0x180806108200800UL, 0x4000020e01040044UL, 0x300000261044000aUL,
        0x802241102020002UL, 0x20906061210001UL, 0x5a84841004010310UL, 0x4010801011c04UL,
        0xa010109502200UL, 0x4a02012000UL, 0x500201010098b028UL, 0x8040002811040900UL,
        0x28000010020204UL, 0x6000020202d0240UL, 0x8918844842082200UL, 0x4010011029020020UL
    ];

    // =========================================================================
    //  SECTION 4 — Initialization
    //
    //  Runs once from the static constructor. The "on-the-fly" generators
    //  below are helpers used only here to populate the magic tables.
    // =========================================================================

    static PieceAttacks()
    {
        InitLeaperAttacks();
        InitSliderAttacks(isBishop: true);
        InitSliderAttacks(isBishop: false);
    }

    // --- Leaper helpers (init-only) ---

    private static ulong MaskPawnAttacks(int side, int sq)
    {
        ulong bb = 1UL << sq;
        ulong attacks = 0;

        if (side == White)
        {
            if (((bb >> 7) & NotAFile) != 0) attacks |= bb >> 7;
            if (((bb >> 9) & NotHFile) != 0) attacks |= bb >> 9;
        }
        else
        {
            if (((bb << 7) & NotHFile) != 0) attacks |= bb << 7;
            if (((bb << 9) & NotAFile) != 0) attacks |= bb << 9;
        }
        return attacks;
    }

    private static ulong MaskKnightAttacks(int sq)
    {
        ulong bb = 1UL << sq;
        ulong attacks = 0;

        if ((bb >> 17 & NotHFile) != 0) attacks |= bb >> 17;
        if ((bb >> 15 & NotAFile) != 0) attacks |= bb >> 15;
        if ((bb >> 10 & NotHGFile) != 0) attacks |= bb >> 10;
        if ((bb >> 6 & NotABFile) != 0) attacks |= bb >> 6;
        if ((bb << 17 & NotAFile) != 0) attacks |= bb << 17;
        if ((bb << 15 & NotHFile) != 0) attacks |= bb << 15;
        if ((bb << 10 & NotABFile) != 0) attacks |= bb << 10;
        if ((bb << 6 & NotHGFile) != 0) attacks |= bb << 6;

        return attacks;
    }

    private static ulong MaskKingAttacks(int sq)
    {
        ulong bb = 1UL << sq;
        ulong attacks = 0;

        if ((bb >> 8) != 0) attacks |= bb >> 8;
        if (((bb >> 9) & NotHFile) != 0) attacks |= bb >> 9;
        if (((bb >> 7) & NotAFile) != 0) attacks |= bb >> 7;
        if (((bb >> 1) & NotHFile) != 0) attacks |= bb >> 1;
        if ((bb << 8) != 0) attacks |= bb << 8;
        if (((bb << 9) & NotAFile) != 0) attacks |= bb << 9;
        if (((bb << 7) & NotHFile) != 0) attacks |= bb << 7;
        if (((bb << 1) & NotAFile) != 0) attacks |= bb << 1;

        return attacks;
    }

    private static void InitLeaperAttacks()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            pawnAttacks[White, sq] = MaskPawnAttacks(White, sq);
            pawnAttacks[Black, sq] = MaskPawnAttacks(Black, sq);
            knightAttacks[sq] = MaskKnightAttacks(sq);
            kingAttacks[sq] = MaskKingAttacks(sq);
        }
    }

    // --- Slider helpers (init-only) ---

    // Build the blocker mask for a bishop on `sq`. Edge squares are excluded
    // because a blocker on the edge can't block anything beyond the board.
    private static ulong MaskBishopAttacks(int sq)
    {
        ulong attacks = 0;
        int tr = sq >> 3, tf = sq & 7;

        for (int r = tr + 1, f = tf + 1; r <= 6 && f <= 6; r++, f++) attacks |= 1UL << (r * 8 + f);
        for (int r = tr - 1, f = tf + 1; r >= 1 && f <= 6; r--, f++) attacks |= 1UL << (r * 8 + f);
        for (int r = tr + 1, f = tf - 1; r <= 6 && f >= 1; r++, f--) attacks |= 1UL << (r * 8 + f);
        for (int r = tr - 1, f = tf - 1; r >= 1 && f >= 1; r--, f--) attacks |= 1UL << (r * 8 + f);

        return attacks;
    }

    // Build the blocker mask for a rook on `sq`. Same edge-exclusion logic.
    private static ulong MaskRookAttacks(int sq)
    {
        ulong attacks = 0;
        int tr = sq >> 3, tf = sq & 7;

        for (int r = tr + 1; r <= 6; r++) attacks |= 1UL << (r * 8 + tf);
        for (int r = tr - 1; r >= 1; r--) attacks |= 1UL << (r * 8 + tf);
        for (int f = tf + 1; f <= 6; f++) attacks |= 1UL << (tr * 8 + f);
        for (int f = tf - 1; f >= 1; f--) attacks |= 1UL << (tr * 8 + f);

        return attacks;
    }

    // Given a bitmask of relevant squares and an index 0..2^n-1, set the
    // bits of `occupancy` to match the binary representation of `index`.
    // This enumerates all possible blocker configurations for a slider.
    private static ulong SetOccupancy(int index, int bitsInMask, ulong attackMask)
    {
        ulong occupancy = 0;
        for (int i = 0; i < bitsInMask; i++)
        {
            int sq = TrailingZeroCount(attackMask);
            attackMask &= attackMask - 1;
            if ((index & (1 << i)) != 0)
                occupancy |= 1UL << sq;
        }
        return occupancy;
    }

    // Compute exact bishop attacks for a given blocker configuration by
    // ray-casting in all four diagonal directions. Init-only.
    private static ulong BishopAttacksOnTheFly(int sq, ulong block)
    {
        ulong attacks = 0;
        int tr = sq >> 3, tf = sq & 7;

        for (int r = tr + 1, f = tf + 1; r <= 7 && f <= 7; r++, f++)
        { ulong s = 1UL << (r * 8 + f); attacks |= s; if ((s & block) != 0) break; }
        for (int r = tr - 1, f = tf + 1; r >= 0 && f <= 7; r--, f++)
        { ulong s = 1UL << (r * 8 + f); attacks |= s; if ((s & block) != 0) break; }
        for (int r = tr + 1, f = tf - 1; r <= 7 && f >= 0; r++, f--)
        { ulong s = 1UL << (r * 8 + f); attacks |= s; if ((s & block) != 0) break; }
        for (int r = tr - 1, f = tf - 1; r >= 0 && f >= 0; r--, f--)
        { ulong s = 1UL << (r * 8 + f); attacks |= s; if ((s & block) != 0) break; }

        return attacks;
    }

    // Compute exact rook attacks for a given blocker configuration by
    // ray-casting in all four orthogonal directions. Init-only.
    private static ulong RookAttacksOnTheFly(int sq, ulong block)
    {
        ulong attacks = 0;
        int tr = sq >> 3, tf = sq & 7;

        for (int r = tr + 1; r <= 7; r++)
        { ulong s = 1UL << (r * 8 + tf); attacks |= s; if ((s & block) != 0) break; }
        for (int r = tr - 1; r >= 0; r--)
        { ulong s = 1UL << (r * 8 + tf); attacks |= s; if ((s & block) != 0) break; }
        for (int f = tf + 1; f <= 7; f++)
        { ulong s = 1UL << (tr * 8 + f); attacks |= s; if ((s & block) != 0) break; }
        for (int f = tf - 1; f >= 0; f--)
        { ulong s = 1UL << (tr * 8 + f); attacks |= s; if ((s & block) != 0) break; }

        return attacks;
    }

    // Populate the magic attack table for bishops or rooks.
    private static void InitSliderAttacks(bool isBishop)
    {
        for (int sq = 0; sq < 64; sq++)
        {
            if (isBishop)
            {
                bishopMasks[sq] = MaskBishopAttacks(sq);
                int bits = bishopRelevantBits[sq];
                int count = 1 << bits;

                for (int i = 0; i < count; i++)
                {
                    ulong occ = SetOccupancy(i, bits, bishopMasks[sq]);
                    int magicIdx = (int)((occ * bishopMagicNumbers[sq]) >> (64 - bits));
                    bishopAttacks[(sq << 9) | magicIdx] = BishopAttacksOnTheFly(sq, occ);
                }
            }
            else
            {
                rookMasks[sq] = MaskRookAttacks(sq);
                int bits = rookRelevantBits[sq];
                int count = 1 << bits;

                for (int i = 0; i < count; i++)
                {
                    ulong occ = SetOccupancy(i, bits, rookMasks[sq]);
                    int magicIdx = (int)((occ * rookMagicNumbers[sq]) >> (64 - bits));
                    rookAttacks[(sq << 12) | magicIdx] = RookAttacksOnTheFly(sq, occ);
                }
            }
        }
    }

    // =========================================================================
    //  SECTION 5 — Hot-path attack lookups
    //
    //  These three functions are called millions of times per second during
    //  search and evaluation. Each is a mask → multiply → shift → table read.
    //  No branches, no loops. The magic numbers are verified to produce no
    //  collisions, so no bounds check is needed on the index.
    // =========================================================================

    public static ulong GetBishopAttacks(int square, ulong occupancy)
    {
        occupancy &= bishopMasks[square];
        occupancy *= bishopMagicNumbers[square];
        occupancy >>= 64 - bishopRelevantBits[square];
        return bishopAttacks[(square << 9) | (int)occupancy];
    }

    public static ulong GetRookAttacks(int square, ulong occupancy)
    {
        occupancy &= rookMasks[square];
        occupancy *= rookMagicNumbers[square];
        occupancy >>= 64 - rookRelevantBits[square];
        return rookAttacks[(square << 12) | (int)occupancy];
    }

    public static ulong GetQueenAttacks(int square, ulong occupancy)
    {
        return GetBishopAttacks(square, occupancy) | GetRookAttacks(square, occupancy);
    }

    // =========================================================================
    //  SECTION 6 — Square attack testing
    //
    //  "Is `square` attacked by any piece of the given `side`?" Uses the
    //  reverse-attack trick for pawns: to check if a White pawn attacks
    //  `square`, we look up the Black pawn attack pattern from `square`
    //  and AND it with White's pawn bitboard.
    //
    //  Early returns on the first hit — most squares are attacked by at
    //  most one piece type, so the average case exits after 1–2 checks.
    // =========================================================================

    public static bool IsSquareAttacked(int square, int side)
    {
        // Piece-code offset: 0 for White (P..K = 0..5), 6 for Black (p..k = 6..11).
        int offset = side == White ? 0 : 6;
        ulong occ = occupancies[Both];

        if ((pawnAttacks[side ^ 1, square] & bitboards[P + offset]) != 0) return true;
        if ((knightAttacks[square] & bitboards[N + offset]) != 0) return true;
        if ((GetBishopAttacks(square, occ) & bitboards[B + offset]) != 0) return true;
        if ((GetRookAttacks(square, occ) & bitboards[R + offset]) != 0) return true;
        if ((GetQueenAttacks(square, occ) & bitboards[Q + offset]) != 0) return true;
        if ((kingAttacks[square] & bitboards[K + offset]) != 0) return true;

        return false;
    }

    // =========================================================================
    //  SECTION 7 — Debug helpers
    // =========================================================================

    public static void PrintAttackedSquares(int side)
    {
        Console.WriteLine();
        for (int rank = 0; rank < 8; rank++)
        {
            for (int file = 0; file < 8; file++)
            {
                if (file == 0) Console.Write($"  {8 - rank} ");
                Console.Write($" {IsSquareAttacked(rank * 8 + file, side)}");
            }
            Console.WriteLine();
        }
        Console.WriteLine("\n     a b c d e f g h\n");
    }
}