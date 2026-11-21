using System.Runtime.CompilerServices;
using static System.Numerics.BitOperations;
using static Board;
using static MoveGenerator;
using static PieceAttacks;

public static class Evaluation
{
    #region Material & Phase

    // Base material values in centipawns, indexed by piece type:
    //   0=Pawn  1=Knight  2=Bishop  3=Rook  4=Queen  5=King
    // King is 0 because it's never captured; its "value" is implicit in game
    // termination, not in evaluation.
    internal static readonly int[] MgMaterial = [56, 182, 191, 268, 719, 0];
    internal static readonly int[] EgMaterial = [69, 152, 161, 280, 484, 0];

    // Starting position phase: 2 sides × (2×Knight + 2×Bishop + 2×Rook + 1×Queen)
    //   = 2 × (2·1 + 2·1 + 2·2 + 1·4) = 24.
    // Phase is accumulated inline during evaluation (Knight/Bishop += 1,
    // Rook += 2, Queen += 4) rather than through a lookup table.
    internal const int TotalPhase = 24;

    #endregion

    #region Positional Bonuses / Penalties

    // Having both bishops is worth a small bonus because they cover
    // complementary colour complexes.
    internal const int BishopPairMg = 4, BishopPairEg = 32;

    // Mobility = (number of safe destination squares) − (a baseline).
    // Subtracting a baseline keeps the "average" piece near zero so the
    // mobility term doesn't silently inflate material values.
    internal const int KnightMobMg = 0, KnightMobEg = 0, KnightMobBase = 4;
    internal const int BishopMobMg = 1, BishopMobEg = 3, BishopMobBase = 6;

    // Rook on a file with no friendly pawn:
    //   semi-open = only enemy pawns on it (good attacking lane)
    //   open      = no pawns at all (best — rook sees the whole file)
    internal const int RookSemiOpenMg = 8, RookSemiOpenEg = 4;
    internal const int RookOpenMg = 18, RookOpenEg = 4;

    // Passed pawn bonus indexed by squares left to promotion (0..7):
    //   index 1 = one step from promoting, index 6 = still on its start square.
    // A pawn gets this if no enemy pawn can block or capture it on its way
    // to promotion. The closer to promotion, the bigger the bonus — much
    // larger in the endgame where promotion threats dominate.
    internal static readonly int[] PassedMg = [0, -29, 31, 14, 1, -1, 2, 0];
    internal static readonly int[] PassedEg = [0, -80, 35, 23, 17, 9, 7, 0];

    // A pawn with no friendly pawn on either adjacent file. Weak because it
    // can never be defended by another pawn.
    internal const int IsolatedMg = -7, IsolatedEg = -7;

    // King safety: penalties for open/semi-open files near the king. Only
    // applied in the middlegame — in the endgame the king wants to be active.
    internal const int KingOwnOpenMg = 38, KingOwnSemiOpenMg = 11;
    internal const int KingAdjacentOpenMg = 18, KingAdjacentSemiOpenMg = 9;

    // Once both queens are gone, encourage the king toward the centre so it
    // can help its pawns promote.
    internal const int QueenlessKingCenterMg = 25;

    // Knight sitting on a square where:
    //   • it's protected by a friendly pawn
    //   • it can't be kicked by any enemy pawn on adjacent files
    // Outposts are classical strong squares (think c5/d5/e5/f5 for White).
    internal const int KnightOutpostMg = 17;

    // Tempo bonus: static advantage awarded to the side to move
    internal const int TempoBonus = 5;

    #endregion

    // =========================================================================
    //  SECTION 2 — Piece–square tables (PeSTO)
    //
    //  One 64-entry table per piece type, written from White's perspective
    //  with square a8 at index 0 and h1 at index 63 — exactly matching the
    //  board's visual layout when you read top-to-bottom, left-to-right.
    //
    //  These are "source" tables. At startup we fold them with the material
    //  values into a single flat lookup (PstScoreTable) for fast access.
    // =========================================================================

    #region PST Data

    internal static readonly int[][] MgPstSource =
    [
        // Pawn — the back rank is zero because pawns never stand there.
        [    0,   0,   0,   0,   0,   0,   0,   0,
           110, 109, 106, 104, 110,  93,  10,  35,
           -19, -11,  -4,  -8,  -2,  21,  -4, -18,
           -21, -18, -17, -11,  -3,  -5, -12, -21,
           -25, -24, -20, -18, -14, -15, -18, -24,
           -27, -29, -25, -24, -19, -17, -17, -21,
           -29, -30, -30, -29, -28, -15, -11, -24,
             0,   0,   0,   0,   0,   0,   0,   0 ],
        // Knight — rim is dim; centre squares are prized.
        [ -121, -59, -52,   5,  30, -48,  64, -88,
            -4,  -5,  19,  27,  31,  46,  -6,  24,
           -10,   1,  18,  25,  41,  40,  25,  19,
            -3,   3,  13,  17,  14,  28,  15,  18,
           -10,   1,   7,   7,  12,  10,  18,   0,
           -12,  -2,   1,   9,  10,   5,   4,  -5,
           -17, -16,  -7,  -2,  -4,   0,  -5,  -5,
           -32, -14, -23, -15, -10,  -6, -11, -32 ],
        // Bishop — long diagonals and fianchetto squares.
        [  -35, -16, -57, -37, -27, -70,  41, -18,
           -14,  -1,  -1, -16,  -4,  11, -14,   4,
             0,   7,  15,  13,  25,  15,  25,   9,
            -4,   9,   8,  24,  14,  17,   7,   4,
            -1,   3,   5,  10,  11,   1,   5,   3,
            -2,   5,   4,   4,   3,   4,   5,   8,
             3,   5,   6,  -2,   1,   2,  11,   7,
             7,   4,  -6, -13, -10,  -2,  -3,   2 ],
        // Rook — likes the 7th rank and central files.
        [   15,  22,  15,  21,  25,  44,  66,  53,
            -4,  -6,   9,  19,  18,  40,  27,  31,
           -11,   5,   2,  13,  24,  39,  45,  20,
           -16, -10,  -6,   4,   1,   9,  12,   6,
           -24, -22, -22, -17, -18, -13,  -2, -11,
           -26, -22, -25, -22, -19, -18,  -3, -14,
           -36, -23, -22, -20, -19, -12,  -8, -27,
           -18, -18, -17, -13, -14, -12,  -4, -13 ],
        // Queen — modest PST; mobility and tactics dominate its value.
        [  -20,   2,  12,  15,  29,  34,  34,  36,
           -17, -27, -10, -19, -10,  34, -18,  25,
           -13,  -7,  -9,  -3,  14,  41,  48,  19,
            -7,  -8,  -9,  -4,  -2,   5,  11,   9,
            -7,  -5,  -7,  -7,  -6,   0,   1,   6,
            -9,  -4,  -3,  -5,  -4,  -4,   3,   0,
           -11,  -4,  -2,  -4,  -3,   1,   3, -11,
            -6,  -9,  -8,  -6,  -6, -14, -23, -27 ],
        // King (middlegame) — castled corners are safe; the centre is fatal.
        [  -26, 102, 101,  59,  25,  34,  42,  18,
            42,  71,  91,  93,  62,  66,  39, -32,
            49,  78,  70,  32,  43,  53,  65,   6,
            33,  52,  36,  -1,   2,  27,  25,   8,
            11,  20,  18, -32, -23,  -7,   9, -18,
            -9,  -4, -19, -21, -24, -28,  -2, -13,
            -2,  -8, -17, -31, -23, -26,   2,   3,
            -4,  11,  -3, -27, -14, -35,   5,   8 ]
    ];

    internal static readonly int[][] EgPstSource =
    [
        // Pawn — promotion race dominates; advanced pawns are huge.
        [    0,   0,   0,   0,   0,   0,   0,   0,
           119, 114, 111, 100,  94,  98, 124, 121,
             1,  -5, -13, -23, -28, -24, -18, -14,
           -12, -17, -21, -30, -29, -29, -24, -23,
           -18, -20, -26, -27, -27, -26, -27, -26,
           -21, -23, -25, -26, -23, -24, -28, -28,
           -17, -21, -19, -20, -16, -19, -26, -30,
             0,   0,   0,   0,   0,   0,   0,   0 ],
        // Knight — still prefers the centre, but values are compressed.
        [  -15,   1,  10,  -1,  -3,  19, -16, -27,
           -16,  -3,   1,  10,   7,  -7,   0, -19,
            -7,   7,  15,  15,  10,  15,   4,  -8,
            -6,   8,  17,  22,  24,  17,  15,  -2,
            -5,   8,  18,  20,  21,  20,   9,  -1,
           -20,   2,   8,  15,  14,   8,   4, -13,
           -16,  -3,  -2,   3,   6,  -3,  -6, -13,
           -35, -32, -11,  -6,  -6, -10, -22, -41 ],
        // Bishop — long diagonals remain preferable.
        [   13,   9,  12,  11,   8,  16,  -5,   5,
             2,   4,   5,   7,   5,   2,   7,  -1,
             2,   3,   0,  -1,  -2,   6,   2,   3,
             0,   3,   1,  -1,   3,  -1,   8,   3,
            -3,   0,   4,   2,   0,   3,   2,  -2,
            -4,   0,   0,   0,   2,  -1,  -2,  -5,
            -4, -11,  -6,  -3,  -3,  -6,  -8, -19,
           -13,  -4, -13,  -3,  -3,  -6,  -7, -11 ],
        // Rook — activity matters more than any one square.
        [    6,   5,   9,   6,   8,   3,  -3,   1,
            11,  13,   9,   8,   8,  -4,   1,   0,
             8,   3,   7,   2,  -2,  -2,  -9,   0,
             5,   6,   7,   4,   5,   2,  -2,   0,
             1,   6,   7,   5,   4,   5,   0,  -2,
            -9,  -2,  -2,  -3,  -2,  -2,  -5, -10,
            -8, -10,  -6,  -8,  -7,  -9, -11,  -9,
            -8,  -7,  -4,  -6,  -6,  -5, -11, -14 ],
        // Queen — rewards centralization in simplified positions.
        [   13,   3,   6,  10,  10,  22,  18,  11,
             2,  17,  16,  33,  46,  23,  49,   9,
           -10,  -9,   8,  18,  26,  19,  13,  22,
           -22,  -1,   5,  16,  26,  33,  30,  21,
           -24,  -4,  -1,  13,  11,   7,   5,   4,
           -25, -19, -10, -13, -14,  -8, -16, -12,
           -25, -27, -34, -25, -28, -42, -46,  -9,
           -27, -34, -32, -33, -33, -30, -10,  35 ],
        // King (endgame) — march to the centre to support pawns.
        [  -74, -45, -32, -22, -19, -22, -19, -56,
           -22,  -9, -13, -19, -13,  -7,   7,  -6,
            -7,   1,  -3,   0,  -3,   3,   5,   0,
            -5,   1,   6,   6,   5,   3,   5,  -3,
           -10,   3,   5,  12,   9,   6,   2,  -3,
            -2,   2,   4,   9,   8,   4,  -3,  -5,
             2,  -1,   4,   6,   4,   4,  -8, -12,
           -16,  -8,   0,  -4, -15,   2, -11, -36 ]
    ];
    #endregion

    // SWAR packed (mg, eg) constants: (eg << 16) + mg
    private const int BishopPairScore = (BishopPairEg << 16) + BishopPairMg;
    private const int KnightMobScore = (KnightMobEg << 16) + KnightMobMg;
    private const int BishopMobScore = (BishopMobEg << 16) + BishopMobMg;
    private const int RookSemiOpenScore = (RookSemiOpenEg << 16) + RookSemiOpenMg;
    private const int RookOpenScore = (RookOpenEg << 16) + RookOpenMg;
    private const int IsolatedScore = (IsolatedEg << 16) + IsolatedMg;

    private static readonly int[] PstScoreTable = new int[12 << 6];
    private static readonly int[] PassedScore = new int[8];

    internal static readonly ulong[] FileMask = new ulong[8];
    internal static readonly ulong[] AdjacentFiles = new ulong[8];

    internal static readonly ulong[] WhitePassedMask = new ulong[64];
    internal static readonly ulong[] BlackPassedMask = new ulong[64];

    internal static readonly ulong[] WhiteOutpostMask = new ulong[64];
    internal static readonly ulong[] BlackOutpostMask = new ulong[64];

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

    static Evaluation()
    {
        InitFileMasks();
        InitAdjacentFileMasks();
        InitPassedPawnMasks();
        InitOutpostMasks();
        InitMaterialPstTables();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PackScore(int mg, int eg) => (eg << 16) + mg;

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
        for (int sq = 0; sq < 64; sq++)
        {
            int file = sq & 7;
            int rank = sq >> 3;
            ulong relevantFiles = FileMask[file] | AdjacentFiles[file];

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

    private static void InitMaterialPstTables()
    {
        for (int piece = 0; piece < 6; piece++)
        {
            int whiteBase = piece << 6;
            int blackBase = (piece + 6) << 6;
            int mgMat = MgMaterial[piece];
            int egMat = EgMaterial[piece];
            int[] mgPst = MgPstSource[piece];
            int[] egPst = EgPstSource[piece];

            for (int sq = 0; sq < 64; sq++)
            {
                PstScoreTable[whiteBase + sq] = PackScore(mgMat + mgPst[sq], egMat + egPst[sq]);
                PstScoreTable[blackBase + sq] = PackScore(mgMat + mgPst[sq ^ 56], egMat + egPst[sq ^ 56]);
            }
        }

        for (int r = 0; r < 8; r++)
        {
            PassedScore[r] = PackScore(PassedMg[r], PassedEg[r]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int Evaluate()
    {
        ulong[] bb = bitboards;
        ulong[] occ = occupancies;

        ulong wP = bb[P], wN = bb[N], wB = bb[B], wR = bb[R], wQ = bb[Q], wK = bb[K];
        ulong bP = bb[p], bN = bb[n], bBi = bb[b], bR = bb[r], bQ = bb[q], bK = bb[k];

        ulong wOcc = occ[White];
        ulong bOcc = occ[Black];
        ulong allOcc = occ[Both];
        ulong allPawns = wP | bP;

        int scorePacked = 0;
        int phase = 0;

        // White Pawns
        for (ulong x = wP; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            scorePacked += PstScoreTable[(P << 6) | sq];

            if ((WhitePassedMask[sq] & bP) == 0)
                scorePacked += PassedScore[sq >> 3];

            if ((AdjacentFiles[sq & 7] & wP) == 0)
                scorePacked += IsolatedScore;
        }

        // Black Pawns
        for (ulong x = bP; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            scorePacked -= PstScoreTable[(p << 6) | sq];

            if ((BlackPassedMask[sq] & wP) == 0)
                scorePacked -= PassedScore[7 - (sq >> 3)];

            if ((AdjacentFiles[sq & 7] & bP) == 0)
                scorePacked -= IsolatedScore;
        }

        // White Knights
        for (ulong x = wN; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            scorePacked += PstScoreTable[(N << 6) | sq];
            phase += 1;

            int mobility = PopCount(knightAttacks[sq] & ~wOcc) - KnightMobBase;
            scorePacked += mobility * KnightMobScore;

            int rank = sq >> 3;
            if ((uint)(rank - 2) <= 2
                && (WhiteOutpostMask[sq] & bP) == 0
                && (pawnAttacks[Black, sq] & wP) != 0)
            {
                scorePacked += KnightOutpostMg;
            }
        }

        // Black Knights
        for (ulong x = bN; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            scorePacked -= PstScoreTable[(n << 6) | sq];
            phase += 1;

            int mobility = PopCount(knightAttacks[sq] & ~bOcc) - KnightMobBase;
            scorePacked -= mobility * KnightMobScore;

            int rank = sq >> 3;
            if ((uint)(rank - 3) <= 2
                && (BlackOutpostMask[sq] & wP) == 0
                && (pawnAttacks[White, sq] & bP) != 0)
            {
                scorePacked -= KnightOutpostMg;
            }
        }

        // White Bishops
        if ((wB & (wB - 1)) != 0) scorePacked += BishopPairScore;
        for (ulong x = wB; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            scorePacked += PstScoreTable[(B << 6) | sq];
            phase += 1;

            int mobility = PopCount(GetBishopAttacks(sq, allOcc) & ~wOcc) - BishopMobBase;
            scorePacked += mobility * BishopMobScore;
        }

        // Black Bishops
        if ((bBi & (bBi - 1)) != 0) scorePacked -= BishopPairScore;
        for (ulong x = bBi; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            scorePacked -= PstScoreTable[(b << 6) | sq];
            phase += 1;

            int mobility = PopCount(GetBishopAttacks(sq, allOcc) & ~bOcc) - BishopMobBase;
            scorePacked -= mobility * BishopMobScore;
        }

        // White Rooks
        for (ulong x = wR; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            scorePacked += PstScoreTable[(R << 6) | sq];
            phase += 2;

            ulong fileMask = FileMask[sq & 7];
            if ((wP & fileMask) == 0)
            {
                scorePacked += ((allPawns & fileMask) == 0) ? RookOpenScore : RookSemiOpenScore;
            }
        }

        // Black Rooks
        for (ulong x = bR; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            scorePacked -= PstScoreTable[(r << 6) | sq];
            phase += 2;

            ulong fileMask = FileMask[sq & 7];
            if ((bP & fileMask) == 0)
            {
                scorePacked -= ((allPawns & fileMask) == 0) ? RookOpenScore : RookSemiOpenScore;
            }
        }

        // White Queens
        for (ulong x = wQ; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            scorePacked += PstScoreTable[(Q << 6) | sq];
            phase += 4;
        }

        // Black Queens
        for (ulong x = bQ; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            scorePacked -= PstScoreTable[(q << 6) | sq];
            phase += 4;
        }

        // Kings
        int wkSq = TrailingZeroCount(wK);
        int bkSq = TrailingZeroCount(bK);
        scorePacked += PstScoreTable[(K << 6) | wkSq] - PstScoreTable[(k << 6) | bkSq];

        // Unpack SWAR (mg, eg)
        int mg = (short)scorePacked;
        int eg = (scorePacked + 0x8000) >> 16;

        mg -= ScoreKingFileExposure(wkSq & 7, wP, allPawns);
        mg += ScoreKingFileExposure(bkSq & 7, bP, allPawns);

        if ((wQ | bQ) == 0)
        {
            mg += (KingCenterTable[wkSq] - KingCenterTable[bkSq]) * QueenlessKingCenterMg;
        }

        int mgPhase = phase > TotalPhase ? TotalPhase : phase;
        int egPhase = TotalPhase - mgPhase;
        int score = (mg * mgPhase + eg * egPhase) / TotalPhase;

        return (side == White ? score : -score) + TempoBonus;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScoreKingFileExposure(int kingFile, ulong friendlyPawns, ulong allPawns)
    {
        int penalty = ScoreOneKingFile(kingFile, friendlyPawns, allPawns, KingOwnOpenMg, KingOwnSemiOpenMg);

        if (kingFile > 0)
            penalty += ScoreOneKingFile(kingFile - 1, friendlyPawns, allPawns, KingAdjacentOpenMg, KingAdjacentSemiOpenMg);
        if (kingFile < 7)
            penalty += ScoreOneKingFile(kingFile + 1, friendlyPawns, allPawns, KingAdjacentOpenMg, KingAdjacentSemiOpenMg);

        return penalty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScoreOneKingFile(int file, ulong friendlyPawns, ulong allPawns,
        int openPenalty, int semiOpenPenalty)
    {
        ulong mask = FileMask[file];
        if ((friendlyPawns & mask) != 0) return 0;
        return ((allPawns & mask) == 0) ? openPenalty : semiOpenPenalty;
    }

}