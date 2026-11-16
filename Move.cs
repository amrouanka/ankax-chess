using static Board;

// =============================================================================
//  Move encoding — packs all move info into a single 32-bit int.
//
//  Bit layout:
//    0-5   source square    (6 bits, 0-63)
//    6-11  target square    (6 bits, 0-63)
//    12-15 piece            (4 bits, 0-11)
//    16-19 promoted piece   (4 bits, 0-11, 0 = no promotion)
//    20    capture flag     (1 bit)
//    21    double push flag (1 bit)
//    22    en passant flag  (1 bit)
//    23    castling flag    (1 bit)
// =============================================================================

public static class MoveEncoding
{
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

    public static string GetMove(int move) =>
        $"{squareToCoordinates[GetMoveSource(move)]}" +
        $"{squareToCoordinates[GetMoveTarget(move)]}" +
        $"{promotedPieces[GetMovePromoted(move)]}";

    public static void PrintMove(int move) => Console.WriteLine(GetMove(move));
}

// =============================================================================
//  Pre-allocated move list — avoids heap allocation during search.
//  256 slots is the theoretical max (real max is 218 in chess).
// =============================================================================

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
//  Board state snapshot — used for copy/takeback in search.
//  Flat fields (not arrays) so the struct is a single contiguous block
//  that copies efficiently via memcpy.
// =============================================================================

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