// =============================================================================
//  AnkaX Time Management
// =============================================================================
//
//  Allocates thinking time per move based on the UCI time control.
//  Uses a two-tier system inspired by Stockfish:
//
//    Optimum time — the target budget for this move. The search stops after
//                   completing the current iteration once this is exceeded.
//                   This produces clean "info" lines at stable depths.
//
//    Maximum time — the absolute ceiling. The search aborts mid-node if this
//                   is exceeded. Prevents flagging in sudden death.
//
//  Time allocation formula (Stockfish-style):
//
//    baseTime  = remaining / min(movestogo, 50)
//    optimum   = baseTime + increment * 3/4
//    maximum   = min(remaining * 0.8, optimum * 5)
//
//  The "min(movestogo, 50)" cap prevents the engine from thinking it has
//  more moves than it realistically will when movestogo is very large or
//  absent (sudden death). 50 is a standard heuristic.
//
//  Overhead compensation:
//    A fixed amount (default 50ms) is subtracted from the remaining time
//    before allocation to account for GUI communication latency and
//    clock synchronization drift. This prevents flagging due to lag.
//
//  Time control modes:
//    1. Infinite  — no time limit, search until "stop"
//    2. Movetime  — fixed time per move (e.g., "go movetime 1000")
//    3. Clock     — full game clock with increment (e.g., "go wtime 60000 btime 60000 winc 1000")
//    4. MovesToGo — clock with moves-to-go (e.g., "go wtime 60000 movestogo 20")
//
// =============================================================================

using System;

public static class TimeManagement
{
    // =========================================================================
    //  UCI Time Control Inputs (set by ParseGo)
    // =========================================================================

    /// <summary>Fixed time per move in ms (-1 if not set).</summary>
    public static int movetime = -1;

    /// <summary>Remaining clock time for the side to move in ms (-1 if not set).</summary>
    public static int time = -1;

    /// <summary>Clock increment per move in ms.</summary>
    public static int inc = 0;

    /// <summary>Moves until next time control (0 = sudden death).</summary>
    public static int movestogo = 0;

    // =========================================================================
    //  Search State
    // =========================================================================

    /// <summary>True when a time limit is active (movetime or clock).</summary>
    public static bool timeset = false;

    /// <summary>Set to true to abort the search immediately.</summary>
    public static bool stopped = false;

    /// <summary>Set to true to exit the UCI loop entirely.</summary>
    public static bool quit = false;

    // =========================================================================
    //  Timing Points (absolute milliseconds from Environment.TickCount64)
    // =========================================================================

    /// <summary>Timestamp when the current search started.</summary>
    public static long starttime = 0;

    /// <summary>
    /// Soft limit: stop after completing the current iteration.
    /// Checked once per iteration in SearchPosition.
    /// </summary>
    public static long softStopTime = 0;

    /// <summary>
    /// Hard limit: abort the search immediately, even mid-node.
    /// Checked every ~16K nodes in AlphaBeta.
    /// </summary>
    public static long stoptime = 0;

    // =========================================================================
    //  Configuration
    // =========================================================================

    /// <summary>
    /// Milliseconds reserved for GUI communication overhead per move.
    /// Prevents flagging due to latency between engine and GUI.
    /// </summary>
    private const int OverheadMs = 50;

    /// <summary>
    /// Maximum assumed moves-to-go for sudden death time controls.
    /// Prevents over-allocating when no movestogo is given.
    /// </summary>
    private const int DefaultMovesToGo = 50;

    /// <summary>
    /// Hard time cap as a fraction of remaining time (0.8 = 80%).
    /// Ensures the engine never spends almost all its time on a single move.
    /// </summary>
    private const double MaxTimeFraction = 0.8;

    /// <summary>
    /// Hard time cap as a multiplier of optimum time.
    /// Allows deeper searches when the position is complex, but bounded.
    /// </summary>
    private const int MaxTimeMultiplier = 5;

    /// <summary>
    /// Minimum time to spend on any move (ms).
    /// Prevents zero-time moves that would produce depth-1 results.
    /// </summary>
    private const int MinMoveTimeMs = 10;

    // =========================================================================
    //  Clock
    // =========================================================================

    /// <summary>
    /// Returns the current time in milliseconds. Uses TickCount64 to avoid
    /// the 24.9-day wraparound bug of the 32-bit TickCount.
    /// </summary>
    public static long GetTimeMs()
    {
        return Environment.TickCount64;
    }

    // =========================================================================
    //  Reset
    // =========================================================================

    /// <summary>
    /// Resets all time control state before parsing a new "go" command.
    /// </summary>
    public static void ResetForGo()
    {
        movetime = -1;
        time = -1;
        inc = 0;
        movestogo = 0;

        timeset = false;
        stopped = false;

        starttime = 0;
        softStopTime = 0;
        stoptime = 0;
    }

    // =========================================================================
    //  Time Control Starters
    // =========================================================================

    /// <summary>
    /// Starts an infinite search (no time limit). Search continues until
    /// "stop" is received or maximum depth is reached.
    /// </summary>
    public static void StartInfiniteSearch()
    {
        starttime = GetTimeMs();
        timeset = false;
        stopped = false;
        softStopTime = 0;
        stoptime = 0;
    }

    /// <summary>
    /// Starts a fixed-time search (e.g., "go movetime 1000").
    /// Both soft and hard limits are set to the same value minus overhead.
    /// </summary>
    public static void StartMoveTimeSearch(int moveTimeMs)
    {
        starttime = GetTimeMs();
        timeset = true;
        stopped = false;

        int spend = Math.Max(MinMoveTimeMs, moveTimeMs - OverheadMs);

        softStopTime = starttime + spend;
        stoptime = softStopTime;
    }

    /// <summary>
    /// Starts a clock-based search with increment and optional moves-to-go.
    /// Uses Stockfish-style allocation with optimum/maximum split.
    /// </summary>
    public static void StartClockSearch(int remainingMs, int incrementMs)
    {
        starttime = GetTimeMs();
        timeset = true;
        stopped = false;

        // --- Sanitize inputs ---
        remainingMs = Math.Max(1, remainingMs - OverheadMs);
        incrementMs = Math.Max(0, incrementMs);

        // --- Determine moves to go ---
        // If the GUI provides movestogo (e.g., "40 moves in 2 hours"), use it.
        // Otherwise assume sudden death with DefaultMovesToGo.
        int mtg = movestogo > 0 ? movestogo : DefaultMovesToGo;
        mtg = Math.Min(mtg, DefaultMovesToGo);  // Cap to prevent over-allocation

        // --- Optimum time (target budget) ---
        // Formula: remaining / mtg + increment * 3/4
        // The increment bonus lets the engine "bank" increment time for
        // critical moments later in the game.
        int optimum = remainingMs / mtg + incrementMs * 3 / 4;

        // --- Maximum time (hard ceiling) ---
        // Two constraints, take the tighter one:
        //   1) Never spend more than MaxTimeFraction of remaining time
        //   2) Never exceed optimum * MaxTimeMultiplier
        int maxTime = (int)(remainingMs * MaxTimeFraction);
        maxTime = Math.Min(maxTime, optimum * MaxTimeMultiplier);

        // --- Apply bounds ---
        optimum = Math.Max(MinMoveTimeMs, Math.Min(optimum, remainingMs));
        maxTime = Math.Max(MinMoveTimeMs, Math.Min(maxTime, remainingMs));

        // Ensure optimum doesn't exceed maxTime
        optimum = Math.Min(optimum, maxTime);

        // --- Emergency low-time mode ---
        // When very little time remains, tighten the gap between soft and hard
        // to avoid flagging on a deep iteration that overshoots.
        if (remainingMs < 1000)
        {
            optimum = Math.Max(MinMoveTimeMs, remainingMs / 3);
            maxTime = Math.Max(optimum, remainingMs / 2);
        }

        softStopTime = starttime + optimum;
        stoptime = starttime + maxTime;
    }

    // =========================================================================
    //  Time Checks (called during search)
    // =========================================================================

    /// <summary>
    /// Hard time check. Called every ~16K nodes from AlphaBeta.
    /// Sets stopped = true if the hard limit is exceeded.
    /// </summary>
    public static void Communicate()
    {
        if (timeset && GetTimeMs() >= stoptime)
            stopped = true;
    }

    /// <summary>
    /// Soft time check. Called once per iteration in SearchPosition.
    /// Returns true if the optimum time has been exceeded, signaling that
    /// the search should stop after this iteration completes.
    /// </summary>
    public static bool ShouldStopAfterIteration()
    {
        return timeset && GetTimeMs() >= softStopTime;
    }
}