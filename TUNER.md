# AnkaX Texel Evaluation Tuner (`Tuner.cs`)

## Technical Specification & Operational Manual

---

## 1. Architectural Overview & Mathematical Foundations

The **AnkaX Texel Tuner** is an ultra-fast, multi-threaded parameter optimization system built directly into the AnkaX chess engine. It utilizes the **Texel Tuning Method** (pioneered by Peter Österlund) to minimize the Mean Squared Error (MSE) between evaluation scores converted into win probabilities and the actual game outcomes of Millions of quiet positions.

```
       +-------------------------------------------------------+
       |               Dataset (FEN + Outcome)                 |
       +-------------------------------------------------------+
                                   |
                                   v
       +-------------------------------------------------------+
       |       Sparse Compressed Row Storage (CSR) Matrix       |
       |             (386 Extracted Features/Pos)              |
       +-------------------------------------------------------+
                                   |
                                   v
       +-------------------------------------------------------+
       |       Golden Section Search for Optimal K-Factor      |
       +-------------------------------------------------------+
                                   |
                                   v
       +-------------------------------------------------------+
       |       Multi-Threaded Adam Gradient Descent Loop       |
       |      (ThreadLocal<ThreadBuffer> Zero Allocation)      |
       +-------------------------------------------------------+
                                   |
                                   v
       +-------------------------------------------------------+
       |       Formatted Output -> C# Code Copy-Paste          |
       +-------------------------------------------------------+
```

### 1.1 The Evaluation Function & Phase Interpolation

AnkaX uses a tapered evaluation system with two static evaluation vectors: **Middlegame (MG)** and **Endgame (EG)**. The evaluation score $S(p)$ for a position $p$ is calculated as a linear interpolation based on the game phase $\Phi(p) \in [0, 24]$:

$$S(p) = \frac{\Phi(p) \cdot S_{\text{MG}}(p) + (24 - \Phi(p)) \cdot S_{\text{EG}}(p)}{24}$$

Where $S_{\text{MG}}(p)$ and $S_{\text{EG}}(p)$ are calculated as dot-products between position feature vectors $\mathbf{f}(p)$ and weight vectors $\mathbf{w}_{\text{MG}}, \mathbf{w}_{\text{EG}}$:

$$S_{\text{MG}}(p) = \mathbf{f}(p) \cdot \mathbf{w}_{\text{MG}}, \quad S_{\text{EG}}(p) = \mathbf{f}(p) \cdot \mathbf{w}_{\text{EG}}$$

### 1.2 Sigmoid Win Probability Mapping & Objective Function

To compare an evaluation score $S(p)$ (in centipawns) against a game result $R(p) \in \{1.0 \text{ (Win)}, 0.5 \text{ (Draw)}, 0.0 \text{ (Loss)}\}$, $S(p)$ is mapped to predicted win probability $P(p)$ via a custom Sigmoid function:

$$P(p) = \frac{1}{1 + 10^{-K \cdot S(p) / 400}}$$

The objective function to minimize is the Mean Squared Error ($\text{MSE}$) across $N$ positions in the training dataset:

$$\text{MSE}(\mathbf{w}_{\text{MG}}, \mathbf{w}_{\text{EG}}) = \frac{1}{N} \sum_{i=1}^{N} \left( R_i - P(p_i) \right)^2$$

### 1.3 Scaling Factor ($K$) Fitting via Golden Section Search

The constant $K$ scales evaluation scores so that a given centipawn advantage accurately reflects win probability. Before optimizing weight vectors, $K$ is optimized using **Golden Section Search** over the interval $K \in [0.0001, 0.0100]$ until convergence ($\Delta K < 10^{-6}$).

When zero-bias (Tabula Rasa) tuning is enabled, $K$ is optimized against initial baseline weights (e.g., PeSTO) **before** weight zeroing. This ensures $K$ is calibrated on meaningful evaluation gradients rather than flat $0.0$ scores.

### 1.4 Adam Gradient Optimization

The tuner uses the **Adam (Adaptive Moment Estimation)** algorithm to update weights. For each weight parameter $w_j$ (both MG and EG), first-moment estimates $m_j$ (mean) and second-moment estimates $v_j$ (uncentered variance) are maintained:

1. **Gradient Compute**:
   $$\frac{\partial \text{MSE}}{\partial w_{j, \text{MG}}} = \frac{-2}{N} \sum_{i=1}^{N} \left( R_i - P(p_i) \right) \cdot P(p_i)(1 - P(p_i)) \cdot \ln(10) \cdot \frac{K}{400} \cdot \left( \frac{\Phi(p_i)}{24} \right) \cdot f_j(p_i)$$

   $$\frac{\partial \text{MSE}}{\partial w_{j, \text{EG}}} = \frac{-2}{N} \sum_{i=1}^{N} \left( R_i - P(p_i) \right) \cdot P(p_i)(1 - P(p_i)) \cdot \ln(10) \cdot \frac{K}{400} \cdot \left( \frac{24 - \Phi(p_i)}{24} \right) \cdot f_j(p_i)$$

2. **Adam Update Steps**:
   $$m_t = \beta_1 m_{t-1} + (1 - \beta_1) g_t$$
   $$v_t = \beta_2 v_{t-1} + (1 - \beta_2) g_t^2$$
   $$\hat{m}_t = \frac{m_t}{1 - \beta_1^t}, \quad \hat{v}_t = \frac{v_t}{1 - \beta_2^t}$$
   $$w_t = w_{t-1} - \frac{\alpha_t}{\sqrt{\hat{v}_t} + \epsilon} \hat{m}_t$$

   *Hyperparameters*: $\beta_1 = 0.9$, $\beta_2 = 0.999$, $\epsilon = 10^{-8}$, initial learning rate $\alpha_0 = 0.4$ with epoch decay $\alpha_t = \frac{\alpha_0}{1 + 0.0005 \cdot t}$.

---

## 2. Feature Space Architecture (386 Features)

AnkaX extracts 386 distinct feature indices per position. Indices $0..379$ contain both Middlegame (MG) and Endgame (EG) parameters. Indices $380..385$ represent King Safety attacks and are **Middlegame-Only** (indicated by `KooIdx = 380`).

| Feature Index Range | Feature Description | Parameter Count | Weight Type |
| :--- | :--- | :---: | :---: |
| `0 .. 47` | Pawn Piece-Square Table (Ranks 2 to 7, Squares 8..55) | 48 | MG & EG |
| `48 .. 111` | Knight Piece-Square Table (Squares 0..63) | 64 | MG & EG |
| `112 .. 175` | Bishop Piece-Square Table (Squares 0..63) | 64 | MG & EG |
| `176 .. 239` | Rook Piece-Square Table (Squares 0..63) | 64 | MG & EG |
| `240 .. 303` | Queen Piece-Square Table (Squares 0..63) | 64 | MG & EG |
| `304 .. 367` | King Piece-Square Table (Squares 0..63) | 64 | MG & EG |
| `368` (`BpIdx`) | Bishop Pair Bonus | 1 | MG & EG |
| `369` (`KmIdx`) | Knight Mobility (per legal move) | 1 | MG & EG |
| `370` (`BmIdx`) | Bishop Mobility (per legal move) | 1 | MG & EG |
| `371` (`RsoIdx`) | Rook on Semi-Open File | 1 | MG & EG |
| `372` (`RoIdx`) | Rook on Open File | 1 | MG & EG |
| `373 .. 378` (`PassedBaseIdx`) | Passed Pawn Bonuses (Ranks 1 to 6) | 6 | MG & EG |
| `379` (`IsoIdx`) | Isolated Pawn Penalty | 1 | MG & EG |
| `380` (`KooIdx`) | King Attackers Outer Shield | 1 | **MG Only** |
| `381` (`KosoIdx`) | King Attackers Semi-Outer Shield | 1 | **MG Only** |
| `382` (`KaoIdx`) | King Attackers Inner Ring | 1 | **MG Only** |
| `383` (`KasoIdx`) | King Attackers Semi-Inner Ring | 1 | **MG Only** |
| `384` (`QkcIdx`) | Queen Check Threat | 1 | **MG Only** |
| `385` (`KnoIdx`) | Knight Check Threat | 1 | **MG Only** |

---

## 3. High-Performance CSR Engine Implementation

To process millions of positions per second without garbage collection overhead, the tuner uses a **Compressed Sparse Row (CSR)** matrix format paired with pooled thread buffers.

```
CSR Array Structure:
RowPointers:    [ 0,       12,       25,       37, ... ]  (Position boundaries)
FeatureIndices: [ 12, 54, 368, 14, 58, 370, ... ]         (Sparse active feature IDs)
FeatureValues:  [  1, -1,   1,  1,  1,  -2, ... ]         (Feature scalar values)
```

### 3.1 Memory Layout

```csharp
public sealed class CsrDataset
{
    public int[] RowPointers;      // Size: PositionCount + 1
    public ushort[] FeatureIndices;// Size: Total active feature occurrences
    public sbyte[] FeatureValues;  // Size: Total active feature occurrences (-128 to 127)
    public byte[] Phase;           // Size: PositionCount (0..24)
    public double[] Results;       // Size: PositionCount (1.0, 0.5, 0.0)
}
```

### 3.2 Unrolled Evaluation Inner Loop

The evaluation and gradient calculations use 4x unrolled loops to maximize CPU instruction-level parallelism (ILP):

```csharp
// Unrolled Dot Product Calculation
int k = rowStart;
for (; k <= rowEnd - 4; k += 4)
{
    mgScore += featureValues[k] * mgWeights[featureIndices[k]];
    egScore += featureValues[k] * egWeights[featureIndices[k]];

    mgScore += featureValues[k+1] * mgWeights[featureIndices[k+1]];
    egScore += featureValues[k+1] * egWeights[featureIndices[k+1]];

    mgScore += featureValues[k+2] * mgWeights[featureIndices[k+2]];
    egScore += featureValues[k+2] * egWeights[featureIndices[k+2]];

    mgScore += featureValues[k+3] * mgWeights[featureIndices[k+3]];
    egScore += featureValues[k+3] * egWeights[featureIndices[k+3]];
}
for (; k < rowEnd; k++)
{
    mgScore += featureValues[k] * mgWeights[featureIndices[k]];
    egScore += featureValues[k] * egWeights[featureIndices[k]];
}
```

### 3.3 Zero-Allocation Multithreading Structure

To eliminate thread allocation churn and partition race conditions, gradient accumulation uses `ThreadLocal<ThreadBuffer>`:

```csharp
private class ThreadBuffer
{
    public readonly double[] MgGrad = new double[NumFeatures];
    public readonly double[] EgGrad = new double[NumFeatures];
    public double MseSum;

    public void Clear()
    {
        Array.Clear(MgGrad, 0, NumFeatures);
        Array.Clear(EgGrad, 0, NumFeatures);
        MseSum = 0.0;
    }
}
```

During execution:

1. `threadBuffers.Values` clears accumulated buffers at the start of each epoch.
2. `Parallel.ForEach` processes data ranges concurrently across all available CPU threads.
3. Thread accumulators merge into the global gradient array at the end of each epoch.

---

## 4. Operational Modes & Setup

### 4.1 Mode Comparison

#### Mode A: PeSTO Fine-Tuning (Default)

* **Description**: Initializes parameters with PeSTO baseline values. Refines existing heuristic parameters.
* **Use Case**: Production engine optimization to improve existing strength.
* **Code State**: `Array.Clear(initialMg);` lines remain commented out.

#### Mode B: Zero-Bias / Tabula Rasa Tuning (From Scratch)

* **Description**: Clears all initial weights to zero, deriving all evaluation terms entirely from training data.
* **Use Case**: Learning balance weights independently without human pre-bias.
* **Code Implementation**: Uncomment lines 145–146 in `Tuner.cs`:

```csharp
// Uncomment to perform Tabula Rasa tuning from zero:
Array.Clear(initialMg, 0, initialMg.Length);
Array.Clear(initialEg, 0, initialEg.Length);
```

### 4.2 Early Stopping and Convergence Safeguards

To prevent overfitting and stop execution when improvements stall:

* **Best MSE Tracking**: `bestMse` initializes to `double.MaxValue` (preventing premature stopping caused by comparing against pre-zeroed MSE metrics).
* **Patience Window**: 50 consecutive epochs.
* **Improvement Threshold**: `minImprovementThreshold = 1e-7`.
* If $\text{MSE}_{\text{current}} < \text{MSE}_{\text{best}} - 10^{-7}$, `bestMse` updates and the plateau counter resets to 0.

---

## 5. Command-Line Interface & Execution

### 5.1 Dataset Requirement

Training datasets must consist of quiet position FENs with game outcome annotations (`[1.0]`, `[0.5]`, `[0.0]`):

```text
r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4 [0.5]
rnbqk2r/ppp1bppp/4pn2/3p4/2PP4/5NP1/PP2PPBP/RNBQK2R b KQkq - 1 5 [1.0]
```

### 5.2 Executing the Tuner Command

To run the tuner from the terminal, compile AnkaX and execute:

```bash
# General Syntax
dotnet run -c Release -- tune <path_to_epd> [epochs]

# Example: Tuning on 2,000,000 positions for up to 300 epochs
dotnet run -c Release -- tune C:\Datasets\quiet-labeled.epd 300
```

### 5.3 Sample Log Output

```text
=== AnkaX Texel Evaluation Tuner ===
Dataset File: QuietPositions.epd
Target Epochs: 300
Threads Allocated: 16

[1/3] Parsing dataset into CSR format...
Processed 1,000,000 positions...
Processed 2,000,000 positions...
Dataset parsed successfully: 2,451,029 positions loaded.
CSR Matrix Memory Usage: 118.42 MB

[2/3] Optimizing Sigmoid K Factor...
Iter 01 | K = 0.00381966 | MSE = 0.06812041
Iter 05 | K = 0.00542109 | MSE = 0.06210492
Iter 12 | K = 0.00581024 | MSE = 0.06180311
Optimal Scaling Factor Fitted: K = 0.00581024

[3/3] Starting Adam Optimization Loop...
Epoch 001/300 | MSE: 0.06180311 | LR: 0.400000 | Imp: +0.00000000 | Best
Epoch 002/300 | MSE: 0.05941022 | LR: 0.399800 | Imp: +0.00239289 | Best
Epoch 010/300 | MSE: 0.05410291 | LR: 0.398208 | Imp: +0.00031021 | Best
...
Epoch 142/300 | MSE: 0.04810231 | LR: 0.373483 | Imp: +0.00000002 | Stop Counter: 50/50
Early stopping triggered: No MSE improvement > 1e-07 for 50 consecutive epochs.

Tuning Complete. Total Time: 00:04:12.82
Final MSE: 0.04810231 (Initial MSE: 0.06180311, Delta: -0.01370080)
```

---

## 6. Integrating Results into Engine Source (`Evaluation.cs`)

When tuning finishes, `PrintFormattedResults()` generates clean, production-ready C# code. Parameters print using `private const` for scalar values and `private static readonly` for array values. This enables **JIT constant folding** and zero-overhead memory lookups, maximizing Engine Nodes Per Second (NPS).

### 6.1 Copy-Paste Source Code Output Example

Copy the console output directly into `Evaluation.cs`:

```csharp
// ============================================================================
// AUTO-GENERATED BY ANKAX TEXEL TUNER
// Final MSE: 0.04810231 | K: 0.00581024
// ============================================================================

// --- Positional Bonuses / Penalties ---
private const int BishopPairBonusMg = 32;
private const int BishopPairBonusEg = 48;

private const int KnightMobilityBonusMg = 4;
private const int KnightMobilityBonusEg = 4;

private const int BishopMobilityBonusMg = 5;
private const int BishopMobilityBonusEg = 5;

private const int RookSemiOpenBonusMg = 12;
private const int RookSemiOpenBonusEg = 8;

private const int RookOpenBonusMg = 24;
private const int RookOpenBonusEg = 16;

private const int IsolatedPawnPenaltyMg = 12;
private const int IsolatedPawnPenaltyEg = 14;

private static readonly int[] PassedPawnBonusMg = { 0, 8, 14, 28, 52, 98 };
private static readonly int[] PassedPawnBonusEg = { 0, 12, 22, 44, 82, 140 };

// --- King Safety Parameters (MG Only) ---
private const int KingOuterAttackMg = 4;
private const int KingSemiOuterAttackMg = 8;
private const int KingInnerAttackMg = 16;
private const int KingSemiInnerAttackMg = 24;
private const int QueenCheckThreatMg = 32;
private const int KnightCheckThreatMg = 20;

// --- PST Data ---
private static readonly int[] MgPawnTable = {
      0,   0,   0,   0,   0,   0,   0,   0,
     98, 134,  61,  95,  68, 126,  34, -11,
     -6,   7,  26,  31,  65,  56,  25, -20,
    -14,  13,  18,  21,  23,  12,  17, -23,
    -27,  -2,  -5,  12,  17,   6,  10, -25,
    -26,  -4,  -4, -10,   3,   3,  33, -12,
    -35,  -1, -20,  -8,  -1,  24,  38, -22,
      0,   0,   0,   0,   0,   0,   0,   0
};

private static readonly int[] EgPawnTable = {
      0,   0,   0,   0,   0,   0,   0,   0,
    178, 173, 158, 134, 147, 132, 165, 187,
     94, 100,  85,  67,  56,  69,  82,  82,
     32,  45,  31,  27,  32,  33,  31,  11,
     13,  17,   8,  13,  19,  11,  12, -11,
      4,   7,  -6,   1,   0,  -5,  -1, -11,
    -13,  -2,  -8,  -2,  -6,  -5,  -6, -22,
      0,   0,   0,   0,   0,   0,   0,   0
};

// [Truncated for brevity: Knight, Bishop, Rook, Queen, King PSTs follow exact pattern]
```

---

## 7. Validation Strategy & SPRT Testing Framework

Tuned evaluation parameters must be validated via Sequential Probability Ratio Testing (SPRT) using match automation tools like `cutechess`.

### 7.1 Testing Rules

1. **Never merge tuned values blindly**: Lower training MSE does not guarantee practical engine ELO gains.
2. **Always test against previous baseline engine build**.
