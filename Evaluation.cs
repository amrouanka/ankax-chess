using System.Runtime.CompilerServices;
using static System.Numerics.BitOperations;
using static Board;
using static MoveGenerator;
using static PieceAttacks;

public static class Evaluation
{
    #region Material & Phase

    internal static readonly int[] MgMaterial = [82, 337, 365, 477, 1025, 0];
    internal static readonly int[] EgMaterial = [94, 281, 297, 512, 936, 0];

    internal const int TotalPhase = 24;

    #endregion

    #region Positional Bonuses / Penalties

    internal const int BishopPairMg = 0, BishopPairEg = 0;

    internal const int KnightMobMg = 0, KnightMobEg = 0, KnightMobBase = 4;
    internal const int BishopMobMg = 0, BishopMobEg = 0, BishopMobBase = 6;

    internal const int RookSemiOpenMg = 0, RookSemiOpenEg = 0;
    internal const int RookOpenMg = 0, RookOpenEg = 0;

    internal static readonly int[] PassedMg = [0, 0, 0, 0, 0, 0, 0, 0];
    internal static readonly int[] PassedEg = [0, 0, 0, 0, 0, 0, 0, 0];

    internal const int IsolatedMg = 0, IsolatedEg = 0;

    internal const int KingOwnOpenMg = 0, KingOwnSemiOpenMg = 0;
    internal const int KingAdjacentOpenMg = 0, KingAdjacentSemiOpenMg = 0;

    internal const int QueenlessKingCenterMg = 0;

    internal const int KnightOutpostMg = 0;

    internal const int TempoBonus = 8;

    #endregion

    #region PST Data

    internal static readonly int[][] MgPstSource =
    [
        [    0,   0,   0,   0,   0,   0,   0,   0,
            98, 134,  61,  95,  68, 126,  34, -11,
            -6,   7,  26,  31,  65,  56,  25, -20,
           -14,  13,   6,  21,  23,  12,  17, -23,
           -27,  -2,  -5,  12,  17,   6,  10, -25,
           -26,  -4,  -4, -10,   3,   3,  33, -12,
           -35,  -1, -20, -23, -15,  24,  38, -22,
             0,   0,   0,   0,   0,   0,   0,   0 ],

        [ -167, -89, -34, -49,  61,-110, -15,-107,
           -73, -41,  72,  36,  23,  62,   7, -17,
           -47,  60,  37,  65,  84, 129,  73,  44,
            -9,  17,  19,  53,  37,  69,  18,  22,
           -13,   4,  16,  13,  28,  19,  21,  -8,
           -23,  -9,  12,  10,  19,  17,  25, -19,
           -29, -53, -12,  -3,  -1,  18, -14, -19,
          -105, -21, -58, -33, -17, -28, -19, -23 ],

        [  -29,   4,-128, -54, -42, -50,   8,  -2,
           -26,   3, -19, -42,  16,  12,  -3, -32,
            -4,  26,  21,  19,  18,  58,  34,   7,
           -13,  15,  11,  41,  25,  22,   0,   7,
           -12,   5,  10,  21,  22,  -8,   2,   0,
           -16,  13,   8,   4,   2,  19,  12,   4,
            -4,  15,  12,  -7,   2,  12,  33,   7,
           -23,  -3, -14, -37, -25,  -9, -16, -10 ],

        [   27,  32,  12,  41,  38,  -5,  45,  18,
            16,  13,  44,  61,  67,  67,   4,  30,
           -18,  14,   8,  22,   5,  54,  69,  13,
           -30, -11,  10,  30,  16,  35,  -4, -15,
           -42, -18, -12,   2,  -2,   4,   8, -25,
           -46, -25, -15, -19,   0,   5,  11, -21,
           -70, -23, -20, -13,  -6,  10,   4, -58,
           -25, -16, -14,  -2,   3,  13, -11, -19 ],

        [  -37,  11,  12,  23,  87, 102, 100,  36,
           -26, -58,   2,  10, -22,  83,   6,  53,
           -14, -13,  -2,   1,  18,  82,  68,  47,
           -22, -18, -11,  -9,   2,  24,  15,   7,
           -17, -23, -12, -14,  -9,   2,   6,   8,
           -24,  -2,  -3,  -8,  -5,  -2,  10,  -4,
           -31, -11,  -1,  -4,  -3,   1,   0,  -9,
           -26, -25, -17,  -4, -18, -37, -49, -32 ],

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
        [    0,   0,   0,   0,   0,   0,   0,   0,
           152, 126, 116,  82,  84,  86, 124, 143,
            46,  32,   6, -17, -31, -19,   1,  15,
            16,   1, -11, -38, -32, -32, -14,  -9,
             2,  -6, -23, -28, -26, -27, -22, -18,
            -7, -16, -21, -29, -18, -21, -28, -25,
             3, -11,  -8, -17,  -3, -10, -23, -25,
             0,   0,   0,   0,   0,   0,   0,   0 ],

        [  -31, -12,  12,   0, -13,  13, -48, -78,
           -13,   6,  -4,  18,  22, -16,  -8, -35,
           -12,  11,  20,  21,   8,  14,   0, -25,
            -5,  13,  30,  45,  46,  31,  27,  -2,
            -7,  11,  29,  36,  40,  34,  18,  -1,
           -32,   3,   5,  24,  20,   4,  -4, -28,
           -36,  -8, -15,   2,   8, -18, -21, -34,
           -18, -58, -23, -11, -18, -20, -51, -73 ],

        [   11,   4,  24,  16,  13,   8,   1, -11,
             0,   8,   8,  12,   3,   7,   5,   1,
             4,   4,  -3,  -6,  -2,   6,   7,   4,
             1,   6,   0,  -6,   3,  -2,  14,   7,
            -4,  -2,   8,   0,  -6,   2,  -1,  -4,
            -8,   1,  -4,  -3,   2, -10, -11, -11,
            -7, -26, -15, -11,  -8, -21, -22, -43,
           -17,  -6, -30,  -9,  -9, -15, -13, -20 ],

        [   23,  19,  31,  20,  24,  37,  27,  31,
            22,  27,  16,  10,   1,   2,  24,  13,
            20,  10,  16,   7,  13,   8,  -8,  13,
            15,  13,  15,   5,   8,   6,   8,  16,
            -1,  13,  13,   5,   6,   7,  -4,  -5,
           -23,  -6, -10, -15, -13, -16, -17, -23,
           -32, -31, -21, -28, -30, -33, -32, -21,
           -20, -18, -12, -20, -18, -10, -17, -27 ],

        [   16,  36,  47,  49,  11,  12, -16,  46,
            -5,  46,  46,  64, 117,  37,  34,   7,
           -20,  -2,  26,  62,  75,  45,  37,  12,
           -22,  21,  30,  61,  80,  68,  76,  53,
           -38,  18,  17,  53,  45,  31,  23,  24,
           -40, -25,  -5, -14, -19,  -7, -28, -17,
           -49, -50, -67, -49, -58, -82, -96, -65,
           -63, -64, -59, -65, -55, -76, -49, -33 ],

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