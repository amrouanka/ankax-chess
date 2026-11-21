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
    internal static readonly int[] MgMaterial = [90, 337, 361, 497, 1047, 0];
    internal static readonly int[] EgMaterial = [135, 307, 335, 584, 986, 0];

    // Starting position phase: 2 sides × (2×Knight + 2×Bishop + 2×Rook + 1×Queen)
    //   = 2 × (2·1 + 2·1 + 2·2 + 1·4) = 24.
    // Phase is accumulated inline during evaluation (Knight/Bishop += 1,
    // Rook += 2, Queen += 4) rather than through a lookup table.
    internal const int TotalPhase = 24;

    #endregion

    #region Positional Bonuses / Penalties

    // Having both bishops is worth a small bonus because they cover
    // complementary colour complexes.
    internal const int BishopPairMg = 14, BishopPairEg = 84;

    // Mobility = (number of safe destination squares) − (a baseline).
    // Subtracting a baseline keeps the "average" piece near zero so the
    // mobility term doesn't silently inflate material values.
    internal const int KnightMobMg = -2, KnightMobEg = 3, KnightMobBase = 4;
    internal const int BishopMobMg = 4, BishopMobEg = 9, BishopMobBase = 6;

    // Rook on a file with no friendly pawn:
    //   semi-open = only enemy pawns on it (good attacking lane)
    //   open      = no pawns at all (best — rook sees the whole file)
    internal const int RookSemiOpenMg = 19, RookSemiOpenEg = 20;
    internal const int RookOpenMg = 42, RookOpenEg = 27;

    // Passed pawn bonus indexed by squares left to promotion (0..7):
    //   index 1 = one step from promoting, index 6 = still on its start square.
    // A pawn gets this if no enemy pawn can block or capture it on its way
    // to promotion. The closer to promotion, the bigger the bonus — much
    // larger in the endgame where promotion threats dominate.
    internal static readonly int[] PassedMg = [0, 155, 84, 37, -3, -10, -3, 0];
    internal static readonly int[] PassedEg = [0, 143, 75, 58, 43, 23, 17, 0];

    // A pawn with no friendly pawn on either adjacent file. Weak because it
    // can never be defended by another pawn.
    internal const int IsolatedMg = -16, IsolatedEg = -17;

    // King safety: penalties for open/semi-open files near the king. Only
    // applied in the middlegame — in the endgame the king wants to be active.
    internal const int KingOwnOpenMg = 100, KingOwnSemiOpenMg = 26;
    internal const int KingAdjacentOpenMg = 47, KingAdjacentSemiOpenMg = 21;

    // Once both queens are gone, encourage the king toward the centre so it
    // can help its pawns promote.
    internal const int QueenlessKingCenterMg = 59;

    // Knight sitting on a square where:
    //   • it's protected by a friendly pawn
    //   • it can't be kicked by any enemy pawn on adjacent files
    // Outposts are classical strong squares (think c5/d5/e5/f5 for White).
    internal const int KnightOutpostMg = 46;

    // Tempo bonus: static advantage awarded to the side to move
    internal const int TempoBonus = 13;

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
            60,  71,  18,  43,  17,  18,-128, -96,
            -4,  21,  39,  24,  41, 113,  40,  -3,
            -7,   4,   3,  18,  40,  35,  16, -11,
           -18, -15,  -8,  -5,   5,  -1,  -5, -18,
           -22, -33, -25, -22,  -9,  -6,  -5, -12,
           -29, -36, -40, -38, -36,   0,   9, -21,
             0,   0,   0,   0,   0,   0,   0,   0 ],
        // Knight — rim is dim; centre squares are prized.
        [ -182, -78, -54,  -8,  77, -60,   4,-107,
           -14,  -4,  52,  73,  77, 107,   2,  36,
           -25,   1,  52,  66, 108,  98,  64,  46,
           -10,   4,  35,  46,  37,  74,  36,  45,
           -32,  -2,  16,  15,  26,  24,  46,  -4,
           -39,  -9,  -1,  21,  22,   9,   4, -21,
           -52, -50, -24, -11, -17,  -6, -19, -22,
          -116, -44, -71, -46, -32, -25, -36, -88 ],
        // Bishop — long diagonals and fianchetto squares.
        [  -48,   4, -86, -45, -34, -89,  29,  -6,
           -38,  -5,  -4, -26,  -5,  25, -30,   6,
            -4,  17,  37,  31,  59,  41,  64,  17,
           -15,  16,  18,  58,  32,  43,  10,   7,
           -10,   0,   5,  18,  21,  -7,   6,  -1,
           -14,   4,   1,   1,  -1,   2,   5,  11,
             0,   5,   9, -14,  -8,  -2,  21,  14,
             8,   4, -25, -49, -40, -16, -21,  -5 ],
        // Rook — likes the 7th rank and central files.
        [   27,  42, -13,   4,   9,  37,  83,  93,
             5,  -3,  34,  55,  58,  89,  63,  86,
           -14,  29,  22,  49,  70, 101, 110,  56,
           -28, -14,   0,  27,  17,  35,  38,  30,
           -52, -50, -49, -38, -40, -30,   3, -18,
           -57, -51, -59, -49, -43, -42,  -1, -28,
           -84, -51, -47, -42, -41, -20, -10, -68,
           -36, -37, -32, -24, -25, -21,  -5, -25 ],
        // Queen — modest PST; mobility and tactics dominate its value.
        [  -18,   6,  28,  19,  69,  90,  87,  80,
           -38, -63, -18, -26,  -3,  81,  -6,  72,
           -31, -16, -16,  -7,  29,  95,  95,  50,
           -18, -25, -22, -16,  -9,  16,  25,  19,
           -22, -15, -24, -26, -25,  -5,   0,  15,
           -24, -15, -13, -19, -15, -14,   8,   4,
           -26, -15, -11, -15, -13,   1,   9, -17,
           -20, -26, -25, -20, -19, -43, -57, -35 ],
        // King (middlegame) — castled corners are safe; the centre is fatal.
        [  -66,  32,  26,  -3, -51, -33,   5,   7,
            31,  22,   9,  13,   5,  16, -16, -36,
             7,  69,  47,   8,   8,  43,  64, -20,
             6,  34,  32, -16, -11,  20,  32, -19,
           -26,  36,  25, -55, -37,  -7,  31, -47,
           -22, -12, -42, -47, -44, -53,   8, -24,
            -1, -18, -44, -78, -57, -59,  10,  17,
            -8,  37,  -3, -68, -31, -85,  20,  29 ]
    ];

    internal static readonly int[][] EgPstSource =
    [
        // Pawn — promotion race dominates; advanced pawns are huge.
        [    0,   0,   0,   0,   0,   0,   0,   0,
            34,  11,  17, -21, -24, -38,   9,  11,
            76,  57,  35,  11,  -5,   3,  18,  30,
            29,  13,   1, -27, -24, -23, -10,  -7,
            11,   5, -17, -21, -20, -18, -18, -16,
             4,  -6, -12, -19, -11, -11, -21, -22,
            18,   3,   4,  -3,   8,   5, -12, -20,
             0,   0,   0,   0,   0,   0,   0,   0 ],
        // Knight — still prefers the centre, but values are compressed.
        [  -35,   5,  21,  17,   5,  40, -26, -92,
           -31,   0,   5,  33,  26, -12,   3, -34,
           -12,  24,  36,  38,  26,  38,  17,  -9,
            -6,  19,  40,  58,  58,  44,  41,   6,
            -9,  20,  41,  48,  51,  46,  26,   3,
           -53,   4,  10,  31,  27,  10,   5, -33,
           -44,  -7, -15,   3,  11, -19, -21, -37,
           -74, -81, -35, -20, -23, -33, -64,-118 ],
        // Bishop — long diagonals remain preferable.
        [   27,  16,  28,  26,  20,  24,   5,   7,
             2,  12,  14,  18,  15,  11,  18,  -2,
             6,   9,   1,   0,  -1,  20,  11,  11,
             1,   9,   2,  -3,   7,   1,  24,  13,
            -4,  -1,  11,   2,  -3,   7,   3,   0,
            -8,   3,  -2,  -1,   3,  -9, -12, -10,
            -8, -30, -18, -13, -10, -23, -25, -54,
           -30,  -9, -35, -11, -11, -19, -24, -31 ],
        // Rook — activity matters more than any one square.
        [   31,  29,  47,  39,  44,  42,  31,  30,
            31,  37,  28,  25,  23,   6,  19,  10,
            26,  12,  21,   7,   0,   7,  -7,  12,
            17,  17,  20,   9,  11,  10,   4,   8,
             1,  16,  16,   8,   8,  11,   0,  -4,
           -31, -10, -13, -19, -19, -18, -24, -32,
           -39, -41, -30, -38, -39, -45, -46, -35,
           -26, -25, -18, -27, -27, -17, -30, -40 ],
        // Queen — rewards centralization in simplified positions.
        [   15,  34,  44,  62,  56,  57,  41,  37,
            13,  62,  55,  89, 113,  63,  53,  17,
            -9,   1,  36,  69,  83,  66,  44,  47,
           -34,  24,  34,  72,  95,  89,  80,  58,
           -46,   3,  17,  57,  52,  34,  19,  16,
           -68, -36, -13, -22, -29, -15, -49, -59,
           -84, -68, -84, -61, -73,-130,-142, -88,
           -82,-100, -91, -82,-105,-106, -96, -62 ],
        // King (endgame) — march to the centre to support pawns.
        [  -92, -15,   1,  15,   8,   5,   9, -49,
           -19,  32,  34,  18,  20,  26,  54, -11,
            18,  42,  36,  33,  27,  42,  45,  16,
            10,  33,  40,  35,  33,  33,  33,   9,
           -12,  20,  29,  40,  32,  24,  10,  -5,
            -7,   7,  15,  29,  24,  11, -10, -15,
             6,  -2,  12,  16,  11,  11, -20, -32,
           -48, -22,  -2, -15, -44,   0, -30, -94 ]
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