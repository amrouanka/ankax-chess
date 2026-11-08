// Flag types: What kind of score did we store?
public enum TTFlag
{
    Exact,       // Score is exact (found within alpha-beta window)
    Alpha,       // Score is UPPER bound (failed low, real score ≤ this)
    Beta         // Score is LOWER bound (failed high, real score ≥ this)
}

// What we store for each position
public struct TTEntry
{
    public ulong hashKey;   // Full hash to verify position
    public int depth;       // Search depth when this was stored
    public int score;       // The evaluation score
    public TTFlag flag;     // Type of score (exact, upper, or lower bound)
    public int bestMove;    // Best move found from this position
}
/*
    WHY do we need flags?
    
    In alpha-beta search:
    - If score >= beta: we CUTOFF and return beta
      But the REAL score might be even higher! So it's a LOWER bound.
      
    - If score <= alpha: the move was bad, we didn't improve
      The REAL score might be even lower! So it's an UPPER bound.
      
    - If alpha < score < beta: we found the exact score!
*/
public static class TranspositionTable
{
    // Table size must be a power of 2 for fast modulo with bitwise AND
    // 1 << 20 = 2,666,666 entries ≈ 64MB (each entry ~24 bytes)
    private const int TableSize = 1 << 20;

    // The actual table - just a big array of entries
    private static TTEntry[] table = new TTEntry[TableSize];

    // Special value meaning "no score found"
    public const int NoScore = int.MinValue;

    // Clear the entire table (call at the start of new game only)
    public static void Clear()
    {
        Array.Clear(table, 0, table.Length);
    }

    public static void Store(ulong hashkey, int depth, int score, int move, TTFlag flag, int ply)
    {
        // Calculate which slot to use
        // Using & (TableSize - 1) is faster than % TableSize
        int index = (int)(hashkey & (TableSize - 1));

        // Adjust mate scores before storing (explained down)
        if (score > 48000) score += ply;
        else if (score < -48000) score -= ply;
        /*
        Why Mate Scores Need Special Treatment? Here's an example:
        Depth 5 search: Found checkmate in 3 moves
        Stored score: +49000 - 3 = +48997  ← relative to ROOT

        Later, depth 3 search at ply=2:
        Retrieved score: +48997
        But now we're at ply 2, so "mate in 3" 
        from root = "mate in 1" from here!
        Correct score should be: +49000 - 1 = +48999

        FIX:
        When STORING: add ply    (convert from relative to absolute)
        When READING: subtract ply (convert from absolute to relative)
        */
        // Simple replacment strategy: always replace
        table[index] = new TTEntry
        {
            hashKey = hashkey,
            depth = depth,
            score = score,
            bestMove = move,
            flag = flag
        };
    }

    // Probe(): Look if we've seen this position before
    public static int Probe(ulong hashkey, int depth, int alpha, int beta, int ply, out int ttMove)
    {
        /*
        The out keyword means "this parameter is a return value going OUT of the function". It lets a function return multiple values.

        // Normal function: returns ONE thing
        int Probe(...) → returns score

        // With 'out': returns TWO things
        int Probe(..., out int ttMove) → returns score AND ttMove
        */
        ttMove = 0; // Default: no move found

        int index = (int)(hashkey & (TableSize - 1));
        TTEntry entry = table[index];

        // VERIFICATION: Does this entry belongs to our position?
        // Hash collisions can occur - 2 different positions can map to
        // the same index and its totaly normal!
        if (entry.hashKey != hashkey)
            return NoScore;

        // Retrieve the best move regardless of depth
        // (Useful for move ordering even if we can't use the score)\
        ttMove = entry.bestMove;

        // Only use the score if we searched at least as deep as needed
        if (entry.depth < depth)
            return NoScore; // Not deep enough, can't trust it

        // Adjust mate scores after retrieving
        int score = entry.score;
        if (score > 48000) score -= ply;
        else if (score < -48000) score += ply;

        // Use the score based on what TYPE of result it is
        switch (entry.flag)
        {
            case TTFlag.Exact:
                return score; // Exact score - use it directly

            case TTFlag.Alpha:
                // This was an upper bound - if it's still less than
                // alpha we can fail low immediately
                if (score <= alpha) return alpha;
                break;

            case TTFlag.Beta:
                // This was a lower bound - if it's still higher than
                // beta we can fail high immediatly (beta cutoff!)
                if (score >= beta) return beta;
                break;
        }

        return NoScore; // Score doesn't allow cutoff, search normaly
    }
}