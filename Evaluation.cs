using static System.Numerics.BitOperations;
using System.Runtime.CompilerServices;
using static Board;
using static MoveGenerator;
using static PieceAttacks;

public static class Evaluation
{
    // =========================================================================
    //  SECTION 1 — Tunable weights (PeSTO-derived)
    //
    //  Nothing in this section allocates at runtime; everything is a constant
    //  or a readonly array built once. Changing a number here is the only
    //  way to retune the evaluation — no logic needs to move.
    //  Before Texel Tuning:
    //      - Replace "private readonly"        with "public" for arrays
    //      - Replace "private const"           with "public static" for int
    //  Don't forget to revert after Texel Tuning and before build
    // =========================================================================

    #region Material & Phase

    // Base material values in centipawns, indexed by piece type:
    //   0=Pawn  1=Knight  2=Bishop  3=Rook  4=Queen  5=King
    // King is 0 because it's never captured; its "value" is implicit in game
    // termination, not in evaluation.
    public static int[] MgMaterial = [82, 337, 365, 477, 1025, 0];
    public static int[] EgMaterial = [94, 281, 297, 512, 936, 0];

    // Starting position phase: 2 sides × (2×Knight + 2×Bishop + 2×Rook + 1×Queen)
    //   = 2 × (2·1 + 2·1 + 2·2 + 1·4) = 24.
    // Phase is accumulated inline during evaluation (Knight/Bishop += 1,
    // Rook += 2, Queen += 4) rather than through a lookup table.
    internal const int TotalPhase = 24;

    #endregion

    #region Positional Bonuses / Penalties

    // Having both bishops is worth a small bonus because they cover
    // complementary colour complexes.
    public static int BishopPairMg = 0, BishopPairEg = 0;

    // Mobility = (number of safe destination squares) − (a baseline).
    // Subtracting a baseline keeps the "average" piece near zero so the
    // mobility term doesn't silently inflate material values.
    public static int KnightMobMg = 0, KnightMobEg = 0, KnightMobBase = 4;
    public static int BishopMobMg = 0, BishopMobEg = 0, BishopMobBase = 6;

    // Rook on a file with no friendly pawn:
    //   semi-open = only enemy pawns on it (good attacking lane)
    //   open      = no pawns at all (best — rook sees the whole file)
    public static int RookSemiOpenMg = 0, RookSemiOpenEg = 0;
    public static int RookOpenMg = 0, RookOpenEg = 0;

    // Passed pawn bonus indexed by squares left to promotion (0..7):
    //   index 1 = one step from promoting, index 6 = still on its start square.
    // A pawn gets this if no enemy pawn can block or capture it on its way
    // to promotion. The closer to promotion, the bigger the bonus — much
    // larger in the endgame where promotion threats dominate.
    public static int[] PassedMg = [0, 0, 0, 0, 0, 0, 0, 0];
    public static int[] PassedEg = [0, 0, 0, 0, 0, 0, 0, 0];

    // A pawn with no friendly pawn on either adjacent file. Weak because it
    // can never be defended by another pawn.
    public static int IsolatedMg = 0, IsolatedEg = 0;

    // King safety: penalties for open/semi-open files near the king. Only
    // applied in the middlegame — in the endgame the king wants to be active.
    public static int KingOwnOpenMg = 0, KingOwnSemiOpenMg = 0;
    public static int KingAdjacentOpenMg = 0, KingAdjacentSemiOpenMg = 0;

    // Once both queens are gone, encourage the king toward the centre so it
    // can help its pawns promote.
    public static int QueenlessKingCenterMg = 0;

    // Knight sitting on a square where:
    //   • it's protected by a friendly pawn
    //   • it can't be kicked by any enemy pawn on adjacent files
    // Outposts are classical strong squares (think c5/d5/e5/f5 for White).
    public static int KnightOutpostMg = 0;

    // Tempo bonus: static advantage awarded to the side to move
    internal const int TempoBonus = 8;

    #endregion

    // =========================================================================
    //  SECTION 2 — Piece–square tables (PeSTO)
    //
    //  One 64-entry table per piece type, written from White's perspective
    //  with square a8 at index 0 and h1 at index 63 — exactly matching the
    //  board's visual layout when you read top-to-bottom, left-to-right.
    //
    //  These are "source" tables. At startup we fold them with the material
    //  values into a single flat lookup (MgTable/EgTable) for fast access.
    // =========================================================================

    #region PST Data

    internal static readonly int[][] MgPstSource =
    [
        // Pawn — the back rank is zero because pawns never stand there.
        [    0,   0,   0,   0,   0,   0,   0,   0,
            98, 134,  61,  95,  68, 126,  34, -11,
            -6,   7,  26,  31,  65,  56,  25, -20,
           -14,  13,   6,  21,  23,  12,  17, -23,
           -27,  -2,  -5,  12,  17,   6,  10, -25,
           -26,  -4,  -4, -10,   3,   3,  33, -12,
           -35,  -1, -20, -23, -15,  24,  38, -22,
             0,   0,   0,   0,   0,   0,   0,   0 ],
        // Knight — rim is dim; centre squares are prized.
        [ -167, -89, -34, -49,  61,-110, -15,-107,
           -73, -41,  72,  36,  23,  62,   7, -17,
           -47,  60,  37,  65,  84, 129,  73,  44,
            -9,  17,  19,  53,  37,  69,  18,  22,
           -13,   4,  16,  13,  28,  19,  21,  -8,
           -23,  -9,  12,  10,  19,  17,  25, -19,
           -29, -53, -12,  -3,  -1,  18, -14, -19,
          -105, -21, -58, -33, -17, -28, -19, -23 ],
        // Bishop — long diagonals and fianchetto squares.
        [  -29,   4,-128, -54, -42, -50,   8,  -2,
           -26,   3, -19, -42,  16,  12,  -3, -32,
            -4,  26,  21,  19,  18,  58,  34,   7,
           -13,  15,  11,  41,  25,  22,   0,   7,
           -12,   5,  10,  21,  22,  -8,   2,   0,
           -16,  13,   8,   4,   2,  19,  12,   4,
            -4,  15,  12,  -7,   2,  12,  33,   7,
           -23,  -3, -14, -37, -25,  -9, -16, -10 ],
        // Rook — likes the 7th rank and central files.
        [   27,  32,  12,  41,  38,  -5,  45,  18,
            16,  13,  44,  61,  67,  67,   4,  30,
           -18,  14,   8,  22,   5,  54,  69,  13,
           -30, -11,  10,  30,  16,  35,  -4, -15,
           -42, -18, -12,   2,  -2,   4,   8, -25,
           -46, -25, -15, -19,   0,   5,  11, -21,
           -70, -23, -20, -13,  -6,  10,   4, -58,
           -25, -16, -14,  -2,   3,  13, -11, -19 ],
        // Queen — modest PST; mobility and tactics dominate its value.
        [  -37,  11,  12,  23,  87, 102, 100,  36,
           -26, -58,   2,  10, -22,  83,   6,  53,
           -14, -13,  -2,   1,  18,  82,  68,  47,
           -22, -18, -11,  -9,   2,  24,  15,   7,
           -17, -23, -12, -14,  -9,   2,   6,   8,
           -24,  -2,  -3,  -8,  -5,  -2,  10,  -4,
           -31, -11,  -1,  -4,  -3,   1,   0,  -9,
           -26, -25, -17,  -4, -18, -37, -49, -32 ],
        // King (middlegame) — castled corners are safe; the centre is fatal.
        [  -59,  31,  23,  -2, -43, -26,  14,  17,
            33,  12, -11,  -4,   0,   8, -20, -32,
             0,  46,  18,   1,  -2,  19,  38, -15,
            -6,   1,   6, -21, -21,  -6,   7, -26,
           -39,  22,  -5, -42, -37, -22,   4, -42,
           -13,  -3, -20, -38, -32, -32,  20,  -5,
            28,  12, -18, -50, -33, -29,  33,  39,
            20,  62,  28, -39,   0, -48,  44,  49 ]
    ];

    internal static readonly int[][] EgPstSource =
    [
        // Pawn — promotion race dominates; advanced pawns are huge.
        [    0,   0,   0,   0,   0,   0,   0,   0,
           152, 126, 116,  82,  84,  86, 124, 143,
            46,  32,   6, -17, -31, -19,   1,  15,
            16,   1, -11, -38, -32, -32, -14,  -9,
             2,  -6, -23, -28, -26, -27, -22, -18,
            -7, -16, -21, -29, -18, -21, -28, -25,
             3, -11,  -8, -17,  -3, -10, -23, -25,
             0,   0,   0,   0,   0,   0,   0,   0 ],
        // Knight — still prefers the centre, but values are compressed.
        [  -31, -12,  12,   0, -13,  13, -48, -78,
           -13,   6,  -4,  18,  22, -16,  -8, -35,
           -12,  11,  20,  21,   8,  14,   0, -25,
            -5,  13,  30,  45,  46,  31,  27,  -2,
            -7,  11,  29,  36,  40,  34,  18,  -1,
           -32,   3,   5,  24,  20,   4,  -4, -28,
           -36,  -8, -15,   2,   8, -18, -21, -34,
           -18, -58, -23, -11, -18, -20, -51, -73 ],
        // Bishop — long diagonals remain preferable.
        [   11,   4,  24,  16,  13,   8,   1, -11,
             0,   8,   8,  12,   3,   7,   5,   1,
             4,   4,  -3,  -6,  -2,   6,   7,   4,
             1,   6,   0,  -6,   3,  -2,  14,   7,
            -4,  -2,   8,   0,  -6,   2,  -1,  -4,
            -8,   1,  -4,  -3,   2, -10, -11, -11,
            -7, -26, -15, -11,  -8, -21, -22, -43,
           -17,  -6, -30,  -9,  -9, -15, -13, -20 ],
        // Rook — activity matters more than any one square.
        [   23,  19,  31,  20,  24,  37,  27,  31,
            22,  27,  16,  10,   1,   2,  24,  13,
            20,  10,  16,   7,  13,   8,  -8,  13,
            15,  13,  15,   5,   8,   6,   8,  16,
            -1,  13,  13,   5,   6,   7,  -4,  -5,
           -23,  -6, -10, -15, -13, -16, -17, -23,
           -32, -31, -21, -28, -30, -33, -32, -21,
           -20, -18, -12, -20, -18, -10, -17, -27 ],
        // Queen — rewards centralization in simplified positions.
        [   16,  36,  47,  49,  11,  12, -16,  46,
            -5,  46,  46,  64, 117,  37,  34,   7,
           -20,  -2,  26,  62,  75,  45,  37,  12,
           -22,  21,  30,  61,  80,  68,  76,  53,
           -38,  18,  17,  53,  45,  31,  23,  24,
           -40, -25,  -5, -14, -19,  -7, -28, -17,
           -49, -50, -67, -49, -58, -82, -96, -65,
           -63, -64, -59, -65, -55, -76, -49, -33 ],
        // King (endgame) — march to the centre to support pawns.
        [  -78, -28, -13,  -2,  10,  18,  -1, -36,
           -22,  28,  31,  17,  18,  26,  48,  -1,
            15,  39,  36,  32,  27,  41,  44,  15,
             9,  34,  40,  33,  31,  34,  33,   9,
            -7,  19,  31,  35,  29,  24,  14,  -5,
            -7,   6,  10,  24,  19,   5, -13, -18,
            -3,  -7,   7,  12,   7,   6, -22, -35,
           -50, -26,  -6, -16, -45,  -5, -31, -91 ]
    ];

    #endregion

    // =========================================================================
    //  SECTION 3 — Precomputed lookup tables
    //
    //  All of these are filled once in the static constructor. During search
    //  they are read-only, cache-friendly, and completely allocation-free.
    // =========================================================================

    // Flat 1D PST + material lookups. Indexing:
    //
    //     MgTable[(piece << 6) | square]
    //
    // The shift-by-6 trick works because every row is exactly 64 squares,
    // so (piece * 64 + sq) == (piece << 6) | sq. A single array load per
    // piece per square is enough to get material + positional score for
    // either side (black piece codes start at 6, so their rows slot in
    // naturally without any flipping at lookup time).
    private static readonly int[] MgTable = new int[12 << 6];
    private static readonly int[] EgTable = new int[12 << 6];

    // File masks: all 8 squares of a given file set to 1.
    //   FileMask[0] = a-file, FileMask[7] = h-file.
    internal static readonly ulong[] FileMask = new ulong[8];

    // The union of the two files adjacent to a given file. Used for
    // isolated-pawn detection and for passed-pawn blockade lanes.
    internal static readonly ulong[] AdjacentFiles = new ulong[8];

    // "Does any enemy pawn stand in front of me, on my file or adjacent?"
    // If (PassedMask & enemyPawns) == 0 → this pawn is passed.
    //
    //   WhitePassedMask[sq] — squares strictly north of `sq` on the three
    //                        relevant files (own + both neighbours).
    //   BlackPassedMask[sq] — same, but strictly south.
    internal static readonly ulong[] WhitePassedMask = new ulong[64];
    internal static readonly ulong[] BlackPassedMask = new ulong[64];

    // Outpost masks mirror the passed-pawn idea, but only cover the two
    // adjacent files (an enemy pawn on our own file cannot kick us off the
    // outpost — it can only push past us). Used for knight outposts.
    internal static readonly ulong[] WhiteOutpostMask = new ulong[64];
    internal static readonly ulong[] BlackOutpostMask = new ulong[64];

    // "How central is this square?" 0 at the rim, 3 at the four centre
    // squares. Used only when both queens are gone, to pull the king in.
    //
    //   Layout (a8 at top-left, h1 at bottom-right):
    //
    //     0 0 0 0 0 0 0 0
    //     0 1 1 1 1 1 1 0
    //     0 1 2 2 2 2 1 0
    //     0 1 2 3 3 2 1 0
    //     0 1 2 3 3 2 1 0
    //     0 1 2 2 2 2 1 0
    //     0 1 1 1 1 1 1 0
    //     0 0 0 0 0 0 0 0
    internal static readonly int[] KingCenterTable =
    [
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 1, 1, 1, 1, 1, 0,
        0, 1, 2, 2, 2, 2, 1, 0,
        0, 1, 2, 3, 3, 2, 1, 0,
        0, 1, 2, 3, 3, 2, 1, 0,
        0, 1, 2, 2, 2, 2, 1, 0,
        0, 1, 1, 1, 1, 1, 1, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
    ];

    // =========================================================================
    //  SECTION 4 — One-time table initialization
    //
    //  Runs on first access to the Evaluation class. All heavy work happens
    //  here so Evaluate() itself never has to compute masks or combine PSTs.
    // =========================================================================

    static Evaluation()
    {
        InitFileMasks();
        InitAdjacentFileMasks();
        InitPassedPawnMasks();
        InitOutpostMasks();
        InitMaterialPstTables();
    }

    // Set every bit on each file: for file f, that's squares f, f+8, f+16, ...
    private static void InitFileMasks()
    {
        for (int f = 0; f < 8; f++)
            for (int r = 0; r < 8; r++)
                FileMask[f] |= 1UL << (r * 8 + f);
    }

    // Union the file on the left and the file on the right (when they exist).
    // The a-file has no left neighbour; the h-file has no right neighbour.
    private static void InitAdjacentFileMasks()
    {
        for (int f = 0; f < 8; f++)
        {
            if (f > 0) AdjacentFiles[f] |= FileMask[f - 1];
            if (f < 7) AdjacentFiles[f] |= FileMask[f + 1];
        }
    }

    // For each square, build "all squares a blocking enemy pawn could occupy".
    //
    // Conceptually (for a white pawn on sq):
    //
    //        . X X X .     <- enemy pawns here would stop us
    //        . X X X .
    //        . X X X .
    //        . . P . .     <- our pawn
    //
    // We AND this mask with the enemy pawn bitboard at eval time. If the
    // result is zero, nothing can stop our pawn → it's passed.
    private static void InitPassedPawnMasks()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            int file = sq & 7;
            int rank = sq >> 3;
            ulong relevantFiles = FileMask[file] | AdjacentFiles[file];

            // "ahead" depends on side, since ranks are indexed from Black's
            // back rank (rank 0 == rank 8 on a real board).
            ulong aheadWhite = 0UL, aheadBlack = 0UL;
            for (int r = 0; r < rank; r++)
                for (int f = 0; f < 8; f++)
                    aheadWhite |= 1UL << (r * 8 + f);
            for (int r = rank + 1; r < 8; r++)
                for (int f = 0; f < 8; f++)
                    aheadBlack |= 1UL << (r * 8 + f);

            WhitePassedMask[sq] = relevantFiles & aheadWhite;
            BlackPassedMask[sq] = relevantFiles & aheadBlack;
        }
    }

    // Outpost masks: same idea as passed-pawn masks, but restricted to the
    // two neighbouring files only. An enemy pawn on our own file cannot
    // capture us, so it doesn't threaten the outpost.
    //
    //        . X . X .     <- only side-file enemy pawns matter
    //        . X . X .
    //        . X . X .
    //        . . N . .     <- our knight
    private static void InitOutpostMasks()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            int file = sq & 7;
            int rank = sq >> 3;
            ulong wMask = 0UL, bMask = 0UL;

            if (file > 0)
            {
                for (int r = 0; r < rank; r++) wMask |= 1UL << (r * 8 + file - 1);
                for (int r = rank + 1; r < 8; r++) bMask |= 1UL << (r * 8 + file - 1);
            }
            if (file < 7)
            {
                for (int r = 0; r < rank; r++) wMask |= 1UL << (r * 8 + file + 1);
                for (int r = rank + 1; r < 8; r++) bMask |= 1UL << (r * 8 + file + 1);
            }
            WhiteOutpostMask[sq] = wMask;
            BlackOutpostMask[sq] = bMask;
        }
    }

    // Fold material values into the PST once, so a single table read gives
    // "material + positional" for a piece on a square. Also mirror the
    // tables for Black: `sq ^ 56` flips the rank (a8 ↔ a1, b8 ↔ b1, ...),
    // which is how we reuse one set of White-oriented PSTs for both sides.
    private static void InitMaterialPstTables()
    {
        for (int piece = 0; piece < 6; piece++)
        {
            int whiteBase = piece << 6;         // row for the White piece code
            int blackBase = (piece + 6) << 6;   // row for the Black piece code
            for (int sq = 0; sq < 64; sq++)
            {
                MgTable[whiteBase + sq] = MgMaterial[piece] + MgPstSource[piece][sq];
                EgTable[whiteBase + sq] = EgMaterial[piece] + EgPstSource[piece][sq];
                MgTable[blackBase + sq] = MgMaterial[piece] + MgPstSource[piece][sq ^ 56];
                EgTable[blackBase + sq] = EgMaterial[piece] + EgPstSource[piece][sq ^ 56];
            }
        }
    }

    // =========================================================================
    //  SECTION 5 — The hot function: Evaluate()
    //
    //  Called millions of times per second. Every design choice here is in
    //  service of two goals:
    //
    //    1. Touch each piece exactly once.
    //    2. Keep all per-piece work in hot registers (locals) rather than
    //       bouncing through static arrays and properties.
    //
    //  Bitboard iteration idiom used everywhere below:
    //
    //      for (ulong x = pieces; x != 0; x &= x - 1)
    //      {
    //          int sq = TrailingZeroCount(x);
    //          ...
    //      }
    //
    //  `TrailingZeroCount` returns the index of the lowest set bit; `x & (x-1)`
    //  clears that bit. Together they walk the set bits in order with no
    //  branches and no lookup tables. On x64 these compile to `tzcnt` and a
    //  `blsr`/`lea` pair — about as tight as bit iteration gets.
    // =========================================================================

    public static int Evaluate()
    {
        // Pull everything into locals. Repeated `bitboards[P]` reads would go
        // through a static field, which the JIT can't always prove is stable;
        // snapping them here makes the whole function work on registers.
        ulong[] bb = bitboards;
        ulong[] occ = occupancies;

        ulong wP = bb[P], wN = bb[N], wB = bb[B], wR = bb[R], wQ = bb[Q], wK = bb[K];
        ulong bP = bb[p], bN = bb[n], bBi = bb[b], bR = bb[r], bQ = bb[q], bK = bb[k];

        ulong wOcc = occ[White];
        ulong bOcc = occ[Black];
        ulong allOcc = occ[Both];
        ulong allPawns = wP | bP;

        // Running totals. `phase` tracks remaining non-pawn material and is
        // used at the end to blend mg and eg.
        int mg = 0, eg = 0, phase = 0;

        // ---------------------------------------------------------------------
        //  Pawns (White)
        //
        //  Per pawn: material+PST, then passed-pawn bonus by rank, then an
        //  isolated-pawn penalty if no friendly pawn sits on either adjacent
        //  file. Pawns don't contribute to game phase.
        // ---------------------------------------------------------------------
        for (ulong x = wP; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            int idx = (P << 6) | sq;

            mg += MgTable[idx];
            eg += EgTable[idx];

            // No enemy pawn can block or capture us on our way to promotion.
            if ((WhitePassedMask[sq] & bP) == 0)
            {
                int rank = sq >> 3;
                mg += PassedMg[rank];
                eg += PassedEg[rank];
            }

            // Isolated: no friendly pawn on either adjacent file at all.
            if ((AdjacentFiles[sq & 7] & wP) == 0)
            {
                mg += IsolatedMg;
                eg += IsolatedEg;
            }
        }

        // ---------------------------------------------------------------------
        //  Pawns (Black) — mirror of White, with scores negated.
        // ---------------------------------------------------------------------
        for (ulong x = bP; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            int idx = (p << 6) | sq;

            mg -= MgTable[idx];
            eg -= EgTable[idx];

            if ((BlackPassedMask[sq] & wP) == 0)
            {
                // From Black's side, "advance toward promotion" means rank
                // index 7 heading down to 0, so we flip it.
                int rank = 7 - (sq >> 3);
                mg -= PassedMg[rank];
                eg -= PassedEg[rank];
            }

            if ((AdjacentFiles[sq & 7] & bP) == 0)
            {
                mg -= IsolatedMg;
                eg -= IsolatedEg;
            }
        }

        // ---------------------------------------------------------------------
        //  Knights (White)
        //
        //  Material/PST, phase, mobility (empty + enemy squares, excluding
        //  our own pieces), and outpost bonus.
        //
        //  Outpost condition:
        //    • knight is on rank 4..6 from White's view, encoded here as
        //      rank index 2..4 because rank 0 is Black's back rank
        //    • it's supported by a friendly pawn (an enemy pawn attack
        //      pattern from that square hits one of our pawns — classic
        //      "attack by colour" trick: if the enemy would attack a
        //      friendly pawn from here, a friendly pawn defends us)
        //    • no enemy pawn on an adjacent file can advance past us
        //
        //  The expression `(uint)(rank - 2) <= 2` is a branchless range
        //  check: underflow wraps to a large number, so only rank ∈ {2,3,4}
        //  passes the comparison.
        // ---------------------------------------------------------------------
        for (ulong x = wN; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            int idx = (N << 6) | sq;

            mg += MgTable[idx];
            eg += EgTable[idx];
            phase += 1;

            int mobility = PopCount(knightAttacks[sq] & ~wOcc) - KnightMobBase;
            mg += mobility * KnightMobMg;
            eg += mobility * KnightMobEg;

            int rank = sq >> 3;
            if ((uint)(rank - 2) <= 2
                && (pawnAttacks[Black, sq] & wP) != 0
                && (WhiteOutpostMask[sq] & bP) == 0)
            {
                mg += KnightOutpostMg;
            }
        }

        // ---------------------------------------------------------------------
        //  Knights (Black) — mirrored; outpost rank window shifts to 3..5.
        // ---------------------------------------------------------------------
        for (ulong x = bN; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            int idx = (n << 6) | sq;

            mg -= MgTable[idx];
            eg -= EgTable[idx];
            phase += 1;

            int mobility = PopCount(knightAttacks[sq] & ~bOcc) - KnightMobBase;
            mg -= mobility * KnightMobMg;
            eg -= mobility * KnightMobEg;

            int rank = sq >> 3;
            if ((uint)(rank - 3) <= 2
                && (pawnAttacks[White, sq] & bP) != 0
                && (BlackOutpostMask[sq] & wP) == 0)
            {
                mg -= KnightOutpostMg;
            }
        }

        // ---------------------------------------------------------------------
        //  Bishops (White)
        //
        //  Standard material/PST + phase + mobility. We also tally bishop
        //  count inline so the pair bonus costs us one comparison instead
        //  of a second PopCount pass.
        // ---------------------------------------------------------------------
        int wBishopCount = 0;
        for (ulong x = wB; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            int idx = (B << 6) | sq;

            mg += MgTable[idx];
            eg += EgTable[idx];
            phase += 1;
            wBishopCount++;

            int mobility = PopCount(GetBishopAttacks(sq, allOcc) & ~wOcc) - BishopMobBase;
            mg += mobility * BishopMobMg;
            eg += mobility * BishopMobEg;
        }
        if (wBishopCount >= 2) { mg += BishopPairMg; eg += BishopPairEg; }

        // ---------------------------------------------------------------------
        //  Bishops (Black) — mirrored.
        // ---------------------------------------------------------------------
        int bBishopCount = 0;
        for (ulong x = bBi; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            int idx = (b << 6) | sq;

            mg -= MgTable[idx];
            eg -= EgTable[idx];
            phase += 1;
            bBishopCount++;

            int mobility = PopCount(GetBishopAttacks(sq, allOcc) & ~bOcc) - BishopMobBase;
            mg -= mobility * BishopMobMg;
            eg -= mobility * BishopMobEg;
        }
        if (bBishopCount >= 2) { mg -= BishopPairMg; eg -= BishopPairEg; }

        // ---------------------------------------------------------------------
        //  Rooks (White)
        //
        //  Material/PST/phase, plus file structure:
        //    • no friendly pawn on the file   → at least semi-open
        //    • no pawn of either colour       → fully open (bigger bonus)
        // ---------------------------------------------------------------------
        for (ulong x = wR; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            int idx = (R << 6) | sq;

            mg += MgTable[idx];
            eg += EgTable[idx];
            phase += 2;

            ulong fileMask = FileMask[sq & 7];
            if ((wP & fileMask) == 0)
            {
                if ((allPawns & fileMask) == 0) { mg += RookOpenMg; eg += RookOpenEg; }
                else { mg += RookSemiOpenMg; eg += RookSemiOpenEg; }
            }
        }

        // ---------------------------------------------------------------------
        //  Rooks (Black) — mirrored.
        // ---------------------------------------------------------------------
        for (ulong x = bR; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            int idx = (r << 6) | sq;

            mg -= MgTable[idx];
            eg -= EgTable[idx];
            phase += 2;

            ulong fileMask = FileMask[sq & 7];
            if ((bP & fileMask) == 0)
            {
                if ((allPawns & fileMask) == 0) { mg -= RookOpenMg; eg -= RookOpenEg; }
                else { mg -= RookSemiOpenMg; eg -= RookSemiOpenEg; }
            }
        }

        // ---------------------------------------------------------------------
        //  Queens — only material/PST/phase. No mobility term because queen
        //  mobility is noisy and easy to double-count with rook+bishop ideas.
        // ---------------------------------------------------------------------
        for (ulong x = wQ; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            int idx = (Q << 6) | sq;

            mg += MgTable[idx];
            eg += EgTable[idx];
            phase += 4;
        }

        for (ulong x = bQ; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            int idx = (q << 6) | sq;

            mg -= MgTable[idx];
            eg -= EgTable[idx];
            phase += 4;
        }

        // ---------------------------------------------------------------------
        //  Kings
        //
        //  Exactly one of each. Skip the loop and extract the square directly.
        //  We score:
        //    • material/PST (the "king material" is 0 but the PST is huge —
        //      it's the entire king safety signal in the middlegame table
        //      and the "walk to the centre" signal in the endgame table)
        //    • open-file exposure penalties on own + adjacent files
        //    • queenless-endgame centralization bonus
        // ---------------------------------------------------------------------
        int wkSq = TrailingZeroCount(wK);
        int bkSq = TrailingZeroCount(bK);
        mg += MgTable[(K << 6) | wkSq];
        eg += EgTable[(K << 6) | wkSq];
        mg -= MgTable[(k << 6) | bkSq];
        eg -= EgTable[(k << 6) | bkSq];

        // sign = -1 means "penalise White" (helper adds to mg; negative means
        // subtracting from White's score). Flipped for Black.
        ScoreKingFileExposure(wkSq & 7, wP, allPawns, -1, ref mg);
        ScoreKingFileExposure(bkSq & 7, bP, allPawns, +1, ref mg);

        // Queens off → activate the kings.
        if (wQ == 0 && bQ == 0)
        {
            mg += KingCenterTable[wkSq] * QueenlessKingCenterMg;
            mg -= KingCenterTable[bkSq] * QueenlessKingCenterMg;
        }

        // ---------------------------------------------------------------------
        //  Tapered blend
        //
        //  Clamp phase to [0, TotalPhase]. Early game: mgPhase=24, egPhase=0.
        //  Bare king endgame: mgPhase=0, egPhase=24. The integer division is
        //  intentional — we want a plain weighted average, no rounding tricks.
        // ---------------------------------------------------------------------
        int mgPhase = phase > TotalPhase ? TotalPhase : phase;
        int egPhase = TotalPhase - mgPhase;
        int score = (mg * mgPhase + eg * egPhase) / TotalPhase;

        // Flip so the score is always from the moving side's perspective.
        return (side == White ? score : -score) + TempoBonus;
    }

    // =========================================================================
    //  SECTION 6 — King-safety helpers
    //
    //  Both helpers are tiny and only called from one spot each, so we hint
    //  the JIT to inline them. The `ref int mg` parameter lets us accumulate
    //  into the caller's local without returning a value.
    // =========================================================================

    // Evaluate exposure on the king's file and both neighbouring files.
    // Edge files have only one neighbour, so we guard the lookups.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScoreKingFileExposure(int kingFile, ulong friendlyPawns,
        ulong allPawns, int sign, ref int mg)
    {
        // The king's own file is the most dangerous when open — use the
        // larger penalty constants.
        ScoreOneKingFile(kingFile, friendlyPawns, allPawns, sign,
            KingOwnOpenMg, KingOwnSemiOpenMg, ref mg);

        // Adjacent files still matter (rook/queen swings), but less.
        if (kingFile > 0)
            ScoreOneKingFile(kingFile - 1, friendlyPawns, allPawns, sign,
                KingAdjacentOpenMg, KingAdjacentSemiOpenMg, ref mg);
        if (kingFile < 7)
            ScoreOneKingFile(kingFile + 1, friendlyPawns, allPawns, sign,
                KingAdjacentOpenMg, KingAdjacentSemiOpenMg, ref mg);
    }

    // Apply the open / semi-open / closed decision for a single file.
    //
    //   Closed      → friendly pawn on the file → no penalty, return.
    //   Open        → no pawns of either colour on the file → biggest penalty.
    //   Semi-open   → only enemy pawns on the file → smaller penalty.
    //
    // `sign` folds in the side: -1 means "this is White's king, so penalising
    // means subtracting from White's score" (and vice versa for +1).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScoreOneKingFile(int file, ulong friendlyPawns, ulong allPawns,
        int sign, int openPenalty, int semiOpenPenalty, ref int mg)
    {
        ulong mask = FileMask[file];
        if ((friendlyPawns & mask) != 0) return;
        mg += sign * (((allPawns & mask) == 0) ? openPenalty : semiOpenPenalty);
    }
}