using static Board;

// =============================================================================
//  AnkaX Move Data Structures
//
//  Compact 32-bit move encoding, fixed-capacity move container, and flat
//  board state snapshot for copy/takeback.
//
//  Move Bit Layout (32-bit int):
//      0–5   Source square     (6 bits, 0–63)
//      6–11  Target square     (6 bits, 0–63)
//      12–15 Piece             (4 bits, 0–11)
//      16–19 Promoted piece    (4 bits, 0–11; 0 = none)
//      20    Capture flag      (1 bit)
//      21    Double push flag  (1 bit)
//      22    En passant flag   (1 bit)
//      23    Castling flag     (1 bit)
//      24–31 Unused
// =============================================================================

// =============================================================================
//  SECTION 1 — Move Encoding (32-bit Integer)
// =============================================================================

public static class MoveEncoding
{
    /// <summary>
    /// Packs all move attributes into a single 32-bit integer.
    /// </summary>
    public static int EncodeMove(int source, int target, int piece, int promoted,
        int capture, int @double, int enpassant, int castling)
    {
        return source
             | (target << 6)
             | (piece << 12)
             | (promoted << 16)
             | (capture << 20)
             | (@double << 21)
             | (enpassant << 22)
             | (castling << 23);
    }

    public static int GetMoveSource(int move) => move & 0x3F;
    public static int GetMoveTarget(int move) => (move >> 6) & 0x3F;
    public static int GetMovePiece(int move) => (move >> 12) & 0xF;
    public static int GetMovePromoted(int move) => (move >> 16) & 0xF;
    public static int GetMoveCapture(int move) => move & 0x100000;
    public static int GetMoveDouble(int move) => move & 0x200000;
    public static int GetMoveEnpassant(int move) => move & 0x400000;
    public static int GetMoveCastling(int move) => move & 0x800000;

    /// <summary>
    /// Converts an encoded move to UCI long algebraic notation (e.g. "e2e4", "e7e8q").
    /// </summary>
    public static string GetMove(int move) =>
        $"{squareToCoordinates[GetMoveSource(move)]}" +
        $"{squareToCoordinates[GetMoveTarget(move)]}" +
        $"{promotedPieces[GetMovePromoted(move)]}";
}

// =============================================================================
//  SECTION 2 — Move List Container
// =============================================================================

/// <summary>
/// Fixed-capacity container holding up to 256 moves and their ordering scores.
/// Pre-allocated per search ply to eliminate heap allocations during search.
/// </summary>
public struct MoveList
{
    public int[] moves;
    public int[] scores;
    public int count;

    public MoveList()
    {
        moves = new int[256];
        scores = new int[256];
        count = 0;
    }
}

// =============================================================================
//  SECTION 3 — Board State Snapshot
// =============================================================================

/// <summary>
/// Flat value snapshot of the complete board state for fast copy/takeback.
/// Contains no array references so it copies contiguously on stack pass.
/// </summary>
public struct BoardState
{
    public ulong bb0, bb1, bb2, bb3, bb4, bb5;
    public ulong bb6, bb7, bb8, bb9, bb10, bb11;

    public ulong occ0, occ1, occ2;

    public int side;
    public int enPassant;
    public int castle;
    public int halfmoveClock;

    public ulong hashKey;
}