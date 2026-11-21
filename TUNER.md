# AnkaX Sparse Texel Tuner (`Tuner.cs`)

The **AnkaX Sparse Texel Tuner** is a high-performance, multi-threaded supervised learning pipeline built directly into the AnkaX chess engine. It optimizes all **767 independent evaluation parameters** (387 Middlegame and 380 Endgame weights) simultaneously by minimizing the Mean Squared Error (MSE) between the engine's static evaluation win-probability predictions and resolved game outcomes blended with deep search evaluations.

---

## Table of Contents

1. [Architecture & Performance Highlights](#1-architecture--performance-highlights)
2. [Command-Line Usage & Tuning Modes](#2-command-line-usage--tuning-modes)
3. [Supported Dataset Formats](#3-supported-dataset-formats)
4. [Feature Map & Parameter Layout](#4-feature-map--parameter-layout)
5. [Mathematical Formulation](#5-mathematical-formulation)
6. [Engineering Safeguards & Invariants](#6-engineering-safeguards--invariants)
7. [Material & PST Post-Processing Decomposition](#7-material--pst-post-processing-decomposition)
8. [Integration with `Evaluation.cs` (SWAR Packing)](#8-integration-with-evaluationcs-swar-packing)
9. [Standard Tuning & SPRT Workflow](#9-standard-tuning--sprt-workflow)
10. [How to Add a New Evaluation Term](#10-how-to-add-a-new-evaluation-term)

---

## 1. Architecture & Performance Highlights

Chess positions are inherently sparse: out of **387** possible evaluation features, a typical chess position activates only **~18.9 non-zero features** (`< 5%` density). `Tuner.cs` exploits this sparsity and modern CPU hardware through five core mechanisms:

* **Compressed Sparse Row (CSR) Storage:**
  During dataset loading, every position is parsed once into a flat, contiguous array of `FeatureEntry(ushort Index, short Coeff)` records (4 bytes per active feature) indexed by compact `PositionRecord` headers (12 bytes per position). For 10 million positions (~189 million active entries), the entire dataset fits comfortably in ~880 MB of RAM with linear cache-friendly traversal.
* **SIMD Dual-Lane `Vector128<double>` Execution:**
  Every feature weight, gradient, and Adam moment is stored as a 128-bit hardware vector (`Vector128.Create(mg, eg)`). Middlegame (lane 0) and Endgame (lane 1) evaluations, gradients, and parameter updates execute in parallel in a single SIMD instruction stream.
* **4-Way Loop Unrolling & Pointer Arithmetic:**
  The hot forward and backward passes bypass C# array bounds checking via `MemoryMarshal.GetArrayDataReference` and `Unsafe.Add`, using dual accumulators (`sum0`, `sum1`) unrolled 4 entries per iteration to break CPU dependency chains.
* **Lock-Free Multi-Threaded Map-Reduce:**
  Positions are statically partitioned across `Environment.ProcessorCount` worker threads. Each thread accumulates squared error and feature gradients into its own isolated `ThreadBuffer` with zero lock contention or false sharing, followed by a fast vector reduction across the 387 features at the end of each epoch.
* **Throughput:**
  Processes **~10,000,000 positions (~189,000,000 active features) in ~410 ms per epoch** on a 4-core CPU, converging a full fresh tune in ~3.5 minutes.

---

## 2. Command-Line Usage & Tuning Modes

### Syntax

```powershell
dotnet run -c Release -- tune <dataset-path> [maxEpochs] [--fresh]
```

### Arguments

| Argument | Default | Description |
| :--- | :--- | :--- |
| `<dataset-path>` | *(Required)* | Path to the training book/EPD/FEN file. |
| `[maxEpochs]` | `20000` | Maximum number of Adam epochs before forced termination. |
| `--fresh` *(aliases: `-f`, `--reset`, `--pesto`)* | `false` | Forces a clean reset to the official PeSTO baseline. |

### Mode 1: Fresh Baseline Tune (`--fresh`)

```powershell
dotnet run -c Release -- tune data/E12.52-1M-D12-Resolved.book --fresh
```

* **Initial Weights:** Initializes Material and Piece-Square Tables (PSTs) from the immutable `OfficialMgMaterial`, `OfficialEgMaterial`, `OfficialMgPst`, and `OfficialEgPst` arrays embedded inside `Tuner.cs`. All secondary positional bonuses/penalties and `TempoBonus` start at `0`.
* **Initial Learning Rate:** `1.0` (decays via `LR = 1.0 / (1.0 + 0.005 * epoch)`).
* **When to Use:**
  * Performing a full periodic re-tune (e.g., weekly/monthly) after accumulating a large collection of new self-play games.
  * Recovering from corrupted or mis-scaled weights in `Evaluation.cs`.
  * Establishing a clean baseline after major structural changes to the evaluation function.

### Mode 2: Incremental Tune (Default)

```powershell
dotnet run -c Release -- tune data/new_games_batch.book
```

* **Initial Weights:** Warm-starts directly from the current constants and tables compiled in `Evaluation.cs` (`GetCurrentEvaluationWeights()`).
* **Initial Learning Rate:** `0.25` (decays via `LR = 0.25 / (1.0 + 0.005 * epoch)`).
* **When to Use:**
  * Fine-tuning existing weights when a **new dataset** of games becomes available.
  * Tuning a **newly added evaluation term** without having to re-learn all existing PSTs from scratch.
  * Running a quick `Ctrl+C` startup check to verify that `Tuner.cs` and `Evaluation.cs` match 100% across the dataset.

> **Important:** Do not run Incremental Mode repeatedly back-to-back on the *same* dataset file after a tune has already converged. Once the common features have converged on a dataset, forcing additional epochs only chases statistical noise on rarely visited squares.

### Graceful `Ctrl+C` Interruption

Pressing `Ctrl+C` at any point during step `[4/4]` does **not** abort the process or lose progress. The `Console.CancelKeyPress` handler intercepts the signal, finishes the current epoch (~0.4s), prints the best MSE achieved so far, and outputs the full formatted C# code for `bestWeights`.

---

## 3. Supported Dataset Formats

`TryParseDatasetLine` automatically detects and parses three standard quiet-position dataset formats:

1. **Bracketed Result with Optional Search Score (Recommended):**

   ```text
   rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1 [0.5] 18
   r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3 [1.0] +42
   r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4 [0-1] -35
   ```

   * The value inside `[...]` is the game outcome from **White's perspective** (`1.0` / `1-0` = White win, `0.5` / `1/2-1/2` = Draw, `0.0` / `0-1` = Black win, or any continuous float in `[0.0, 1.0]`).
   * The integer after `]` (if present and non-zero) is the search evaluation in centipawns from the **side-to-move's perspective** (`pos.Eval`).

2. **Pipe-Delimited Format:**

   ```text
   1.0 | rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1
   0.5 | r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3
   ```

3. **Standard EPD `c9` Comment Format:**

   ```text
   rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1 c9 "1/2-1/2";
   ```

### Automatic Sanity Filtering

During `LoadDataset`, any malformed line or illegal board state is automatically skipped if:

* `PopCount(bitboards[K]) != 1` or `PopCount(bitboards[k]) != 1` (missing or multiple kings).
* `((bitboards[P] | bitboards[p]) & 0xFF000000000000FFUL) != 0` (pawns on the 1st or 8th rank).

---

## 4. Feature Map & Parameter Layout

`Tuner.cs` defines `TotalFeatures = 387`. Each feature index $i \in [0, 386]$ holds a `Vector128<double>` containing `(MG_Weight, EG_Weight)`. Because 6 features (`380..385`) are Middlegame-only and 1 feature (`386`, Tempo) is phase-independent, there are **$387 \text{ MG} + 380 \text{ EG} = 767$** independent scalar weights.

### Square Indexing Convention

AnkaX uses big-endian rank-file indexing where **`a8 = 0`**, **`h8 = 7`**, **`a1 = 56`**, and **`h1 = 63`**:

* `rank = sq >> 3` (`0` = 8th rank, `1` = 7th rank, ..., `6` = 2nd rank, `7` = 1st rank).
* `file = sq & 7` (`0` = a-file, ..., `7` = h-file).
* Black pieces mirror their square vertically via `sq ^ 56`.

### Complete Feature Table

| Feature Index | Constant Name | Count | Phase Mode | Coefficient $x_i$ (White $-$ Black) |
| :--- | :--- | :--- | :--- | :--- |
| `0 .. 47` | *(Pawn PST)* | 48 | MG + EG | White pawn on `sq` ($+1$) minus Black pawn on `sq ^ 56` ($-1$), for `sq` $\in [8, 55]$ (`index = sq - 8`). *(Note: `373` is frozen and decomposed into `8..15` post-tune).* |
| `48 .. 111` | *(Knight PST)* | 64 | MG + EG | White knight on `sq` ($+1$) minus Black knight on `sq ^ 56` ($-1$) (`index = 48 + sq`). |
| `112 .. 175` | *(Bishop PST)* | 64 | MG + EG | White bishop on `sq` ($+1$) minus Black bishop on `sq ^ 56` ($-1$) (`index = 112 + sq`). |
| `176 .. 239` | *(Rook PST)* | 64 | MG + EG | White rook on `sq` ($+1$) minus Black rook on `sq ^ 56` ($-1$) (`index = 176 + sq`). |
| `240 .. 303` | *(Queen PST)* | 64 | MG + EG | White queen on `sq` ($+1$) minus Black queen on `sq ^ 56` ($-1$) (`index = 240 + sq`). |
| `304 .. 367` | *(King PST)* | 64 | MG + EG | White king on `sq` ($+1$) minus Black king on `sq ^ 56` ($-1$) (`index = 304 + sq`). |
| `368` | `BpIdx` | 1 | MG + EG | Bishop pair indicator: $+1$ if White has $\ge 2$ bishops, $-1$ if Black has $\ge 2$ bishops. |
| `369` | `KmIdx` | 1 | MG + EG | Net Knight mobility above baseline (`PopCount(attacks & ~friendlyOcc) - 4`). |
| `370` | `BmIdx` | 1 | MG + EG | Net Bishop mobility above baseline (`PopCount(attacks & ~friendlyOcc) - 6`). |
| `371` | `RsoIdx` | 1 | MG + EG | Net Rooks on semi-open files (no friendly pawns, $\ge 1$ enemy pawn on file). |
| `372` | `RoIdx` | 1 | MG + EG | Net Rooks on open files (no pawns of either color on file). |
| `373 .. 378` | `PassedBaseIdx + d - 1` | 6 | MG + EG | Net Passed Pawns at rank distance $d \in [1, 6]$ from promotion (`373` is frozen during Adam and decomposed post-tune). |
| `379` | `IsoIdx` | 1 | MG + EG | Net Isolated Pawns (no friendly pawns on adjacent files). |
| `380` | `KooIdx` | 1 | **MG-Only** | King on own open file penalty (`-1` if White king file open, `+1` if Black king file open). |
| `381` | `KosoIdx` | 1 | **MG-Only** | King on own semi-open file penalty (`-1` for White, `+1` for Black). |
| `382` | `KaoIdx` | 1 | **MG-Only** | King adjacent open file penalty (`-1` per open adjacent file for White, `+1` for Black). |
| `383` | `KasoIdx` | 1 | **MG-Only** | King adjacent semi-open file penalty (`-1` per semi-open adjacent file for White, `+1` for Black). |
| `384` | `QkcIdx` | 1 | **MG-Only** | Queenless king centralization (`KingCenterTable[wkSq] - KingCenterTable[bkSq]` when `wQ \| bQ == 0`). |
| `385` | `KnoIdx` | 1 | **MG-Only** | Net Knights on outposts (ranks 4–6, defended by friendly pawn, no enemy pawn on adjacent files ahead). |
| `386` | `TempoIdx` | 1 | **Phase-Indep.** | Side-to-move bonus (`+1` if White to move, `-1` if Black to move). |

---

## 5. Mathematical Formulation

### 5.1 Tapered Evaluation Model

For each position $p$, the game phase $\phi_p \in [0, 24]$ is computed from non-pawn material:

$$\phi_p = \min\Big(24,\; N_{\text{knights}} + N_{\text{bishops}} + 2 N_{\text{rooks}} + 4 N_{\text{queens}}\Big)$$

Let $\mathbf{w}_{\text{ phase}}(\phi_p) = \left[\frac{\phi_p}{24},\; \frac{24 - \phi_p}{24}\right]^T$ be the precomputed phase weight vector (`PhaseWeights[pos.Phase]`). The continuous White-perspective linear evaluation $E_p$ is the dot product of the accumulated feature sum and the phase weights:

$$\mathbf{S}_p = \begin{bmatrix} S_{p,\text{MG}} \\ S_{p,\text{EG}} \end{bmatrix} = \sum_{(i, c) \in \text{CSR}(p)} c \cdot \begin{bmatrix} w_{i,\text{MG}} \\ w_{i,\text{EG}} \end{bmatrix}, \qquad E_p = \mathbf{S}_p \cdot \mathbf{w}_{\text{phase}}(\phi_p)$$

### 5.2 Win-Probability Sigmoid & Target Blending

The evaluation $E_p$ is mapped to a win probability $\hat{y}_p \in (0, 1)$ via a logistic sigmoid scaled by constant $K$:

$$s = \frac{K \ln(10)}{400}, \qquad \hat{y}_p = \sigma(s \cdot E_p) = \frac{1}{1 + e^{-s E_p}}$$

To reduce variance from tactical blunders or endgame accidents in fast self-play games, the training target $y_p$ blends the game outcome $R_p \in \{0.0, 0.5, 1.0\}$ with the dataset's search evaluation $E_{p,\text{search}}$ (converted to White's perspective via $\text{StmSign}_p$) whenever a non-zero search score is present:

$$y_p = \begin{cases} 0.5 \cdot R_p + 0.5 \cdot \sigma\left(s \cdot \text{StmSign}_p \cdot E_{p,\text{search}}\right) & \text{if } E_{p,\text{search}} \neq 0 \\ R_p & \text{if } E_{p,\text{search}} = 0 \end{cases}$$

### 5.3 Objective Function & Analytical Gradients

The tuner minimizes the Mean Squared Error across all $N$ positions:

$$\mathcal{L}(\mathbf{W}) = \frac{1}{N} \sum_{p=1}^{N} \left(\hat{y}_p - y_p\right)^2$$

By the chain rule, using $\frac{d\sigma(z)}{dz} = \sigma(z)\left(1 - \sigma(z)\right)$, the partial derivative with respect to feature $i$'s MG and EG weights for position $p$ is:

$$\delta_p = 2 s \left(\hat{y}_p - y_p\right) \hat{y}_p \left(1 - \hat{y}_p\right)$$

$$\nabla_{\mathbf{w}_i} \mathcal{L}_p = c_{p,i} \cdot \delta_p \cdot \begin{bmatrix} \frac{\phi_p}{24} \\[4pt] \frac{24 - \phi_p}{24} \end{bmatrix}$$

### 5.4 Multi-Threaded Adam Update Rule

At each epoch $t$, the global gradient $\mathbf{g}_i = \frac{1}{N} \sum_{p=1}^N \nabla_{\mathbf{w}_i} \mathcal{L}_p$ updates first and second moment vectors $\mathbf{m}_i, \mathbf{v}_i \in \mathbb{R}^2$ (`beta1 = 0.9`, `beta2 = 0.999`, `eps = 1e-8`):

$$\mathbf{m}_i^{(t)} = \beta_1 \mathbf{m}_i^{(t-1)} + (1 - \beta_1) \mathbf{g}_i$$

$$\mathbf{v}_i^{(t)} = \beta_2 \mathbf{v}_i^{(t-1)} + (1 - \beta_2) \left(\mathbf{g}_i \odot \mathbf{g}_i\right)$$

$$\hat{\mathbf{m}}_i^{(t)} = \frac{\mathbf{m}_i^{(t)}}{1 - \beta_1^t}, \qquad \hat{\mathbf{v}}_i^{(t)} = \frac{\mathbf{v}_i^{(t)}}{1 - \beta_2^t}$$

$$\mathbf{w}_i^{(t)} = \mathbf{w}_i^{(t-1)} - \frac{\eta_t}{\sqrt{\hat{\mathbf{v}}_i^{(t)}} + \epsilon} \odot \hat{\mathbf{m}}_i^{(t)}, \qquad \eta_t = \frac{\eta_0}{1 + 0.005 t}$$

---

## 6. Engineering Safeguards & Invariants

Several subtle numerical pitfalls commonly break Texel tuners. `Tuner.cs` enforces five strict invariants to guarantee mathematical and engine-level correctness:

### 6.1 100% Startup Score Validation (`ValidateScores`)

Before running a single optimization step, step `[2/4]` reconstructs the integer evaluation of every single position in the dataset from its extracted CSR features and compares it against `Evaluation.Evaluate()`.

* In `Evaluation.Evaluate()`, `TempoBonus` is added **after** the integer division `/ TotalPhase`:

  ```csharp
  int whiteScore = (mgScore * pos.Phase + egScore * (Evaluation.TotalPhase - pos.Phase)) / Evaluation.TotalPhase;
  int calcEval = (pos.StmSign * whiteScore) + tempoBonus;
  ```

* Because C# integer division truncates toward zero, folding `TempoBonus * 24` into `mgScore` and `egScore` prior to dividing by `24` can differ by `1 cp` whenever `whiteScore` and `tempoBonus` have opposite signs and the numerator is not divisible by 24. `ValidateScores` mirrors `Evaluation.Evaluate()`'s exact integer arithmetic and throws an `InvalidOperationException` if even 1 position out of 10,000,000 disagrees by `1 cp`.

### 6.2 Fixed Centipawn Scale (`Optimal K` Anchored to PeSTO)

In a linear evaluation where every feature is tuned, the sigmoid prediction $\sigma(s \cdot E(\mathbf{W}))$ depends only on the product $K \cdot \mathbf{W}$.

* If `FindOptimalK` were calibrated against the *current* tuned weights in Incremental Mode, adding new positional bonuses (which widens the evaluation distribution) would cause `FindOptimalK` to shrink $K$.
* A smaller $K$ compresses the blended search-eval targets $\sigma(s \cdot E_{\text{search}})$ toward `0.5` and reduces the sigmoid slope, forcing Adam to **inflate all material and positional weights** to compensate—creating a runaway feedback loop across incremental runs.
* **Solution:** `Tuner.cs` **always** calibrates $K$ against the immutable `GetPestoBaselineWeights()` via a 30-iteration Golden-Section Search over $[0.1, 5.0]$. Furthermore, `FindOptimalK` precomputes the static PeSTO evaluations once before the search loop, making step `[3/4]` ~15× faster and locking $K$ to an invariant centipawn scale on any given dataset.

### 6.3 7th-Rank Passed Pawn Collinearity Fix (`PassedBaseIdx`)

On a chessboard (`a8=0..h1=63`), a White pawn on the 7th rank (`rank = 1`, squares `8..15`) only has the 8th rank (`rank = 0`) ahead of it. Because enemy pawns can never exist on the 1st or 8th rank, `WhitePassedMask[sq] & bP` is **always zero** for every pawn on the 7th rank.

* Consequently, 100% of pawns on the 7th rank trigger both their 7th-rank Pawn PST feature (`index = 0..7`) **and** the 7th-rank Passed Pawn feature (`PassedBaseIdx = 373`).
* The sum of the 8 Pawn PST features on rank 7 equals the `PassedBaseIdx` feature identically ($\sum_{f=0}^7 x_{\text{PST}(8+f)} \equiv x_{\text{Passed}[1]}$), creating **perfect linear collinearity**.
* If Adam updates both `PassedBaseIdx` and `PawnPST[8..15]`, their weights drift in opposite directions (driving `PassedMg[1]` and `PassedEg[1]` negative while inflating the 7th-rank Pawn PSTs).
* **Solution:** `Tuner.cs` freezes `PassedBaseIdx` (`if (i == PassedBaseIdx) continue;`) during Adam updates so the 8 seventh-rank Pawn PST features absorb the full combined value of a 7th-rank pawn, and then decomposes them cleanly in `PrintFormattedResults` (see Section 7).

### 6.4 Phase-Constrained & Phase-Independent Gradients

* **Middlegame-Only Terms (`KooIdx..KnoIdx`, indices `380..385`):**
  King file exposure, queenless king centralization, and knight outposts are evaluated only in the middlegame in `Evaluation.cs`. In the Adam loop, the Endgame step lane is explicitly zeroed:

  ```csharp
  if (i >= KooIdx && i <= KnoIdx)
  {
      step = step.WithElement(1, 0.0);
  }
  ```

* **Phase-Independent Tempo (`TempoIdx`, index `386`):**
  `TempoBonus` is added unscaled regardless of game phase, meaning $\frac{\partial E_p}{\partial w_{\text{Tempo}}} = c_{\text{Tempo}} \left(\frac{\phi_p}{24} + \frac{24 - \phi_p}{24}\right) = c_{\text{Tempo}}$. Before the Adam update, the tuner sums both phase gradient lanes into a single unified gradient:

  ```csharp
  double tempoTotalGrad = grad0[TempoIdx].GetElement(0) + grad0[TempoIdx].GetElement(1);
  grad0[TempoIdx] = Vector128.Create(tempoTotalGrad, tempoTotalGrad);
  ```

### 6.5 Windowed Relative Early Stopping

To prevent endless training when MSE improvements become negligible, `Tuner.cs` tracks a reference `anchorMse` alongside `bestMse`:

* `noImprovementEpochs` resets to `0` (and `anchorMse` updates to `currentMse`) **only** when the cumulative relative MSE drop from `anchorMse` exceeds `minRelImprovement = 2e-4` (`0.02%`).
* If `patience = 30` consecutive epochs pass without achieving a `0.02%` relative reduction from the last anchor, training halts automatically and outputs `bestWeights`.

---

## 7. Material & PST Post-Processing Decomposition

During optimization, base material values are folded into the Piece-Square Tables so every piece-square weight $w_{pc, sq}$ represents the **total value** of piece $pc$ standing on square $sq$. When training finishes, `PrintFormattedResults` decomposes those raw weights into clean, zero-centered PSTs and base material constants ready to paste into `Evaluation.cs`:

1. **Pieces (`Knight`, `Bishop`, `Rook`, `Queen`, $pc \in \{1, 2, 3, 4\}$):**
   * Base Material is the mean across all 64 squares:
     $$\text{Mat}_{pc} = \text{round}\left(\frac{1}{64} \sum_{sq=0}^{63} w_{pc, sq}\right)$$
   * Each PST entry is the residual from the piece's base material:
     $$\text{PST}_{pc}[sq] = \text{round}\left(w_{pc, sq}\right) - \text{Mat}_{pc}$$

2. **Pawns ($pc = 0$) & 7th-Rank Passed Pawn (`Passed[1]`):**
   * Because 7th-rank pawns (`sq = 8..15`) carry the huge promotion-threat bonus of a passed pawn one step from queening, averaging them into `MgMaterial[0]` / `EgMaterial[0]` would artificially inflate base pawn material and make ranks 2–6 look heavily negative.
   * Instead, base Pawn Material is computed as the mean over **ranks 2 through 6** (`sq = 16..55`, 40 squares):
     $$\text{Mat}_{\text{Pawn}} = \text{round}\left(\frac{1}{40} \sum_{sq=16}^{55} w_{0, sq}\right)$$
   * For ranks 2–6 (`sq = 16..55`), the Pawn PST is simply $\text{round}(w_{0, sq}) - \text{Mat}_{\text{Pawn}}$.
   * For the 7th rank (`sq = 8..15`), let $T_f = \text{round}\left(w_{0, 8+f} + w_{\text{PassedBaseIdx}}\right)$ be the total tuned value of a pawn on file $f \in [0, 7]$ of the 7th rank. The 7th-rank passed pawn bonus (`PassedMg[1]` / `PassedEg[1]`) is extracted as the average excess over base pawn material:
     $$\text{Passed}[1] = \text{round}\left(\frac{1}{8} \sum_{f=0}^{7} \left(T_f - \text{Mat}_{\text{Pawn}}\right)\right)$$
   * Finally, the 7th-rank Pawn PST entries (`sq = 8..15`) store the file-specific residuals:
     $$\text{PST}_{\text{Pawn}}[8 + f] = T_f - \text{Mat}_{\text{Pawn}} - \text{Passed}[1]$$
   * This guarantees that in `Evaluation.Evaluate()`, the sum $\text{Mat}_{\text{Pawn}} + \text{PST}_{\text{Pawn}}[8+f] + \text{Passed}[1] = T_f$ reproduces the exact tuned weight with zero loss of accuracy.

3. **King ($pc = 5$):**
   * King material is fixed at `0`, so $\text{PST}_{\text{King}}[sq] = \text{round}(w_{5, sq})$.

---

## 8. Integration with `Evaluation.cs` (SWAR Packing)

When the output of `Tuner.cs` is pasted into `Evaluation.cs`, the static constructor `Evaluation()` folds all material arrays, PST source tables, and positional constants into **SWAR (SIMD Within A Register)** 32-bit packed integers:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static int PackScore(int mg, int eg) => (eg << 16) + mg;
```

* At startup, `Evaluation` builds a single flat lookup table `PstScoreTable[12 * 64]` where each entry holds `PackScore(MgMaterial[pc] + MgPstSource[pc][sq], EgMaterial[pc] + EgPstSource[pc][sq])` for White pieces and its negation for mirrored Black pieces.
* During `Evaluation.Evaluate()`, both Middlegame and Endgame scores are accumulated in a single 32-bit integer `scorePacked` using standard `+` and `-` operations, and unpacked at the end via sign-extension correction:

  ```csharp
  int mg = (short)scorePacked;
  int eg = (scorePacked + 0x8000) >> 16;
  ```

---

## 9. Standard Tuning & SPRT Workflow

Follow this exact workflow whenever tuning AnkaX:

### Step 1: Run the Tuner

For a clean full tune from the PeSTO baseline:

```powershell
dotnet run -c Release -- tune data/E12.52-1M-D12-Resolved.book --fresh
```

### Step 2: Inspect the Output Sanity Checks

Before pasting the generated code into `Evaluation.cs`, verify that:

1. **Material Ordering:** `0 < Pawn < Knight < Bishop < Rook < Queen` in both `MgMaterial` and `EgMaterial`.
2. **Passed Pawn Progression:** `PassedEg[1..6]` increases monotonically as the index approaches `1` (e.g., `PassedEg = [0, 143, 75, 58, 43, 23, 17, 0]`).
3. **Positional Signs:**
   * `BishopPairMg > 0` and `BishopPairEg > 0`
   * `0 < RookSemiOpen < RookOpen` (both MG and EG)
   * `IsolatedMg < 0` and `IsolatedEg < 0`
   * `KingOwnOpenMg > KingAdjacentOpenMg > 0` and `KingOwnSemiOpenMg > 0`
   * `KnightOutpostMg > 0` and `TempoBonus > 0`

### Step 3: Paste & Verify 100% Score Match

1. Copy the printed `#region Material & Phase`, `#region Positional Bonuses / Penalties`, and `#region PST Data` blocks from the console output and replace those three regions in `Evaluation.cs`.
2. Run the tuner without `--fresh` and press `Ctrl+C` right after `[2/4]`:

   ```powershell
   dotnet run -c Release -- tune data/E12.52-1M-D12-Resolved.book
   ```

3. Confirm that `[2/4]` outputs:

   ```text
   [Validation] 100% match across 9,998,762 positions with Evaluation.Evaluate().
   ```

### Step 4: Run an SPRT Match

Never commit tuned weights based on MSE reduction alone—always validate with a Sequential Probability Ratio Test (SPRT) in self-play using `fastchess` or `cutechess-cli`:

```powershell
# Build the candidate binary
dotnet publish -c Release -o ./bin/candidate

# Example fastchess SPRT command (STC: 8.0s + 0.08s)
fastchess `
  -engine cmd=./bin/candidate/ankax.exe name=AnkaX_Tuned `
  -engine cmd=./bin/master/ankax.exe name=AnkaX_Base `
  -each tc=8+0.08 `
  -rounds 10000 -repeat -concurrency 4 `
  -openings file=books/UHO_Lichess_4852_v1.epd format=epd order=random `
  -sprt elo0=0.0 elo1=5.0 alpha=0.05 beta=0.05
```

---

## 10. How to Add a New Evaluation Term

Because `Tuner.cs` validates 100% of positions against `Evaluation.Evaluate()` at startup, adding a new evaluation feature is completely foolproof—if the feature extraction in `Tuner.cs` does not match `Evaluation.cs` on even a single board state, `[2/4]` will immediately throw an exception telling you the exact position and score difference.

To add a new feature:

1. **`Evaluation.cs`:**
   * Add your new `internal const int` bonus/penalty in `#region Positional Bonuses / Penalties`.
   * If it has both MG and EG components, pack it via `private static readonly int MyNewTermScore = PackScore(MyNewTermMg, MyNewTermEg);` and add it to `scorePacked` in `Evaluate()`.
   * If it is MG-only, add it to `mg` in `Evaluate()`.
2. **`Tuner.cs`:**
   * Increment `TotalFeatures` (e.g., from `387` to `388`) and define a new feature index constant (keep `TempoIdx` as the final index).
   * In `ExtractFeatures(short[] counts)`, increment `counts[MyNewIdx]` for White and decrement `counts[MyNewIdx]` for Black using the exact same condition as `Evaluation.cs`.
   * If the term is MG-only, include its index in the `step = step.WithElement(1, 0.0);` range in `Run()`.
   * Load the initial weight in `GetCurrentEvaluationWeights()` and print the tuned constant in `PrintFormattedResults()`.
