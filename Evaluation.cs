using static Board;
using static PieceAttacks;
using static MoveGenerator;

/// <summary>
/// Tapered evaluation with PeSTO piece-square tables and positional features.
///
/// Board square mapping:
///
///     a8=0  b8=1  c8=2  d8=3  e8=4  f8=5  g8=6  h8=7
///     a7=8  b7=9  ...                             h7=15
///     ...
///     a2=48 ...                                   h2=55
///     a1=56 b1=57 c1=58 d1=59 e1=60 f1=61 g1=62 h1=63
///
///     rank = square / 8    (0 = rank 8, 7 = rank 1)
///     file = square % 8    (0 = a-file, 7 = h-file)
///
/// Score convention:
///     Positive = White is better.
///     Negated at the end if Black is to move.
///
/// Proven features (kept):
///     ✓ Material + PST (tapered)
///     ✓ Bishop pair
///     ✓ Passed pawns
///     ✓ Isolated pawns
///     ✓ Knight + bishop mobility
///     ✓ Rook open / semi-open files
///     ✓ King open-file penalty
///     ✓ Knight outposts
///
/// Tested and rejected:
///     ✗ Doubled pawns
///     ✗ Rook on 7th rank
///     ✗ Bishop outposts
///     ✗ Connected passed pawns
///     ✗ Pawn shield king safety
/// </summary>
public static class Evaluation
{
    // ================================================================
    //  Constants
    // ================================================================

    #region Material & Phase

    private static readonly int[] MgMaterial = [82, 337, 365, 457, 1041, 0];
    private static readonly int[] EgMaterial = [90, 281, 297, 520, 940, 0];

    //                                          P  N  B  R  Q  K  p  n  b  r  q  k
    private static readonly int[] PhaseWeight = [0, 1, 1, 2, 4, 0, 0, 1, 1, 2, 4, 0];
    private const int TotalPhase = 24;

    #endregion

    #region Positional Bonuses / Penalties

    // Bishop pair
    private const int BishopPairMg = 25;
    private const int BishopPairEg = 49;

    // Mobility: bonus per square above baseline, penalty per square below
    //
    //   score += (moves - baseline) * weight
    //
    //   Knight baseline ~4, Bishop baseline ~6
    private const int KnightMobMg = 1, KnightMobEg = 0, KnightMobBase = 4;
    private const int BishopMobMg = 4, BishopMobEg = 4, BishopMobBase = 6;

    // Rook on open / semi-open file
    //
    //   Semi-open = no friendly pawns on that file
    //   Open      = no pawns at all on that file
    private const int RookSemiOpenMg = 21, RookSemiOpenEg = 15;
    private const int RookOpenMg = 54, RookOpenEg = 8;

    // Passed pawns (indexed by engine rank, see table below)
    //
    //   Engine rank:    0    1    2    3    4    5    6    7
    //   Chess rank:     8    7    6    5    4    3    2    1
    //   White pawn:   (impossible)  ←── advancing ──→  (start)
    //   Black mirror: mirroredRank = 7 - rank
    private static readonly int[] PassedMg = [0, 0, 34, 14, 11, 11, 10, 0];
    private static readonly int[] PassedEg = [0, 48, 95, 52, 26, 7, 4, 0];

    // Isolated pawn (no friendly pawn on adjacent files)
    private const int IsolatedMg = -19;
    private const int IsolatedEg = -8;

    // King on open / semi-open file (middlegame only)
    //
    //   Penalizes kings whose file (and adjacent files) lack friendly pawns.
    //
    //   Example — White king on e1, no pawns on d/e/f files:
    //     Own file (e):    -54 (open)
    //     Adjacent (d):    -24 (open)
    //     Adjacent (f):    -24 (open)
    //     Total:          -102
    private const int KingOwnOpenMg = 71, KingOwnSemiOpenMg = 18;
    private const int KingAdjacentOpenMg = 37, KingAdjacentSemiOpenMg = 13;

    private const int QueenlessKingCenterMg = 20;

    // Knight outpost (middlegame only)
    //
    //   Conditions:
    //     1) Knight on ranks 4–6 (engine rank 2–4 for White)
    //     2) Supported by a friendly pawn
    //     3) No enemy pawn on adjacent files can still advance to challenge it
    private const int KnightOutpostMg = 46;

    #endregion

    // ================================================================
    //  Piece-Square Tables (PeSTO)
    // ================================================================
    //
    //  Layout matches engine square mapping: index 0 = a8, index 63 = h1.
    //
    //  White reads PST[square] directly.
    //  Black reads PST[square ^ 56] (vertical mirror).

    #region PST Data

    private static readonly int[][] MgPst =
    [
        // Pawn
        [
           0,   0,   0,   0,   0,   0,   0,   0,
         105, 125,  83, 132,  83,  66,   0, -16,
           6,  -7,  17,   5,  35,  73,  17,  -7,
          -4,   5,   9,  24,  26,  18,   5, -18,
         -21, -16,  -1,  12,  14,  10,  -9, -24,
         -19, -21,  -4,  -9,   1,   4,  10, -11,
         -31, -15, -23, -20, -16,  21,  18, -21,
           0,   0,   0,   0,   0,   0,   0,   0
    ],
    // Knight
    [
        -181, -83, -48, -31,  97,-108,  -1,-118,
         -76, -46,  38,  53,  -4,  66,   8, -11,
         -45,  39,  34,  53,  91, 108,  66,  46,
          -8,  18,   7,  49,  38,  71,  26,  29,
         -10,  13,  18,  14,  34,  27,  32,  -2,
         -16,  -1,  18,  23,  35,  28,  34,  -7,
         -23, -38,  -3,  10,  15,  25,   5,  -3,
        -109, -15, -42, -29,  -1, -14,  -9, -13
    ],
    // Bishop
    [
         -29, -14,-129, -64, -48, -49, -14, -14,
         -32, -13, -32, -61,  18,  -1,   1, -63,
         -28,  20,  -2,  14,   8,  48,  17,  -6,
         -12,  -2,  -1,  32,  20,  15,  -1,  -5,
         -11,   3,  -3,  20,  21,  -3,   0,   2,
          -6,   9,   6,   2,   5,  23,  12,   4,
          -5,  15,   6,  -4,   5,  13,  33,   3,
         -37, -11, -12, -24, -15, -11, -42, -26
    ],
    // Rook
    [
          17,  32,   7,  52,  47,  -8,  28,  11,
          15,   8,  52,  66,  86,  70,  -8,  35,
         -17,   1,  -2,  14,  -9,  41,  69,  -3,
         -37, -24,  -5,  12,   4,  29, -10, -30,
         -41, -30, -26,  -9,  -5,  -4,  14, -24,
         -42, -25, -20, -25,  -7,   8,   0, -25,
         -39, -14, -28, -17,  -5,   7,  -5, -63,
         -11, -13,  -7,   1,   4,  13, -30, -14
    ],
    // Queen
    [
         -40, -14,  -1,   4,  93,  78,  91,  24,
         -26, -50, -17,   0, -56,  45,  -6,  43,
          -6, -17, -12,  -9,   5,  65,  43,  56,
         -31, -25, -22, -22, -10,   8,  -3,  -6,
         -10, -34, -10, -11,  -3,  -1,  -1,  -4,
         -18,   2, -11,   2,  -1,   2,  11,   2,
         -37,  -8,  11,   6,  15,  20,   4,   9,
           1, -16,  -9,  10, -16, -16, -36, -54
    ],
    // King
    [
         -65,  23,  16, -15, -56, -34,   2,  13,
          29,  -1, -20,  -7,  -8,  -4, -38, -29,
          -9,  24,   2, -16, -20,   6,  22, -22,
         -17, -20, -12, -27, -30, -25, -14, -36,
         -49,  -1, -27, -39, -46, -44, -33, -51,
         -14, -14, -22, -46, -44, -30, -15, -27,
          27,  31,  -6, -44, -31, -10,  35,  38,
          10,  66,  44, -30,  32, -13,  50,  42
    ],
];

    private static readonly int[][] EgPst =
    [
        // Pawn
        [
           0,   0,   0,   0,   0,   0,   0,   0,
         164, 147, 132, 100, 117, 112, 152, 183,
          50,  46,  25,   7,  -4,  -7,  22,  34,
          21,   8,  -4, -22, -18, -13,   1,   6,
          11,   5,  -8, -15, -12, -14,  -6,  -5,
           1,   0, -10,  -8,  -6, -10, -14, -13,
          13,  -2,   5,  -5,   4,  -9, -13, -11,
           0,   0,   0,   0,   0,   0,   0,   0
    ],
    // Knight
    [
         -37, -30,   0, -24, -36, -15, -61, -86,
         -18,   2, -17,  -2,   2, -27, -20, -47,
         -20, -11,  12,  13,  -8, -10, -19, -45,
         -12,   5,  28,  23,  26,  10,   9, -17,
         -12,  -3,  22,  30,  20,  20,   9, -17,
         -16,   6,   8,  18,  12,   4, -15, -15,
         -30, -15,  -1,   2,   3, -12, -19, -40,
          -6, -45, -15,  -5, -16, -11, -49, -63
    ],
    // Bishop
    [
         -11, -16,   6,  -1,   3,  -2,  -4, -22,
          -3,  -5,   3,  -6, -10,  -5,  -8,  -4,
           4, -10,  -2, -11, -12, -14,  -4,   4,
          -2,   3,   4,  -7,  -2,  -3,  -5,   2,
          -5,  -3,   3,   1, -10,  -1,  -7,  -6,
          -6,  -6,   2,  -1,   5, -11,  -3, -11,
          -6, -18,  -9,  -3,  -2,  -9, -16, -24,
          -9,  -3, -13,  -1,  -2,  -7,   3,  -9
    ],
    // Rook
    [
          19,  14,  23,  11,  13,  24,  17,  19,
          16,  21,   9,   3, -12,   3,  21,  11,
          17,  17,  13,  11,  12,  -1,  -5,   7,
          18,  14,  19,   5,   8,   5,   7,  18,
          15,  15,  18,   6,   3,   2,  -4,   3,
           8,  10,   3,   5,  -3,  -8,  -3,  -5,
           8,   0,   8,   8,  -5,  -5,  -7,  12,
           7,  10,  11,   7,   3,  -3,  12,  -8
    ],
    // Queen
    [
          25,  52,  60,  55,  14,  11, -12,  58,
          -1,  49,  66,  71, 118,  35,  46,  17,
          -9,  31,  43,  87,  83,  45,  37,  15,
          33,  44,  54,  73,  90,  68,  85,  62,
           2,  66,  49,  81,  60,  61,  71,  51,
          18, -11,  43,  32,  39,  47,  37,  43,
          12,   7,  -6,   6,   3,  -6, -24, -18,
         -19,  -2,   6, -27,  24, -16,   7, -13
    ],
    // King
    [
         -74, -47, -33, -26,   2,  25, -11, -26,
         -35,   4,  10,   9,  11,  26,  23,  21,
          -9,   5,  13,   9,  13,  33,  28,   5,
         -20,  19,  19,  34,  28,  33,  26,   5,
         -15,  -8,  25,  36,  45,  27,  18,  -3,
         -21,  -3,  17,  33,  35,  24,   7,  -9,
         -35, -11,  12,  25,  26,  12,  -9, -29,
         -65, -50, -25,   1, -24,  -6, -36, -65
    ],
];

    #endregion

    // ================================================================
    //  Precomputed Lookup Tables
    // ================================================================

    // Material + PST combined: MgTable[piece, square], EgTable[piece, square]
    //   White pieces 0..5, Black pieces 6..11
    private static readonly int[,] MgTable = new int[12, 64];
    private static readonly int[,] EgTable = new int[12, 64];

    //  FileMask[f]          = all 8 squares on file f
    //  AdjacentFiles[f]     = all squares on files f-1 and f+1
    //  WhitePassedMask[sq]  = same + adjacent files, ranks ahead for White
    //  BlackPassedMask[sq]  = same + adjacent files, ranks ahead for Black
    //  WhiteOutpostMask[sq] = adjacent files only, ranks ahead for White
    //  BlackOutpostMask[sq] = adjacent files only, ranks ahead for Black
    private static readonly ulong[] FileMask = new ulong[8];
    private static readonly ulong[] AdjacentFiles = new ulong[8];
    private static readonly ulong[] WhitePassedMask = new ulong[64];
    private static readonly ulong[] BlackPassedMask = new ulong[64];
    private static readonly ulong[] WhiteOutpostMask = new ulong[64];
    private static readonly ulong[] BlackOutpostMask = new ulong[64];

    // King centralization bonus table (queenless middlegame only)
    //
    //  Concentric rings from center:
    //
    //      3  3  3  3  3  3  3  3    rank 8
    //      3  2  2  2  2  2  2  3    rank 7
    //      3  2  1  1  1  1  2  3    rank 6
    //      3  2  1  0  0  1  2  3    rank 5
    //      3  2  1  0  0  1  2  3    rank 4
    //      3  2  1  1  1  1  2  3    rank 3
    //      3  2  2  2  2  2  2  3    rank 2
    //      3  3  3  3  3  3  3  3    rank 1
    //
    //  Ring 0 = innermost (best), Ring 3 = outermost (worst)
    //  Score = (3 - ring) * weight, so center = +3*weight, edge = 0
    private static readonly int[] KingCenterTable =
    [
        0, 0, 0, 0, 0, 0, 0, 0,   // rank 8 (engine rank 0) — all edge
        0, 1, 1, 1, 1, 1, 1, 0,   // rank 7 (engine rank 1)
        0, 1, 2, 2, 2, 2, 1, 0,   // rank 6 (engine rank 2)
        0, 1, 2, 3, 3, 2, 1, 0,   // rank 5 (engine rank 3)
        0, 1, 2, 3, 3, 2, 1, 0,   // rank 4 (engine rank 4)
        0, 1, 2, 2, 2, 2, 1, 0,   // rank 3 (engine rank 5)
        0, 1, 1, 1, 1, 1, 1, 0,   // rank 2 (engine rank 6)
        0, 0, 0, 0, 0, 0, 0, 0,   // rank 1 (engine rank 7) — all edge
    ];

    // ================================================================
    //  Initialization
    // ================================================================

    static Evaluation()
    {
        InitFileMasks();
        InitAdjacentFileMasks();
        InitPassedPawnMasks();
        InitOutpostMasks();
        InitMaterialPstTables();
    }

    private static void InitFileMasks()
    {
        for (int f = 0; f < 8; f++)
            for (int r = 0; r < 8; r++)
                FileMask[f] |= 1UL << (r * 8 + f);
    }

    private static void InitAdjacentFileMasks()
    {
        for (int f = 0; f < 8; f++)
        {
            if (f > 0) AdjacentFiles[f] |= FileMask[f - 1];
            if (f < 7) AdjacentFiles[f] |= FileMask[f + 1];
        }
    }

    private static void InitPassedPawnMasks()
    {
        //  For a White pawn on square s:
        //    "passed" means no Black pawn exists on [same + adjacent files]
        //    on any rank closer to Black's side (lower rank index).
        //
        //  Visual — White pawn on d4 (square 35, rank=4, file=3):
        //
        //      . . X X X . . .    rank 8  (index 0)
        //      . . X X X . . .    rank 7  (index 1)
        //      . . X X X . . .    rank 6  (index 2)
        //      . . X X X . . .    rank 5  (index 3)
        //      . . . P . . . .    rank 4  (index 4)  ← pawn here
        //      . . . . . . . .    rank 3  (index 5)
        //      . . . . . . . .    rank 2  (index 6)
        //      . . . . . . . .    rank 1  (index 7)
        //
        //  X = squares in WhitePassedMask[35]

        for (int sq = 0; sq < 64; sq++)
        {
            int file = sq % 8;
            int rank = sq / 8;

            ulong relevantFiles = FileMask[file] | AdjacentFiles[file];

            ulong aheadWhite = 0UL;
            for (int r = 0; r < rank; r++)
                for (int f = 0; f < 8; f++)
                    aheadWhite |= 1UL << (r * 8 + f);

            ulong aheadBlack = 0UL;
            for (int r = rank + 1; r < 8; r++)
                for (int f = 0; f < 8; f++)
                    aheadBlack |= 1UL << (r * 8 + f);

            WhitePassedMask[sq] = relevantFiles & aheadWhite;
            BlackPassedMask[sq] = relevantFiles & aheadBlack;
        }
    }

    private static void InitOutpostMasks()
    {
        //  Similar to passed-pawn masks but ONLY adjacent files (not same file),
        //  because pawns attack diagonally, not straight ahead.
        //
        //  Visual — White knight on d5 (square 27, rank=3, file=3):
        //
        //      . . X . X . . .    rank 8  (index 0)
        //      . . X . X . . .    rank 7  (index 1)
        //      . . X . X . . .    rank 6  (index 2)
        //      . . . N . . . .    rank 5  (index 3)  ← knight here
        //      . . . . . . . .
        //      . . . . . . . .
        //      . . . . . . . .
        //      . . . . . . . .
        //
        //  X = squares in WhiteOutpostMask[27]
        //  If a Black pawn is on any X, it can still advance and challenge d5.

        for (int sq = 0; sq < 64; sq++)
        {
            int file = sq % 8;
            int rank = sq / 8;

            ulong wMask = 0UL, bMask = 0UL;

            if (file > 0)
            {
                for (int r = 0; r < rank; r++)
                    wMask |= 1UL << (r * 8 + file - 1);

                for (int r = rank + 1; r < 8; r++)
                    bMask |= 1UL << (r * 8 + file - 1);
            }

            if (file < 7)
            {
                for (int r = 0; r < rank; r++)
                    wMask |= 1UL << (r * 8 + file + 1);

                for (int r = rank + 1; r < 8; r++)
                    bMask |= 1UL << (r * 8 + file + 1);
            }

            WhiteOutpostMask[sq] = wMask;
            BlackOutpostMask[sq] = bMask;
        }
    }

    private static void InitMaterialPstTables()
    {
        for (int piece = 0; piece < 6; piece++)
        {
            for (int sq = 0; sq < 64; sq++)
            {
                MgTable[piece, sq] = MgMaterial[piece] + MgPst[piece][sq];
                EgTable[piece, sq] = EgMaterial[piece] + EgPst[piece][sq];
                MgTable[piece + 6, sq] = MgMaterial[piece] + MgPst[piece][sq ^ 56];
                EgTable[piece + 6, sq] = EgMaterial[piece] + EgPst[piece][sq ^ 56];
            }
        }
    }

    // ================================================================
    //  Main Entry Point
    // ================================================================

    public static int Evaluate()
    {
        int mg = 0, eg = 0, phase = 0;

        // ---- Material + PST ----
        ScorePieces(P, K, +1, ref mg, ref eg, ref phase);
        ScorePieces(p, k, -1, ref mg, ref eg, ref phase);

        // ---- Positional features ----
        ScoreBishopPair(ref mg, ref eg);
        ScorePassedPawns(ref mg, ref eg);
        ScoreIsolatedPawns(ref mg, ref eg);
        ScoreMobility(ref mg, ref eg);
        ScoreRookFiles(ref mg, ref eg);
        ScoreKingExposure(ref mg);
        ScoreKnightOutposts(ref mg);
        ScoreQueenlessKingCenter(ref mg);

        // ---- Taper and return ----
        int mgPhase = Math.Min(phase, TotalPhase);
        int egPhase = TotalPhase - mgPhase;
        int score = (mg * mgPhase + eg * egPhase) / TotalPhase;

        return side == White ? score : -score;
    }

    // ================================================================
    //  Evaluation Helpers
    // ================================================================

    private static void ScorePieces(int first, int last, int sign,
        ref int mg, ref int eg, ref int phase)
    {
        for (int piece = first; piece <= last; piece++)
        {
            ulong bb = bitboards[piece];
            while (bb != 0)
            {
                int sq = BitboardOperations.GetLs1bIndex(bb);
                mg += sign * MgTable[piece, sq];
                eg += sign * EgTable[piece, sq];
                phase += PhaseWeight[piece];
                BitboardOperations.PopBit(ref bb, sq);
            }
        }
    }

    private static void ScoreBishopPair(ref int mg, ref int eg)
    {
        if (BitboardOperations.CountBits(bitboards[B]) >= 2) { mg += BishopPairMg; eg += BishopPairEg; }
        if (BitboardOperations.CountBits(bitboards[b]) >= 2) { mg -= BishopPairMg; eg -= BishopPairEg; }
    }

    private static void ScorePassedPawns(ref int mg, ref int eg)
    {
        ulong wPawns = bitboards[P], bPawns = bitboards[p];

        for (ulong bb = wPawns; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            if ((WhitePassedMask[sq] & bPawns) == 0)
            {
                int rank = sq / 8;
                mg += PassedMg[rank];
                eg += PassedEg[rank];
            }
            BitboardOperations.PopBit(ref bb, sq);
        }

        for (ulong bb = bPawns; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            if ((BlackPassedMask[sq] & wPawns) == 0)
            {
                int rank = 7 - sq / 8;
                mg -= PassedMg[rank];
                eg -= PassedEg[rank];
            }
            BitboardOperations.PopBit(ref bb, sq);
        }
    }

    private static void ScoreIsolatedPawns(ref int mg, ref int eg)
    {
        ulong wPawns = bitboards[P], bPawns = bitboards[p];

        for (ulong bb = wPawns; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            if ((AdjacentFiles[sq % 8] & wPawns) == 0) { mg += IsolatedMg; eg += IsolatedEg; }
            BitboardOperations.PopBit(ref bb, sq);
        }

        for (ulong bb = bPawns; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            if ((AdjacentFiles[sq % 8] & bPawns) == 0) { mg -= IsolatedMg; eg -= IsolatedEg; }
            BitboardOperations.PopBit(ref bb, sq);
        }
    }

    private static void ScoreMobility(ref int mg, ref int eg)
    {
        ulong wOcc = occupancies[White];
        ulong bOcc = occupancies[Black];
        ulong all = occupancies[Both];

        // White Knights
        for (ulong bb = bitboards[N]; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            int mobility = BitboardOperations.CountBits(knightAttacks[sq] & ~wOcc) - KnightMobBase;
            mg += mobility * KnightMobMg;
            eg += mobility * KnightMobEg;
            BitboardOperations.PopBit(ref bb, sq);
        }

        // Black Knights
        for (ulong bb = bitboards[n]; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            int mobility = BitboardOperations.CountBits(knightAttacks[sq] & ~bOcc) - KnightMobBase;
            mg -= mobility * KnightMobMg;
            eg -= mobility * KnightMobEg;
            BitboardOperations.PopBit(ref bb, sq);
        }

        // White Bishops
        for (ulong bb = bitboards[B]; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            int mobility = BitboardOperations.CountBits(GetBishopAttacks(sq, all) & ~wOcc) - BishopMobBase;
            mg += mobility * BishopMobMg;
            eg += mobility * BishopMobEg;
            BitboardOperations.PopBit(ref bb, sq);
        }

        // Black Bishops
        for (ulong bb = bitboards[b]; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            int mobility = BitboardOperations.CountBits(GetBishopAttacks(sq, all) & ~bOcc) - BishopMobBase;
            mg -= mobility * BishopMobMg;
            eg -= mobility * BishopMobEg;
            BitboardOperations.PopBit(ref bb, sq);
        }
    }

    private static void ScoreRookFiles(ref int mg, ref int eg)
    {
        ulong wPawns = bitboards[P], bPawns = bitboards[p];
        ulong allPawns = wPawns | bPawns;

        for (ulong bb = bitboards[R]; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            ScoreFileBonus(sq % 8, wPawns, allPawns, +1, ref mg, ref eg);
            BitboardOperations.PopBit(ref bb, sq);
        }

        for (ulong bb = bitboards[r]; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            ScoreFileBonus(sq % 8, bPawns, allPawns, -1, ref mg, ref eg);
            BitboardOperations.PopBit(ref bb, sq);
        }
    }

    private static void ScoreFileBonus(int file, ulong friendlyPawns, ulong allPawns,
        int sign, ref int mg, ref int eg)
    {
        ulong mask = FileMask[file];

        if ((friendlyPawns & mask) != 0) return;

        if ((allPawns & mask) == 0) { mg += sign * RookOpenMg; eg += sign * RookOpenEg; }
        else { mg += sign * RookSemiOpenMg; eg += sign * RookSemiOpenEg; }
    }

    private static void ScoreKingExposure(ref int mg)
    {
        ulong wPawns = bitboards[P], bPawns = bitboards[p];
        ulong allPawns = wPawns | bPawns;

        int wkFile = BitboardOperations.GetLs1bIndex(bitboards[K]) % 8;
        int bkFile = BitboardOperations.GetLs1bIndex(bitboards[k]) % 8;

        // White king
        ScoreKingFile(wkFile, wPawns, allPawns, -1, KingOwnOpenMg, KingOwnSemiOpenMg, ref mg);
        if (wkFile > 0) ScoreKingFile(wkFile - 1, wPawns, allPawns, -1, KingAdjacentOpenMg, KingAdjacentSemiOpenMg, ref mg);
        if (wkFile < 7) ScoreKingFile(wkFile + 1, wPawns, allPawns, -1, KingAdjacentOpenMg, KingAdjacentSemiOpenMg, ref mg);

        // Black king
        ScoreKingFile(bkFile, bPawns, allPawns, +1, KingOwnOpenMg, KingOwnSemiOpenMg, ref mg);
        if (bkFile > 0) ScoreKingFile(bkFile - 1, bPawns, allPawns, +1, KingAdjacentOpenMg, KingAdjacentSemiOpenMg, ref mg);
        if (bkFile < 7) ScoreKingFile(bkFile + 1, bPawns, allPawns, +1, KingAdjacentOpenMg, KingAdjacentSemiOpenMg, ref mg);
    }

    private static void ScoreKingFile(int file, ulong friendlyPawns, ulong allPawns,
        int sign, int openPenalty, int semiOpenPenalty, ref int mg)
    {
        ulong mask = FileMask[file];
        if ((friendlyPawns & mask) != 0) return;
        mg += sign * (((allPawns & mask) == 0) ? openPenalty : semiOpenPenalty);
    }

    private static void ScoreKnightOutposts(ref int mg)
    {
        ulong wPawns = bitboards[P], bPawns = bitboards[p];

        //  White knight outposts — engine ranks 2..4 (chess ranks 6..4)
        //
        //  pawnAttacks[Black, sq] gives the squares from which a BLACK pawn
        //  would attack sq. Those same squares are where a WHITE pawn must be
        //  to support sq. So:
        //
        //    supportedByWhitePawn = (pawnAttacks[Black, sq] & whitePawns) != 0
        for (ulong bb = bitboards[N]; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            int rank = sq / 8;

            if (rank >= 2 && rank <= 4
                && (pawnAttacks[Black, sq] & wPawns) != 0
                && (WhiteOutpostMask[sq] & bPawns) == 0)
            {
                mg += KnightOutpostMg;
            }

            BitboardOperations.PopBit(ref bb, sq);
        }

        //  Black knight outposts — engine ranks 3..5 (chess ranks 5..3)
        for (ulong bb = bitboards[n]; bb != 0;)
        {
            int sq = BitboardOperations.GetLs1bIndex(bb);
            int rank = sq / 8;

            if (rank >= 3 && rank <= 5
                && (pawnAttacks[White, sq] & bPawns) != 0
                && (BlackOutpostMask[sq] & wPawns) == 0)
            {
                mg -= KnightOutpostMg;
            }

            BitboardOperations.PopBit(ref bb, sq);
        }
    }

    private static void ScoreQueenlessKingCenter(ref int mg)
    {
        // Only activates when BOTH queens are off the board.
        // Uses MG score only — the EG king PST already handles king centralization
        // in pure endgames. This targets the queenless middlegame gap.
        if (bitboards[Q] != 0 || bitboards[q] != 0)
            return;

        int wkSq = BitboardOperations.GetLs1bIndex(bitboards[K]);
        int bkSq = BitboardOperations.GetLs1bIndex(bitboards[k]);

        // White king: reward centralization
        mg += KingCenterTable[wkSq] * QueenlessKingCenterMg;

        // Black king: reward centralization (subtract because positive = white better)
        mg -= KingCenterTable[bkSq] * QueenlessKingCenterMg;
    }
}