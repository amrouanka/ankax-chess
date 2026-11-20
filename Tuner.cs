using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using static System.Numerics.BitOperations;
using static Board;
using static MoveGenerator;
using static PieceAttacks;
using static Evaluation;

// =============================================================================
//  Tuner — Fast Texel Evaluation Parameter Tuning Tool
//
//  Methodology:
//      Texel tuning fits static evaluation terms to real game outcomes using
//      quiet position datasets (`score | FEN` per line).
//
//  Sigmoid Function:
//      P(Win) = 1 / (1 + 10 ^ (-K * Eval / 400))
//
//  Speed:
//      Every tunable term in Evaluate() is linear:  eval = Σ coef_f × weight_f
//      plus a per-position constant (the PSTs). Feature coefficients are
//      extracted ONCE per position. Coordinate descent then only touches
//      integer arrays — Evaluate() and the board are never called again.
//
//  Guarantees:
//      After extraction, the feature-based score is checked against
//      Evaluate() for every position. Any mismatch aborts the run.
//
//  Not tuned (by design):
//      TempoBonus      — side-to-move artifact in game datasets
//      KnightMobBase / BishopMobBase — baked into the mobility coefficient
//      PSTs            — treated as a per-position constant
// =============================================================================

public static class Tuner
{
    // =========================================================================
    //  SECTION 1 — Constants & Feature Layout
    // =========================================================================

    private const int TotalPhase = 24;
    private const int TempoBonus = 8;
    private const double Ln10 = 2.302585092994046;

    // Middlegame features
    private const int MgMat = 0;                 // 5 slots: P N B R Q
    private const int MgPassed = 5;              // 6 slots: PassedMg[1..6]
    private const int MgIsolated = 11;
    private const int MgKnightMob = 12;
    private const int MgKnightOutpost = 13;
    private const int MgBishopMob = 14;
    private const int MgBishopPair = 15;
    private const int MgRookSemiOpen = 16;
    private const int MgRookOpen = 17;
    private const int MgKingOwnOpen = 18;
    private const int MgKingOwnSemiOpen = 19;
    private const int MgKingAdjOpen = 20;
    private const int MgKingAdjSemiOpen = 21;
    private const int MgQueenlessCenter = 22;

    // Endgame features
    private const int EgMat = 23;                // 5 slots: P N B R Q
    private const int EgPassed = 28;             // 6 slots: PassedEg[1..6]
    private const int EgIsolated = 34;
    private const int EgKnightMob = 35;
    private const int EgBishopMob = 36;
    private const int EgBishopPair = 37;
    private const int EgRookSemiOpen = 38;
    private const int EgRookOpen = 39;

    private const int FirstEgFeature = EgMat;
    private const int FeatureCount = 40;

    // =========================================================================
    //  SECTION 2 — Data Structures
    // =========================================================================

    // Maps a feature slot to the Evaluation.cs field it represents.
    private sealed record Param(string Name, int Feature, Func<int> Get, Action<int> Set);

    private static int _count;
    private static short[][] _coef = [];     // _coef[feature][position]
    private static int[] _mgSum = [];        // current mg total per position (PST + Σ coef×w)
    private static int[] _egSum = [];        // current eg total per position
    private static byte[] _phase = [];       // mgPhase (0..24)
    private static sbyte[] _stm = [];        // +1 White to move, -1 Black to move
    private static double[] _result = [];    // 1.0 / 0.5 / 0.0 from White's view
    private static int[] _weights = new int[FeatureCount];

    // =========================================================================
    //  SECTION 3 — Main Entry Point & Tuning Loop
    // =========================================================================

    public static int Run(string[] args)
    {
        string dataPath = args.Length > 1 ? args[1] : "data/zurichess.txt";
        int maxEpochs = args.Length > 2 && int.TryParse(args[2], out int e) ? e : 50;

        if (!File.Exists(dataPath))
        {
            Console.WriteLine($"[!] Error: Dataset file not found at '{dataPath}'");
            return 1;
        }

        Console.WriteLine("=================================================");
        Console.WriteLine("   AnkaX Texel Evaluation Tuning Tool (fast)");
        Console.WriteLine("=================================================");

        List<Param> parameters = RegisterParameters();
        int[] initialWeights = new int[FeatureCount];
        foreach (var prm in parameters)
        {
            _weights[prm.Feature] = prm.Get();
            initialWeights[prm.Feature] = prm.Get();
        }

        // Step 1: Load dataset, extract features, verify against Evaluate()
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Loading dataset: '{dataPath}'...");
        int[] refEval = LoadAndExtract(dataPath);
        Console.WriteLine($"Loaded {_count:N0} positions, extracted {FeatureCount} features in {sw.Elapsed.TotalSeconds:F1}s.");

        for (int f = 0; f < FeatureCount; f++)
            ApplyDelta(f, _weights[f]);

        int mismatches = Verify(refEval);
        if (mismatches != 0)
        {
            Console.WriteLine($"[!] Feature extraction does not match Evaluate() on {mismatches:N0} positions. Aborting.");
            return 1;
        }
        Console.WriteLine("[+] Feature extraction verified: matches Evaluate() on every position.\n");

        // Step 2: Initial K and loss
        Console.WriteLine("Calculating optimal scaling constant K...");
        double bestK = FindOptimalK();
        double initialLoss = ComputeLoss(bestK);
        Console.WriteLine($"Initial K = {bestK:F6} | Initial MSE Loss = {initialLoss:F8}\n");

        // Step 3: Coordinate descent
        Console.WriteLine($"Starting optimization for {parameters.Count} parameters (Max Epochs: {maxEpochs})...\n");
        double currentLoss = initialLoss;

        for (int epoch = 1; epoch <= maxEpochs; epoch++)
        {
            sw.Restart();

            // Re-calibrate K each epoch; the loss must be re-measured under the new K
            bestK = FindOptimalK();
            currentLoss = ComputeLoss(bestK);
            int updatedCount = 0;

            foreach (var prm in parameters)
            {
                int f = prm.Feature;

                // Step +1
                ApplyDelta(f, +1);
                double lossPlus = ComputeLoss(bestK);
                if (lossPlus < currentLoss)
                {
                    _weights[f]++;
                    currentLoss = lossPlus;
                    updatedCount++;
                    continue;
                }

                // Step -1 (net from +1 is -2)
                ApplyDelta(f, -2);
                double lossMinus = ComputeLoss(bestK);
                if (lossMinus < currentLoss)
                {
                    _weights[f]--;
                    currentLoss = lossMinus;
                    updatedCount++;
                    continue;
                }

                // Revert to original (net 0)
                ApplyDelta(f, +1);
            }

            Console.WriteLine($"Epoch {epoch,3}: MSE Loss = {currentLoss:F8} | K = {bestK:F6} | Updated = {updatedCount,2} | {sw.Elapsed.TotalSeconds:F1}s");

            if (updatedCount == 0)
            {
                Console.WriteLine("\n[+] Convergence reached: Local minimum found.");
                break;
            }
        }

        // Step 4: Write results back to Evaluation fields and print
        foreach (var prm in parameters)
            prm.Set(_weights[prm.Feature]);

        PrintChanges(parameters, initialWeights);
        PrintTunedCode(initialLoss, currentLoss);

        return 0;
    }

    // =========================================================================
    //  SECTION 4 — Parameter Registration
    // =========================================================================

    private static List<Param> RegisterParameters()
    {
        List<Param> list = new();

        // Material (Pawn, Knight, Bishop, Rook, Queen). King is always 0.
        string[] pieceNames = ["Pawn", "Knight", "Bishop", "Rook", "Queen"];
        for (int i = 0; i < 5; i++)
        {
            int idx = i;
            list.Add(new($"MgMaterial[{pieceNames[idx]}]", MgMat + idx, () => MgMaterial[idx], v => MgMaterial[idx] = v));
            list.Add(new($"EgMaterial[{pieceNames[idx]}]", EgMat + idx, () => EgMaterial[idx], v => EgMaterial[idx] = v));
        }

        // Passed pawns by rank index 1..6 (index = sq >> 3 for White, a8 = 0,
        // so index 1 is one step from promotion and index 6 is the start rank).
        for (int r = 1; r <= 6; r++)
        {
            int idx = r;
            list.Add(new($"PassedMg[{idx}]", MgPassed + idx - 1, () => PassedMg[idx], v => PassedMg[idx] = v));
            list.Add(new($"PassedEg[{idx}]", EgPassed + idx - 1, () => PassedEg[idx], v => PassedEg[idx] = v));
        }

        list.Add(new("IsolatedMg", MgIsolated, () => IsolatedMg, v => IsolatedMg = v));
        list.Add(new("IsolatedEg", EgIsolated, () => IsolatedEg, v => IsolatedEg = v));

        list.Add(new("KnightMobMg", MgKnightMob, () => KnightMobMg, v => KnightMobMg = v));
        list.Add(new("KnightMobEg", EgKnightMob, () => KnightMobEg, v => KnightMobEg = v));
        list.Add(new("KnightOutpostMg", MgKnightOutpost, () => KnightOutpostMg, v => KnightOutpostMg = v));

        list.Add(new("BishopMobMg", MgBishopMob, () => BishopMobMg, v => BishopMobMg = v));
        list.Add(new("BishopMobEg", EgBishopMob, () => BishopMobEg, v => BishopMobEg = v));
        list.Add(new("BishopPairMg", MgBishopPair, () => BishopPairMg, v => BishopPairMg = v));
        list.Add(new("BishopPairEg", EgBishopPair, () => BishopPairEg, v => BishopPairEg = v));

        list.Add(new("RookSemiOpenMg", MgRookSemiOpen, () => RookSemiOpenMg, v => RookSemiOpenMg = v));
        list.Add(new("RookSemiOpenEg", EgRookSemiOpen, () => RookSemiOpenEg, v => RookSemiOpenEg = v));
        list.Add(new("RookOpenMg", MgRookOpen, () => RookOpenMg, v => RookOpenMg = v));
        list.Add(new("RookOpenEg", EgRookOpen, () => RookOpenEg, v => RookOpenEg = v));

        list.Add(new("KingOwnOpenMg", MgKingOwnOpen, () => KingOwnOpenMg, v => KingOwnOpenMg = v));
        list.Add(new("KingOwnSemiOpenMg", MgKingOwnSemiOpen, () => KingOwnSemiOpenMg, v => KingOwnSemiOpenMg = v));
        list.Add(new("KingAdjacentOpenMg", MgKingAdjOpen, () => KingAdjacentOpenMg, v => KingAdjacentOpenMg = v));
        list.Add(new("KingAdjacentSemiOpenMg", MgKingAdjSemiOpen, () => KingAdjacentSemiOpenMg, v => KingAdjacentSemiOpenMg = v));

        list.Add(new("QueenlessKingCenterMg", MgQueenlessCenter, () => QueenlessKingCenterMg, v => QueenlessKingCenterMg = v));

        return list;
    }

    // =========================================================================
    //  SECTION 5 — Dataset Loading & Feature Extraction
    // =========================================================================

    // Parses the dataset, extracts features for every position, and returns
    // Evaluate()'s score for each position (used only for verification).
    private static int[] LoadAndExtract(string path)
    {
        // Pass 1: parse lines so the column-major arrays can be sized exactly
        List<string> fens = new();
        List<double> results = new();

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int pipeIdx = line.IndexOf('|');
            if (pipeIdx == -1) continue;

            string scoreStr = line[..pipeIdx].Trim();
            string fenStr = line[(pipeIdx + 1)..].Trim();

            if (double.TryParse(scoreStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double res))
            {
                fens.Add(fenStr);
                results.Add(res);
            }
        }

        _count = fens.Count;
        _result = results.ToArray();
        _mgSum = new int[_count];
        _egSum = new int[_count];
        _phase = new byte[_count];
        _stm = new sbyte[_count];
        _coef = new short[FeatureCount][];
        for (int f = 0; f < FeatureCount; f++)
            _coef[f] = new short[_count];

        // Pass 2: set up each position once, record Evaluate(), extract features
        int[] refEval = new int[_count];
        Span<int> c = stackalloc int[FeatureCount];

        for (int i = 0; i < _count; i++)
        {
            ParseFEN(fens[i]);
            refEval[i] = Evaluate();
            ExtractPosition(i, c);
        }

        return refEval;
    }

    // Mirrors Evaluate() term by term, but instead of multiplying by weights it
    // records how many times each weight would be applied (the coefficient).
    // PST values have no tunable weight, so they go straight into the constant.
    private static void ExtractPosition(int i, Span<int> c)
    {
        c.Clear();

        ulong[] bb = bitboards;
        ulong[] occ = occupancies;

        ulong wP = bb[P], wN = bb[N], wB = bb[B], wR = bb[R], wQ = bb[Q], wK = bb[K];
        ulong bP = bb[p], bN = bb[n], bBi = bb[b], bR = bb[r], bQ = bb[q], bK = bb[k];

        ulong wOcc = occ[White];
        ulong bOcc = occ[Black];
        ulong allOcc = occ[Both];
        ulong allPawns = wP | bP;

        int pstMg = 0, pstEg = 0, phase = 0;

        // ---- Pawns -----------------------------------------------------------
        for (ulong x = wP; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            pstMg += MgPstSource[0][sq];
            pstEg += EgPstSource[0][sq];
            c[MgMat + 0]++; c[EgMat + 0]++;

            if ((WhitePassedMask[sq] & bP) == 0)
            {
                int rank = sq >> 3;
                if ((uint)(rank - 1) < 6) { c[MgPassed + rank - 1]++; c[EgPassed + rank - 1]++; }
            }
            if ((AdjacentFiles[sq & 7] & wP) == 0) { c[MgIsolated]++; c[EgIsolated]++; }
        }

        for (ulong x = bP; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            pstMg -= MgPstSource[0][sq ^ 56];
            pstEg -= EgPstSource[0][sq ^ 56];
            c[MgMat + 0]--; c[EgMat + 0]--;

            if ((BlackPassedMask[sq] & wP) == 0)
            {
                int rank = 7 - (sq >> 3);
                if ((uint)(rank - 1) < 6) { c[MgPassed + rank - 1]--; c[EgPassed + rank - 1]--; }
            }
            if ((AdjacentFiles[sq & 7] & bP) == 0) { c[MgIsolated]--; c[EgIsolated]--; }
        }

        // ---- Knights ---------------------------------------------------------
        for (ulong x = wN; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            pstMg += MgPstSource[1][sq];
            pstEg += EgPstSource[1][sq];
            c[MgMat + 1]++; c[EgMat + 1]++;
            phase += 1;

            int mobility = PopCount(knightAttacks[sq] & ~wOcc) - KnightMobBase;
            c[MgKnightMob] += mobility;
            c[EgKnightMob] += mobility;

            int rank = sq >> 3;
            if ((uint)(rank - 2) <= 2
                && (pawnAttacks[Black, sq] & wP) != 0
                && (WhiteOutpostMask[sq] & bP) == 0)
            {
                c[MgKnightOutpost]++;
            }
        }

        for (ulong x = bN; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            pstMg -= MgPstSource[1][sq ^ 56];
            pstEg -= EgPstSource[1][sq ^ 56];
            c[MgMat + 1]--; c[EgMat + 1]--;
            phase += 1;

            int mobility = PopCount(knightAttacks[sq] & ~bOcc) - KnightMobBase;
            c[MgKnightMob] -= mobility;
            c[EgKnightMob] -= mobility;

            int rank = sq >> 3;
            if ((uint)(rank - 3) <= 2
                && (pawnAttacks[White, sq] & bP) != 0
                && (BlackOutpostMask[sq] & wP) == 0)
            {
                c[MgKnightOutpost]--;
            }
        }

        // ---- Bishops ---------------------------------------------------------
        int wBishopCount = 0;
        for (ulong x = wB; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            pstMg += MgPstSource[2][sq];
            pstEg += EgPstSource[2][sq];
            c[MgMat + 2]++; c[EgMat + 2]++;
            phase += 1;
            wBishopCount++;

            int mobility = PopCount(GetBishopAttacks(sq, allOcc) & ~wOcc) - BishopMobBase;
            c[MgBishopMob] += mobility;
            c[EgBishopMob] += mobility;
        }
        if (wBishopCount >= 2) { c[MgBishopPair]++; c[EgBishopPair]++; }

        int bBishopCount = 0;
        for (ulong x = bBi; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            pstMg -= MgPstSource[2][sq ^ 56];
            pstEg -= EgPstSource[2][sq ^ 56];
            c[MgMat + 2]--; c[EgMat + 2]--;
            phase += 1;
            bBishopCount++;

            int mobility = PopCount(GetBishopAttacks(sq, allOcc) & ~bOcc) - BishopMobBase;
            c[MgBishopMob] -= mobility;
            c[EgBishopMob] -= mobility;
        }
        if (bBishopCount >= 2) { c[MgBishopPair]--; c[EgBishopPair]--; }

        // ---- Rooks -----------------------------------------------------------
        for (ulong x = wR; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            pstMg += MgPstSource[3][sq];
            pstEg += EgPstSource[3][sq];
            c[MgMat + 3]++; c[EgMat + 3]++;
            phase += 2;

            ulong fileMask = FileMask[sq & 7];
            if ((wP & fileMask) == 0)
            {
                if ((allPawns & fileMask) == 0) { c[MgRookOpen]++; c[EgRookOpen]++; }
                else { c[MgRookSemiOpen]++; c[EgRookSemiOpen]++; }
            }
        }

        for (ulong x = bR; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            pstMg -= MgPstSource[3][sq ^ 56];
            pstEg -= EgPstSource[3][sq ^ 56];
            c[MgMat + 3]--; c[EgMat + 3]--;
            phase += 2;

            ulong fileMask = FileMask[sq & 7];
            if ((bP & fileMask) == 0)
            {
                if ((allPawns & fileMask) == 0) { c[MgRookOpen]--; c[EgRookOpen]--; }
                else { c[MgRookSemiOpen]--; c[EgRookSemiOpen]--; }
            }
        }

        // ---- Queens ----------------------------------------------------------
        for (ulong x = wQ; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            pstMg += MgPstSource[4][sq];
            pstEg += EgPstSource[4][sq];
            c[MgMat + 4]++; c[EgMat + 4]++;
            phase += 4;
        }

        for (ulong x = bQ; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            pstMg -= MgPstSource[4][sq ^ 56];
            pstEg -= EgPstSource[4][sq ^ 56];
            c[MgMat + 4]--; c[EgMat + 4]--;
            phase += 4;
        }

        // ---- Kings -----------------------------------------------------------
        int wkSq = TrailingZeroCount(wK);
        int bkSq = TrailingZeroCount(bK);
        pstMg += MgPstSource[5][wkSq] - MgPstSource[5][bkSq ^ 56];
        pstEg += EgPstSource[5][wkSq] - EgPstSource[5][bkSq ^ 56];

        KingFileCoefs(c, wkSq & 7, wP, allPawns, -1);
        KingFileCoefs(c, bkSq & 7, bP, allPawns, +1);

        if (wQ == 0 && bQ == 0)
            c[MgQueenlessCenter] += KingCenterTable[wkSq] - KingCenterTable[bkSq];

        // ---- Store -----------------------------------------------------------
        _mgSum[i] = pstMg;                         // weights are added via ApplyDelta
        _egSum[i] = pstEg;
        _phase[i] = (byte)(phase > TotalPhase ? TotalPhase : phase);
        _stm[i] = (sbyte)(side == White ? 1 : -1);

        for (int f = 0; f < FeatureCount; f++)
            _coef[f][i] = checked((short)c[f]);
    }

    // Same structure as Evaluation.ScoreKingFileExposure, recording coefficients.
    private static void KingFileCoefs(Span<int> c, int kingFile, ulong friendlyPawns, ulong allPawns, int sign)
    {
        KingOneFile(c, kingFile, friendlyPawns, allPawns, sign, MgKingOwnOpen, MgKingOwnSemiOpen);
        if (kingFile > 0) KingOneFile(c, kingFile - 1, friendlyPawns, allPawns, sign, MgKingAdjOpen, MgKingAdjSemiOpen);
        if (kingFile < 7) KingOneFile(c, kingFile + 1, friendlyPawns, allPawns, sign, MgKingAdjOpen, MgKingAdjSemiOpen);
    }

    private static void KingOneFile(Span<int> c, int file, ulong friendlyPawns, ulong allPawns,
        int sign, int openFeature, int semiOpenFeature)
    {
        ulong mask = FileMask[file];
        if ((friendlyPawns & mask) != 0) return;
        c[(allPawns & mask) == 0 ? openFeature : semiOpenFeature] += sign;
    }

    // =========================================================================
    //  SECTION 6 — Incremental Scoring & Loss
    // =========================================================================

    // Adds `delta × coef` of one feature to every position's mg or eg total.
    // This is the only thing a ±1 tuning step has to do before measuring loss.
    private static void ApplyDelta(int feature, int delta)
    {
        if (delta == 0) return;
        short[] col = _coef[feature];
        int[] sums = feature >= FirstEgFeature ? _egSum : _mgSum;
        for (int i = 0; i < _count; i++)
            sums[i] += delta * col[i];
    }

    // Identical arithmetic to the tail of Evaluate(), expressed from White's view:
    //   Evaluate() = (stm == White ? score : -score) + Tempo
    //   WhiteEval  = score + stm × Tempo
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WhiteEval(int i)
    {
        int ph = _phase[i];
        int score = (_mgSum[i] * ph + _egSum[i] * (TotalPhase - ph)) / TotalPhase;
        return score + _stm[i] * TempoBonus;
    }

    private static int Verify(int[] refEval)
    {
        int mismatches = 0;
        for (int i = 0; i < _count; i++)
            if (_stm[i] * WhiteEval(i) != refEval[i]) mismatches++;
        return mismatches;
    }

    private static double FindOptimalK()
    {
        double bestK = 0.0035;
        double minLoss = double.MaxValue;

        for (double k = 0.0010; k <= 0.0100; k += 0.0001)
        {
            double loss = ComputeLoss(k);
            if (loss < minLoss)
            {
                minLoss = loss;
                bestK = k;
            }
        }

        return bestK;
    }

    // MSE between game result and sigmoid(eval). Each thread sums a fixed
    // contiguous chunk in a fixed order, so the result is deterministic.
    private static double ComputeLoss(double K)
    {
        double scale = -K * Ln10 / 400.0;
        int threads = Environment.ProcessorCount;
        double[] partial = new double[threads];
        int n = _count;

        Parallel.For(0, threads, t =>
        {
            int start = (int)((long)n * t / threads);
            int end = (int)((long)n * (t + 1) / threads);
            double sum = 0.0;

            for (int i = start; i < end; i++)
            {
                double sigmoid = 1.0 / (1.0 + Math.Exp(WhiteEval(i) * scale));
                double diff = _result[i] - sigmoid;
                sum += diff * diff;
            }

            partial[t] = sum;
        });

        double total = 0.0;
        for (int t = 0; t < threads; t++) total += partial[t];
        return total / n;
    }

    // =========================================================================
    //  SECTION 7 — Output
    // =========================================================================

    private static void PrintChanges(List<Param> parameters, int[] initialWeights)
    {
        Console.WriteLine("\n=================================================");
        Console.WriteLine("   Parameter Changes");
        Console.WriteLine("=================================================");
        Console.WriteLine($"{"Parameter",-26}{"Before",8}{"After",8}{"Delta",8}");

        foreach (var prm in parameters)
        {
            int before = initialWeights[prm.Feature];
            int after = _weights[prm.Feature];
            if (before != after)
                Console.WriteLine($"{prm.Name,-26}{before,8}{after,8}{after - before,8:+#;-#;0}");
        }
    }

    private static void PrintTunedCode(double initialLoss, double finalLoss)
    {
        Console.WriteLine("\n=================================================");
        Console.WriteLine("   Copy-Paste Ready C# Code for Evaluation.cs");
        Console.WriteLine("=================================================\n");

        Console.WriteLine("    #region Material & Phase\n");
        Console.WriteLine("    // Base material values in centipawns, indexed by piece type:");
        Console.WriteLine("    //   0=Pawn  1=Knight  2=Bishop  3=Rook  4=Queen  5=King");
        Console.WriteLine("    // King is 0 because it's never captured; its \"value\" is implicit in game");
        Console.WriteLine("    // termination, not in evaluation.");
        Console.WriteLine($"    public static int[] MgMaterial = [{string.Join(", ", MgMaterial)}];");
        Console.WriteLine($"    public static int[] EgMaterial = [{string.Join(", ", EgMaterial)}];\n");
        Console.WriteLine("    // Starting position phase: 2 sides × (2×Knight + 2×Bishop + 2×Rook + 1×Queen)");
        Console.WriteLine("    //   = 2 × (2·1 + 2·1 + 2·2 + 1·4) = 24.");
        Console.WriteLine("    // Phase is accumulated inline during evaluation (Knight/Bishop += 1,");
        Console.WriteLine("    // Rook += 2, Queen += 4) rather than through a lookup table.");
        Console.WriteLine("    private const int TotalPhase = 24;\n");
        Console.WriteLine("    #endregion\n");

        Console.WriteLine("    #region Positional Bonuses / Penalties\n");
        Console.WriteLine("    // Having both bishops is worth a small bonus because they cover");
        Console.WriteLine("    // complementary colour complexes.");
        Console.WriteLine($"    public static int BishopPairMg = {BishopPairMg}, BishopPairEg = {BishopPairEg};\n");
        Console.WriteLine("    // Mobility = (number of safe destination squares) − (a baseline).");
        Console.WriteLine("    // Subtracting a baseline keeps the \"average\" piece near zero so the");
        Console.WriteLine("    // mobility term doesn't silently inflate material values.");
        Console.WriteLine($"    public static int KnightMobMg = {KnightMobMg}, KnightMobEg = {KnightMobEg}, KnightMobBase = {KnightMobBase};");
        Console.WriteLine($"    public static int BishopMobMg = {BishopMobMg}, BishopMobEg = {BishopMobEg}, BishopMobBase = {BishopMobBase};\n");
        Console.WriteLine("    // Rook on a file with no friendly pawn:");
        Console.WriteLine("    //   semi-open = only enemy pawns on it (good attacking lane)");
        Console.WriteLine("    //   open      = no pawns at all (best — rook sees the whole file)");
        Console.WriteLine($"    public static int RookSemiOpenMg = {RookSemiOpenMg}, RookSemiOpenEg = {RookSemiOpenEg};");
        Console.WriteLine($"    public static int RookOpenMg = {RookOpenMg}, RookOpenEg = {RookOpenEg};\n");
        Console.WriteLine("    // Passed pawn bonus indexed by \"distance from own back rank\" (0..7).");
        Console.WriteLine("    // A pawn gets this if no enemy pawn can block or capture it on its way");
        Console.WriteLine("    // to promotion. The closer to promotion, the bigger the bonus — much");
        Console.WriteLine("    // larger in the endgame where promotion threats dominate.");
        Console.WriteLine($"    public static int[] PassedMg = [{string.Join(", ", PassedMg)}];");
        Console.WriteLine($"    public static int[] PassedEg = [{string.Join(", ", PassedEg)}];\n");
        Console.WriteLine("    // A pawn with no friendly pawn on either adjacent file. Weak because it");
        Console.WriteLine("    // can never be defended by another pawn.");
        Console.WriteLine($"    public static int IsolatedMg = {IsolatedMg}, IsolatedEg = {IsolatedEg};\n");
        Console.WriteLine("    // King safety: penalties for open/semi-open files near the king. Only");
        Console.WriteLine("    // applied in the middlegame — in the endgame the king wants to be active.");
        Console.WriteLine($"    public static int KingOwnOpenMg = {KingOwnOpenMg}, KingOwnSemiOpenMg = {KingOwnSemiOpenMg};");
        Console.WriteLine($"    public static int KingAdjacentOpenMg = {KingAdjacentOpenMg}, KingAdjacentSemiOpenMg = {KingAdjacentSemiOpenMg};\n");
        Console.WriteLine("    // Once both queens are gone, encourage the king toward the centre so it");
        Console.WriteLine("    // can help its pawns promote.");
        Console.WriteLine($"    public static int QueenlessKingCenterMg = {QueenlessKingCenterMg};\n");
        Console.WriteLine("    // Knight sitting on a square where:");
        Console.WriteLine("    //   • it's protected by a friendly pawn");
        Console.WriteLine("    //   • it can't be kicked by any enemy pawn on adjacent files");
        Console.WriteLine("    // Outposts are classical strong squares (think c5/d5/e5/f5 for White).");
        Console.WriteLine($"    public static int KnightOutpostMg = {KnightOutpostMg};\n");
        Console.WriteLine("    // Tempo bonus: static advantage awarded to the side to move");
        Console.WriteLine("    private const int TempoBonus = 8;\n");
        Console.WriteLine("    #endregion");

        Console.WriteLine("\n=================================================");
        Console.WriteLine($"Final MSE Loss: {finalLoss:F8} (Started at {initialLoss:F8})");
        Console.WriteLine("=================================================\n");
    }
}