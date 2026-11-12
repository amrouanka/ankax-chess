# AnkaX Search — Technical Documentation

This document describes the search algorithm in `Search.cs`. It covers the
high-level flow, each individual heuristic with its rationale, and the
performance techniques used to keep the search fast in a managed language.

---

## Table of Contents

1. [Search Flow Overview](#1-search-flow-overview)
2. [Iterative Deepening](#2-iterative-deepening)
3. [Aspiration Windows](#3-aspiration-windows)
4. [Principal Variation Search (PVS)](#4-principal-variation-search-pvs)
5. [Alpha-Beta Core](#5-alpha-beta-core)
6. [Pruning Techniques](#6-pruning-techniques)
7. [Reductions](#7-reductions)
8. [Extensions](#8-extensions)
9. [Move Ordering](#9-move-ordering)
10. [Quiescence Search](#10-quiescence-search)
11. [Transposition Table Interaction](#11-transposition-table-interaction)
12. [Move History and Killers](#12-move-history-and-killers)
13. [Performance Techniques](#13-performance-techniques)
14. [Terminology Reference](#14-terminology-reference)

---

## 1. Search Flow Overview

A single call to `SearchPosition(depth)` does the following:

```
SearchPosition(depth)
├── Clear search state (PV, killers, history, nodes, ply)
├── for currentDepth = 1 .. depth:
│   ├── AlphaBeta(alpha, beta, currentDepth)           [1 iteration]
│   │   ├── Repetition / 50-move check
│   │   ├── Check extension
│   │   ├── Quiescence if depth == 0
│   │   ├── Mate distance pruning
│   │   ├── TT probe
│   │   ├── Static evaluation
│   │   ├── Reverse futility pruning
│   │   ├── Null move pruning
│   │   ├── Internal Iterative Reduction
│   │   ├── Futility pruning setup
│   │   ├── Move generation + ordering
│   │   └── for each move:
│   │       ├── MakeMove
│   │       ├── Recursive AlphaBeta (PVS logic)
│   │       ├── TakeBack
│   │       ├── Update best / alpha / PV
│   │       ├── Beta cutoff → killer + history + TT store
│   │       └── End of moves → TT store
│   ├── Aspiration window re-search if score outside [alpha, beta]
│   ├── Print UCI info
│   └── Time check → break if exceeded
└── Print bestmove
```

Each iteration explores the tree one ply deeper. The best move from a shallower
iteration seeds the next iteration's move ordering, making deeper searches
dramatically faster than they would be from scratch.

---

## 2. Iterative Deepening

### What it does

Searches to depth 1, then depth 2, then 3, and so on until time runs out or
the max depth is reached.

### Why it's used

It sounds wasteful — why search depth 1 if you're going to search depth 10?
Three reasons:

1. **Best-move stability**: if we run out of time at depth 7, we still have a
   complete depth-6 result to return. Without iterative deepening, we'd return
   whatever the (incomplete) depth-10 search found — which could be anything.

2. **Move ordering feedback**: the best move at depth N is almost always a
   good move at depth N+1. Storing it in the TT means the N+1 search tries it
   first, causing immediate beta cutoffs and reducing the branching factor.

3. **Time management**: we can decide "I have time for one more iteration" or
   "stop now" after each completed depth, instead of committing blindly.

### In AnkaX

```csharp
for (int currentDepth = 1; currentDepth <= depth; currentDepth++)
{
    score = AlphaBeta(alpha, beta, currentDepth);
    if (TimeManagement.ShouldStopAfterIteration()) break;
}
```

---

## 3. Aspiration Windows

### What it does

Instead of searching with the full window `[-∞, +∞]`, we search with a narrow
window around the previous iteration's score: `[score - 50, score + 50]`.

### Why it's used

Narrower windows prune more aggressively. Most positions don't shift evaluation
by more than 50 centipawns between iterations, so the narrow window succeeds
most of the time — and when it does, we save a lot of work.

### When it fails

If the real score falls outside `[alpha, beta]`:

- **Fail-low** (score ≤ alpha): we don't know the true score, only that it's
  bad. Widen the lower bound and re-search.
- **Fail-high** (score ≥ beta): same, but on the upper side.

### In AnkaX

```csharp
int window = 50;
while (score <= alpha || score >= beta) {
    if (failCount >= 3) {
        alpha = -Infinity; beta = Infinity;  // Give up, use full window
    } else {
        if (score <= alpha) alpha -= window;
        if (score >= beta)  beta  += window;
        window += window / 2;  // Grow window exponentially
    }
    score = AlphaBeta(alpha, beta, currentDepth);
}
```

After 3 failures we switch to a full window, since repeated widening is usually
less efficient than just paying the full cost once.

---

## 4. Principal Variation Search (PVS)

### What it does

PVS assumes the first move examined is the best. For all subsequent moves, it
uses a **zero-window search** `[alpha, alpha+1]` — much cheaper than a full
search — just to verify that none of them beat the first.

### Why it works

If move ordering is good, 95%+ of alternatives really *aren't* better than the
first move. A zero-window search can prove this quickly. Only if a move
unexpectedly beats alpha do we re-search it with the full window.

### Cost / benefit

- **Cost**: when a non-first move really is best, we pay for two searches
  (one zero-window, one full). ~2x the cost on that branch.
- **Benefit**: when the first move is best (most of the time), the siblings
  cost ~half as much as a normal search.

Net effect: 20-40% node reduction in practice.

### In AnkaX

```csharp
if (movesSearched == 0)
    score = -AlphaBeta(-beta, -alpha, newDepth);     // Full window
else
{
    score = -AlphaBeta(-alpha - 1, -alpha, newDepth); // Zero window
    if (score > alpha && score < beta)
        score = -AlphaBeta(-beta, -alpha, newDepth);  // Re-search
}
```

---

## 5. Alpha-Beta Core

### Values returned

AnkaX uses **fail-soft** alpha-beta: the returned score can be outside the
`[alpha, beta]` window.

| Style | Returns when score ≥ β | Returns when score ≤ α |
|-------|------------------------|------------------------|
| Fail-hard | `β` (clamped) | `α` (clamped) |
| Fail-soft | actual score | actual score |

**Why fail-soft?** The TT can store tighter bounds. If we return the actual
score (e.g. 750 when β was 500), the TT knows the position is *at least* 750,
not just ≥ 500. This helps future probes.

### Node classification

- **PV node**: `(beta - alpha) > 1` — the search still has a non-trivial
  window. These are nodes along the principal variation and its siblings.
- **Non-PV node** (zero-window): `beta - alpha == 1`. We only need to know
  "better than alpha or not"; we never need the exact score.

Non-PV nodes can use far more aggressive pruning because exact precision
doesn't matter.

### Why the distinction matters

```csharp
bool pvNode = (beta - alpha) > 1;
if (ttScore != NoScore && !pvNode) return ttScore;  // Use TT cutoff
```

We reject TT cutoffs in PV nodes because we want the exact score for the
principal variation. In non-PV nodes, a bound from the TT is enough.

---

## 6. Pruning Techniques

Pruning techniques **skip** work entirely based on heuristic conditions.
This is where most of the search speedup comes from.

### 6.1 Mate Distance Pruning

```csharp
int matingScore = MateScore - currentPly;
if (matingScore < beta) { beta = matingScore; if (alpha >= beta) return beta; }
```

If we've already found a mate in 3, we don't need to search for mate in 5.
This tightens the window as soon as mates come into view.

### 6.2 Reverse Futility Pruning (RFP / "static NMP")

```csharp
if (depth <= 4 && staticEval - 110 * depth >= beta)
    return beta;
```

If the static eval is **far above beta** and depth is shallow, the position
is so good that it's almost certainly going to produce a beta cutoff.
Skip the search and return beta immediately.

- **Margin**: `110 * depth`. Deeper searches need bigger margins because
  tactics can swing the eval more.
- **Guard**: non-PV, non-check nodes only.

### 6.3 Futility Pruning (forward)

```csharp
canPrune = depth <= 4 && staticEval + 120 * depth <= alpha;
...
if (canPrune && isQuiet && move != ttMove) continue;  // Skip quiet move
```

If static eval is **far below alpha** and depth is shallow, a quiet move is
unlikely to raise the eval enough to beat alpha. Skip it and move on.

- Only skips **quiet moves** (captures and promotions might still swing things).
- Only at shallow depth (≤ 4).
- Never skips the first move (`movesSearched > 0`) — we need at least one
  legal move to avoid false-positive stalemate detection.

### 6.4 Null Move Pruning (NMP)

The idea: "If I give the opponent a free move and I'm *still* doing well,
my position must be really good."

```csharp
// Make null move (just switch side, don't move any piece)
side ^= 1;
hashKey ^= sideKey;

int R = 3 + depth/4 + min((staticEval - beta) / 200, 3);
int score = -AlphaBeta(-beta, -beta + 1, depth - 1 - R, allowNullMove: false);

side ^= 1;  // Undo

if (score >= beta) return beta;
```

- **R (reduction)**: how much shallower we search after the null move.
  Larger R = more aggressive = more speedup but more risk.
- **Adaptive R**: depth-based (`depth/4`) and eval-based (bonus up to +3
  when static eval is well above beta).
- **Guards**:
  - `!allowNullMove` prevents two null moves in a row.
  - `HasNonPawnMaterial` prevents **zugzwang** errors. In king+pawn endings,
    passing a move really is bad, so the NMP assumption breaks.

### Why non-PV only

All pruning techniques check `!pvNode`. In PV nodes we need exact scores;
in non-PV nodes we only need "better than alpha or not", so false positives
from pruning are cheaper to tolerate.

---

## 7. Reductions

Reductions **search less deeply** rather than skipping entirely. They're less
risky than pruning but still save a lot of work.

### 7.1 Late Move Reductions (LMR)

After the first 4 moves, reduce the search depth. The assumption: good move
ordering puts the best moves first, so later moves are probably worse and
don't need as much depth.

```csharp
if (movesSearched >= 4 && depth >= 3 && isQuiet && !inCheck)
{
    int R = lmrTable[(depth << 6) + movesSearched];
    if (pvNode && R > 1) R--;  // PV nodes reduce less
    score = -AlphaBeta(-alpha - 1, -alpha, newDepth - R);
    if (score > alpha)
        score = -AlphaBeta(-alpha - 1, -alpha, newDepth);  // Re-search
}
```

### The LMR table

Computed once at startup using a logarithmic formula:

```csharp
R = 1 + log(depth) * log(moves) / 2
```

Why log? Because the "benefit of reducing more" grows slowly. Doubling the
move index doesn't double the probability that the move is bad — it only
slightly increases it.

### 7.2 Internal Iterative Reduction (IIR)

```csharp
if (depth >= 4 && ttMove == 0 && !inCheck)
    depth--;
```

If we're at a reasonable depth and have **no TT hint** about which move is
best, the move ordering is probably going to be suboptimal. Rather than wasting
time on a poorly-ordered search, reduce the depth by 1 and let the TT fill in
from the shallower search.

**Critical guard**: `!inCheck`. Without this, IIR would cancel the check
extension (check adds 1 ply, IIR subtracts 1 → net 0). This cost ~8 Elo
during testing until the guard was added.

---

## 8. Extensions

Extensions **search deeper** than normal. The only extension in AnkaX is:

### Check Extension

```csharp
if (inCheck && currentPly < MaxPly - 10) depth++;
```

When in check, there are usually few legal responses and sharp tactics.
Giving the search one extra ply here almost always finds the right move.

The `currentPly < MaxPly - 10` guard prevents infinite extension loops in
pathological positions.

---

## 9. Move Ordering

Move ordering is the single most important factor in alpha-beta efficiency.
A beta cutoff at the first move prunes the remaining moves at that node;
a cutoff at the last move prunes nothing.

### Ordering priority (highest first)

| Priority | Move type | Score |
| ---------- | ----------- | ------- |
| 1 | TT move | 30000 |
| 2 | Queen promotion | 29000 |
| 3 | PV move (root only) | 20000 |
| 4 | Captures (MVV-LVA) | 10100–10605 |
| 5 | Killer 1 | 9000 |
| 6 | Killer 2 | 8000 |
| 7 | Castling | 7500 |
| 8 | Other promotions | 7200 |
| 9 | Quiet moves (history) | -7000 to +7000 |

### MVV-LVA (Most Valuable Victim / Least Valuable Attacker)

Captures are ordered so that:

- Capturing a queen with a pawn (QxP → pawn takes queen) comes first
- Capturing a pawn with a queen comes last

Rationale: winning big material is almost always correct, and risking little
material to win big is low-risk. The reverse (queen takes pawn defended by
anything) is usually bad.

### Sort algorithm

AnkaX uses insertion sort. This seems slow (O(n²)) but it's optimal here:

- Move lists are small (usually 20–60 moves)
- Already partially ordered (TT/captures cluster at the front)
- No allocations, excellent cache behavior
- Early-exit when an element is already in order

Quicksort would be slower due to its overhead on small arrays.

---

## 10. Quiescence Search

### The horizon effect

Imagine the search runs out of depth right after you capture a queen. The eval
looks great: you're up a queen! But what if the opponent could immediately
recapture with their rook? You'd actually be down a rook. Without quiescence,
the engine would never see this.

### Solution

When depth hits 0, don't stop — instead, keep searching but **only captures**
(plus promotions). Keep going until there are no captures left ("quiet"
position), then evaluate. This resolves tactics before scoring.

### Stand-pat

Before searching captures, consider the option "don't capture anything":

```csharp
int eval = Evaluation.Evaluate();
if (eval >= beta) return eval;  // Position already great
if (eval > alpha) alpha = eval; // Raise floor
```

The assumption: the side to move always has at least one "do nothing"
option (not technically true, but almost always). This lets us establish
a floor before trying risky captures.

### Delta pruning

Skip captures where even winning the captured piece couldn't raise alpha:

```csharp
if (eval + capturedValue + 200 < alpha) continue;
```

The `200` is a safety margin for positional swings.

### In check

Quiescence must handle check specially: we generate **all** moves
(evasions), not just captures. Otherwise we might miss escape moves.

---

## 11. Transposition Table Interaction

The TT is probed early and stored at exit:

### Probe

```csharp
int ttScore = TT.Probe(hashKey, depth, alpha, beta, ply, out ttMove);
if (ttScore != NoScore && !pvNode) return ttScore;
```

- Returns a score if the stored entry is deep enough and the bounds apply.
- Always returns `ttMove` (even without a cutoff) for move ordering.
- Rejected in PV nodes — we want the exact score, not a bound.

### Store

```csharp
TranspositionTable.Store(hashKey, depth, bestScore, bestMove, flag, ply);
```

Flags:

- **Exact**: `alpha < score < beta` — the real score
- **Beta**: `score >= beta` — lower bound (fail-high)
- **Alpha**: `score <= originalAlpha` — upper bound (fail-low)

---

## 12. Move History and Killers

Both are heuristics for ordering quiet moves (where MVV-LVA can't help).

### Killer moves

Per-ply array: for each ply, store the last two quiet moves that caused a
beta cutoff. The intuition: if move X caused a cutoff in one line at ply 5,
it's likely to cause a cutoff in a *sibling* line at ply 5 too.

```csharp
if (score >= beta && isQuiet) {
    killerMoves[killerIndex + 1] = killerMoves[killerIndex];  // Shift
    killerMoves[killerIndex] = move;
}
```

### History heuristic

For each `(piece, target_square)` combination, track how often that move
caused a beta cutoff or alpha improvement. Good moves accumulate high scores;
bad moves accumulate low scores.

```csharp
historyMoves[(piece << 6) | target] += bonus - history * |bonus| / MaxHistory;
```

The formula is called **history gravity**. It smoothly bounds the score
without hard clamping — scores naturally saturate as they approach ±MaxHistory.

**Why gravity?** Without it, scores grow unbounded and old data drowns out new
observations. With gravity, scores converge to the current signal.

---

## 13. Performance Techniques

C# is not C++. Every static field read, array access, and function call has
potential overhead. These techniques matter.

### 13.1 Flat arrays with bit-shift indexing

Instead of `int[,] lmrTable = new int[64, 64]`, use:

```csharp
int[] lmrTable = new int[64 * 64];
int index = (depth << 6) + moveIndex;
```

**Why?**

- Multi-dim arrays in .NET do **bounds checks on both dimensions**
- Each dim has a separate memory indirection (array-of-array style)
- Flat arrays are a single contiguous block — cache-friendly
- Bit shifts replace multiplication (1 cycle vs 3-4)

Savings: 15-25% on array-heavy code paths.

### 13.2 Caching static fields into locals

```csharp
int currentPly = ply;           // Local — one read
int sideToMove = side;          // Local
ulong hashKey = Zobrist.hashKey; // Local
```

**Why?** The JIT can't prove that static fields won't change between reads
(any method call could modify them). Reading them once into a local register
is strictly faster.

### 13.3 Span<T> for sort slices

```csharp
Span<int> moves = moveList.moves.AsSpan(0, count);
```

**Why?** `Span<T>` tells the JIT the bounds are fixed, enabling bounds-check
elimination inside the sort loop.

### 13.4 StringBuilder reuse for UCI output

```csharp
StringBuilder? infoBuilder = Program.Debug ? null : new StringBuilder(256);
// ... in loop:
infoBuilder.Clear();
infoBuilder.Append("info score ")...
```

**Why?** `$"..."` string interpolation allocates a new string for every
iteration. A reused StringBuilder allocates **once** per search.

Savings: thousands of allocations avoided per game.

### 13.5 Pre-expanded lookup tables

```csharp
// Instead of: mvvLva[attacker % 6, victim % 6]
mvvLvaTable[(attacker << 4) | victim]
```

**Why?** Modulo (`%`) is a slow integer-division instruction. Pre-expanding
the table trades 64 bytes of memory for eliminating `%` from the hot path.

### 13.6 Hoisted loop invariants

```csharp
int newDepth = depth - 1;
int depthSquared = depth * depth;
int lmrRowBase = depth << LmrRowShift;
int pvRowBase = currentPly << PvRowShift;
int nextPly = currentPly + 1;
int childRowBase = nextPly << PvRowShift;

for (int i = 0; i < moveCount; i++) { ... }
```

**Why?** These values don't change during the move loop. The JIT may or
may not hoist them automatically; computing them explicitly guarantees
each is computed once, not 40 times.

### 13.7 Branchless boolean operators

```csharp
bool isQuiet = (GetMoveCapture(move) == 0) & (promoted == 0);
```

Note the single `&` instead of `&&`.

**Why?** `&&` short-circuits, creating a branch. `&` always evaluates both
sides, no branch. For cheap operations, the single-branch version is faster
because the branch predictor can't mispredict what doesn't exist.

**Caveat**: only use `&` when both sides are **cheap and safe** to evaluate
(no null derefs, no side effects).

### 13.8 Pre-allocated move list pool

```csharp
private static readonly MoveList[] moveLists = new MoveList[MaxPly + 2];
```

**Why?** `new MoveList()` would allocate during every node visit (hundreds
of millions of times per game). Pre-allocating one per ply means:

- Zero GC pressure during search
- Memory reused and cache-warm
- No initialization cost per visit

This is the single largest allocation win in the engine.

---

## 14. Terminology Reference

| Term | Meaning |
| ------ | --------- |
| **Ply** | A single half-move. White moves + black moves = 1 full move = 2 ply |
| **PV** | Principal variation — the best sequence of moves found so far |
| **PV node** | A node where `β - α > 1` (full window) |
| **Cut node** | A node that produced a beta cutoff |
| **All node** | A node where no move beat alpha |
| **Beta cutoff** | When a move's score ≥ β; prune the rest |
| **Fail-high** | Score ≥ β (position is at least this good) |
| **Fail-low** | Score ≤ α (position is at most this bad) |
| **Zugzwang** | A position where any move is bad; passing would be better |
| **Horizon effect** | Blunder caused by stopping search just before consequences become visible |
| **Quiet move** | Non-capture, non-promotion |
| **Zero-window** | A search window of 1 centipawn `[α, α+1]` |
| **Soft fail / Hard fail** | Whether returned score is clamped to the window or not |
| **TT** | Transposition table — cache of previously-searched positions |
| **MVV-LVA** | Most Valuable Victim / Least Valuable Attacker (capture ordering) |
| **LMR** | Late Move Reduction |
| **NMP** | Null Move Pruning |
| **IIR** | Internal Iterative Reduction |
| **RFP** | Reverse Futility Pruning |
| **R** | The "reduction" value (how many plies to cut from a search) |
