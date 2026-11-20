using System.Diagnostics;
using System.Globalization;
using static System.Numerics.BitOperations;
using static Board;
using static MoveGenerator;
using static PieceAttacks;

// =============================================================================
//  Tuner — Fast Texel Evaluation Tuning (cached features)
//
//  Sigmoid:
//      P(Win) = 1 / (1 + 10 ^ (-K * Eval / 400))        (K is near 1)
//
//  Why it is fast:
//      Evaluate() is linear in every tunable weight:
//          mg = PST part + sum(weight * coefficient)     (same for eg)
//      The coefficients (e.g. "white has 2 more isolated pawns" = +2) depend
//      only on the position, never on the weights. So we:
//        1. Parse each FEN once and cache its coefficients, PST sums and phase.
//        2. Keep a running mg / eg / squared-error per position.
//        3. To test "weight + 1", only touch positions where that feature's
//           coefficient is non-zero. No board restore, no Evaluate() call.
//
//  Safety net:
//      While loading, the score rebuilt from cached features is compared with
//      Evaluation.Evaluate() for EVERY position. Any mismatch aborts the run,
//      so the tuner can never silently drift out of sync with the engine.
//
//  Adding a new evaluation term later:
//      1. Add a Feat* constant.  2. Count it in ExtractSide().
//      3. Register its weight(s) in RegisterParameters().
//      Forget a step and the startup check tells you.
// =============================================================================

public static class Tuner
{
    // =========================================================================
    //  SECTION 1 — Feature Layout & Data
    // =========================================================================

    // One "feature" = one coefficient per position (White count − Black count).
    // An MG weight and an EG weight can share the same feature.
    private const int FeatMaterial = 0;               // +0..4: Pawn, Knight, Bishop, Rook, Queen
    private const int FeatPassed = 5;                 // +0..7: same index as PassedMg / PassedEg
    private const int FeatBishopPair = 13;
    private const int FeatKnightMob = 14;
    private const int FeatBishopMob = 15;
    private const int FeatRookSemiOpen = 16;
    private const int FeatRookOpen = 17;
    private const int FeatIsolated = 18;
    private const int FeatKingOwnOpen = 19;
    private const int FeatKingOwnSemiOpen = 20;
    private const int FeatKingAdjacentOpen = 21;
    private const int FeatKingAdjacentSemiOpen = 22;
    private const int FeatQueenlessKingCenter = 23;
    private const int FeatKnightOutpost = 24;
    private const int FeatureCount = 25;

    // Phase contribution per piece type (Pawn, Knight, Bishop, Rook, Queen, King).
    private static readonly int[] PhaseWeight = [0, 1, 1, 2, 4, 0];

    // K search range. The optimum should land well inside it.
    private const double KMin = 0.1, KMax = 5.0;

    // A tunable weight: which feature it multiplies, and whether it feeds the
    // middlegame or the endgame score.
    private sealed record Param(string Name, int Feature, bool IsMg, Func<int> Get, Action<int> Set);

    // Per-position cache (index = position number).
    private static int positionCount;
    private static double[] results = [];   // 1.0 / 0.5 / 0.0 from White's view
    private static int[] mgScore = [];      // current middlegame score (White − Black)
    private static int[] egScore = [];      // current endgame score (White − Black)
    private static int[] mgPhase = [];      // 0..TotalPhase
    private static int[] tempo = [];        // +TempoBonus if White to move, else −TempoBonus
    private static double[] sqErr = [];     // current squared error of this position
    private static short[][] coeffs = [];   // coeffs[feature][position]

    private static double sigmoidScale;     // K * ln(10) / 400

    // =========================================================================
    //  SECTION 2 — Main Entry Point & Tuning Loop
    // =========================================================================

    public static int Run(string[] args)
    {
        string dataPath = args.Length > 1 ? args[1] : "data/zurichess.txt";
        int maxEpochs = args.Length > 2 && int.TryParse(args[2], out int parsedEpochs) ? parsedEpochs : 1000;

        if (!File.Exists(dataPath))
        {
            Console.WriteLine($"[!] Error: Dataset file not found at '{dataPath}'");
            return 1;
        }

        Console.WriteLine("=================================================");
        Console.WriteLine("   AnkaX Texel Evaluation Tuning Tool");
        Console.WriteLine("=================================================");
        Console.WriteLine($"Loading dataset: '{dataPath}'...");

        List<Param> parameters = RegisterParameters();

        if (!LoadDataset(dataPath, parameters)) return 1;
        Console.WriteLine($"Loaded {positionCount:N0} positions. Cached features match Evaluate() on all of them.\n");

        // Step 1: Fit K once against the current evaluation, then keep it fixed.
        Console.WriteLine("Calculating scaling constant K...");
        double bestK = FindOptimalK();
        double initialLoss = RefreshErrors(bestK);
        Console.WriteLine($"K = {bestK:F3} | Initial MSE Loss = {initialLoss:F8}");

        if (bestK <= KMin + 1e-9 || bestK >= KMax - 1e-9)
            Console.WriteLine("[!] Warning: K is at the edge of its search range. Check the dataset's result format before trusting this run.");
        Console.WriteLine();

        // Step 2: Coordinate descent (+1 / -1 per weight, open boundaries).
        Console.WriteLine($"Starting optimization for {parameters.Count} parameters (Max Epochs: {maxEpochs})...\n");
        Stopwatch timer = Stopwatch.StartNew();
        double currentLoss = initialLoss;

        for (int epoch = 1; epoch <= maxEpochs; epoch++)
        {
            int updatedCount = 0;

            foreach (Param prm in parameters)
            {
                // Try +1 first; only if that does not help, try -1.
                if (TryStep(prm, +1) || TryStep(prm, -1))
                    updatedCount++;
            }

            currentLoss = MeanSquaredError();
            Console.WriteLine($"Epoch {epoch,4}: MSE Loss = {currentLoss:F8} | Parameters Updated = {updatedCount}");

            if (updatedCount == 0)
            {
                Console.WriteLine("\n[+] Convergence reached: Local minimum found.");
                break;
            }
        }

        Console.WriteLine($"Tuning time: {timer.Elapsed.TotalSeconds:F1} s");

        // Step 3: Print copy-paste ready C# output.
        PrintTunedCode(initialLoss, currentLoss);

        return 0;
    }

    // Tests "weight + delta". Keeps the change only if the total error drops.
    private static bool TryStep(Param prm, int delta)
    {
        short[] column = coeffs[prm.Feature];
        int mgDelta = prm.IsMg ? delta : 0;
        int egDelta = prm.IsMg ? 0 : delta;

        // Pass 1: how would the summed squared error change?
        double change = 0.0;
        for (int i = 0; i < positionCount; i++)
        {
            int c = column[i];
            if (c == 0) continue;   // feature absent: this position is unaffected

            change += SquaredError(i, mgScore[i] + c * mgDelta, egScore[i] + c * egDelta) - sqErr[i];
        }

        if (change >= 0.0) return false;

        // Pass 2: commit the step to the cached scores.
        for (int i = 0; i < positionCount; i++)
        {
            int c = column[i];
            if (c == 0) continue;

            mgScore[i] += c * mgDelta;
            egScore[i] += c * egDelta;
            sqErr[i] = SquaredError(i, mgScore[i], egScore[i]);
        }

        // Keep the Evaluation field in sync so PrintTunedCode can read it.
        // (Evaluate() itself is not called again after loading.)
        prm.Set(prm.Get() + delta);
        return true;
    }

    // =========================================================================
    //  SECTION 3 — Parameter Registration
    // =========================================================================

    private static List<Param> RegisterParameters()
    {
        List<Param> list = new();

        void AddMg(string name, int feature, Func<int> get, Action<int> set) => list.Add(new(name, feature, true, get, set));
        void AddEg(string name, int feature, Func<int> get, Action<int> set) => list.Add(new(name, feature, false, get, set));

        // Material (Pawn, Knight, Bishop, Rook, Queen)
        string[] pieceNames = ["Pawn", "Knight", "Bishop", "Rook", "Queen"];
        for (int i = 0; i < 5; i++)
        {
            int idx = i;
            AddMg($"MgMaterial_{pieceNames[idx]}", FeatMaterial + idx, () => Evaluation.MgMaterial[idx], v => Evaluation.MgMaterial[idx] = v);
            AddEg($"EgMaterial_{pieceNames[idx]}", FeatMaterial + idx, () => Evaluation.EgMaterial[idx], v => Evaluation.EgMaterial[idx] = v);
        }

        // Passed pawns. Array index = squares left to promotion
        // (1 = on the 7th rank, 6 = on its starting square).
        for (int i = 1; i <= 6; i++)
        {
            int idx = i;
            AddMg($"PassedMg[{idx}]", FeatPassed + idx, () => Evaluation.PassedMg[idx], v => Evaluation.PassedMg[idx] = v);
            AddEg($"PassedEg[{idx}]", FeatPassed + idx, () => Evaluation.PassedEg[idx], v => Evaluation.PassedEg[idx] = v);
        }

        // Positional features
        AddMg("BishopPairMg", FeatBishopPair, () => Evaluation.BishopPairMg, v => Evaluation.BishopPairMg = v);
        AddEg("BishopPairEg", FeatBishopPair, () => Evaluation.BishopPairEg, v => Evaluation.BishopPairEg = v);
        AddMg("KnightMobMg", FeatKnightMob, () => Evaluation.KnightMobMg, v => Evaluation.KnightMobMg = v);
        AddEg("KnightMobEg", FeatKnightMob, () => Evaluation.KnightMobEg, v => Evaluation.KnightMobEg = v);
        AddMg("BishopMobMg", FeatBishopMob, () => Evaluation.BishopMobMg, v => Evaluation.BishopMobMg = v);
        AddEg("BishopMobEg", FeatBishopMob, () => Evaluation.BishopMobEg, v => Evaluation.BishopMobEg = v);
        AddMg("RookSemiOpenMg", FeatRookSemiOpen, () => Evaluation.RookSemiOpenMg, v => Evaluation.RookSemiOpenMg = v);
        AddEg("RookSemiOpenEg", FeatRookSemiOpen, () => Evaluation.RookSemiOpenEg, v => Evaluation.RookSemiOpenEg = v);
        AddMg("RookOpenMg", FeatRookOpen, () => Evaluation.RookOpenMg, v => Evaluation.RookOpenMg = v);
        AddEg("RookOpenEg", FeatRookOpen, () => Evaluation.RookOpenEg, v => Evaluation.RookOpenEg = v);
        AddMg("IsolatedMg", FeatIsolated, () => Evaluation.IsolatedMg, v => Evaluation.IsolatedMg = v);
        AddEg("IsolatedEg", FeatIsolated, () => Evaluation.IsolatedEg, v => Evaluation.IsolatedEg = v);
        AddMg("KingOwnOpenMg", FeatKingOwnOpen, () => Evaluation.KingOwnOpenMg, v => Evaluation.KingOwnOpenMg = v);
        AddMg("KingOwnSemiOpenMg", FeatKingOwnSemiOpen, () => Evaluation.KingOwnSemiOpenMg, v => Evaluation.KingOwnSemiOpenMg = v);
        AddMg("KingAdjacentOpenMg", FeatKingAdjacentOpen, () => Evaluation.KingAdjacentOpenMg, v => Evaluation.KingAdjacentOpenMg = v);
        AddMg("KingAdjacentSemiOpenMg", FeatKingAdjacentSemiOpen, () => Evaluation.KingAdjacentSemiOpenMg, v => Evaluation.KingAdjacentSemiOpenMg = v);
        AddMg("QueenlessKingCenterMg", FeatQueenlessKingCenter, () => Evaluation.QueenlessKingCenterMg, v => Evaluation.QueenlessKingCenterMg = v);
        AddMg("KnightOutpostMg", FeatKnightOutpost, () => Evaluation.KnightOutpostMg, v => Evaluation.KnightOutpostMg = v);

        return list;
    }

    // =========================================================================
    //  SECTION 4 — Dataset Loading & Feature Extraction
    // =========================================================================

    private static bool LoadDataset(string path, List<Param> parameters)
    {
        string[] lines = File.ReadAllLines(path);
        int capacity = lines.Length;

        results = new double[capacity];
        mgScore = new int[capacity];
        egScore = new int[capacity];
        mgPhase = new int[capacity];
        tempo = new int[capacity];
        sqErr = new double[capacity];
        coeffs = new short[FeatureCount][];
        for (int f = 0; f < FeatureCount; f++)
            coeffs[f] = new short[capacity];

        int[] coeff = new int[FeatureCount];
        positionCount = 0;

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int pipeIdx = line.IndexOf('|');
            if (pipeIdx == -1) continue;

            string scoreStr = line[..pipeIdx].Trim();
            string fenStr = line[(pipeIdx + 1)..].Trim();

            if (!double.TryParse(scoreStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
                continue;

            Board.ParseFEN(fenStr);
            ExtractFeatures(coeff, out int mgPst, out int egPst, out int phase);

            // Starting score = fixed PST part + every weight times its coefficient.
            int mg = mgPst, eg = egPst;
            foreach (Param prm in parameters)
            {
                int contribution = prm.Get() * coeff[prm.Feature];
                if (prm.IsMg) mg += contribution; else eg += contribution;
            }

            int i = positionCount;
            results[i] = result;
            mgScore[i] = mg;
            egScore[i] = eg;
            mgPhase[i] = Math.Min(phase, Evaluation.TotalPhase);
            tempo[i] = side == White ? Evaluation.TempoBonus : -Evaluation.TempoBonus;
            for (int f = 0; f < FeatureCount; f++)
                coeffs[f][i] = checked((short)coeff[f]);

            // Safety net: the cached score must equal the engine's score exactly.
            int engineEval = Evaluation.Evaluate();
            int engineWhite = side == White ? engineEval : -engineEval;
            int cachedWhite = WhiteEval(i, mg, eg);

            if (cachedWhite != engineWhite)
            {
                Console.WriteLine("[!] Error: Tuner features are out of sync with Evaluation.Evaluate().");
                Console.WriteLine($"    FEN:    {fenStr}");
                Console.WriteLine($"    Engine: {engineWhite} | Tuner: {cachedWhite}");
                Console.WriteLine("    Update ExtractSide() / RegisterParameters() to match Evaluate().");
                return false;
            }

            positionCount++;
        }

        if (positionCount == 0)
        {
            Console.WriteLine("[!] Error: No positions could be read (expected 'result | FEN' per line).");
            return false;
        }

        return true;
    }

    // Fills `coeff` with (White count − Black count) for every feature, and
    // returns the non-tunable parts: PST sums and game phase.
    private static void ExtractFeatures(int[] coeff, out int mgPst, out int egPst, out int phase)
    {
        Array.Clear(coeff);
        mgPst = 0;
        egPst = 0;
        phase = 0;

        ExtractSide(true, coeff, ref mgPst, ref egPst, ref phase);
        ExtractSide(false, coeff, ref mgPst, ref egPst, ref phase);
    }

    // Mirrors Evaluation.Evaluate() for one colour. Every block here has a
    // matching block there; `sign` is +1 for White and -1 for Black.
    private static void ExtractSide(bool isWhite, int[] coeff, ref int mgPst, ref int egPst, ref int phase)
    {
        int sign = isWhite ? +1 : -1;
        int offset = isWhite ? 0 : 6;   // black piece index = white piece index + 6

        ulong[] bb = bitboards;
        ulong ownPawns = bb[P + offset];
        ulong enemyPawns = bb[isWhite ? p : P];
        ulong allPawns = ownPawns | enemyPawns;
        ulong ownOcc = occupancies[isWhite ? White : Black];
        ulong allOcc = occupancies[Both];

        // --- Material count, PST and phase for every piece -------------------
        for (int type = 0; type < 6; type++)
        {
            for (ulong x = bb[type + offset]; x != 0; x &= x - 1)
            {
                int sq = TrailingZeroCount(x);
                int pstSq = isWhite ? sq : sq ^ 56;

                mgPst += sign * Evaluation.MgPstSource[type][pstSq];
                egPst += sign * Evaluation.EgPstSource[type][pstSq];
                phase += PhaseWeight[type];

                if (type < 5) coeff[FeatMaterial + type] += sign;   // king has no material value
            }
        }

        // --- Pawns: passed + isolated ----------------------------------------
        for (ulong x = ownPawns; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);

            ulong passedMask = isWhite ? Evaluation.WhitePassedMask[sq] : Evaluation.BlackPassedMask[sq];
            if ((passedMask & enemyPawns) == 0)
            {
                int rank = isWhite ? sq >> 3 : 7 - (sq >> 3);
                coeff[FeatPassed + rank] += sign;
            }

            if ((Evaluation.AdjacentFiles[sq & 7] & ownPawns) == 0)
                coeff[FeatIsolated] += sign;
        }

        // --- Knights: mobility + outpost -------------------------------------
        for (ulong x = bb[N + offset]; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);

            int mobility = PopCount(knightAttacks[sq] & ~ownOcc) - Evaluation.KnightMobBase;
            coeff[FeatKnightMob] += sign * mobility;

            int rank = sq >> 3;
            int firstOutpostRank = isWhite ? 2 : 3;
            ulong pawnDefenders = pawnAttacks[isWhite ? Black : White, sq] & ownPawns;
            ulong outpostMask = isWhite ? Evaluation.WhiteOutpostMask[sq] : Evaluation.BlackOutpostMask[sq];

            if ((uint)(rank - firstOutpostRank) <= 2
                && pawnDefenders != 0
                && (outpostMask & enemyPawns) == 0)
            {
                coeff[FeatKnightOutpost] += sign;
            }
        }

        // --- Bishops: mobility + pair ----------------------------------------
        int bishopCount = 0;
        for (ulong x = bb[B + offset]; x != 0; x &= x - 1)
        {
            int sq = TrailingZeroCount(x);
            bishopCount++;

            int mobility = PopCount(GetBishopAttacks(sq, allOcc) & ~ownOcc) - Evaluation.BishopMobBase;
            coeff[FeatBishopMob] += sign * mobility;
        }
        if (bishopCount >= 2) coeff[FeatBishopPair] += sign;

        // --- Rooks: open / semi-open file ------------------------------------
        for (ulong x = bb[R + offset]; x != 0; x &= x - 1)
        {
            ulong fileMask = Evaluation.FileMask[TrailingZeroCount(x) & 7];
            if ((ownPawns & fileMask) != 0) continue;

            coeff[(allPawns & fileMask) == 0 ? FeatRookOpen : FeatRookSemiOpen] += sign;
        }

        // --- King: file exposure + queenless centralisation ------------------
        int kingSq = TrailingZeroCount(bb[K + offset]);
        int kingFile = kingSq & 7;

        AddKingFile(coeff, kingFile, ownPawns, allPawns, sign, FeatKingOwnOpen, FeatKingOwnSemiOpen);
        if (kingFile > 0)
            AddKingFile(coeff, kingFile - 1, ownPawns, allPawns, sign, FeatKingAdjacentOpen, FeatKingAdjacentSemiOpen);
        if (kingFile < 7)
            AddKingFile(coeff, kingFile + 1, ownPawns, allPawns, sign, FeatKingAdjacentOpen, FeatKingAdjacentSemiOpen);

        if (bb[Q] == 0 && bb[q] == 0)
            coeff[FeatQueenlessKingCenter] += sign * Evaluation.KingCenterTable[kingSq];
    }

    // King-file weights are penalties: Evaluate() subtracts them from the
    // king's owner, so the coefficient is -sign.
    private static void AddKingFile(int[] coeff, int file, ulong ownPawns, ulong allPawns,
        int sign, int openFeature, int semiOpenFeature)
    {
        ulong mask = Evaluation.FileMask[file];
        if ((ownPawns & mask) != 0) return;

        coeff[(allPawns & mask) == 0 ? openFeature : semiOpenFeature] -= sign;
    }

    // =========================================================================
    //  SECTION 5 — Score, Loss & K
    // =========================================================================

    // Same tapering and tempo arithmetic as Evaluate(), from White's view.
    private static int WhiteEval(int i, int mg, int eg)
    {
        int phase = mgPhase[i];
        int score = (mg * phase + eg * (Evaluation.TotalPhase - phase)) / Evaluation.TotalPhase;
        return score + tempo[i];
    }

    private static double SquaredError(int i, int mg, int eg)
    {
        double predicted = 1.0 / (1.0 + Math.Exp(-sigmoidScale * WhiteEval(i, mg, eg)));
        double diff = results[i] - predicted;
        return diff * diff;
    }

    private static double MeanSquaredError()
    {
        double total = 0.0;
        for (int i = 0; i < positionCount; i++)
            total += sqErr[i];
        return total / positionCount;
    }

    // Sets K, recomputes every position's squared error, returns the MSE.
    private static double RefreshErrors(double kValue)
    {
        sigmoidScale = kValue * Math.Log(10.0) / 400.0;

        for (int i = 0; i < positionCount; i++)
            sqErr[i] = SquaredError(i, mgScore[i], egScore[i]);

        return MeanSquaredError();
    }

    // Coarse scan over the whole range, then a fine scan around the best value.
    private static double FindOptimalK()
    {
        double coarse = ScanK(KMin, KMax, 0.1);
        return ScanK(Math.Max(KMin, coarse - 0.1), Math.Min(KMax, coarse + 0.1), 0.005);
    }

    private static double ScanK(double from, double to, double step)
    {
        double bestK = from;
        double bestLoss = double.MaxValue;
        int stepCount = (int)Math.Round((to - from) / step);

        for (int s = 0; s <= stepCount; s++)
        {
            double candidate = from + s * step;
            double loss = RefreshErrors(candidate);
            if (loss < bestLoss)
            {
                bestLoss = loss;
                bestK = candidate;
            }
        }

        return bestK;
    }

    // =========================================================================
    //  SECTION 6 — Formatted Code Output
    // =========================================================================

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
        Console.WriteLine($"    public static int[] MgMaterial = [{string.Join(", ", Evaluation.MgMaterial)}];");
        Console.WriteLine($"    public static int[] EgMaterial = [{string.Join(", ", Evaluation.EgMaterial)}];\n");
        Console.WriteLine("    // Starting position phase: 2 sides × (2×Knight + 2×Bishop + 2×Rook + 1×Queen)");
        Console.WriteLine("    //   = 2 × (2·1 + 2·1 + 2·2 + 1·4) = 24.");
        Console.WriteLine("    // Phase is accumulated inline during evaluation (Knight/Bishop += 1,");
        Console.WriteLine("    // Rook += 2, Queen += 4) rather than through a lookup table.");
        Console.WriteLine($"    internal const int TotalPhase = {Evaluation.TotalPhase};\n");
        Console.WriteLine("    #endregion\n");

        Console.WriteLine("    #region Positional Bonuses / Penalties\n");
        Console.WriteLine("    // Having both bishops is worth a small bonus because they cover");
        Console.WriteLine("    // complementary colour complexes.");
        Console.WriteLine($"    public static int BishopPairMg = {Evaluation.BishopPairMg}, BishopPairEg = {Evaluation.BishopPairEg};\n");
        Console.WriteLine("    // Mobility = (number of safe destination squares) − (a baseline).");
        Console.WriteLine("    // Subtracting a baseline keeps the \"average\" piece near zero so the");
        Console.WriteLine("    // mobility term doesn't silently inflate material values.");
        Console.WriteLine($"    public static int KnightMobMg = {Evaluation.KnightMobMg}, KnightMobEg = {Evaluation.KnightMobEg}, KnightMobBase = {Evaluation.KnightMobBase};");
        Console.WriteLine($"    public static int BishopMobMg = {Evaluation.BishopMobMg}, BishopMobEg = {Evaluation.BishopMobEg}, BishopMobBase = {Evaluation.BishopMobBase};\n");
        Console.WriteLine("    // Rook on a file with no friendly pawn:");
        Console.WriteLine("    //   semi-open = only enemy pawns on it (good attacking lane)");
        Console.WriteLine("    //   open      = no pawns at all (best — rook sees the whole file)");
        Console.WriteLine($"    public static int RookSemiOpenMg = {Evaluation.RookSemiOpenMg}, RookSemiOpenEg = {Evaluation.RookSemiOpenEg};");
        Console.WriteLine($"    public static int RookOpenMg = {Evaluation.RookOpenMg}, RookOpenEg = {Evaluation.RookOpenEg};\n");
        Console.WriteLine("    // Passed pawn bonus indexed by squares left to promotion (0..7):");
        Console.WriteLine("    //   index 1 = one step from promoting, index 6 = still on its start square.");
        Console.WriteLine("    // A pawn gets this if no enemy pawn can block or capture it on its way");
        Console.WriteLine("    // to promotion. The closer to promotion, the bigger the bonus — much");
        Console.WriteLine("    // larger in the endgame where promotion threats dominate.");
        Console.WriteLine($"    public static int[] PassedMg = [{string.Join(", ", Evaluation.PassedMg)}];");
        Console.WriteLine($"    public static int[] PassedEg = [{string.Join(", ", Evaluation.PassedEg)}];\n");
        Console.WriteLine("    // A pawn with no friendly pawn on either adjacent file. Weak because it");
        Console.WriteLine("    // can never be defended by another pawn.");
        Console.WriteLine($"    public static int IsolatedMg = {Evaluation.IsolatedMg}, IsolatedEg = {Evaluation.IsolatedEg};\n");
        Console.WriteLine("    // King safety: penalties for open/semi-open files near the king. Only");
        Console.WriteLine("    // applied in the middlegame — in the endgame the king wants to be active.");
        Console.WriteLine($"    public static int KingOwnOpenMg = {Evaluation.KingOwnOpenMg}, KingOwnSemiOpenMg = {Evaluation.KingOwnSemiOpenMg};");
        Console.WriteLine($"    public static int KingAdjacentOpenMg = {Evaluation.KingAdjacentOpenMg}, KingAdjacentSemiOpenMg = {Evaluation.KingAdjacentSemiOpenMg};\n");
        Console.WriteLine("    // Once both queens are gone, encourage the king toward the centre so it");
        Console.WriteLine("    // can help its pawns promote.");
        Console.WriteLine($"    public static int QueenlessKingCenterMg = {Evaluation.QueenlessKingCenterMg};\n");
        Console.WriteLine("    // Knight sitting on a square where:");
        Console.WriteLine("    //   • it's protected by a friendly pawn");
        Console.WriteLine("    //   • it can't be kicked by any enemy pawn on adjacent files");
        Console.WriteLine("    // Outposts are classical strong squares (think c5/d5/e5/f5 for White).");
        Console.WriteLine($"    public static int KnightOutpostMg = {Evaluation.KnightOutpostMg};\n");
        Console.WriteLine("    // Tempo bonus: static advantage awarded to the side to move");
        Console.WriteLine($"    internal const int TempoBonus = {Evaluation.TempoBonus};\n");
        Console.WriteLine("    #endregion");

        Console.WriteLine("\n=================================================");
        Console.WriteLine($"Final MSE Loss: {finalLoss:F8} (Started at {initialLoss:F8})");
        Console.WriteLine("=================================================\n");
    }
}