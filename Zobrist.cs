using static System.Numerics.BitOperations;

public static class Zobrist
{
    // Random piece keys [piece][square]
    public static readonly ulong[,] pieceKeys = new ulong[12, 64];

    // Random enpassant keys [square]
    public static readonly ulong[] enpassantKeys = new ulong[64];

    // Random castling keys
    public static readonly ulong[] castleKeys = new ulong[16];

    // Random side key
    public static ulong sideKey;

    // Random number generator state
    private static uint randomState = 1804289383;

    // Hash key for board position (like unique position ID)
    public static ulong hashKey;

    static Zobrist()
    {
        /* In C#, a static constructor is guaranteed to be executed by the .NET runtime before any static members of the class are used  
        for the first time. So, the moment the search algorithm calls Evaluation.Evaluate(), the runtime ensures that
        InitializeTables() has already been run exactly once. */

        InitRandomKeys();    // Initialize Zobrist hash keys
    }

    private static uint GetRandomU32Number()
    {
        // Get current state
        uint number = randomState;

        // XOR shift algorithm
        number ^= number << 13;
        number ^= number >> 17;
        number ^= number << 5;

        // Update random number state
        randomState = number;

        // Return random number
        return number;
    }

    private static ulong GetRandomU64Number()
    {
        ulong n1, n2, n3, n4;

        // Init random numbers slicing 16 bits from MS1B side
        n1 = (ulong)GetRandomU32Number() & 0xFFFF;    // And operation to get 16 bits (0xFFFF) - first 16 bits (bits 0-15)
        n2 = (ulong)GetRandomU32Number() & 0xFFFF;    // Second 16 bits (bits 16-31)
        n3 = (ulong)GetRandomU32Number() & 0xFFFF;    // Third 16 bits (bits 32-47)
        n4 = (ulong)GetRandomU32Number() & 0xFFFF;    // Fourth 16 bits (bits 48-63)

        // Return random number by combining the four 16-bit parts into a 64-bit number
        return n1 | (n2 << 16) | (n3 << 32) | (n4 << 48);
    }

    // Initialize random hash keys
    public static void InitRandomKeys()
    {
        // Update pseudo random number state (its already defined but just for the sake of precaution)
        randomState = 1804289383;

        // Loop over piece codes (P=0 to k=11)
        for (int piece = Board.P; piece <= Board.k; piece++)
        {
            // Loop over board squares
            for (int square = 0; square < 64; square++)
            {
                // Init random piece keys
                pieceKeys[piece, square] = GetRandomU64Number();
            }
        }

        // Loop over board squares
        for (int square = 0; square < 64; square++)
        {
            // Init random enpassant keys
            enpassantKeys[square] = GetRandomU64Number();
        }

        // Loop over castling keys
        for (int index = 0; index < 16; index++)
        {
            // Init castling keys
            castleKeys[index] = GetRandomU64Number();
        }

        // Init random side key
        sideKey = GetRandomU64Number();
    }

    // Generate hash key for current board position
    public static ulong GenerateHashKey()
    {
        ulong hashKey = 0UL;

        // Hash pieces
        for (int piece = Board.P; piece <= Board.k; piece++)
        {
            ulong bitboard = Board.bitboards[piece];

            while (bitboard != 0)
            {
                int square = TrailingZeroCount(bitboard);
                hashKey ^= pieceKeys[piece, square];
                BitboardOperations.PopBit(ref bitboard, square);
            }
        }

        // Hash side to move
        if (Board.side == MoveGenerator.White)
            hashKey ^= sideKey;

        // Hash en passant square
        if (Board.enPassant != (int)Square.noSquare)
            hashKey ^= enpassantKeys[Board.enPassant];

        // Hash castling rights
        hashKey ^= castleKeys[Board.castle];

        return hashKey;
    }
}
