using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using static System.Numerics.BitOperations;
using static Board;
using static MoveGenerator;
using static PieceAttacks;

public static class Tuner
{
    private const int TotalFeatures = 387;

    private const int BpIdx = 368;
    private const int KmIdx = 369;
    private const int BmIdx = 370;
    private const int RsoIdx = 371;
    private const int RoIdx = 372;
    private const int PassedBaseIdx = 373;
    private const int IsoIdx = 379;
    private const int KooIdx = 380;
    private const int KosoIdx = 381;
    private const int KaoIdx = 382;
    private const int KasoIdx = 383;
    private const int QkcIdx = 384;
    private const int KnoIdx = 385;
    private const int TempoIdx = 386;

    private const short NoEval = short.MinValue;

    private static readonly Vector128<double>[] PhaseWeights = CreatePhaseWeights();

    #region Immutable Official PeSTO Baseline (For --fresh Tunes)

    private static readonly int[] OfficialMgMaterial = [82, 337, 365, 477, 1025, 0];
    private static readonly int[] OfficialEgMaterial = [94, 281, 297, 512, 936, 0];

    private static readonly int[][] OfficialMgPst =
    [
        // Pawn
        [    0,   0,   0,   0,   0,   0,   0,   0,
            98, 134,  61,  95,  68, 126,  34, -11,
            -6,   7,  26,  31,  65,  56,  25, -20,
           -14,  13,   6,  21,  23,  12,  17, -23,
           -27,  -2,  -5,  12,  17,   6,  10, -25,
           -26,  -4,  -4, -10,   3,   3,  33, -12,
           -35,  -1, -20, -23, -15,  24,  38, -22,
             0,   0,   0,   0,   0,   0,   0,   0 ],
        // Knight
        [ -167, -89, -34, -49,  61, -97, -15,-107,
           -73, -41,  72,  36,  23,  62,   7, -17,
           -47,  60,  37,  65,  84, 129,  73,  44,
            -9,  17,  19,  53,  37,  69,  18,  22,
           -13,   4,  16,  13,  28,  19,  21,  -8,
           -23,  -9,  12,  10,  19,  17,  25, -16,
           -29, -53, -12,  -3,  -1,  18, -14, -19,
          -105, -21, -58, -33, -17, -28, -19, -23 ],
        // Bishop
        [  -29,   4, -82, -37, -25, -42,   7,  -8,
           -26,  16, -18, -13,  30,  59,  18, -47,
           -16,  37,  43,  40,  35,  50,  37,  -2,
            -4,   5,  19,  50,  37,  37,   7,  -2,
            -6,  13,  13,  26,  34,  12,  10,   4,
             0,  15,  15,  15,  14,  27,  18,  10,
             4,  15,  16,   0,   7,  21,  33,   1,
           -33,  -3, -14, -21, -13, -12, -39, -21 ],
        // Rook
        [   32,  42,  32,  51,  63,   9,  31,  43,
            27,  32,  58,  62,  80,  67,  26,  44,
            -5,  19,  26,  36,  17,  45,  61,  16,
           -24, -11,   7,  26,  24,  35,  -8, -20,
           -36, -26, -12,  -1,   9,  -7,   6, -23,
           -45, -25, -16, -17,   3,   0,  -5, -33,
           -44, -16, -20,  -9,  -1,  11,  -6, -71,
           -19, -13,   1,  17,  16,   7, -37, -26 ],
        // Queen
        [  -28,   0,  29,  12,  59,  44,  43,  45,
           -24, -39,  -5,   1, -16,  57,  28,  54,
           -13, -17,   7,   8,  29,  56,  47,  57,
           -27, -27, -16, -16,  -1,  17,  -2,   1,
            -9, -26,  -9, -10,  -2,  -4,   3,  -3,
           -14,   2, -11,  -2,  -5,   2,  14,   5,
           -35,  -8,  11,   2,   8,  15,  -3,   1,
            -1, -18,  -9,  10, -15, -25, -31, -50 ],
        // King
        [  -65,  23,  16, -15, -56, -34,   2,  13,
            29,  -1, -20,  -7,  -8,  -4, -38, -29,
            -9,  24,   2, -16, -20,   6,  22, -22,
           -17, -20, -12, -27, -30, -25, -14, -36,
           -49,  -1, -27, -39, -46, -44, -33, -51,
           -14, -14, -22, -46, -44, -30, -15, -27,
             1,   7,  -8, -64, -43, -16,   9,   8,
           -15,  36,  12, -54,   8, -28,  24,  14 ]
    ];

    private static readonly int[][] OfficialEgPst =
    [
        // Pawn
        [    0,   0,   0,   0,   0,   0,   0,   0,
           178, 173, 158, 134, 147, 132, 165, 187,
            94, 100,  85,  67,  56,  53,  82,  84,
            32,  24,  13,   5,  -2,   4,  17,  17,
            13,   9,  -3,  -7,  -7,  -8,   3,  -1,
             4,   7,  -6,   1,   0,  -5,  -1,  -8,
            13,   8,   8,  10,  13,   0,   2,  -7,
             0,   0,   0,   0,   0,   0,   0,   0 ],
        // Knight
        [  -58, -38, -13, -28, -31, -27, -63, -99,
           -25,  -8, -25,  -2,  -9, -25, -24, -52,
           -24, -20,  10,   9,  -1,  -9, -19, -41,
           -17,   3,  22,  22,  22,  11,   8, -18,
           -18,  -6,  16,  25,  16,  17,   4, -18,
           -23,  -3,  -1,  15,  10,  -3, -20, -22,
           -42, -20, -10,  -5,  -2, -20, -23, -44,
           -29, -51, -23, -15, -22, -18, -50, -64 ],
        // Bishop
        [  -14, -21, -11,  -8,  -7,  -9, -17, -24,
            -8,  -4,   7, -12,  -3, -13,  -4, -14,
             2,  -8,   0,  -1,  -2,   6,   0,   4,
            -3,   9,  12,   9,  14,  10,   3,   2,
            -6,   3,  13,  19,   7,  10,  -3,  -9,
           -12,  -3,   8,  10,  13,   3,  -7, -15,
           -14, -18,  -7,  -1,   4,  -9, -15, -27,
           -23,  -9, -23,  -5,  -9, -16,  -5, -17 ],
        // Rook
        [   13,  10,  18,  15,  12,  12,   8,   5,
            11,  13,  13,  11,  -3,   3,   8,   3,
             7,   7,   7,   5,   4,  -3,  -5,  -3,
             4,   3,  13,   1,   2,   1,  -1,   2,
             3,   5,   8,   4,  -5,  -6,  -8, -11,
            -4,   0,  -5,  -1,  -7, -12,  -8, -16,
            -6,  -6,   0,   2,  -9,  -9, -11,  -3,
            -9,   2,   3,  -1,  -5, -13,   4, -20 ],
        // Queen
        [   -9,  22,  22,  27,  27,  19,  10,  20,
           -17,  20,  32,  41,  58,  25,  30,   0,
           -20,   6,   9,  49,  47,  35,  19,   9,
             3,  22,  24,  45,  57,  40,  57,  36,
           -18,  28,  19,  47,  31,  34,  39,  23,
           -16, -27,  15,   6,   9,  17,  10,   5,
           -22, -23, -30, -16, -16, -23, -36, -32,
           -33, -28, -22, -43,  -5, -32, -20, -41 ],
        // King
        [  -74, -35, -18, -18, -11,  15,   4, -17,
           -12,  17,  14,  17,  17,  38,  23,  11,
            10,  17,  23,  15,  20,  45,  44,  13,
            -8,  22,  24,  27,  26,  33,  26,   3,
           -18,  -4,  21,  24,  27,  23,   9, -11,
           -19,  -3,  11,  21,  23,  16,   7,  -9,
           -27, -11,   4,  13,  14,   4,  -5, -17,
           -53, -34, -21, -11, -28, -14, -24, -43 ]
    ];

    #endregion

    public readonly record struct FeatureEntry(ushort Index, short Coeff);

    public readonly record struct PositionRecord(
        int StartOffset,
        ushort Length,
        byte Phase,
        sbyte StmSign,
        float Result,
        short Eval
    );

    private sealed class ThreadBuffer(int featureCount)
    {
        public Vector128<double>[] Grad { get; } = new Vector128<double>[featureCount];
        public double SquaredError;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            Array.Clear(Grad);
            SquaredError = 0.0;
        }
    }

    private static Vector128<double>[] CreatePhaseWeights()
    {
        Vector128<double>[] table = new Vector128<double>[Evaluation.TotalPhase + 1];
        const double invTotalPhase = 1.0 / Evaluation.TotalPhase;
        for (int phase = 0; phase <= Evaluation.TotalPhase; phase++)
        {
            table[phase] = Vector128.Create(
                phase * invTotalPhase,
                (Evaluation.TotalPhase - phase) * invTotalPhase
            );
        }
        return table;
    }

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ankax tune <dataset-path> [maxEpochs] [--fresh]");
            return 1;
        }

        string datasetPath = args[1];
        int maxEpochs = 20000;
        bool freshBaseline = false;

        for (int a = 2; a < args.Length; a++)
        {
            string arg = args[a];
            if (arg.Equals("--fresh", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-f", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--reset", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--pesto", StringComparison.OrdinalIgnoreCase))
            {
                freshBaseline = true;
            }
            else if (int.TryParse(arg, out int ep) && ep > 0)
            {
                maxEpochs = ep;
            }
        }

        Console.WriteLine("=== AnkaX Sparse Texel Tuner (CSR + Multi-Threaded Adam) ===");
        Console.WriteLine($"Dataset:    {datasetPath}");
        Console.WriteLine($"Max Epochs: {maxEpochs}");
        Console.WriteLine($"Start Mode: {(freshBaseline ? "FRESH (Official PeSTO PSTs + Material; all other terms = 0)" : "INCREMENTAL (Current Evaluation.cs weights)")}");
        Console.WriteLine($"Parameters: {TotalFeatures} MG + {TotalFeatures - 7} EG ({TotalFeatures * 2 - 7} independent weights)\n");

        if (!File.Exists(datasetPath))
        {
            Console.Error.WriteLine($"[Error] Dataset file not found: {datasetPath}");
            return 1;
        }

        Stopwatch totalSw = Stopwatch.StartNew();

        Console.WriteLine("[1/4] Extracting sparse CSR features from dataset...");
        (PositionRecord[] positions, FeatureEntry[] features, int[] expectedEvals) = LoadDataset(datasetPath);
        if (positions.Length == 0)
        {
            Console.Error.WriteLine("[Error] No valid positions loaded from dataset.");
            return 1;
        }

        double avgFeatures = features.Length / (double)positions.Length;
        Console.WriteLine($"Loaded {positions.Length:N0} positions, {features.Length:N0} active entries (avg {avgFeatures:F1} features/pos) in {totalSw.ElapsedMilliseconds:N0} ms.");

        Console.WriteLine("\n[2/4] Running 100% startup score validation net...");
        Vector128<double>[] evalWeights = GetCurrentEvaluationWeights();
        ValidateScores(positions, features, expectedEvals, evalWeights);

        Vector128<double>[] weights;
        if (freshBaseline)
        {
            Console.WriteLine("\n[3/4] Setting up FRESH PeSTO baseline (PSTs + Material; Tempo and all secondary terms = 0)...");
            weights = GetPestoBaselineWeights();
        }
        else
        {
            Console.WriteLine("\n[3/4] Initializing from current Evaluation.cs weights (pass --fresh for a clean PeSTO reset)...");
            weights = (Vector128<double>[])evalWeights.Clone();
        }

        double k = FindOptimalK(positions, features, weights);
        double scale = k * Math.Log(10.0) / 400.0;

        double[] targets = PrecomputeTargets(positions, scale);
        double initialMse = ComputeMseWithTargets(positions, features, targets, weights, scale);
        Console.WriteLine($"Optimal K = {k:F4} (scale = {scale:F6}), Initial MSE = {initialMse:F6}");

        int workerCount = Environment.ProcessorCount;
        Console.WriteLine($"\n[4/4] Running Adam across {workerCount} CPU cores...");

        Vector128<double>[] mVec = new Vector128<double>[TotalFeatures];
        Vector128<double>[] vVec = new Vector128<double>[TotalFeatures];
        Vector128<double>[] bestWeights = new Vector128<double>[TotalFeatures];
        Array.Copy(weights, bestWeights, TotalFeatures);

        ThreadBuffer[] buffers = new ThreadBuffer[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            buffers[w] = new ThreadBuffer(TotalFeatures);
        }

        const double beta1 = 0.9;
        const double beta2 = 0.999;
        const double oneMinusBeta1 = 1.0 - beta1;
        const double oneMinusBeta2 = 1.0 - beta2;
        const double eps = 1e-8;
        double initialLr = freshBaseline ? 1.0 : 0.20;
        const double lrDecay = 0.004;
        const double lambda = 0.0;
        const double minRelImprovement = 1e-4; // Requires ~0.000003 MSE drop within patience window
        const int patience = 40;

        Vector128<double> epsVec = Vector128.Create(eps);
        double beta1T = 1.0;
        double beta2T = 1.0;

        Stopwatch epochSw = Stopwatch.StartNew();
        double invN = 1.0 / positions.Length;
        double twoScale = 2.0 * scale;

        double bestMse = initialMse;
        double anchorMse = initialMse;
        int noImprovementEpochs = 0;
        int totalPositions = positions.Length;
        int chunkSize = (totalPositions + workerCount - 1) / workerCount;

        // Allow pressing Ctrl+C at any time to stop early and still print the best weights
        bool cancelRequested = false;
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cancelRequested = true;
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            for (int epoch = 1; epoch <= maxEpochs; epoch++)
            {
                if (cancelRequested)
                {
                    Console.WriteLine($"\n[Interrupted] Stopping at epoch {epoch} and outputting best weights (Best MSE = {bestMse:F8})...");
                    break;
                }

                beta1T *= beta1;
                beta2T *= beta2;
                double invBiasCorr1 = 1.0 / (1.0 - beta1T);
                double invBiasCorr2 = 1.0 / (1.0 - beta2T);

                Parallel.For(0, workerCount, workerId =>
                {
                    int start = workerId * chunkSize;
                    int end = Math.Min(start + chunkSize, totalPositions);
                    ThreadBuffer local = buffers[workerId];
                    local.Clear();

                    if (start >= end) return;

                    ref PositionRecord posRef = ref MemoryMarshal.GetArrayDataReference(positions);
                    ref FeatureEntry featRef = ref MemoryMarshal.GetArrayDataReference(features);
                    ref double targetRef = ref MemoryMarshal.GetArrayDataReference(targets);
                    ref Vector128<double> wRef = ref MemoryMarshal.GetArrayDataReference(weights);
                    ref Vector128<double> gRef = ref MemoryMarshal.GetArrayDataReference(local.Grad);
                    ref Vector128<double> phaseWRef = ref MemoryMarshal.GetArrayDataReference(PhaseWeights);

                    double localErr = 0.0;

                    for (nint p = start; p < end; p++)
                    {
                        ref readonly PositionRecord pos = ref Unsafe.Add(ref posRef, p);
                        ref FeatureEntry fBase = ref Unsafe.Add(ref featRef, (nint)pos.StartOffset);
                        nint len = pos.Length;

                        Vector128<double> sum0 = Vector128<double>.Zero;
                        Vector128<double> sum1 = Vector128<double>.Zero;
                        nint i = 0;

                        for (; i <= len - 4; i += 4)
                        {
                            ref FeatureEntry fCurr = ref Unsafe.Add(ref fBase, i);
                            ref readonly FeatureEntry f0 = ref fCurr;
                            ref readonly FeatureEntry f1 = ref Unsafe.Add(ref fCurr, 1);
                            ref readonly FeatureEntry f2 = ref Unsafe.Add(ref fCurr, 2);
                            ref readonly FeatureEntry f3 = ref Unsafe.Add(ref fCurr, 3);

                            sum0 += Unsafe.Add(ref wRef, (nint)f0.Index) * (double)f0.Coeff
                                  + Unsafe.Add(ref wRef, (nint)f1.Index) * (double)f1.Coeff;
                            sum1 += Unsafe.Add(ref wRef, (nint)f2.Index) * (double)f2.Coeff
                                  + Unsafe.Add(ref wRef, (nint)f3.Index) * (double)f3.Coeff;
                        }

                        Vector128<double> sum = sum0 + sum1;
                        for (; i < len; i++)
                        {
                            ref readonly FeatureEntry f = ref Unsafe.Add(ref fBase, i);
                            sum += Unsafe.Add(ref wRef, (nint)f.Index) * (double)f.Coeff;
                        }

                        Vector128<double> phaseW = Unsafe.Add(ref phaseWRef, (nint)pos.Phase);
                        double eval = Vector128.Dot(sum, phaseW);
                        double sigmoid = 1.0 / (1.0 + Math.Exp(-scale * eval));

                        double diff = sigmoid - Unsafe.Add(ref targetRef, p);
                        localErr += diff * diff;

                        double baseGrad = twoScale * diff * sigmoid * (1.0 - sigmoid);
                        Vector128<double> gradVec = phaseW * baseGrad;

                        i = 0;
                        for (; i <= len - 4; i += 4)
                        {
                            ref FeatureEntry fCurr = ref Unsafe.Add(ref fBase, i);
                            ref readonly FeatureEntry f0 = ref fCurr;
                            ref readonly FeatureEntry f1 = ref Unsafe.Add(ref fCurr, 1);
                            ref readonly FeatureEntry f2 = ref Unsafe.Add(ref fCurr, 2);
                            ref readonly FeatureEntry f3 = ref Unsafe.Add(ref fCurr, 3);

                            Unsafe.Add(ref gRef, (nint)f0.Index) += gradVec * (double)f0.Coeff;
                            Unsafe.Add(ref gRef, (nint)f1.Index) += gradVec * (double)f1.Coeff;
                            Unsafe.Add(ref gRef, (nint)f2.Index) += gradVec * (double)f2.Coeff;
                            Unsafe.Add(ref gRef, (nint)f3.Index) += gradVec * (double)f3.Coeff;
                        }

                        for (; i < len; i++)
                        {
                            ref readonly FeatureEntry f = ref Unsafe.Add(ref fBase, i);
                            Unsafe.Add(ref gRef, (nint)f.Index) += gradVec * (double)f.Coeff;
                        }
                    }

                    local.SquaredError = localErr;
                });

                double totalSquaredError = buffers[0].SquaredError;
                Vector128<double>[] grad0 = buffers[0].Grad;
                for (int w = 1; w < workerCount; w++)
                {
                    totalSquaredError += buffers[w].SquaredError;
                    Vector128<double>[] workerGrad = buffers[w].Grad;
                    for (int i = 0; i < TotalFeatures; i++)
                    {
                        grad0[i] += workerGrad[i];
                    }
                }

                double currentMse = totalSquaredError * invN;
                double lr = initialLr / (1.0 + lrDecay * epoch);

                if (currentMse < bestMse)
                {
                    bestMse = currentMse;
                    Array.Copy(weights, bestWeights, TotalFeatures);
                }

                if ((anchorMse - currentMse) > (anchorMse * minRelImprovement))
                {
                    anchorMse = currentMse;
                    noImprovementEpochs = 0;
                }
                else
                {
                    noImprovementEpochs++;
                }

                if (noImprovementEpochs >= patience)
                {
                    Console.WriteLine($"\n[Early Stopping] Converged at epoch {epoch}. Best MSE = {bestMse:F8}.");
                    break;
                }

                // TempoBonus is a single phase-independent parameter in Evaluation.cs,
                // so its gradient is the sum of the MG and EG partial derivatives.
                double tempoTotalGrad = grad0[TempoIdx].GetElement(0) + grad0[TempoIdx].GetElement(1);
                grad0[TempoIdx] = Vector128.Create(tempoTotalGrad, tempoTotalGrad);

                for (int i = 0; i < TotalFeatures; i++)
                {
                    Vector128<double> g = (grad0[i] * invN) + (weights[i] * lambda);
                    Vector128<double> m = (mVec[i] * beta1) + (g * oneMinusBeta1);
                    Vector128<double> v = (vVec[i] * beta2) + (g * g * oneMinusBeta2);
                    mVec[i] = m;
                    vVec[i] = v;

                    Vector128<double> mHat = m * invBiasCorr1;
                    Vector128<double> vHat = v * invBiasCorr2;
                    Vector128<double> step = (mHat * lr) / (Vector128.Sqrt(vHat) + epsVec);

                    // Zero out Endgame update strictly for Middlegame-only King Safety / Outpost features
                    if (i >= KooIdx && i <= KnoIdx)
                    {
                        step = step.WithElement(1, 0.0);
                    }

                    weights[i] -= step;
                }

                if (epoch == 1 || epoch % 25 == 0 || epoch == maxEpochs)
                {
                    double msPerEpoch = epochSw.Elapsed.TotalMilliseconds / epoch;
                    Console.WriteLine($"Epoch {epoch,4} / {maxEpochs} | MSE: {currentMse:F6} | LR: {lr:F4} | Speed: {msPerEpoch:F2} ms/epoch");
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        Console.WriteLine($"\nOptimization complete in {totalSw.ElapsedMilliseconds:N0} ms! Best MSE: {bestMse:F8}");
        PrintFormattedResults(bestWeights);
        return 0;
    }

    private static (PositionRecord[] Positions, FeatureEntry[] Features, int[] ExpectedEvals) LoadDataset(string path)
    {
        List<PositionRecord> posList = new(1_000_000);
        List<FeatureEntry> featList = new(32_000_000);
        List<int> evalList = new(1_000_000);

        short[] counts = new short[TotalFeatures];

        foreach (string rawLine in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            if (!TryParseDatasetLine(rawLine.AsSpan(), out string fenPart, out float whiteResult, out short searchEval))
                continue;

            ParseFEN(fenPart);

            // Guard against missing/duplicate kings or pawns on ranks 1/8
            if (PopCount(bitboards[K]) != 1 || PopCount(bitboards[k]) != 1 ||
                ((bitboards[P] | bitboards[p]) & 0xFF000000000000FFUL) != 0)
            {
                continue;
            }

            Array.Clear(counts);
            ExtractFeatures(counts);

            int startOffset = featList.Count;
            ushort activeCount = 0;

            for (int i = 0; i < TotalFeatures; i++)
            {
                short c = counts[i];
                if (c != 0)
                {
                    featList.Add(new FeatureEntry((ushort)i, c));
                    activeCount++;
                }
            }

            int phaseCount =
                PopCount(bitboards[N] | bitboards[n]) +
                PopCount(bitboards[B] | bitboards[b]) +
                (PopCount(bitboards[R] | bitboards[r]) << 1) +
                (PopCount(bitboards[Q] | bitboards[q]) << 2);

            byte phase = (byte)Math.Min(Evaluation.TotalPhase, phaseCount);
            sbyte stmSign = (sbyte)(side == White ? 1 : -1);

            posList.Add(new PositionRecord(startOffset, activeCount, phase, stmSign, whiteResult, searchEval));
            evalList.Add(Evaluation.Evaluate());
        }

        return (posList.ToArray(), featList.ToArray(), evalList.ToArray());
    }

    private static bool TryParseDatasetLine(ReadOnlySpan<char> line, out string fen, out float whiteResult, out short searchEval)
    {
        fen = string.Empty;
        whiteResult = 0.5f;
        searchEval = NoEval;

        int bracketStart = line.IndexOf('[');
        if (bracketStart != -1)
        {
            int closeRel = line[(bracketStart + 1)..].IndexOf(']');
            if (closeRel != -1)
            {
                int bracketEnd = bracketStart + 1 + closeRel;
                ReadOnlySpan<char> resSpan = line.Slice(bracketStart + 1, closeRel).Trim();

                if (!float.TryParse(resSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out whiteResult))
                {
                    if (resSpan is "1-0" or "1.0") whiteResult = 1.0f;
                    else if (resSpan is "0-1" or "0.0") whiteResult = 0.0f;
                    else if (resSpan is "1/2-1/2" or "0.5") whiteResult = 0.5f;
                    else return false;
                }

                ReadOnlySpan<char> remaining = line[(bracketEnd + 1)..].Trim();
                if (!remaining.IsEmpty)
                {
                    int spaceIdx = remaining.IndexOf(' ');
                    ReadOnlySpan<char> evalSpan = spaceIdx == -1 ? remaining : remaining[..spaceIdx];
                    if (short.TryParse(evalSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out short ev))
                    {
                        searchEval = ev;
                    }
                }

                fen = line[..bracketStart].Trim().ToString();
                return true;
            }
        }

        int pipeIdx = line.IndexOf('|');
        if (pipeIdx > 0)
        {
            ReadOnlySpan<char> resSpan = line[..pipeIdx].Trim();

            if (!float.TryParse(resSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out whiteResult))
            {
                if (resSpan.Contains("1-0", StringComparison.Ordinal)) whiteResult = 1.0f;
                else if (resSpan.Contains("0-1", StringComparison.Ordinal)) whiteResult = 0.0f;
                else if (resSpan.Contains("1/2", StringComparison.Ordinal)) whiteResult = 0.5f;
                else return false;
            }

            fen = line[(pipeIdx + 1)..].Trim().ToString();
            return true;
        }

        int c9Idx = line.IndexOf("c9", StringComparison.Ordinal);
        if (c9Idx > 0)
        {
            ReadOnlySpan<char> resPart = line[c9Idx..];
            if (resPart.Contains("\"1-0\"", StringComparison.Ordinal) || resPart.Contains("\"1.0\"", StringComparison.Ordinal))
                whiteResult = 1.0f;
            else if (resPart.Contains("\"0-1\"", StringComparison.Ordinal) || resPart.Contains("\"0.0\"", StringComparison.Ordinal))
                whiteResult = 0.0f;
            else if (resPart.Contains("\"1/2-1/2\"", StringComparison.Ordinal) || resPart.Contains("\"0.5\"", StringComparison.Ordinal))
                whiteResult = 0.5f;
            else
                return false;

            fen = line[..c9Idx].Trim().ToString();
            return true;
        }

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
            counts[sq - 8] += 1;

            if ((Evaluation.WhitePassedMask[sq] & bP) == 0)
            {
                int rank = sq >> 3;
                if ((uint)(rank - 1) <= 5) counts[PassedBaseIdx + rank - 1] += 1;
            }
            if ((Evaluation.AdjacentFiles[sq & 7] & wP) == 0)
                counts[IsoIdx] += 1;
        }

        for (ulong x = bP; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[(sq ^ 56) - 8] -= 1;

            if ((Evaluation.BlackPassedMask[sq] & wP) == 0)
            {
                int rank = 7 - (sq >> 3);
                if ((uint)(rank - 1) <= 5) counts[PassedBaseIdx + rank - 1] -= 1;
            }
            if ((Evaluation.AdjacentFiles[sq & 7] & bP) == 0)
                counts[IsoIdx] -= 1;
        }

        int kmDiff = 0;
        for (ulong x = wN; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[48 + sq] += 1;

            kmDiff += PopCount(knightAttacks[sq] & ~wOcc) - Evaluation.KnightMobBase;

            int rank = sq >> 3;
            if ((uint)(rank - 2) <= 2
                && (Evaluation.WhiteOutpostMask[sq] & bP) == 0
                && (pawnAttacks[Black, sq] & wP) != 0)
            {
                counts[KnoIdx] += 1;
            }
        }

        for (ulong x = bN; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[48 + (sq ^ 56)] -= 1;

            kmDiff -= PopCount(knightAttacks[sq] & ~bOcc) - Evaluation.KnightMobBase;

            int rank = sq >> 3;
            if ((uint)(rank - 3) <= 2
                && (Evaluation.BlackOutpostMask[sq] & wP) == 0
                && (pawnAttacks[White, sq] & bP) != 0)
            {
                counts[KnoIdx] -= 1;
            }
        }
        counts[KmIdx] += (short)kmDiff;

        int bmDiff = 0;
        for (ulong x = wB; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[112 + sq] += 1;
            bmDiff += PopCount(GetBishopAttacks(sq, allOcc) & ~wOcc) - Evaluation.BishopMobBase;
        }
        if ((wB & (wB - 1)) != 0) counts[BpIdx] += 1;

        for (ulong x = bBi; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[112 + (sq ^ 56)] -= 1;
            bmDiff -= PopCount(GetBishopAttacks(sq, allOcc) & ~bOcc) - Evaluation.BishopMobBase;
        }
        if ((bBi & (bBi - 1)) != 0) counts[BpIdx] -= 1;
        counts[BmIdx] += (short)bmDiff;

        for (ulong x = wR; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            counts[176 + sq] += 1;

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
            counts[176 + (sq ^ 56)] -= 1;

            ulong fMask = Evaluation.FileMask[sq & 7];
            if ((bP & fMask) == 0)
            {
                if ((allPawns & fMask) == 0) counts[RoIdx] -= 1;
                else counts[RsoIdx] -= 1;
            }
        }

        for (ulong x = wQ; x != 0; x &= x - 1)
            counts[240 + TrailingZeroCount(x)] += 1;

        for (ulong x = bQ; x != 0; x &= x - 1)
            counts[240 + (TrailingZeroCount(x) ^ 56)] -= 1;

        int wkSq = TrailingZeroCount(wK);
        int bkSq = TrailingZeroCount(bK);
        counts[304 + wkSq] += 1;
        counts[304 + (bkSq ^ 56)] -= 1;

        ExtractKingExposure(wkSq & 7, wP, allPawns, -1, counts);
        ExtractKingExposure(bkSq & 7, bP, allPawns, +1, counts);

        if ((wQ | bQ) == 0)
        {
            counts[QkcIdx] += (short)(Evaluation.KingCenterTable[wkSq] - Evaluation.KingCenterTable[bkSq]);
        }

        counts[TempoIdx] = (short)(side == White ? 1 : -1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        return piece == 0 ? square - 8 : (piece << 6) - 16 + square;
    }

    private static void ValidateScores(
        PositionRecord[] positions,
        FeatureEntry[] features,
        int[] expectedEvals,
        Vector128<double>[] initialWeights)
    {
        ref Vector128<double> wRef = ref MemoryMarshal.GetArrayDataReference(initialWeights);
        int tempoBonus = (int)Math.Round(initialWeights[TempoIdx].GetElement(0));

        for (int p = 0; p < positions.Length; p++)
        {
            ref readonly PositionRecord pos = ref positions[p];
            ReadOnlySpan<FeatureEntry> feats = features.AsSpan(pos.StartOffset, pos.Length);

            Vector128<double> sum = Vector128<double>.Zero;
            foreach (ref readonly FeatureEntry f in feats)
            {
                if (f.Index == TempoIdx) continue;
                sum += Unsafe.Add(ref wRef, (nint)f.Index) * (double)f.Coeff;
            }

            int mgScore = (int)Math.Round(sum.GetElement(0));
            int egScore = (int)Math.Round(sum.GetElement(1));
            int whiteScore = (mgScore * pos.Phase + egScore * (Evaluation.TotalPhase - pos.Phase)) / Evaluation.TotalPhase;
            int calcEval = (pos.StmSign * whiteScore) + tempoBonus;

            if (calcEval != expectedEvals[p])
            {
                throw new InvalidOperationException(
                    $"Score mismatch at position {p}: Cached={calcEval}, Evaluation.Evaluate()={expectedEvals[p]}");
            }
        }

        Console.WriteLine($"[Validation] 100% match across {positions.Length:N0} positions with Evaluation.Evaluate().");
    }

    private static double[] PrecomputeTargets(PositionRecord[] positions, double scale)
    {
        double[] targets = new double[positions.Length];
        int workerCount = Environment.ProcessorCount;
        int chunkSize = (positions.Length + workerCount - 1) / workerCount;

        Parallel.For(0, workerCount, workerId =>
        {
            int start = workerId * chunkSize;
            int end = Math.Min(start + chunkSize, positions.Length);
            ref PositionRecord posRef = ref MemoryMarshal.GetArrayDataReference(positions);
            ref double targetRef = ref MemoryMarshal.GetArrayDataReference(targets);

            for (nint p = start; p < end; p++)
            {
                ref readonly PositionRecord pos = ref Unsafe.Add(ref posRef, p);
                double target = pos.Result;
                if (pos.Eval != NoEval)
                {
                    double searchEvalWhite = pos.Eval * pos.StmSign;
                    double evalTargetProb = 1.0 / (1.0 + Math.Exp(-scale * searchEvalWhite));
                    target = 0.5 * pos.Result + 0.5 * evalTargetProb;
                }
                Unsafe.Add(ref targetRef, p) = target;
            }
        });

        return targets;
    }

    private static double ComputeMseWithTargets(
        PositionRecord[] positions,
        FeatureEntry[] features,
        double[] targets,
        Vector128<double>[] weights,
        double scale)
    {
        int workerCount = Environment.ProcessorCount;
        double[] workerErrors = new double[workerCount];
        int chunkSize = (positions.Length + workerCount - 1) / workerCount;

        Parallel.For(0, workerCount, workerId =>
        {
            int start = workerId * chunkSize;
            int end = Math.Min(start + chunkSize, positions.Length);
            if (start >= end) return;

            ref PositionRecord posRef = ref MemoryMarshal.GetArrayDataReference(positions);
            ref FeatureEntry featRef = ref MemoryMarshal.GetArrayDataReference(features);
            ref double targetRef = ref MemoryMarshal.GetArrayDataReference(targets);
            ref Vector128<double> wRef = ref MemoryMarshal.GetArrayDataReference(weights);
            ref Vector128<double> phaseWRef = ref MemoryMarshal.GetArrayDataReference(PhaseWeights);

            double localErr = 0.0;

            for (nint p = start; p < end; p++)
            {
                ref readonly PositionRecord pos = ref Unsafe.Add(ref posRef, p);
                ref FeatureEntry fBase = ref Unsafe.Add(ref featRef, (nint)pos.StartOffset);
                nint len = pos.Length;

                Vector128<double> sum0 = Vector128<double>.Zero;
                Vector128<double> sum1 = Vector128<double>.Zero;
                nint i = 0;

                for (; i <= len - 4; i += 4)
                {
                    ref FeatureEntry fCurr = ref Unsafe.Add(ref fBase, i);
                    sum0 += Unsafe.Add(ref wRef, (nint)fCurr.Index) * (double)fCurr.Coeff
                          + Unsafe.Add(ref wRef, (nint)Unsafe.Add(ref fCurr, 1).Index) * (double)Unsafe.Add(ref fCurr, 1).Coeff;
                    sum1 += Unsafe.Add(ref wRef, (nint)Unsafe.Add(ref fCurr, 2).Index) * (double)Unsafe.Add(ref fCurr, 2).Coeff
                          + Unsafe.Add(ref wRef, (nint)Unsafe.Add(ref fCurr, 3).Index) * (double)Unsafe.Add(ref fCurr, 3).Coeff;
                }

                Vector128<double> sum = sum0 + sum1;
                for (; i < len; i++)
                {
                    ref readonly FeatureEntry f = ref Unsafe.Add(ref fBase, i);
                    sum += Unsafe.Add(ref wRef, (nint)f.Index) * (double)f.Coeff;
                }

                Vector128<double> phaseW = Unsafe.Add(ref phaseWRef, (nint)pos.Phase);
                double eval = Vector128.Dot(sum, phaseW);
                double sigmoid = 1.0 / (1.0 + Math.Exp(-scale * eval));
                double diff = sigmoid - Unsafe.Add(ref targetRef, p);
                localErr += diff * diff;
            }

            workerErrors[workerId] = localErr;
        });

        double totalError = 0.0;
        for (int w = 0; w < workerCount; w++)
        {
            totalError += workerErrors[w];
        }

        return totalError / positions.Length;
    }

    private static double ComputeMse(
        PositionRecord[] positions,
        FeatureEntry[] features,
        Vector128<double>[] weights,
        double scale)
    {
        int workerCount = Environment.ProcessorCount;
        double[] workerErrors = new double[workerCount];
        int chunkSize = (positions.Length + workerCount - 1) / workerCount;

        Parallel.For(0, workerCount, workerId =>
        {
            int start = workerId * chunkSize;
            int end = Math.Min(start + chunkSize, positions.Length);
            if (start >= end) return;

            ref PositionRecord posRef = ref MemoryMarshal.GetArrayDataReference(positions);
            ref FeatureEntry featRef = ref MemoryMarshal.GetArrayDataReference(features);
            ref Vector128<double> wRef = ref MemoryMarshal.GetArrayDataReference(weights);
            ref Vector128<double> phaseWRef = ref MemoryMarshal.GetArrayDataReference(PhaseWeights);

            double localErr = 0.0;

            for (nint p = start; p < end; p++)
            {
                ref readonly PositionRecord pos = ref Unsafe.Add(ref posRef, p);
                ref FeatureEntry fBase = ref Unsafe.Add(ref featRef, (nint)pos.StartOffset);
                nint len = pos.Length;

                Vector128<double> sum0 = Vector128<double>.Zero;
                Vector128<double> sum1 = Vector128<double>.Zero;
                nint i = 0;

                for (; i <= len - 4; i += 4)
                {
                    ref FeatureEntry fCurr = ref Unsafe.Add(ref fBase, i);
                    sum0 += Unsafe.Add(ref wRef, (nint)fCurr.Index) * (double)fCurr.Coeff
                          + Unsafe.Add(ref wRef, (nint)Unsafe.Add(ref fCurr, 1).Index) * (double)Unsafe.Add(ref fCurr, 1).Coeff;
                    sum1 += Unsafe.Add(ref wRef, (nint)Unsafe.Add(ref fCurr, 2).Index) * (double)Unsafe.Add(ref fCurr, 2).Coeff
                          + Unsafe.Add(ref wRef, (nint)Unsafe.Add(ref fCurr, 3).Index) * (double)Unsafe.Add(ref fCurr, 3).Coeff;
                }

                Vector128<double> sum = sum0 + sum1;
                for (; i < len; i++)
                {
                    ref readonly FeatureEntry f = ref Unsafe.Add(ref fBase, i);
                    sum += Unsafe.Add(ref wRef, (nint)f.Index) * (double)f.Coeff;
                }

                Vector128<double> phaseW = Unsafe.Add(ref phaseWRef, (nint)pos.Phase);
                double eval = Vector128.Dot(sum, phaseW);
                double sigmoid = 1.0 / (1.0 + Math.Exp(-scale * eval));

                double diff = sigmoid - pos.Result;
                localErr += diff * diff;
            }

            workerErrors[workerId] = localErr;
        });

        double totalError = 0.0;
        for (int w = 0; w < workerCount; w++)
        {
            totalError += workerErrors[w];
        }

        return totalError / positions.Length;
    }

    private static double FindOptimalK(PositionRecord[] positions, FeatureEntry[] features, Vector128<double>[] weights)
    {
        double a = 0.1, b = 5.0;
        double phi = (1.0 + Math.Sqrt(5.0)) / 2.0;
        double resphi = 2.0 - phi;
        double logFactor = Math.Log(10.0) / 400.0;

        double c = a + resphi * (b - a);
        double d = b - resphi * (b - a);
        double fc = ComputeMse(positions, features, weights, c * logFactor);
        double fd = ComputeMse(positions, features, weights, d * logFactor);

        for (int i = 0; i < 30; i++)
        {
            if (fc < fd)
            {
                b = d; d = c; fd = fc;
                c = a + resphi * (b - a);
                fc = ComputeMse(positions, features, weights, c * logFactor);
            }
            else
            {
                a = c; c = d; fc = fd;
                d = b - resphi * (b - a);
                fd = ComputeMse(positions, features, weights, d * logFactor);
            }
        }

        return (a + b) * 0.5;
    }

    private static void PrintFormattedResults(Vector128<double>[] weights)
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

        for (int pc = 0; pc < 5; pc++)
        {
            int minSq = pc == 0 ? 8 : 0;
            int maxSq = pc == 0 ? 56 : 64;
            int count = maxSq - minSq;

            Vector128<double> sum = Vector128<double>.Zero;
            for (int sq = minSq; sq < maxSq; sq++)
            {
                sum += weights[GetPstIndex(pc, sq)];
            }

            int mgAvg = (int)Math.Round(sum.GetElement(0) / count);
            int egAvg = (int)Math.Round(sum.GetElement(1) / count);

            tunedMgMat[pc] = mgAvg;
            tunedEgMat[pc] = egAvg;

            for (int sq = minSq; sq < maxSq; sq++)
            {
                Vector128<double> w = weights[GetPstIndex(pc, sq)];
                tunedMgPst[pc][sq] = (int)Math.Round(w.GetElement(0)) - mgAvg;
                tunedEgPst[pc][sq] = (int)Math.Round(w.GetElement(1)) - egAvg;
            }
        }

        for (int sq = 0; sq < 64; sq++)
        {
            Vector128<double> w = weights[GetPstIndex(5, sq)];
            tunedMgPst[5][sq] = (int)Math.Round(w.GetElement(0));
            tunedEgPst[5][sq] = (int)Math.Round(w.GetElement(1));
        }

        int[] passMg = new int[8], passEg = new int[8];
        for (int d = 1; d <= 6; d++)
        {
            Vector128<double> w = weights[PassedBaseIdx + d - 1];
            passMg[d] = (int)Math.Round(w.GetElement(0));
            passEg[d] = (int)Math.Round(w.GetElement(1));
        }

        Console.WriteLine("\n===============================================================================");
        Console.WriteLine("// Ready to paste tuned parameters for Evaluation.cs:");
        Console.WriteLine("===============================================================================");
        Console.WriteLine("    #region Material & Phase\n");
        Console.WriteLine("    // Base material values in centipawns, indexed by piece type:");
        Console.WriteLine("    //   0=Pawn  1=Knight  2=Bishop  3=Rook  4=Queen  5=King");
        Console.WriteLine("    // King is 0 because it's never captured; its \"value\" is implicit in game");
        Console.WriteLine("    // termination, not in evaluation.");
        Console.WriteLine($"    internal static readonly int[] MgMaterial = [{tunedMgMat[0]}, {tunedMgMat[1]}, {tunedMgMat[2]}, {tunedMgMat[3]}, {tunedMgMat[4]}, 0];");
        Console.WriteLine($"    internal static readonly int[] EgMaterial = [{tunedEgMat[0]}, {tunedEgMat[1]}, {tunedEgMat[2]}, {tunedEgMat[3]}, {tunedEgMat[4]}, 0];\n");
        Console.WriteLine("    // Starting position phase: 2 sides × (2×Knight + 2×Bishop + 2×Rook + 1×Queen)");
        Console.WriteLine("    //   = 2 × (2·1 + 2·1 + 2·2 + 1·4) = 24.");
        Console.WriteLine("    // Phase is accumulated inline during evaluation (Knight/Bishop += 1,");
        Console.WriteLine("    // Rook += 2, Queen += 4) rather than through a lookup table.");
        Console.WriteLine("    internal const int TotalPhase = 24;\n");
        Console.WriteLine("    #endregion\n");

        Console.WriteLine("    #region Positional Bonuses / Penalties\n");
        Console.WriteLine("    // Having both bishops is worth a small bonus because they cover");
        Console.WriteLine("    // complementary colour complexes.");
        Console.WriteLine($"    internal const int BishopPairMg = {(int)Math.Round(weights[BpIdx].GetElement(0))}, BishopPairEg = {(int)Math.Round(weights[BpIdx].GetElement(1))};\n");
        Console.WriteLine("    // Mobility = (number of safe destination squares) − (a baseline).");
        Console.WriteLine("    // Subtracting a baseline keeps the \"average\" piece near zero so the");
        Console.WriteLine("    // mobility term doesn't silently inflate material values.");
        Console.WriteLine($"    internal const int KnightMobMg = {(int)Math.Round(weights[KmIdx].GetElement(0))}, KnightMobEg = {(int)Math.Round(weights[KmIdx].GetElement(1))}, KnightMobBase = 4;");
        Console.WriteLine($"    internal const int BishopMobMg = {(int)Math.Round(weights[BmIdx].GetElement(0))}, BishopMobEg = {(int)Math.Round(weights[BmIdx].GetElement(1))}, BishopMobBase = 6;\n");
        Console.WriteLine("    // Rook on a file with no friendly pawn:");
        Console.WriteLine("    //   semi-open = only enemy pawns on it (good attacking lane)");
        Console.WriteLine("    //   open      = no pawns at all (best — rook sees the whole file)");
        Console.WriteLine($"    internal const int RookSemiOpenMg = {(int)Math.Round(weights[RsoIdx].GetElement(0))}, RookSemiOpenEg = {(int)Math.Round(weights[RsoIdx].GetElement(1))};");
        Console.WriteLine($"    internal const int RookOpenMg = {(int)Math.Round(weights[RoIdx].GetElement(0))}, RookOpenEg = {(int)Math.Round(weights[RoIdx].GetElement(1))};\n");
        Console.WriteLine("    // Passed pawn bonus indexed by squares left to promotion (0..7):");
        Console.WriteLine("    //   index 1 = one step from promoting, index 6 = still on its start square.");
        Console.WriteLine("    // A pawn gets this if no enemy pawn can block or capture it on its way");
        Console.WriteLine("    // to promotion. The closer to promotion, the bigger the bonus — much");
        Console.WriteLine("    // larger in the endgame where promotion threats dominate.");
        Console.WriteLine($"    internal static readonly int[] PassedMg = [{passMg[0]}, {passMg[1]}, {passMg[2]}, {passMg[3]}, {passMg[4]}, {passMg[5]}, {passMg[6]}, {passMg[7]}];");
        Console.WriteLine($"    internal static readonly int[] PassedEg = [{passEg[0]}, {passEg[1]}, {passEg[2]}, {passEg[3]}, {passEg[4]}, {passEg[5]}, {passEg[6]}, {passEg[7]}];\n");
        Console.WriteLine("    // A pawn with no friendly pawn on either adjacent file. Weak because it");
        Console.WriteLine("    // can never be defended by another pawn.");
        Console.WriteLine($"    internal const int IsolatedMg = {(int)Math.Round(weights[IsoIdx].GetElement(0))}, IsolatedEg = {(int)Math.Round(weights[IsoIdx].GetElement(1))};\n");
        Console.WriteLine("    // King safety: penalties for open/semi-open files near the king. Only");
        Console.WriteLine("    // applied in the middlegame — in the endgame the king wants to be active.");
        Console.WriteLine($"    internal const int KingOwnOpenMg = {(int)Math.Round(weights[KooIdx].GetElement(0))}, KingOwnSemiOpenMg = {(int)Math.Round(weights[KosoIdx].GetElement(0))};");
        Console.WriteLine($"    internal const int KingAdjacentOpenMg = {(int)Math.Round(weights[KaoIdx].GetElement(0))}, KingAdjacentSemiOpenMg = {(int)Math.Round(weights[KasoIdx].GetElement(0))};\n");
        Console.WriteLine("    // Once both queens are gone, encourage the king toward the centre so it");
        Console.WriteLine("    // can help its pawns promote.");
        Console.WriteLine($"    internal const int QueenlessKingCenterMg = {(int)Math.Round(weights[QkcIdx].GetElement(0))};\n");
        Console.WriteLine("    // Knight sitting on a square where:");
        Console.WriteLine("    //   • it's protected by a friendly pawn");
        Console.WriteLine("    //   • it can't be kicked by any enemy pawn on adjacent files");
        Console.WriteLine("    // Outposts are classical strong squares (think c5/d5/e5/f5 for White).");
        Console.WriteLine($"    internal const int KnightOutpostMg = {(int)Math.Round(weights[KnoIdx].GetElement(0))};\n");
        Console.WriteLine("    // Tempo bonus: static advantage awarded to the side to move");
        Console.WriteLine($"    internal const int TempoBonus = {(int)Math.Round(weights[TempoIdx].GetElement(0))};\n");
        Console.WriteLine("    #endregion\n");

        Console.WriteLine("    // =========================================================================");
        Console.WriteLine("    //  SECTION 2 — Piece–square tables (PeSTO)");
        Console.WriteLine("    //");
        Console.WriteLine("    //  One 64-entry table per piece type, written from White's perspective");
        Console.WriteLine("    //  with square a8 at index 0 and h1 at index 63 — exactly matching the");
        Console.WriteLine("    //  board's visual layout when you read top-to-bottom, left-to-right.");
        Console.WriteLine("    //");
        Console.WriteLine("    //  These are \"source\" tables. At startup we fold them with the material");
        Console.WriteLine("    //  values into a single flat lookup (PstScoreTable) for fast access.");
        Console.WriteLine("    // =========================================================================\n");
        Console.WriteLine("    #region PST Data\n");

        string[] mgPstComments = [
            "// Pawn — the back rank is zero because pawns never stand there.",
            "// Knight — rim is dim; centre squares are prized.",
            "// Bishop — long diagonals and fianchetto squares.",
            "// Rook — likes the 7th rank and central files.",
            "// Queen — modest PST; mobility and tactics dominate its value.",
            "// King (middlegame) — castled corners are safe; the centre is fatal."
        ];

        Console.WriteLine("    internal static readonly int[][] MgPstSource =");
        Console.WriteLine("    [");
        for (int pc = 0; pc < 6; pc++)
        {
            Console.WriteLine($"        {mgPstComments[pc]}");
            PrintPstBlock(tunedMgPst[pc], pc < 5);
        }
        Console.WriteLine("    ];\n");

        string[] egPstComments = [
            "// Pawn — promotion race dominates; advanced pawns are huge.",
            "// Knight — still prefers the centre, but values are compressed.",
            "// Bishop — long diagonals remain preferable.",
            "// Rook — activity matters more than any one square.",
            "// Queen — rewards centralization in simplified positions.",
            "// King (endgame) — march to the centre to support pawns."
        ];

        Console.WriteLine("    internal static readonly int[][] EgPstSource =");
        Console.WriteLine("    [");
        for (int pc = 0; pc < 6; pc++)
        {
            Console.WriteLine($"        {egPstComments[pc]}");
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

    private static Vector128<double>[] GetCurrentEvaluationWeights()
    {
        Vector128<double>[] w = new Vector128<double>[TotalFeatures];
        for (int pc = 0; pc < 6; pc++)
        {
            int minSq = pc == 0 ? 8 : 0;
            int maxSq = pc == 0 ? 56 : 64;
            int mgMat = Evaluation.MgMaterial[pc];
            int egMat = Evaluation.EgMaterial[pc];
            int[] mgPst = Evaluation.MgPstSource[pc];
            int[] egPst = Evaluation.EgPstSource[pc];

            for (int sq = minSq; sq < maxSq; sq++)
            {
                w[GetPstIndex(pc, sq)] = Vector128.Create(
                    (double)(mgMat + mgPst[sq]),
                    (double)(egMat + egPst[sq])
                );
            }
        }

        w[BpIdx] = Vector128.Create((double)Evaluation.BishopPairMg, (double)Evaluation.BishopPairEg);
        w[KmIdx] = Vector128.Create((double)Evaluation.KnightMobMg, (double)Evaluation.KnightMobEg);
        w[BmIdx] = Vector128.Create((double)Evaluation.BishopMobMg, (double)Evaluation.BishopMobEg);
        w[RsoIdx] = Vector128.Create((double)Evaluation.RookSemiOpenMg, (double)Evaluation.RookSemiOpenEg);
        w[RoIdx] = Vector128.Create((double)Evaluation.RookOpenMg, (double)Evaluation.RookOpenEg);

        for (int d = 1; d <= 6; d++)
            w[PassedBaseIdx + d - 1] = Vector128.Create((double)Evaluation.PassedMg[d], (double)Evaluation.PassedEg[d]);

        w[IsoIdx] = Vector128.Create((double)Evaluation.IsolatedMg, (double)Evaluation.IsolatedEg);
        w[KooIdx] = Vector128.Create((double)Evaluation.KingOwnOpenMg, 0.0);
        w[KosoIdx] = Vector128.Create((double)Evaluation.KingOwnSemiOpenMg, 0.0);
        w[KaoIdx] = Vector128.Create((double)Evaluation.KingAdjacentOpenMg, 0.0);
        w[KasoIdx] = Vector128.Create((double)Evaluation.KingAdjacentSemiOpenMg, 0.0);
        w[QkcIdx] = Vector128.Create((double)Evaluation.QueenlessKingCenterMg, 0.0);
        w[KnoIdx] = Vector128.Create((double)Evaluation.KnightOutpostMg, 0.0);
        w[TempoIdx] = Vector128.Create((double)Evaluation.TempoBonus, (double)Evaluation.TempoBonus);
        return w;
    }

    private static Vector128<double>[] GetPestoBaselineWeights()
    {
        Vector128<double>[] w = new Vector128<double>[TotalFeatures];

        for (int pc = 0; pc < 6; pc++)
        {
            int minSq = pc == 0 ? 8 : 0;
            int maxSq = pc == 0 ? 56 : 64;
            int mgMat = OfficialMgMaterial[pc];
            int egMat = OfficialEgMaterial[pc];
            int[] mgPst = OfficialMgPst[pc];
            int[] egPst = OfficialEgPst[pc];

            for (int sq = minSq; sq < maxSq; sq++)
            {
                w[GetPstIndex(pc, sq)] = Vector128.Create(
                    (double)(mgMat + mgPst[sq]),
                    (double)(egMat + egPst[sq])
                );
            }
        }

        // All secondary positional features (368..385) and TempoIdx (386) remain 0.0
        return w;
    }
}