using System.Runtime.CompilerServices;

// =============================================================================
//  AnkaX Transposition Table
//
//  Caches search results to prune redundant subtrees reached via transpositions.
//
//  Indexing:
//      Size is always rounded down to a power of 2 so index lookup uses a fast
//      bitwise mask (`hashKey & indexMask`) instead of expensive modulo.
//
//  Replacement Strategy:
//      Always-replace scheme (newest entry overwrites the slot).
//
//  Mate Score Normalization:
//      Mate scores are stored relative to root (absolute distance) so they remain
//      valid when probed at different ply depths in another search branch:
//          Store:  score >  48000 -> score += ply
//          Probe:  score >  48000 -> score -= ply
//
//  Bound Flags (Fail-Soft):
//      Exact — True minimax score (alpha < score < beta)
//      Alpha — Upper bound (fail-low: true score <= score)
//      Beta  — Lower bound (fail-high: true score >= score)
// =============================================================================

/// <summary>
/// Indicates the score bound type of a transposition table entry.
/// </summary>
public enum TTFlag
{
    Exact,  // Score is exact (within alpha-beta window)
    Alpha,  // Score is an upper bound (failed low)
    Beta    // Score is a lower bound (failed high)
}

/// <summary>
/// A single transposition table entry (24 bytes).
/// </summary>
public struct TTEntry
{
    public ulong hashKey;  // Zobrist hash key for collision verification
    public int depth;      // Depth of the search that produced this entry
    public int score;      // Evaluation score (mate-adjusted)
    public TTFlag flag;    // Bound type (Exact, Alpha, or Beta)
    public int bestMove;   // Best move found in this position
}

public static class TranspositionTable
{
    // =========================================================================
    //  SECTION 1 — Configuration and State
    // =========================================================================

    public const int DefaultHashMb = 16;
    public const int MinHashMb = 1;
    public const int MaxHashMb = 1024;

    public const int NoScore = int.MinValue;

    private static TTEntry[] table = [];
    private static int indexMask;

    // =========================================================================
    //  SECTION 2 — Sizing and Initialization
    // =========================================================================

    static TranspositionTable()
    {
        Resize(DefaultHashMb);
    }

    /// <summary>
    /// Resizes the table to use up to <paramref name="megabytes"/> of memory,
    /// rounding down to the nearest power-of-two entry count.
    /// </summary>
    public static void Resize(int megabytes)
    {
        if (megabytes < MinHashMb) megabytes = MinHashMb;
        if (megabytes > MaxHashMb) megabytes = MaxHashMb;

        int entrySize = Unsafe.SizeOf<TTEntry>();
        long requestedBytes = (long)megabytes * 1024 * 1024;
        long maxEntries = requestedBytes / entrySize;

        int entries = 1;
        while ((long)entries * 2 <= maxEntries)
            entries *= 2;

        table = new TTEntry[entries];
        indexMask = entries - 1;
    }

    /// <summary>
    /// Clears all entries in the transposition table.
    /// </summary>
    public static void Clear() => Array.Clear(table, 0, table.Length);

    // =========================================================================
    //  SECTION 3 — Table Operations (Store & Probe)
    // =========================================================================

    /// <summary>
    /// Stores a search result in the table using the always-replace scheme.
    /// </summary>
    public static void Store(ulong hashkey, int depth, int score, int move, TTFlag flag, int ply)
    {
        // Step 1. Compute entry index using bitwise mask
        int index = (int)(hashkey & (ulong)indexMask);

        // Step 2. Normalize mate score to root-relative distance
        if (score > 48000) score += ply;
        else if (score < -48000) score -= ply;

        // Step 3. Write entry
        table[index] = new TTEntry
        {
            hashKey = hashkey,
            depth = depth,
            score = score,
            bestMove = move,
            flag = flag
        };
    }

    /// <summary>
    /// Probes the table for a stored position. Sets <paramref name="ttMove"/> for
    /// move ordering regardless of whether a score cutoff occurs.
    /// </summary>
    public static int Probe(ulong hashkey, int depth, int alpha, int beta, int ply, out int ttMove)
    {
        ttMove = 0;

        // Step 1. Compute index and verify hash key
        int index = (int)(hashkey & (ulong)indexMask);
        TTEntry entry = table[index];

        if (entry.hashKey != hashkey)
            return NoScore;

        // Step 2. Always supply the stored best move for move ordering
        ttMove = entry.bestMove;

        // Step 3. Ensure stored depth is sufficient for a cutoff
        if (entry.depth < depth)
            return NoScore;

        // Step 4. Adjust mate score back to ply-relative distance
        int score = entry.score;
        if (score > 48000) score -= ply;
        else if (score < -48000) score += ply;

        // Step 5. Evaluate cutoff conditions (fail-soft score return)
        switch (entry.flag)
        {
            case TTFlag.Exact:
                return score;

            case TTFlag.Alpha:
                if (score <= alpha) return score;
                break;

            case TTFlag.Beta:
                if (score >= beta) return score;
                break;
        }

        return NoScore;
    }
}