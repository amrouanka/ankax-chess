using static System.Numerics.BitOperations;
using System.Runtime.CompilerServices;
using static Board;
using static MoveGenerator;
using static PieceAttacks;

// =============================================================================
//  Evaluation — static, tapered (middlegame ↔ endgame) positional scoring.
//
//  Score convention:
//      Positive  = White is better
//      Negative  = Black is better
//      Returned score is flipped at the end if it's Black to move, so the
//      search always sees "my score" from the side-to-move's perspective.
//
//  Tapered eval:
//      Every term produces two numbers: `mg` (middlegame) and `eg` (endgame).
//      A `phase` counter tracks how much non-pawn material is still on the
//      board (queens weigh most, minors least). The final score blends mg/eg
//      proportionally to the current phase. This gives smooth transitions
//      rather than sudden jumps when a piece trade crosses some threshold.
//
//  Feature set (all baked into the same mg/eg pair):
//      • Material + piece–square tables (PeSTO values)
//      • Bishop pair bonus
//      • Knight and bishop mobility
//      • Knight outposts
//      • Rook on open / semi-open file
//      • Passed pawns (by rank)
//      • Isolated pawns
//      • King exposure on own and adjacent files
//      • Queenless endgame king centralization
// =============================================================================

public static class Evaluation
{
    // =========================================================================
    //  SECTION 1 — Tunable weights (PeSTO-derived)
    //
    //  Nothing in this section allocates at runtime; everything is a constant
    //  or a readonly array built once. Changing a number here is the only
    //  way to retune the evaluation — no logic needs to move.
    // =========================================================================

    #region Material & Phase

    // Base material values in centipawns, indexed by piece type:
    //   0=Pawn  1=Knight  2=Bishop  3=Rook  4=Queen  5=King
    // King is 0 because it's never captured; its "value" is implicit in game
    // termination, not in evaluation.
    private static readonly int[] MgMaterial = [82, 337, 365, 457, 1041, 0];
    private static readonly int[] EgMaterial = [90, 281, 297, 520, 940, 0];

    // Starting position phase: 2 sides × (2×Knight + 2×Bishop + 2×Rook + 1×Queen)
    //   = 2 × (2·1 + 2·1 + 2·2 + 1·4) = 24.
    // Phase is accumulated inline during evaluation (Knight/Bishop += 1,
    // Rook += 2, Queen += 4) rather than through a lookup table.
    private const int TotalPhase = 24;

    #endregion

    #region Positional Bonuses / Penalties

    // Having both bishops is worth a small bonus because they cover
    // complementary colour complexes.
    private const int BishopPairMg = 25, BishopPairEg = 49;

    // Mobility = (number of safe destination squares) − (a baseline).
    // Subtracting a baseline keeps the "average" piece near zero so the
    // mobility term doesn't silently inflate material values.
    private const int KnightMobMg = 1, KnightMobEg = 0, KnightMobBase = 4;
    private const int BishopMobMg = 4, BishopMobEg = 4, BishopMobBase = 6;

    // Rook on a file with no friendly pawn:
    //   semi-open = only enemy pawns on it (good attacking lane)
    //   open      = no pawns at all (best — rook sees the whole file)
    private const int RookSemiOpenMg = 21, RookSemiOpenEg = 15;
    private const int RookOpenMg = 54, RookOpenEg = 8;

    // Passed pawn bonus indexed by "distance from own back rank" (0..7).
    // A pawn gets this if no enemy pawn can block or capture it on its way
    // to promotion. The closer to promotion, the bigger the bonus — much
    // larger in the endgame where promotion threats dominate.
    private static readonly int[] PassedMg = [0, 0, 34, 14, 11, 11, 10, 0];
    private static readonly int[] PassedEg = [0, 48, 95, 52, 26, 7, 4, 0];

    // A pawn with no friendly pawn on either adjacent file. Weak because it
    // can never be defended by another pawn.
    private const int IsolatedMg = -19, IsolatedEg = -8;

    // King safety: penalties for open/semi-open files near the king. Only
    // applied in the middlegame — in the endgame the king wants to be active.
    private const int KingOwnOpenMg = 71, KingOwnSemiOpenMg = 18;
    private const int KingAdjacentOpenMg = 37, KingAdjacentSemiOpenMg = 13;

    // Once both queens are gone, encourage the king toward the centre so it
    // can help its pawns promote.
    private const int QueenlessKingCenterMg = 20;

    // Knight sitting on a square where:
    //   • it's protected by a friendly pawn
    //   • it can't be kicked by any enemy pawn on adjacent files
    // Outposts are classical strong squares (think c5/d5/e5/f5 for White).
    private const int KnightOutpostMg = 46;

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

    private static readonly int[][] MgPstSource =
    [
        // Pawn — the back rank is zero because pawns never stand there.
        [    0,   0,   0,   0,   0,   0,   0,   0,
           105, 125,  83, 132,  83,  66,   0, -16,
             6,  -7,  17,   5,  35,  73,  17,  -7,
            -4,   5,   9,  24,  26,  18,   5, -18,
           -21, -16,  -1,  12,  14,  10,  -9, -24,
           -19, -21,  -4,  -9,   1,   4,  10, -11,
           -31, -15, -23, -20, -16,  21,  18, -21,
             0,   0,   0,   0,   0,   0,   0,   0 ],
        // Knight — rim is dim; centre squares are prized.
        [ -181, -83, -48, -31,  97,-108,  -1,-118,
           -76, -46,  38,  53,  -4,  66,   8, -11,
           -45,  39,  34,  53,  91, 108,  66,  46,
            -8,  18,   7,  49,  38,  71,  26,  29,
           -10,  13,  18,  14,  34,  27,  32,  -2,
           -16,  -1,  18,  23,  35,  28,  34,  -7,
           -23, -38,  -3,  10,  15,  25,   5,  -3,
          -109, -15, -42, -29,  -1, -14,  -9, -13 ],
        // Bishop — long diagonals and fianchetto squares.
        [  -29, -14,-129, -64, -48, -49, -14, -14,
           -32, -13, -32, -61,  18,  -1,   1, -63,
           -28,  20,  -2,  14,   8,  48,  17,  -6,
           -12,  -2,  -1,  32,  20,  15,  -1,  -5,
           -11,   3,  -3,  20,  21,  -3,   0,   2,
            -6,   9,   6,   2,   5,  23,  12,   4,
            -5,  15,   6,  -4,   5,  13,  33,   3,
           -37, -11, -12, -24, -15, -11, -42, -26 ],
        // Rook — likes the 7th rank and central files.
        [   17,  32,   7,  52,  47,  -8,  28,  11,
            15,   8,  52,  66,  86,  70,  -8,  35,
           -17,   1,  -2,  14,  -9,  41,  69,  -3,
           -37, -24,  -5,  12,   4,  29, -10, -30,
           -41, -30, -26,  -9,  -5,  -4,  14, -24,
           -42, -25, -20, -25,  -7,   8,   0, -25,
           -39, -14, -28, -17,  -5,   7,  -5, -63,
           -11, -13,  -7,   1,   4,  13, -30, -14 ],
        // Queen — modest PST; mobility and tactics dominate its value.
        [  -40, -14,  -1,   4,  93,  78,  91,  24,
           -26, -50, -17,   0, -56,  45,  -6,  43,
            -6, -17, -12,  -9,   5,  65,  43,  56,
           -31, -25, -22, -22, -10,   8,  -3,  -6,
           -10, -34, -10, -11,  -3,  -1,  -1,  -4,
           -18,   2, -11,   2,  -1,   2,  11,   2,
           -37,  -8,  11,   6,  15,  20,   4,   9,
             1, -16,  -9,  10, -16, -16, -36, -54 ],
        // King (middlegame) — castled corners are safe; the centre is fatal.
        [  -65,  23,  16, -15, -56, -34,   2,  13,
            29,  -1, -20,  -7,  -8,  -4, -38, -29,
            -9,  24,   2, -16, -20,   6,  22, -22,
           -17, -20, -12, -27, -30, -25, -14, -36,
           -49,  -1, -27, -39, -46, -44, -33, -51,
           -14, -14, -22, -46, -44, -30, -15, -27,
            27,  31,  -6, -44, -31, -10,  35,  38,
            10,  66,  44, -30,  32, -13,  50,  42 ],
    ];

    private static readonly int[][] EgPstSource =
    [
        // Pawn — promotion race dominates; advanced pawns are huge.
        [    0,   0,   0,   0,   0,   0,   0,   0,
           164, 147, 132, 100, 117, 112, 152, 183,
            50,  46,  25,   7,  -4,  -7,  22,  34,
            21,   8,  -4, -22, -18, -13,   1,   6,
            11,   5,  -8, -15, -12, -14,  -6,  -5,
             1,   0, -10,  -8,  -6, -10, -14, -13,
            13,  -2,   5,  -5,   4,  -9, -13, -11,
             0,   0,   0,   0,   0,   0,   0,   0 ],
        // Knight — still prefers the centre, but values are compressed.
        [  -37, -30,   0, -24, -36, -15, -61, -86,
           -18,   2, -17,  -2,   2, -27, -20, -47,
           -20, -11,  12,  13,  -8, -10, -19, -45,
           -12,   5,  28,  23,  26,  10,   9, -17,
           -12,  -3,  22,  30,  20,  20,   9, -17,
           -16,   6,   8,  18,  12,   4, -15, -15,
           -30, -15,  -1,   2,   3, -12, -19, -40,
            -6, -45, -15,  -5, -16, -11, -49, -63 ],
        // Bishop — long diagonals remain preferable.
        [  -11, -16,   6,  -1,   3,  -2,  -4, -22,
            -3,  -5,   3,  -6, -10,  -5,  -8,  -4,
             4, -10,  -2, -11, -12, -14,  -4,   4,
            -2,   3,   4,  -7,  -2,  -3,  -5,   2,
            -5,  -3,   3,   1, -10,  -1,  -7,  -6,
            -6,  -6,   2,  -1,   5, -11,  -3, -11,
            -6, -18,  -9,  -3,  -2,  -9, -16, -24,
            -9,  -3, -13,  -1,  -2,  -7,   3,  -9 ],
        // Rook — activity matters more than any one square.
        [   19,  14,  23,  11,  13,  24,  17,  19,
            16,  21,   9,   3, -12,   3,  21,  11,
            17,  17,  13,  11,  12,  -1,  -5,   7,
            18,  14,  19,   5,   8,   5,   7,  18,
            15,  15,  18,   6,   3,   2,  -4,   3,
             8,  10,   3,   5,  -3,  -8,  -3,  -5,
             8,   0,   8,   8,  -5,  -5,  -7,  12,
             7,  10,  11,   7,   3,  -3,  12,  -8 ],
        // Queen — rewards centralization in simplified positions.
        [   25,  52,  60,  55,  14,  11, -12,  58,
            -1,  49,  66,  71, 118,  35,  46,  17,
            -9,  31,  43,  87,  83,  45,  37,  15,
            33,  44,  54,  73,  90,  68,  85,  62,
             2,  66,  49,  81,  60,  61,  71,  51,
            18, -11,  43,  32,  39,  47,  37,  43,
            12,   7,  -6,   6,   3,  -6, -24, -18,
           -19,  -2,   6, -27,  24, -16,   7, -13 ],
        // King (endgame) — march to the centre to support pawns.
        [  -74, -47, -33, -26,   2,  25, -11, -26,
           -35,   4,  10,   9,  11,  26,  23,  21,
            -9,   5,  13,   9,  13,  33,  28,   5,
           -20,  19,  19,  34,  28,  33,  26,   5,
           -15,  -8,  25,  36,  45,  27,  18,  -3,
           -21,  -3,  17,  33,  35,  24,   7,  -9,
           -35, -11,  12,  25,  26,  12,  -9, -29,
           -65, -50, -25,   1, -24,  -6, -36, -65 ],
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
    private static readonly ulong[] FileMask = new ulong[8];

    // The union of the two files adjacent to a given file. Used for
    // isolated-pawn detection and for passed-pawn blockade lanes.
    private static readonly ulong[] AdjacentFiles = new ulong[8];

    // "Does any enemy pawn stand in front of me, on my file or adjacent?"
    // If (PassedMask & enemyPawns) == 0 → this pawn is passed.
    //
    //   WhitePassedMask[sq] — squares strictly north of `sq` on the three
    //                        relevant files (own + both neighbours).
    //   BlackPassedMask[sq] — same, but strictly south.
    private static readonly ulong[] WhitePassedMask = new ulong[64];
    private static readonly ulong[] BlackPassedMask = new ulong[64];

    // Outpost masks mirror the passed-pawn idea, but only cover the two
    // adjacent files (an enemy pawn on our own file cannot kick us off the
    // outpost — it can only push past us). Used for knight outposts.
    private static readonly ulong[] WhiteOutpostMask = new ulong[64];
    private static readonly ulong[] BlackOutpostMask = new ulong[64];

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
    private static readonly int[] KingCenterTable =
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
        mg += MgTable[(k << 6) | bkSq];
        eg += EgTable[(k << 6) | bkSq];

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
        return side == White ? score : -score;
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