// =============================================================================
//  AnkaX Transposition Table
// =============================================================================
//
//  A hash table that caches search results to avoid re-searching positions
//  reached via different move orders (transpositions).
//
//  Configuration:
//    Size:       16 MB = 699,050 max entries = 524,288 entries (12.5 MB)
//    Strategy:   Always-replace (newest entry overwrites oldest)
//    Indexing:   hashKey & (TableSize - 1) — fast bitwise modulo
//
//  Mate score handling:
//    Mate scores are stored relative to the root (e.g., "mate in 3 from root").
//    When probed at a different ply, the score must be adjusted so it reflects
//    the distance from the CURRENT node, not the original root.
//
//    Store:  score += ply   (root-relative → absolute)
//    Probe:  score -= ply   (absolute → current-relative)
//
//    Example:
//      Ply 0 search finds mate in 3 → score = 49000 - 3 = 48997
//      Store at ply 0: 48997 + 0 = 48997 (absolute)
//      Probe at ply 2: 48997 - 2 = 48995 → "mate in 1 from here" ✓
//
//  Flag semantics (fail-soft):
//    Exact — score is the true minimax value (alpha < score < beta)
//    Alpha — score is an upper bound (failed low, true score ≤ stored)
//    Beta  — score is a lower bound (failed high, true score ≥ stored)
//
// =============================================================================

using System.Runtime.CompilerServices;

/// <summary>
/// Describes the type of score stored in a transposition table entry.
/// </summary>
public enum TTFlag
{
    Exact,  // True minimax score (search fell within the alpha-beta window)
    Alpha,  // Upper bound (search failed low — real score ≤ this value)
    Beta    // Lower bound (search failed high — real score ≥ this value)
}

/// <summary>
/// A single transposition table entry (24 bytes).
/// </summary>
public struct TTEntry
{
    public ulong hashKey;  // Full Zobrist hash for collision detection
    public int depth;      // Search depth that produced this entry
    public int score;      // Evaluation score (mate-adjusted, absolute)
    public TTFlag flag;    // Score type: exact, upper bound, or lower bound
    public int bestMove;   // Best move found (used for ordering even on depth miss)
}

public static class TranspositionTable
{
    // =========================================================================
    //  Configuration
    // =========================================================================

    // Default size matches the UCI "default 16" advertisement.
    public const int DefaultHashMb = 16;
    public const int MinHashMb = 1;
    public const int MaxHashMb = 1024;

    // Table is sized to a power of 2 so indexing uses a cheap bitwise AND
    // instead of a modulo. When the user requests N MB, we round DOWN to the
    // largest power-of-2 entry count that fits in N MB.
    private static TTEntry[] table = [];
    private static int indexMask;   // = table.Length - 1 (valid because length is power of 2)

    /// <summary>
    /// Sentinel returned by Probe when no usable entry is found.
    /// </summary>
    public const int NoScore = int.MinValue;

    // One-time init so the engine has a working table even if no setoption is sent.
    static TranspositionTable()
    {
        Resize(DefaultHashMb);
    }

    // =========================================================================
    //  Sizing
    // =========================================================================

    /// <summary>
    /// Resizes the table to use approximately <paramref name="megabytes"/> of memory.
    /// Rounds DOWN to the nearest power of 2 entry count so index masking stays
    /// a single bitwise AND. Always clears the table afterwards — old entries
    /// would hash to wrong indices under the new mask.
    /// </summary>
    public static void Resize(int megabytes)
    {
        if (megabytes < MinHashMb) megabytes = MinHashMb;
        if (megabytes > MaxHashMb) megabytes = MaxHashMb;

        int entrySize = Unsafe.SizeOf<TTEntry>();
        long requestedBytes = (long)megabytes * 1024 * 1024;
        long maxEntries = requestedBytes / entrySize;

        // Round DOWN to nearest power of 2. Ensures indexMask works correctly.
        int entries = 1;
        while ((long)entries * 2 <= maxEntries)
            entries *= 2;

        table = new TTEntry[entries];
        indexMask = entries - 1;
    }

    // =========================================================================
    //  Public API (indexing updated to use indexMask)
    // =========================================================================

    public static void Clear() => Array.Clear(table, 0, table.Length);

    /// <summary>
    /// Stores a search result in the table using always-replace strategy.
    /// Mate scores are converted from ply-relative to absolute before storing.
    /// </summary>
    public static void Store(ulong hashkey, int depth, int score, int move, TTFlag flag, int ply)
    {
        int index = (int)(hashkey & (ulong)indexMask);

        // Convert mate scores from ply-relative to absolute
        if (score > 48000) score += ply;
        else if (score < -48000) score -= ply;

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
    /// Looks up a position in the table. Returns a cutoff score if the entry
    /// is deep enough and the bound allows it, or NoScore otherwise.
    /// Always sets ttMove to the stored best move (useful for move ordering
    /// even when the score cannot be used due to insufficient depth).
    /// </summary>
    public static int Probe(ulong hashkey, int depth, int alpha, int beta, int ply, out int ttMove)
    {
        ttMove = 0;

        int index = (int)(hashkey & (ulong)indexMask);
        TTEntry entry = table[index];

        // Hash collision check: different positions can map to the same index
        if (entry.hashKey != hashkey)
            return NoScore;

        // Always return the stored move for ordering, even on depth miss
        ttMove = entry.bestMove;

        // Depth check: only trust scores from searches at least as deep as needed
        if (entry.depth < depth)
            return NoScore;

        // Convert mate scores from absolute back to ply-relative
        int score = entry.score;
        if (score > 48000) score -= ply;
        else if (score < -48000) score += ply;

        // Attempt cutoff based on the stored bound type
        switch (entry.flag)
        {
            case TTFlag.Exact:
                return score;

            case TTFlag.Alpha:
                if (score <= alpha) return alpha;
                break;

            case TTFlag.Beta:
                if (score >= beta) return beta;
                break;
        }

        return NoScore;
    }
}