using System.Runtime.CompilerServices;
using static System.Numerics.BitOperations;
using static BitboardOperations;
using static CastlingRights;
using static MoveEncoding;
using static PieceAttacks;
using static Square;
using static Board;

// =============================================================================
// AnkaX Chess Engine - Move Generation & Board State Mutation
//
// Fast pseudo-legal move generation using bitboard attack patterns and state
// transition handling (MakeMove).
//
// Pseudo-Legal Generation Strategy:
//   Moves are generated ignoring pins and check exposure. Legality filtering
//   occurs lazily during search via MakeMove() returning 0 if the moving side's
//   king is left in check. This avoids expensive pin-ray pre-computations.
// =============================================================================

public static class MoveGenerator
{
    // =========================================================================
    //  SECTION 1 — Constants and Lookup Tables
    // =========================================================================

    public const int White = 0;
    public const int Black = 1;
    public const int Both = 2;

    // Castling-rights update mask.
    // When a piece moves FROM or TO a square, the current castling rights are
    // bitwise ANDed with this lookup table to revoke invalidated rights.
    // Corner squares and king squares hold masks that revoke specific bits:
    //
    //   a8 -> 13 (0b1101, revoke bQ)    h8 -> 14 (0b1110, revoke bK)
    //   a1 ->  7 (0b0111, revoke wQ)    h1 -> 11 (0b1011, revoke wK)
    //   e1 ->  3 (0b0011, revoke wK+wQ) e8 -> 12 (0b1100, revoke bK+bQ)
    private static readonly int[] castlingRights =
    [
        7, 15, 15, 15, 3, 15, 15, 11,
       15, 15, 15, 15, 15, 15, 15, 15,
       15, 15, 15, 15, 15, 15, 15, 15,
       15, 15, 15, 15, 15, 15, 15, 15,
       15, 15, 15, 15, 15, 15, 15, 15,
       15, 15, 15, 15, 15, 15, 15, 15,
       15, 15, 15, 15, 15, 15, 15, 15,
       13, 15, 15, 15, 12, 15, 15, 14
    ];

    // =========================================================================
    //  SECTION 2 — Move Generation
    // =========================================================================

    // Resolves which enemy piece occupies a known capture target square.
    // Checks enemy pawn first (most common capture), falling through to king.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetCapturedPiece(int target)
    {
        ulong mask = 1UL << target;
        ulong[] bb = bitboards;

        if (side == White)
        {
            if ((bb[p] & mask) != 0) return p;
            if ((bb[n] & mask) != 0) return n;
            if ((bb[b] & mask) != 0) return b;
            if ((bb[r] & mask) != 0) return r;
            if ((bb[q] & mask) != 0) return q;
            return k;
        }
        else
        {
            if ((bb[P] & mask) != 0) return P;
            if ((bb[N] & mask) != 0) return N;
            if ((bb[B] & mask) != 0) return B;
            if ((bb[R] & mask) != 0) return R;
            if ((bb[Q] & mask) != 0) return Q;
            return K;
        }
    }

    // Generates all pseudo-legal moves for the current board state.
    // If `capturesOnly` is true, non-captures and quiet pawn pushes are omitted
    // (used by Quiescence Search).
    public static void GenerateMoves(ref MoveList moveList, bool capturesOnly = false)
    {
        // ---------------------------------------------------------------------
        //  Step 1: Setup occupancies & target masks
        // ---------------------------------------------------------------------
        moveList.count = 0;

        bool isWhite = side == White;
        ulong friendlyOcc = isWhite ? occupancies[White] : occupancies[Black];
        ulong enemyOcc = isWhite ? occupancies[Black] : occupancies[White];
        ulong bothOcc = occupancies[Both];

        // Quiescence target mask limits destinations to enemy-occupied squares.
        ulong targetMask = capturesOnly ? enemyOcc : ~friendlyOcc;

        int pawnPiece = isWhite ? P : p;
        int knightPiece = isWhite ? N : n;
        int bishopPiece = isWhite ? B : b;
        int rookPiece = isWhite ? R : r;
        int queenPiece = isWhite ? Q : q;
        int kingPiece = isWhite ? K : k;
        int enemyPawn = isWhite ? p : P;

        // ---------------------------------------------------------------------
        //  Step 2: Pawn move generation
        // ---------------------------------------------------------------------
        int pawnDir = isWhite ? -8 : 8;
        int promoFirst = isWhite ? (int)a7 : (int)a2;
        int doubleFirst = isWhite ? (int)a2 : (int)a7;

        int qPromo = isWhite ? Q : q;
        int rPromo = isWhite ? R : r;
        int bPromo = isWhite ? B : b;
        int nPromo = isWhite ? N : n;

        ulong epTarget = enPassant != (int)noSquare ? 1UL << enPassant : 0UL;

        for (ulong x = bitboards[pawnPiece]; x != 0; x &= x - 1)
        {
            int src = TrailingZeroCount(x);

            // --- Quiet Pushes (omitted in captures-only mode) ---
            if (!capturesOnly)
            {
                int target = src + pawnDir;

                // Cast to uint handles bounds check (negative wrap-around > 64)
                if ((uint)target < 64 && (bothOcc & (1UL << target)) == 0)
                {
                    // Promotion check: source is on the 7th (White) or 2nd (Black) rank
                    if ((uint)(src - promoFirst) <= 7)
                    {
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, qPromo, 0, 0, 0, 0, 0));
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, rPromo, 0, 0, 0, 0, 0));
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, bPromo, 0, 0, 0, 0, 0));
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, nPromo, 0, 0, 0, 0, 0));
                    }
                    else
                    {
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, 0, 0, 0, 0, 0, 0));

                        // Double push: source is on 2nd (White) / 7th (Black) rank & path is clear
                        if ((uint)(src - doubleFirst) <= 7 &&
                            (bothOcc & (1UL << (target + pawnDir))) == 0)
                        {
                            AddMove(ref moveList, EncodeMove(src, target + pawnDir, pawnPiece, 0, 0, 0, 1, 0, 0));
                        }
                    }
                }
            }

            // --- Captures & Promotion Captures ---
            ulong attacks = pawnAttacks[side, src] & enemyOcc;
            while (attacks != 0)
            {
                int target = TrailingZeroCount(attacks);
                attacks &= attacks - 1;
                int captured = GetCapturedPiece(target);

                if ((uint)(src - promoFirst) <= 7)
                {
                    AddMove(ref moveList, EncodeMove(src, target, pawnPiece, qPromo, captured, 1, 0, 0, 0));
                    AddMove(ref moveList, EncodeMove(src, target, pawnPiece, rPromo, captured, 1, 0, 0, 0));
                    AddMove(ref moveList, EncodeMove(src, target, pawnPiece, bPromo, captured, 1, 0, 0, 0));
                    AddMove(ref moveList, EncodeMove(src, target, pawnPiece, nPromo, captured, 1, 0, 0, 0));
                }
                else
                {
                    AddMove(ref moveList, EncodeMove(src, target, pawnPiece, 0, captured, 1, 0, 0, 0));
                }
            }

            // --- En Passant Captures ---
            if (epTarget != 0 && (pawnAttacks[side, src] & epTarget) != 0)
                AddMove(ref moveList, EncodeMove(src, enPassant, pawnPiece, 0, enemyPawn, 1, 0, 1, 0));
        }

        // ---------------------------------------------------------------------
        //  Step 3: Piece move generation (Knights, Bishops, Rooks, Queens, Kings)
        // ---------------------------------------------------------------------
        for (ulong x = bitboards[knightPiece]; x != 0; x &= x - 1)
        {
            int src = TrailingZeroCount(x);
            ulong attacks = knightAttacks[src] & targetMask;
            while (attacks != 0)
            {
                int target = TrailingZeroCount(attacks);
                attacks &= attacks - 1;
                if (GetBit(enemyOcc, target))
                    AddMove(ref moveList, EncodeMove(src, target, knightPiece, 0, GetCapturedPiece(target), 1, 0, 0, 0));
                else
                    AddMove(ref moveList, EncodeMove(src, target, knightPiece, 0, 0, 0, 0, 0, 0));
            }
        }

        for (ulong x = bitboards[bishopPiece]; x != 0; x &= x - 1)
        {
            int src = TrailingZeroCount(x);
            ulong attacks = GetBishopAttacks(src, bothOcc) & targetMask;
            while (attacks != 0)
            {
                int target = TrailingZeroCount(attacks);
                attacks &= attacks - 1;
                if (GetBit(enemyOcc, target))
                    AddMove(ref moveList, EncodeMove(src, target, bishopPiece, 0, GetCapturedPiece(target), 1, 0, 0, 0));
                else
                    AddMove(ref moveList, EncodeMove(src, target, bishopPiece, 0, 0, 0, 0, 0, 0));
            }
        }

        for (ulong x = bitboards[rookPiece]; x != 0; x &= x - 1)
        {
            int src = TrailingZeroCount(x);
            ulong attacks = GetRookAttacks(src, bothOcc) & targetMask;
            while (attacks != 0)
            {
                int target = TrailingZeroCount(attacks);
                attacks &= attacks - 1;
                if (GetBit(enemyOcc, target))
                    AddMove(ref moveList, EncodeMove(src, target, rookPiece, 0, GetCapturedPiece(target), 1, 0, 0, 0));
                else
                    AddMove(ref moveList, EncodeMove(src, target, rookPiece, 0, 0, 0, 0, 0, 0));
            }
        }

        for (ulong x = bitboards[queenPiece]; x != 0; x &= x - 1)
        {
            int src = TrailingZeroCount(x);
            ulong attacks = GetQueenAttacks(src, bothOcc) & targetMask;
            while (attacks != 0)
            {
                int target = TrailingZeroCount(attacks);
                attacks &= attacks - 1;
                if (GetBit(enemyOcc, target))
                    AddMove(ref moveList, EncodeMove(src, target, queenPiece, 0, GetCapturedPiece(target), 1, 0, 0, 0));
                else
                    AddMove(ref moveList, EncodeMove(src, target, queenPiece, 0, 0, 0, 0, 0, 0));
            }
        }

        for (ulong x = bitboards[kingPiece]; x != 0; x &= x - 1)
        {
            int src = TrailingZeroCount(x);
            ulong attacks = kingAttacks[src] & targetMask;
            while (attacks != 0)
            {
                int target = TrailingZeroCount(attacks);
                attacks &= attacks - 1;
                if (GetBit(enemyOcc, target))
                    AddMove(ref moveList, EncodeMove(src, target, kingPiece, 0, GetCapturedPiece(target), 1, 0, 0, 0));
                else
                    AddMove(ref moveList, EncodeMove(src, target, kingPiece, 0, 0, 0, 0, 0, 0));
            }
        }

        // ---------------------------------------------------------------------
        //  Step 4: Castling move generation (full mode only)
        // ---------------------------------------------------------------------
        if (!capturesOnly)
        {
            int enemySide = isWhite ? Black : White;

            if (isWhite)
            {
                // White Kingside Castling (e1 -> g1)
                if ((castle & (int)wk) != 0 &&
                    (bothOcc & ((1UL << (int)f1) | (1UL << (int)g1))) == 0 &&
                    !IsSquareAttacked((int)e1, enemySide) &&
                    !IsSquareAttacked((int)f1, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e1, (int)g1, K, 0, 0, 0, 0, 0, 1));
                }

                // White Queenside Castling (e1 -> c1)
                if ((castle & (int)wq) != 0 &&
                    (bothOcc & ((1UL << (int)d1) | (1UL << (int)c1) | (1UL << (int)b1))) == 0 &&
                    !IsSquareAttacked((int)e1, enemySide) &&
                    !IsSquareAttacked((int)d1, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e1, (int)c1, K, 0, 0, 0, 0, 0, 1));
                }
            }
            else
            {
                // Black Kingside Castling (e8 -> g8)
                if ((castle & (int)bk) != 0 &&
                    (bothOcc & ((1UL << (int)f8) | (1UL << (int)g8))) == 0 &&
                    !IsSquareAttacked((int)e8, enemySide) &&
                    !IsSquareAttacked((int)f8, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e8, (int)g8, k, 0, 0, 0, 0, 0, 1));
                }

                // Black Queenside Castling (e8 -> c8)
                if ((castle & (int)bq) != 0 &&
                    (bothOcc & ((1UL << (int)d8) | (1UL << (int)c8) | (1UL << (int)b8))) == 0 &&
                    !IsSquareAttacked((int)e8, enemySide) &&
                    !IsSquareAttacked((int)d8, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e8, (int)c8, k, 0, 0, 0, 0, 0, 1));
                }
            }
        }
    }

    // =========================================================================
    //  SECTION 3 — Board State Mutation (MakeMove)
    // =========================================================================

    // Applies a pseudo-legal move to the board and updates Zobrist hash.
    // Returns 1 if the move is strictly legal (moving side's king is safe),
    // or 0 if illegal (king left in check).
    public static int MakeMove(int move)
    {
        bool isWhite = side == White;

        // ---------------------------------------------------------------------
        //  Step 1: Clear old En Passant target key
        // ---------------------------------------------------------------------
        if (enPassant != (int)noSquare)
            Zobrist.hashKey ^= Zobrist.enpassantKeys[enPassant];
        enPassant = (int)noSquare;

        int piece = GetMovePiece(move);
        int capture = GetMoveCapture(move);
        int sourceSquare = GetMoveSource(move);
        int targetSquare = GetMoveTarget(move);

        // Reset halfmove clock on pawn push or capture
        if (piece == P || piece == p || capture != 0)
            halfmoveClock = 0;
        else
            halfmoveClock++;

        // ---------------------------------------------------------------------
        //  Step 2: Relocate moving piece
        // ---------------------------------------------------------------------
        bitboards[piece] &= ~(1UL << sourceSquare);
        SetBit(ref bitboards[piece], targetSquare);
        Zobrist.hashKey ^= Zobrist.pieceKeys[(piece << 6) | sourceSquare];
        Zobrist.hashKey ^= Zobrist.pieceKeys[(piece << 6) | targetSquare];

        // ---------------------------------------------------------------------
        //  Step 3: Remove captured piece (Standard capture, O(1) via encoding)
        // ---------------------------------------------------------------------
        if (capture != 0 && GetMoveEnpassant(move) == 0)
        {
            int capturedPiece = GetMoveCaptured(move);
            bitboards[capturedPiece] &= ~(1UL << targetSquare);
            Zobrist.hashKey ^= Zobrist.pieceKeys[(capturedPiece << 6) | targetSquare];
        }

        // ---------------------------------------------------------------------
        //  Step 4: Handle Pawn Promotion
        // ---------------------------------------------------------------------
        int promotedPiece = GetMovePromoted(move);
        if (promotedPiece != 0)
        {
            int pawnCode = isWhite ? P : p;
            bitboards[pawnCode] &= ~(1UL << targetSquare);
            Zobrist.hashKey ^= Zobrist.pieceKeys[(pawnCode << 6) | targetSquare];

            SetBit(ref bitboards[promotedPiece], targetSquare);
            Zobrist.hashKey ^= Zobrist.pieceKeys[(promotedPiece << 6) | targetSquare];
        }

        // ---------------------------------------------------------------------
        //  Step 5: Handle En Passant Capture
        // ---------------------------------------------------------------------
        if (GetMoveEnpassant(move) != 0)
        {
            if (isWhite)
            {
                bitboards[p] &= ~(1UL << (targetSquare + 8));
                Zobrist.hashKey ^= Zobrist.pieceKeys[(p << 6) | (targetSquare + 8)];
            }
            else
            {
                bitboards[P] &= ~(1UL << (targetSquare - 8));
                Zobrist.hashKey ^= Zobrist.pieceKeys[(P << 6) | (targetSquare - 8)];
            }
        }

        // ---------------------------------------------------------------------
        //  Step 6: Handle Double Pawn Push (Set EP Target)
        // ---------------------------------------------------------------------
        if (GetMoveDouble(move) != 0)
        {
            int epSquare = isWhite ? targetSquare + 8 : targetSquare - 8;
            enPassant = epSquare;
            Zobrist.hashKey ^= Zobrist.enpassantKeys[epSquare];
        }

        // ---------------------------------------------------------------------
        //  Step 7: Handle Castling (Relocate Rook)
        // ---------------------------------------------------------------------
        if (GetMoveCastling(move) != 0)
        {
            switch ((Square)targetSquare)
            {
                case g1:
                    bitboards[R] ^= (1UL << (int)h1) | (1UL << (int)f1);
                    Zobrist.hashKey ^= Zobrist.pieceKeys[(R << 6) | (int)h1];
                    Zobrist.hashKey ^= Zobrist.pieceKeys[(R << 6) | (int)f1];
                    break;
                case c1:
                    bitboards[R] ^= (1UL << (int)a1) | (1UL << (int)d1);
                    Zobrist.hashKey ^= Zobrist.pieceKeys[(R << 6) | (int)a1];
                    Zobrist.hashKey ^= Zobrist.pieceKeys[(R << 6) | (int)d1];
                    break;
                case g8:
                    bitboards[r] ^= (1UL << (int)h8) | (1UL << (int)f8);
                    Zobrist.hashKey ^= Zobrist.pieceKeys[(r << 6) | (int)h8];
                    Zobrist.hashKey ^= Zobrist.pieceKeys[(r << 6) | (int)f8];
                    break;
                case c8:
                    bitboards[r] ^= (1UL << (int)a8) | (1UL << (int)d8);
                    Zobrist.hashKey ^= Zobrist.pieceKeys[(r << 6) | (int)a8];
                    Zobrist.hashKey ^= Zobrist.pieceKeys[(r << 6) | (int)d8];
                    break;
            }
        }

        // ---------------------------------------------------------------------
        //  Step 8: Update Castling Rights, Occupancies & Side to Move
        // ---------------------------------------------------------------------
        Zobrist.hashKey ^= Zobrist.castleKeys[castle];
        castle &= castlingRights[sourceSquare];
        castle &= castlingRights[targetSquare];
        Zobrist.hashKey ^= Zobrist.castleKeys[castle];

        UpdateOccupancies();
        side ^= 1;
        Zobrist.hashKey ^= Zobrist.sideKey;

        // ---------------------------------------------------------------------
        //  Step 9: Legality Check (Is moving side's king in check?)
        // ---------------------------------------------------------------------
        int kingSquare = isWhite
            ? TrailingZeroCount(bitboards[K])
            : TrailingZeroCount(bitboards[k]);

        if (IsSquareAttacked(kingSquare, side))
            return 0;

        return 1;
    }

    // =========================================================================
    //  SECTION 4 — Move List Helpers
    // =========================================================================

    public static void AddMove(ref MoveList moveList, int move)
    {
        moveList.moves[moveList.count] = move;
        moveList.count++;
    }
}