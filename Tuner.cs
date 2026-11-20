using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using static System.Numerics.BitOperations;
using static Board;
using static MoveGenerator;
using static PieceAttacks;

/// <summary>
/// Ultra-fast Texel evaluation parameter tuner for AnkaX.
/// Fits Material, Piece-Square Tables (PSTs), and Positional terms simultaneously
/// using Sparse CSR features, multi-threaded Adam gradient descent, and Golden Section K fitting.
/// </summary>
public static class Tuner
{
    private const int TotalFeatures = 386;

    // Feature Indices:
    //   0..47   = Pawn PST (squares 8..55)
    //  48..111  = Knight PST (squares 0..63)
    // 112..175  = Bishop PST (squares 0..63)
    // 176..239  = Rook PST (squares 0..63)
    // 240..303  = Queen PST (squares 0..63)
    // 304..367  = King PST (squares 0..63)
    private const int BpIdx = 368;
    private const int KmIdx = 369;
    private const int BmIdx = 370;
    private const int RsoIdx = 371;
    private const int RoIdx = 372;
    private const int PassedBaseIdx = 373; // 373..378 for PassedMg/Eg[1..6]
    private const int IsoIdx = 379;
    private const int KooIdx = 380;        // MG-only features start here
    private const int KosoIdx = 381;
    private const int KaoIdx = 382;
    private const int KasoIdx = 383;
    private const int QkcIdx = 384;
    private const int KnoIdx = 385;

    // 4 bytes per active feature
    public readonly record struct FeatureEntry(ushort Index, short Coeff);

    // 12 bytes per position (zero padding)
    public readonly record struct PositionRecord(
        int StartOffset,
        ushort Length,
        byte Phase,
        sbyte StmSign, // +1 if White to move, -1 if Black to move
        float Result   // White's result: 1.0f = White win, 0.5f = Draw, 0.0f = Black win
    );

    private sealed class ThreadBuffer(int featureCount)
    {
        public double[] GradMg { get; } = new double[featureCount];
        public double[] GradEg { get; } = new double[featureCount];
        public double SquaredError;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            Array.Clear(GradMg);
            Array.Clear(GradEg);
            SquaredError = 0.0;
        }
    }

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ankax tune <dataset-path> [maxEpochs]");
            return 1;
        }

        string datasetPath = args[1];
        int maxEpochs = args.Length >= 3 && int.TryParse(args[2], out int ep) && ep > 0 ? ep : 1000;

        Console.WriteLine("=== AnkaX Sparse Texel Tuner (CSR + Multi-Threaded Adam) ===");
        Console.WriteLine($"Dataset:    {datasetPath}");
        Console.WriteLine($"Max Epochs: {maxEpochs}");
        Console.WriteLine($"Parameters: {TotalFeatures} MG + {KooIdx} EG ({TotalFeatures + KooIdx} total weights)\n");

        if (!File.Exists(datasetPath))
        {
            Console.Error.WriteLine($"[Error] Dataset file not found: {datasetPath}");
            return 1;
        }

        Stopwatch totalSw = Stopwatch.StartNew();

        // 1. Load dataset & extract sparse CSR features
        Console.WriteLine("[1/4] Extracting sparse CSR features from dataset...");
        (PositionRecord[] positions, FeatureEntry[] features, int[] expectedEvals) = LoadDataset(datasetPath);
        if (positions.Length == 0)
        {
            Console.Error.WriteLine("[Error] No valid positions loaded from dataset.");
            return 1;
        }

        double avgFeatures = features.Length / (double)positions.Length;
        Console.WriteLine($"Loaded {positions.Length:N0} positions, {features.Length:N0} active entries (avg {avgFeatures:F1} features/pos) in {totalSw.ElapsedMilliseconds:N0} ms.");

        // 2. Startup validation net against Evaluation.Evaluate()
        Console.WriteLine("\n[2/4] Running 100% startup score validation net...");
        double[] initialMg = GetInitialMgWeights();
        double[] initialEg = GetInitialEgWeights();
        ValidateScores(positions, features, expectedEvals, initialMg, initialEg);

        // 3. Optimize scaling constant K via Golden Section Search
        Console.WriteLine("\n[3/4] Fitting optimal sigmoid constant K...");
        double k = FindOptimalK(positions, features, initialMg, initialEg);
        double scale = k * Math.Log(10.0) / 400.0;
        double initialMse = ComputeMse(positions, features, initialMg, initialEg, scale);
        Console.WriteLine($"Optimal K = {k:F4} (scale = {scale:F6}), Initial MSE = {initialMse:F6}");

        // 4. Multi-threaded Adam Gradient Descent
        Console.WriteLine($"\n[4/4] Running Adam across {Environment.ProcessorCount} CPU cores...");
        double[] weightsMg = (double[])initialMg.Clone();
        double[] weightsEg = (double[])initialEg.Clone();

        double[] mMg = new double[TotalFeatures];
        double[] vMg = new double[TotalFeatures];
        double[] mEg = new double[TotalFeatures];
        double[] vEg = new double[TotalFeatures];

        const double beta1 = 0.9;
        const double beta2 = 0.999;
        const double eps = 1e-8;
        const double initialLr = 0.4;

        double beta1T = 1.0;
        double beta2T = 1.0;

        double[] gradMg = new double[TotalFeatures];
        double[] gradEg = new double[TotalFeatures];
        object mergeLock = new();

        // Persistent zero-allocation ThreadLocal pool across all epochs
        using ThreadLocal<ThreadBuffer> threadBuffers = new(() => new ThreadBuffer(TotalFeatures), trackAllValues: true);

        Stopwatch epochSw = Stopwatch.StartNew();
        double currentMse = initialMse;
        double invN = 1.0 / positions.Length;
        const double invTotalPhase = 1.0 / Evaluation.TotalPhase;

        for (int epoch = 1; epoch <= maxEpochs; epoch++)
        {
            beta1T *= beta1;
            beta2T *= beta2;
            double biasCorr1 = 1.0 - beta1T;
            double biasCorr2 = 1.0 - beta2T;

            Array.Clear(gradMg);
            Array.Clear(gradEg);
            double totalSquaredError = 0.0;

            // Reset thread-local buffers at the start of the epoch
            foreach (ThreadBuffer buf in threadBuffers.Values)
            {
                buf.Clear();
            }

            Parallel.ForEach(
                Partitioner.Create(0, positions.Length),
                () => threadBuffers.Value!,
                (range, _, local) =>
                {
                    for (int p = range.Item1; p < range.Item2; p++)
                    {
                        ref readonly PositionRecord pos = ref positions[p];
                        ReadOnlySpan<FeatureEntry> feats = features.AsSpan(pos.StartOffset, pos.Length);

                        double mg = 0.0, eg = 0.0;
                        int len = feats.Length;
                        int i = 0;

                        // 4x Loop Unrolling for ILP Pipelining
                        for (; i <= len - 4; i += 4)
                        {
                            ref readonly FeatureEntry f0 = ref feats[i];
                            ref readonly FeatureEntry f1 = ref feats[i + 1];
                            ref readonly FeatureEntry f2 = ref feats[i + 2];
                            ref readonly FeatureEntry f3 = ref feats[i + 3];

                            mg += weightsMg[f0.Index] * f0.Coeff + weightsMg[f1.Index] * f1.Coeff +
                                  weightsMg[f2.Index] * f2.Coeff + weightsMg[f3.Index] * f3.Coeff;

                            eg += weightsEg[f0.Index] * f0.Coeff + weightsEg[f1.Index] * f1.Coeff +
                                  weightsEg[f2.Index] * f2.Coeff + weightsEg[f3.Index] * f3.Coeff;
                        }
                        for (; i < len; i++)
                        {
                            ref readonly FeatureEntry f = ref feats[i];
                            mg += weightsMg[f.Index] * f.Coeff;
                            eg += weightsEg[f.Index] * f.Coeff;
                        }

                        double mgW = pos.Phase * invTotalPhase;
                        double egW = (Evaluation.TotalPhase - pos.Phase) * invTotalPhase;
                        double eval = (mg * mgW) + (eg * egW) + (pos.StmSign * Evaluation.TempoBonus);

                        double sigmoid = 1.0 / (1.0 + Math.Exp(-scale * eval));
                        double diff = sigmoid - pos.Result;
                        local.SquaredError += diff * diff;

                        double baseGrad = 2.0 * scale * diff * sigmoid * (1.0 - sigmoid);
                        double mgGrad = baseGrad * mgW;
                        double egGrad = baseGrad * egW;

                        i = 0;
                        for (; i <= len - 4; i += 4)
                        {
                            ref readonly FeatureEntry f0 = ref feats[i];
                            ref readonly FeatureEntry f1 = ref feats[i + 1];
                            ref readonly FeatureEntry f2 = ref feats[i + 2];
                            ref readonly FeatureEntry f3 = ref feats[i + 3];

                            local.GradMg[f0.Index] += mgGrad * f0.Coeff;
                            local.GradEg[f0.Index] += egGrad * f0.Coeff;
                            local.GradMg[f1.Index] += mgGrad * f1.Coeff;
                            local.GradEg[f1.Index] += egGrad * f1.Coeff;

                            local.GradMg[f2.Index] += mgGrad * f2.Coeff;
                            local.GradEg[f2.Index] += egGrad * f2.Coeff;
                            local.GradMg[f3.Index] += mgGrad * f3.Coeff;
                            local.GradEg[f3.Index] += egGrad * f3.Coeff;
                        }
                        for (; i < len; i++)
                        {
                            ref readonly FeatureEntry f = ref feats[i];
                            local.GradMg[f.Index] += mgGrad * f.Coeff;
                            local.GradEg[f.Index] += egGrad * f.Coeff;
                        }
                    }
                    return local;
                },
                local =>
                {
                    lock (mergeLock)
                    {
                        totalSquaredError += local.SquaredError;
                        for (int i = 0; i < TotalFeatures; i++)
                        {
                            gradMg[i] += local.GradMg[i];
                            gradEg[i] += local.GradEg[i];
                        }
                    }
                    local.Clear();
                });

            currentMse = totalSquaredError * invN;
            double lr = initialLr / (1.0 + 0.0005 * epoch);

            for (int i = 0; i < TotalFeatures; i++)
            {
                double gMg = gradMg[i] * invN;
                mMg[i] = beta1 * mMg[i] + (1.0 - beta1) * gMg;
                vMg[i] = beta2 * vMg[i] + (1.0 - beta2) * gMg * gMg;
                double mHatMg = mMg[i] / biasCorr1;
                double vHatMg = vMg[i] / biasCorr2;
                weightsMg[i] -= lr * mHatMg / (Math.Sqrt(vHatMg) + eps);

                // Indices >= KooIdx are MG-only in Evaluation.cs
                if (i < KooIdx)
                {
                    double gEg = gradEg[i] * invN;
                    mEg[i] = beta1 * mEg[i] + (1.0 - beta1) * gEg;
                    vEg[i] = beta2 * vEg[i] + (1.0 - beta2) * gEg * gEg;
                    double mHatEg = mEg[i] / biasCorr1;
                    double vHatEg = vEg[i] / biasCorr2;
                    weightsEg[i] -= lr * mHatEg / (Math.Sqrt(vHatEg) + eps);
                }
            }

            if (epoch == 1 || epoch % 25 == 0 || epoch == maxEpochs)
            {
                double msPerEpoch = epochSw.Elapsed.TotalMilliseconds / epoch;
                Console.WriteLine($"Epoch {epoch,4} / {maxEpochs} | MSE: {currentMse:F6} | LR: {lr:F4} | Speed: {msPerEpoch:F2} ms/epoch");
            }
        }

        Console.WriteLine($"\nOptimization complete in {totalSw.ElapsedMilliseconds:N0} ms! Final MSE: {currentMse:F6}");
        PrintFormattedResults(weightsMg, weightsEg);
        return 0;
    }

    private static (PositionRecord[] Positions, FeatureEntry[] Features, int[] ExpectedEvals) LoadDataset(string path)
    {
        List<PositionRecord> posList = new(750_000);
        List<FeatureEntry> featList = new(24_000_000);
        List<int> evalList = new(750_000);

        short[] counts = new short[TotalFeatures];

        foreach (string rawLine in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            if (!TryParseDatasetLine(rawLine, out string fenPart, out float whiteResult))
                continue;

            ParseFEN(fenPart);

            if (bitboards[K] == 0 || bitboards[k] == 0)
                continue;

            Array.Clear(counts);
            ExtractFeatures(counts);

            int startOffset = featList.Count;
            ushort activeCount = 0;

            for (int i = 0; i < TotalFeatures; i++)
            {
                if (counts[i] != 0)
                {
                    featList.Add(new FeatureEntry((ushort)i, counts[i]));
                    activeCount++;
                }
            }

            int phaseCount =
                PopCount(bitboards[N] | bitboards[n]) +
                PopCount(bitboards[B] | bitboards[b]) +
                PopCount(bitboards[R] | bitboards[r]) * 2 +
                PopCount(bitboards[Q] | bitboards[q]) * 4;

            byte phase = (byte)Math.Min(Evaluation.TotalPhase, phaseCount);
            sbyte stmSign = (sbyte)(side == White ? 1 : -1);

            posList.Add(new PositionRecord(startOffset, activeCount, phase, stmSign, whiteResult));
            evalList.Add(Evaluation.Evaluate());
        }

        return (posList.ToArray(), featList.ToArray(), evalList.ToArray());
    }

    private static bool TryParseDatasetLine(string line, out string fen, out float whiteResult)
    {
        int pipeIdx = line.IndexOf('|');
        if (pipeIdx > 0)
        {
            ReadOnlySpan<char> resSpan = line.AsSpan(0, pipeIdx).Trim();
            fen = line[(pipeIdx + 1)..].Trim();

            if (float.TryParse(resSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out whiteResult))
                return true;

            if (resSpan.Contains("1-0", StringComparison.Ordinal)) { whiteResult = 1.0f; return true; }
            if (resSpan.Contains("0-1", StringComparison.Ordinal)) { whiteResult = 0.0f; return true; }
            if (resSpan.Contains("1/2", StringComparison.Ordinal)) { whiteResult = 0.5f; return true; }
            return false;
        }

        int c9Idx = line.IndexOf("c9", StringComparison.Ordinal);
        if (c9Idx > 0)
        {
            fen = line[..c9Idx].Trim();
            string resPart = line[c9Idx..];
            if (resPart.Contains("\"1-0\"", StringComparison.Ordinal) || resPart.Contains("\"1.0\"", StringComparison.Ordinal))
            { whiteResult = 1.0f; return true; }
            if (resPart.Contains("\"0-1\"", StringComparison.Ordinal) || resPart.Contains("\"0.0\"", StringComparison.Ordinal))
            { whiteResult = 0.0f; return true; }
            if (resPart.Contains("\"1/2-1/2\"", StringComparison.Ordinal) || resPart.Contains("\"0.5\"", StringComparison.Ordinal))
            { whiteResult = 0.5f; return true; }
        }

        fen = string.Empty;
        whiteResult = 0.5f;
        return false;
    }

    private static void ExtractFeatures(short[] counts)
    {
        ulong wP = bitboards[P], wN = bitboards[N], wB = bitboards[B], wR = bitboards[R], wQ = bitboards[Q], wK = bitboards[K];
        ulong bP = bitboards[p], bN = bitboards[n], bBi = bitboards[b], bR = bitboards[r], bQ = bitboards[q], bK = bitboards[k];

        ulong wOcc = occupancies[White];
        ulong bOcc = occupancies[Black];
        ulong allOcc = occupancies[Both];
        ulong allPawns = wP | bP;

        for (ulong x = wP; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[GetPstIndex(0, sq)] += 1;

            if ((Evaluation.WhitePassedMask[sq] & bP) == 0)
            {
                int rank = sq >> 3;
                if (rank >= 1 && rank <= 6) counts[PassedBaseIdx + (rank - 1)] += 1;
            }
            if ((Evaluation.AdjacentFiles[sq & 7] & wP) == 0)
                counts[IsoIdx] += 1;
        }

        for (ulong x = bP; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[GetPstIndex(0, sq ^ 56)] -= 1;

            if ((Evaluation.BlackPassedMask[sq] & wP) == 0)
            {
                int rank = 7 - (sq >> 3);
                if (rank >= 1 && rank <= 6) counts[PassedBaseIdx + (rank - 1)] -= 1;
            }
            if ((Evaluation.AdjacentFiles[sq & 7] & bP) == 0)
                counts[IsoIdx] -= 1;
        }

        int kmDiff = 0;
        for (ulong x = wN; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[GetPstIndex(1, sq)] += 1;

            kmDiff += PopCount(knightAttacks[sq] & ~wOcc) - Evaluation.KnightMobBase;

            int rank = sq >> 3;
            if ((uint)(rank - 2) <= 2
                && (pawnAttacks[Black, sq] & wP) != 0
                && (Evaluation.WhiteOutpostMask[sq] & bP) == 0)
            {
                counts[KnoIdx] += 1;
            }
        }

        for (ulong x = bN; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[GetPstIndex(1, sq ^ 56)] -= 1;

            kmDiff -= PopCount(knightAttacks[sq] & ~bOcc) - Evaluation.KnightMobBase;

            int rank = sq >> 3;
            if ((uint)(rank - 3) <= 2
                && (pawnAttacks[White, sq] & bP) != 0
                && (Evaluation.BlackOutpostMask[sq] & wP) == 0)
            {
                counts[KnoIdx] -= 1;
            }
        }
        counts[KmIdx] += (short)kmDiff;

        int bmDiff = 0;
        int wBishopCount = 0;
        for (ulong x = wB; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[GetPstIndex(2, sq)] += 1;
            wBishopCount++;
            bmDiff += PopCount(GetBishopAttacks(sq, allOcc) & ~wOcc) - Evaluation.BishopMobBase;
        }
        if (wBishopCount >= 2) counts[BpIdx] += 1;

        int bBishopCount = 0;
        for (ulong x = bBi; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[GetPstIndex(2, sq ^ 56)] -= 1;
            bBishopCount++;
            bmDiff -= PopCount(GetBishopAttacks(sq, allOcc) & ~bOcc) - Evaluation.BishopMobBase;
        }
        if (bBishopCount >= 2) counts[BpIdx] -= 1;
        counts[BmIdx] += (short)bmDiff;

        for (ulong x = wR; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[GetPstIndex(3, sq)] += 1;

            ulong fMask = Evaluation.FileMask[sq & 7];
            if ((wP & fMask) == 0)
            {
                if ((allPawns & fMask) == 0) counts[RoIdx] += 1;
                else counts[RsoIdx] += 1;
            }
        }

        for (ulong x = bR; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[GetPstIndex(3, sq ^ 56)] -= 1;

            ulong fMask = Evaluation.FileMask[sq & 7];
            if ((bP & fMask) == 0)
            {
                if ((allPawns & fMask) == 0) counts[RoIdx] -= 1;
                else counts[RsoIdx] -= 1;
            }
        }

        for (ulong x = wQ; x != 0; x &= x - 1)
            counts[GetPstIndex(4, TrailingZeroCount(x))] += 1;

        for (ulong x = bQ; x != 0; x &= x - 1)
            counts[GetPstIndex(4, TrailingZeroCount(x) ^ 56)] -= 1;

        int wkSq = TrailingZeroCount(wK);
        int bkSq = TrailingZeroCount(bK);
        counts[GetPstIndex(5, wkSq)] += 1;
        counts[GetPstIndex(5, bkSq ^ 56)] -= 1;

        ExtractKingExposure(wkSq & 7, wP, allPawns, -1, counts);
        ExtractKingExposure(bkSq & 7, bP, allPawns, +1, counts);

        if (wQ == 0 && bQ == 0)
        {
            counts[QkcIdx] += (short)(Evaluation.KingCenterTable[wkSq] - Evaluation.KingCenterTable[bkSq]);
        }
    }

    private static void ExtractKingExposure(int kingFile, ulong friendlyPawns, ulong allPawns, int sign, short[] counts)
    {
        ulong ownMask = Evaluation.FileMask[kingFile];
        if ((friendlyPawns & ownMask) == 0)
        {
            if ((allPawns & ownMask) == 0) counts[KooIdx] += (short)sign;
            else counts[KosoIdx] += (short)sign;
        }

        if (kingFile > 0)
        {
            ulong leftMask = Evaluation.FileMask[kingFile - 1];
            if ((friendlyPawns & leftMask) == 0)
            {
                if ((allPawns & leftMask) == 0) counts[KaoIdx] += (short)sign;
                else counts[KasoIdx] += (short)sign;
            }
        }

        if (kingFile < 7)
        {
            ulong rightMask = Evaluation.FileMask[kingFile + 1];
            if ((friendlyPawns & rightMask) == 0)
            {
                if ((allPawns & rightMask) == 0) counts[KaoIdx] += (short)sign;
                else counts[KasoIdx] += (short)sign;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPstIndex(int piece, int square)
    {
        return piece switch
        {
            0 => square - 8,   // Pawn: 0..47
            1 => 48 + square,  // Knight: 48..111
            2 => 112 + square, // Bishop: 112..175
            3 => 176 + square, // Rook: 176..239
            4 => 240 + square, // Queen: 240..303
            5 => 304 + square, // King: 304..367
            _ => throw new ArgumentOutOfRangeException(nameof(piece))
        };
    }

    private static double[] GetInitialMgWeights()
    {
        double[] mg = new double[TotalFeatures];
        for (int pc = 0; pc < 6; pc++)
        {
            int minSq = pc == 0 ? 8 : 0;
            int maxSq = pc == 0 ? 56 : 64;
            for (int sq = minSq; sq < maxSq; sq++)
                mg[GetPstIndex(pc, sq)] = Evaluation.MgMaterial[pc] + Evaluation.MgPstSource[pc][sq];
        }

        mg[BpIdx] = Evaluation.BishopPairMg;
        mg[KmIdx] = Evaluation.KnightMobMg;
        mg[BmIdx] = Evaluation.BishopMobMg;
        mg[RsoIdx] = Evaluation.RookSemiOpenMg;
        mg[RoIdx] = Evaluation.RookOpenMg;
        for (int d = 1; d <= 6; d++) mg[PassedBaseIdx + d - 1] = Evaluation.PassedMg[d];
        mg[IsoIdx] = Evaluation.IsolatedMg;
        mg[KooIdx] = Evaluation.KingOwnOpenMg;
        mg[KosoIdx] = Evaluation.KingOwnSemiOpenMg;
        mg[KaoIdx] = Evaluation.KingAdjacentOpenMg;
        mg[KasoIdx] = Evaluation.KingAdjacentSemiOpenMg;
        mg[QkcIdx] = Evaluation.QueenlessKingCenterMg;
        mg[KnoIdx] = Evaluation.KnightOutpostMg;
        return mg;
    }

    private static double[] GetInitialEgWeights()
    {
        double[] eg = new double[TotalFeatures];
        for (int pc = 0; pc < 6; pc++)
        {
            int minSq = pc == 0 ? 8 : 0;
            int maxSq = pc == 0 ? 56 : 64;
            for (int sq = minSq; sq < maxSq; sq++)
                eg[GetPstIndex(pc, sq)] = Evaluation.EgMaterial[pc] + Evaluation.EgPstSource[pc][sq];
        }

        eg[BpIdx] = Evaluation.BishopPairEg;
        eg[KmIdx] = Evaluation.KnightMobEg;
        eg[BmIdx] = Evaluation.BishopMobEg;
        eg[RsoIdx] = Evaluation.RookSemiOpenEg;
        eg[RoIdx] = Evaluation.RookOpenEg;
        for (int d = 1; d <= 6; d++) eg[PassedBaseIdx + d - 1] = Evaluation.PassedEg[d];
        eg[IsoIdx] = Evaluation.IsolatedEg;
        return eg;
    }

    private static void ValidateScores(
        PositionRecord[] positions,
        FeatureEntry[] features,
        int[] expectedEvals,
        double[] initialMg,
        double[] initialEg)
    {
        for (int p = 0; p < positions.Length; p++)
        {
            ref readonly PositionRecord pos = ref positions[p];
            ReadOnlySpan<FeatureEntry> feats = features.AsSpan(pos.StartOffset, pos.Length);

            double mg = 0.0, eg = 0.0;
            foreach (ref readonly FeatureEntry f in feats)
            {
                mg += initialMg[f.Index] * f.Coeff;
                eg += initialEg[f.Index] * f.Coeff;
            }

            int mgScore = (int)Math.Round(mg);
            int egScore = (int)Math.Round(eg);
            int whiteScore = (mgScore * pos.Phase + egScore * (Evaluation.TotalPhase - pos.Phase)) / Evaluation.TotalPhase;
            int calcEval = (pos.StmSign * whiteScore) + Evaluation.TempoBonus;

            if (calcEval != expectedEvals[p])
            {
                throw new InvalidOperationException(
                    $"Score mismatch at position {p}: Cached={calcEval}, Evaluation.Evaluate()={expectedEvals[p]}");
            }
        }

        Console.WriteLine($"[Validation] 100% match across {positions.Length:N0} positions with Evaluation.Evaluate().");
    }

    private static double ComputeMse(PositionRecord[] positions, FeatureEntry[] features, double[] weightsMg, double[] weightsEg, double scale)
    {
        double totalError = 0.0;
        object lockObj = new();
        const double invTotalPhase = 1.0 / Evaluation.TotalPhase;

        Parallel.ForEach(
            Partitioner.Create(0, positions.Length),
            () => 0.0,
            (range, _, localErr) =>
            {
                for (int p = range.Item1; p < range.Item2; p++)
                {
                    ref readonly PositionRecord pos = ref positions[p];
                    ReadOnlySpan<FeatureEntry> feats = features.AsSpan(pos.StartOffset, pos.Length);

                    double mg = 0.0, eg = 0.0;
                    foreach (ref readonly FeatureEntry f in feats)
                    {
                        mg += weightsMg[f.Index] * f.Coeff;
                        eg += weightsEg[f.Index] * f.Coeff;
                    }

                    double mgW = pos.Phase * invTotalPhase;
                    double egW = (Evaluation.TotalPhase - pos.Phase) * invTotalPhase;
                    double eval = (mg * mgW) + (eg * egW) + (pos.StmSign * Evaluation.TempoBonus);

                    double sigmoid = 1.0 / (1.0 + Math.Exp(-scale * eval));
                    double diff = sigmoid - pos.Result;
                    localErr += diff * diff;
                }
                return localErr;
            },
            localErr => { lock (lockObj) { totalError += localErr; } });

        return totalError / positions.Length;
    }

    private static double FindOptimalK(PositionRecord[] positions, FeatureEntry[] features, double[] weightsMg, double[] weightsEg)
    {
        double a = 0.1, b = 5.0;
        double phi = (1.0 + Math.Sqrt(5.0)) / 2.0;
        double resphi = 2.0 - phi;

        double c = a + resphi * (b - a);
        double d = b - resphi * (b - a);
        double fc = ComputeMse(positions, features, weightsMg, weightsEg, c * Math.Log(10.0) / 400.0);
        double fd = ComputeMse(positions, features, weightsMg, weightsEg, d * Math.Log(10.0) / 400.0);

        for (int i = 0; i < 30; i++)
        {
            if (fc < fd)
            {
                b = d; d = c; fd = fc;
                c = a + resphi * (b - a);
                fc = ComputeMse(positions, features, weightsMg, weightsEg, c * Math.Log(10.0) / 400.0);
            }
            else
            {
                a = c; c = d; fc = fd;
                d = b - resphi * (b - a);
                fd = ComputeMse(positions, features, weightsMg, weightsEg, d * Math.Log(10.0) / 400.0);
            }
        }
        return (a + b) / 2.0;
    }

    private static void PrintFormattedResults(double[] wMg, double[] wEg)
    {
        int[] tunedMgMat = new int[6];
        int[] tunedEgMat = new int[6];
        int[][] tunedMgPst = new int[6][];
        int[][] tunedEgPst = new int[6][];

        for (int pc = 0; pc < 6; pc++)
        {
            tunedMgPst[pc] = new int[64];
            tunedEgPst[pc] = new int[64];
        }

        // Normalize Pawn..Queen PSTs to zero-mean and fold mean into MgMaterial/EgMaterial
        for (int pc = 0; pc < 5; pc++)
        {
            int minSq = pc == 0 ? 8 : 0;
            int maxSq = pc == 0 ? 56 : 64;
            int count = maxSq - minSq;

            double mgSum = 0.0, egSum = 0.0;
            for (int sq = minSq; sq < maxSq; sq++)
            {
                int idx = GetPstIndex(pc, sq);
                mgSum += wMg[idx];
                egSum += wEg[idx];
            }

            int mgAvg = (int)Math.Round(mgSum / count);
            int egAvg = (int)Math.Round(egSum / count);

            tunedMgMat[pc] = mgAvg;
            tunedEgMat[pc] = egAvg;

            for (int sq = minSq; sq < maxSq; sq++)
            {
                int idx = GetPstIndex(pc, sq);
                tunedMgPst[pc][sq] = (int)Math.Round(wMg[idx]) - mgAvg;
                tunedEgPst[pc][sq] = (int)Math.Round(wEg[idx]) - egAvg;
            }
        }

        // King material stays 0
        for (int sq = 0; sq < 64; sq++)
        {
            int idx = GetPstIndex(5, sq);
            tunedMgPst[5][sq] = (int)Math.Round(wMg[idx]);
            tunedEgPst[5][sq] = (int)Math.Round(wEg[idx]);
        }

        int[] passMg = new int[8], passEg = new int[8];
        for (int d = 1; d <= 6; d++)
        {
            passMg[d] = (int)Math.Round(wMg[PassedBaseIdx + d - 1]);
            passEg[d] = (int)Math.Round(wEg[PassedBaseIdx + d - 1]);
        }

        string[] mgPieceComments =
        [
            "        // Pawn — the back rank is zero because pawns never stand there.",
            "        // Knight — rim is dim; centre squares are prized.",
            "        // Bishop — long diagonals and fianchetto squares.",
            "        // Rook — likes the 7th rank and central files.",
            "        // Queen — modest PST; mobility and tactics dominate its value.",
            "        // King (middlegame) — castled corners are safe; the centre is fatal."
        ];

        string[] egPieceComments =
        [
            "        // Pawn — promotion race dominates; advanced pawns are huge.",
            "        // Knight — still prefers the centre, but values are compressed.",
            "        // Bishop — long diagonals remain preferable.",
            "        // Rook — activity matters more than any one square.",
            "        // Queen — rewards centralization in simplified positions.",
            "        // King (endgame) — march to the centre to support pawns."
        ];

        Console.WriteLine("\n===============================================================================");
        Console.WriteLine("// Ready to paste tuned parameters for Evaluation.cs:");
        Console.WriteLine("===============================================================================");
        Console.WriteLine(); Console.WriteLine(); Console.WriteLine(); Console.WriteLine();

        Console.WriteLine("    #region Material & Phase\n");

        Console.WriteLine("    // Base material values in centipawns, indexed by piece type:");
        Console.WriteLine("    //   0=Pawn  1=Knight  2=Bishop  3=Rook  4=Queen  5=King");
        Console.WriteLine("    // King is 0 because it's never captured; its \"value\" is implicit in game");
        Console.WriteLine("    // termination, not in evaluation.");
        Console.WriteLine($"    private static readonly int[] MgMaterial = [{tunedMgMat[0]}, {tunedMgMat[1]}, {tunedMgMat[2]}, {tunedMgMat[3]}, {tunedMgMat[4]}, 0];");
        Console.WriteLine($"    private static readonly int[] EgMaterial = [{tunedEgMat[0]}, {tunedEgMat[1]}, {tunedEgMat[2]}, {tunedEgMat[3]}, {tunedEgMat[4]}, 0];\n");

        Console.WriteLine("    // Starting position phase: 2 sides × (2×Knight + 2×Bishop + 2×Rook + 1×Queen)");
        Console.WriteLine("    //   = 2 × (2·1 + 2·1 + 2·2 + 1·4) = 24.");
        Console.WriteLine("    // Phase is accumulated inline during evaluation (Knight/Bishop += 1,");
        Console.WriteLine("    // Rook += 2, Queen += 4) rather than through a lookup table.");
        Console.WriteLine("    internal const int TotalPhase = 24;\n");

        Console.WriteLine("    #endregion\n");

        Console.WriteLine("    #region Positional Bonuses / Penalties\n");

        Console.WriteLine("    // Having both bishops is worth a small bonus because they cover");
        Console.WriteLine("    // complementary colour complexes.");
        Console.WriteLine($"    private const int BishopPairMg = {(int)Math.Round(wMg[BpIdx])}, BishopPairEg = {(int)Math.Round(wEg[BpIdx])};\n");

        Console.WriteLine("    // Mobility = (number of safe destination squares) − (a baseline).");
        Console.WriteLine("    // Subtracting a baseline keeps the \"average\" piece near zero so the");
        Console.WriteLine("    // mobility term doesn't silently inflate material values.");
        Console.WriteLine($"    private const int KnightMobMg = {(int)Math.Round(wMg[KmIdx])}, KnightMobEg = {(int)Math.Round(wEg[KmIdx])}, KnightMobBase = 4;");
        Console.WriteLine($"    private const int BishopMobMg = {(int)Math.Round(wMg[BmIdx])}, BishopMobEg = {(int)Math.Round(wEg[BmIdx])}, BishopMobBase = 6;\n");

        Console.WriteLine("    // Rook on a file with no friendly pawn:");
        Console.WriteLine("    //   semi-open = only enemy pawns on it (good attacking lane)");
        Console.WriteLine("    //   open      = no pawns at all (best — rook sees the whole file)");
        Console.WriteLine($"    private const int RookSemiOpenMg = {(int)Math.Round(wMg[RsoIdx])}, RookSemiOpenEg = {(int)Math.Round(wEg[RsoIdx])};");
        Console.WriteLine($"    private const int RookOpenMg = {(int)Math.Round(wMg[RoIdx])}, RookOpenEg = {(int)Math.Round(wEg[RoIdx])};\n");

        Console.WriteLine("    // Passed pawn bonus indexed by squares left to promotion (0..7):");
        Console.WriteLine("    //   index 1 = one step from promoting, index 6 = still on its start square.");
        Console.WriteLine("    // A pawn gets this if no enemy pawn can block or capture it on its way");
        Console.WriteLine("    // to promotion. The closer to promotion, the bigger the bonus — much");
        Console.WriteLine("    // larger in the endgame where promotion threats dominate.");
        Console.WriteLine($"    private static readonly int[] PassedMg = [{passMg[0]}, {passMg[1]}, {passMg[2]}, {passMg[3]}, {passMg[4]}, {passMg[5]}, {passMg[6]}, {passMg[7]}];");
        Console.WriteLine($"    private static readonly int[] PassedEg = [{passEg[0]}, {passEg[1]}, {passEg[2]}, {passEg[3]}, {passEg[4]}, {passEg[5]}, {passEg[6]}, {passEg[7]}];\n");

        Console.WriteLine("    // A pawn with no friendly pawn on either adjacent file. Weak because it");
        Console.WriteLine("    // can never be defended by another pawn.");
        Console.WriteLine($"    private const int IsolatedMg = {(int)Math.Round(wMg[IsoIdx])}, IsolatedEg = {(int)Math.Round(wEg[IsoIdx])};\n");

        Console.WriteLine("    // King safety: penalties for open/semi-open files near the king. Only");
        Console.WriteLine("    // applied in the middlegame — in the endgame the king wants to be active.");
        Console.WriteLine($"    private const int KingOwnOpenMg = {(int)Math.Round(wMg[KooIdx])}, KingOwnSemiOpenMg = {(int)Math.Round(wMg[KosoIdx])};");
        Console.WriteLine($"    private const int KingAdjacentOpenMg = {(int)Math.Round(wMg[KaoIdx])}, KingAdjacentSemiOpenMg = {(int)Math.Round(wMg[KasoIdx])};\n");

        Console.WriteLine("    // Once both queens are gone, encourage the king toward the centre so it");
        Console.WriteLine("    // can help its pawns promote.");
        Console.WriteLine($"    private const int QueenlessKingCenterMg = {(int)Math.Round(wMg[QkcIdx])};\n");

        Console.WriteLine("    // Knight sitting on a square where:");
        Console.WriteLine("    //   • it's protected by a friendly pawn");
        Console.WriteLine("    //   • it can't be kicked by any enemy pawn on adjacent files");
        Console.WriteLine("    // Outposts are classical strong squares (think c5/d5/e5/f5 for White).");
        Console.WriteLine($"    private const int KnightOutpostMg = {(int)Math.Round(wMg[KnoIdx])};\n");

        Console.WriteLine("    // Tempo bonus: static advantage awarded to the side to move");
        Console.WriteLine($"    internal const int TempoBonus = {Evaluation.TempoBonus};\n");

        Console.WriteLine("    #endregion\n");

        Console.WriteLine("    // =========================================================================");
        Console.WriteLine("    //  SECTION 2 — Piece–square tables (PeSTO)");
        Console.WriteLine("    //");
        Console.WriteLine("    //  One 64-entry table per piece type, written from White's perspective");
        Console.WriteLine("    //  with square a8 at index 0 and h1 at index 63 — exactly matching the");
        Console.WriteLine("    //  board's visual layout when you read top-to-bottom, left-to-right.");
        Console.WriteLine("    //");
        Console.WriteLine("    //  These are \"source\" tables. At startup we fold them with the material");
        Console.WriteLine("    //  values into a single flat lookup (MgTable/EgTable) for fast access.");
        Console.WriteLine("    // =========================================================================\n");

        Console.WriteLine("    #region PST Data\n");
        Console.WriteLine("    internal static readonly int[][] MgPstSource =");
        Console.WriteLine("    [");
        for (int pc = 0; pc < 6; pc++)
        {
            Console.WriteLine(mgPieceComments[pc]);
            PrintPstBlock(tunedMgPst[pc], pc < 5);
        }
        Console.WriteLine("    ];\n");

        Console.WriteLine("    internal static readonly int[][] EgPstSource =");
        Console.WriteLine("    [");
        for (int pc = 0; pc < 6; pc++)
        {
            Console.WriteLine(egPieceComments[pc]);
            PrintPstBlock(tunedEgPst[pc], pc < 5);
        }
        Console.WriteLine("    ];");
        Console.WriteLine("    #endregion");
        Console.WriteLine("===============================================================================");
    }

    private static void PrintPstBlock(int[] table, bool trailingComma)
    {
        Console.Write("        [ ");
        for (int rank = 0; rank < 8; rank++)
        {
            if (rank > 0) Console.Write("          ");
            for (int file = 0; file < 8; file++)
            {
                int sq = (rank << 3) | file;
                Console.Write($"{table[sq],4}");
                if (sq < 63) Console.Write(",");
            }
            if (rank < 7) Console.WriteLine();
        }
        Console.WriteLine(trailingComma ? " ]," : " ]");
    }
}