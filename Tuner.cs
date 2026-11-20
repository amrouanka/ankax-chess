using System.Globalization;
using static MoveGenerator;

// =============================================================================
//  Tuner — Texel Evaluation Parameter Tuning Tool
//
//  Methodology:
//      Texel tuning fits static evaluation terms to real game outcomes using
//      quiet position datasets (e.g. `zurichess.txt` containing FENs + results).
//
//  Sigmoid Function:
//      P(Win) = 1 / (1 + 10 ^ (-K * Eval / 400))
//
//  Optimization:
//      1. Finds optimal scaling constant `K` via golden-section search.
//      2. Iterates over all material, passed pawn, and positional weights using
//         local coordinate descent (+1 / -1 steps) to minimize Mean Squared Error.
//      3. Outputs a copy-paste ready C# block formatted with code comments.
// =============================================================================

public static class Tuner
{
    // =========================================================================
    //  SECTION 1 — Data Structures
    // =========================================================================

    private sealed class TunableParam
    {
        public string Name { get; }
        public Func<int> Getter { get; }
        public Action<int> Setter { get; }

        public TunableParam(string name, Func<int> getter, Action<int> setter)
        {
            Name = name;
            Getter = getter;
            Setter = setter;
        }
    }

    // =========================================================================
    //  SECTION 2 — Main Entry Point & Tuning Loop
    // =========================================================================

    public static int Run(string[] args)
    {
        string dataPath = args.Length > 1 ? args[1] : "data/zurichess.txt";
        int maxEpochs = args.Length > 2 && int.TryParse(args[2], out int e) ? e : 100;

        if (!File.Exists(dataPath))
        {
            Console.WriteLine($"[!] Error: Dataset file not found at '{dataPath}'");
            return 1;
        }

        Console.WriteLine("=================================================");
        Console.WriteLine("   AnkaX Texel Evaluation Tuning Tool");
        Console.WriteLine("=================================================");
        Console.WriteLine($"Loading dataset: '{dataPath}'...");

        var (positions, results) = LoadDataset(dataPath);
        Console.WriteLine($"Loaded {positions.Length:N0} quiet positions successfully.\n");

        List<TunableParam> parameters = RegisterParameters();

        // Step 1: Find optimal scaling factor K
        Console.WriteLine("Calculating optimal scaling constant K...");
        double bestK = FindOptimalK(positions, results);
        double initialLoss = ComputeTotalLoss(positions, results, bestK);
        Console.WriteLine($"Optimal K = {bestK:F6} | Initial MSE Loss = {initialLoss:F8}\n");

        // Step 2: Coordinate Descent Optimization
        Console.WriteLine($"Starting optimization for {parameters.Count} parameters (Max Epochs: {maxEpochs})...\n");
        double currentLoss = initialLoss;

        for (int epoch = 1; epoch <= maxEpochs; epoch++)
        {
            int updatedCount = 0;

            foreach (var param in parameters)
            {
                int baseVal = param.Getter();

                // Step +1
                param.Setter(baseVal + 1);
                double lossPlus = ComputeTotalLoss(positions, results, bestK);

                if (lossPlus < currentLoss)
                {
                    currentLoss = lossPlus;
                    updatedCount++;
                    continue;
                }

                // Step -1
                param.Setter(baseVal - 1);
                double lossMinus = ComputeTotalLoss(positions, results, bestK);

                if (lossMinus < currentLoss)
                {
                    currentLoss = lossMinus;
                    updatedCount++;
                    continue;
                }

                // Revert step if no improvement
                param.Setter(baseVal);
            }

            Console.WriteLine($"Epoch {epoch,3}: MSE Loss = {currentLoss:F8} | Parameters Updated = {updatedCount}");

            if (updatedCount == 0)
            {
                Console.WriteLine("\n[+] Convergence reached: Local minimum found.");
                break;
            }
        }

        // Step 3: Print copy-paste ready C# output with full comments
        PrintTunedCode(initialLoss, currentLoss);

        return 0;
    }

    // =========================================================================
    //  SECTION 3 — Parameter Registration
    // =========================================================================

    private static List<TunableParam> RegisterParameters()
    {
        List<TunableParam> paramsList = new();

        // Material Values (Pawn, Knight, Bishop, Rook, Queen)
        string[] pieceNames = ["Pawn", "Knight", "Bishop", "Rook", "Queen"];
        for (int i = 0; i < 5; i++)
        {
            int idx = i;
            paramsList.Add(new($"MgMaterial_{pieceNames[idx]}", () => Evaluation.MgMaterial[idx], v => Evaluation.MgMaterial[idx] = v));
            paramsList.Add(new($"EgMaterial_{pieceNames[idx]}", () => Evaluation.EgMaterial[idx], v => Evaluation.EgMaterial[idx] = v));
        }

        // Passed Pawn Bonus by Rank (Ranks 2 to 7 -> Array indices 1 to 6)
        for (int r = 1; r <= 6; r++)
        {
            int rankIdx = r;
            paramsList.Add(new($"PassedMg_Rank{rankIdx + 1}", () => Evaluation.PassedMg[rankIdx], v => Evaluation.PassedMg[rankIdx] = v));
            paramsList.Add(new($"PassedEg_Rank{rankIdx + 1}", () => Evaluation.PassedEg[rankIdx], v => Evaluation.PassedEg[rankIdx] = v));
        }

        // Positional Features
        paramsList.Add(new("BishopPairMg", () => Evaluation.BishopPairMg, v => Evaluation.BishopPairMg = v));
        paramsList.Add(new("BishopPairEg", () => Evaluation.BishopPairEg, v => Evaluation.BishopPairEg = v));
        paramsList.Add(new("KnightMobMg", () => Evaluation.KnightMobMg, v => Evaluation.KnightMobMg = v));
        paramsList.Add(new("KnightMobEg", () => Evaluation.KnightMobEg, v => Evaluation.KnightMobEg = v));
        paramsList.Add(new("BishopMobMg", () => Evaluation.BishopMobMg, v => Evaluation.BishopMobMg = v));
        paramsList.Add(new("BishopMobEg", () => Evaluation.BishopMobEg, v => Evaluation.BishopMobEg = v));
        paramsList.Add(new("RookSemiOpenMg", () => Evaluation.RookSemiOpenMg, v => Evaluation.RookSemiOpenMg = v));
        paramsList.Add(new("RookSemiOpenEg", () => Evaluation.RookSemiOpenEg, v => Evaluation.RookSemiOpenEg = v));
        paramsList.Add(new("RookOpenMg", () => Evaluation.RookOpenMg, v => Evaluation.RookOpenMg = v));
        paramsList.Add(new("RookOpenEg", () => Evaluation.RookOpenEg, v => Evaluation.RookOpenEg = v));
        paramsList.Add(new("IsolatedMg", () => Evaluation.IsolatedMg, v => Evaluation.IsolatedMg = v));
        paramsList.Add(new("IsolatedEg", () => Evaluation.IsolatedEg, v => Evaluation.IsolatedEg = v));
        paramsList.Add(new("KingOwnOpenMg", () => Evaluation.KingOwnOpenMg, v => Evaluation.KingOwnOpenMg = v));
        paramsList.Add(new("KingOwnSemiOpenMg", () => Evaluation.KingOwnSemiOpenMg, v => Evaluation.KingOwnSemiOpenMg = v));
        paramsList.Add(new("KingAdjacentOpenMg", () => Evaluation.KingAdjacentOpenMg, v => Evaluation.KingAdjacentOpenMg = v));
        paramsList.Add(new("KingAdjacentSemiOpenMg", () => Evaluation.KingAdjacentSemiOpenMg, v => Evaluation.KingAdjacentSemiOpenMg = v));
        paramsList.Add(new("QueenlessKingCenterMg", () => Evaluation.QueenlessKingCenterMg, v => Evaluation.QueenlessKingCenterMg = v));
        paramsList.Add(new("KnightOutpostMg", () => Evaluation.KnightOutpostMg, v => Evaluation.KnightOutpostMg = v));

        return paramsList;
    }

    // =========================================================================
    //  SECTION 4 — Dataset Parsing & Loss Functions
    // =========================================================================

    private static (BoardState[] positions, double[] results) LoadDataset(string path)
    {
        string[] lines = File.ReadAllLines(path);
        List<BoardState> posList = new(lines.Length);
        List<double> resList = new(lines.Length);

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int pipeIdx = line.IndexOf('|');
            if (pipeIdx == -1) continue;

            string scoreStr = line[..pipeIdx].Trim();
            string fenStr = line[(pipeIdx + 1)..].Trim();

            if (double.TryParse(scoreStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double res))
            {
                Board.ParseFEN(fenStr);
                posList.Add(Board.CopyBoard());
                resList.Add(res);
            }
        }

        return (posList.ToArray(), resList.ToArray());
    }

    private static double FindOptimalK(BoardState[] positions, double[] results)
    {
        double bestK = 0.0035;
        double minLoss = double.MaxValue;

        for (double k = 0.0010; k <= 0.0100; k += 0.0001)
        {
            double loss = ComputeTotalLoss(positions, results, k);
            if (loss < minLoss)
            {
                minLoss = loss;
                bestK = k;
            }
        }

        return bestK;
    }

    private static double ComputeTotalLoss(BoardState[] positions, double[] results, double K)
    {
        double totalLoss = 0.0;
        int count = positions.Length;
        double scalingFactor = -K * 2.302585092994046 / 400.0;

        for (int i = 0; i < count; i++)
        {
            Board.TakeBack(in positions[i]);
            int eval = Evaluation.Evaluate();
            int evalWhite = positions[i].side == White ? eval : -eval;

            double sigmoid = 1.0 / (1.0 + Math.Exp(evalWhite * scalingFactor));
            double diff = results[i] - sigmoid;
            totalLoss += diff * diff;
        }

        return totalLoss / count;
    }

    // =========================================================================
    //  SECTION 5 — Formatted Code Output
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
        Console.WriteLine("    private const int TotalPhase = 24;\n");
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
        Console.WriteLine("    // Passed pawn bonus indexed by \"distance from own back rank\" (0..7).");
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
        Console.WriteLine($"    private const int TempoBonus = 8;\n");
        Console.WriteLine("    #endregion");

        Console.WriteLine("\n=================================================");
        Console.WriteLine($"Final MSE Loss: {finalLoss:F8} (Started at {initialLoss:F8})");
        Console.WriteLine("=================================================\n");
    }
}