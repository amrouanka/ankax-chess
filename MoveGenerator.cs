using static Board;
using static CastlingRights;
using static MoveEncoding;
using static PieceAttacks;
using static Square;
using static BitboardOperations;
using static System.Numerics.BitOperations;

// =============================================================================
//  Move generation and board state mutation.
//
//  GenerateMoves() produces a flat list of pseudo-legal moves (all moves that
//  are legal ignoring pins and check exposure). MakeMove() applies a move to
//  the board, updates the Zobrist hash incrementally, and returns 0 if the
//  move leaves the moving side's king in check (i.e. the move was illegal).
//
//  The generator is "pseudo-legal" — it does not filter out moves that leave
//  the king in check. That filtering happens in MakeMove (the return-0 path)
//  and in the search's legality check. This is the standard approach for
//  bitboard engines: generating only legal moves requires expensive pin and
//  check-ray analysis that costs more than just generating a few extra moves
//  and discarding them.
//
//  Captures-only mode (for quiescence search) is handled by narrowing the
//  target mask to enemy-occupied squares and skipping pawn pushes.
// =============================================================================

public static class MoveGenerator
{
    // =========================================================================
    //  SECTION 1 — Constants and lookup tables
    // =========================================================================

    public const int White = 0;
    public const int Black = 1;
    public const int Both = 2;

    // Castling-rights update mask. When a piece moves FROM or TO a square,
    // we AND the current castling rights with this table to revoke any rights
    // that the move invalidates. Most squares map to 15 (0b1111 = no change).
    // The four corners and the two king squares revoke the relevant bits:
    //
    //   a8 → 13 (0b1101)  revoke bQ       h8 → 14 (0b1110)  revoke bK
    //   a1 →  7 (0b0111)  revoke wQ       h1 → 11 (0b1011)  revoke wK
    //   e1 →  3 (0b0011)  revoke wK+wQ    e8 → 12 (0b1100)  revoke bK+bQ
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
    //  SECTION 2 — Move generation
    //
    //  One function handles all piece types. Pawns are special-cased because
    //  their movement, capture, promotion, double-push, and en-passant rules
    //  don't fit the "attack mask AND target mask" pattern used by pieces.
    //
    //  Bitboard iteration idiom (same as Evaluation.cs):
    //
    //      for (ulong x = pieces; x != 0; x &= x - 1)
    //          int sq = TrailingZeroCount(x);
    // =========================================================================

    public static void GenerateMoves(ref MoveList moveList, bool capturesOnly = false)
    {
        moveList.count = 0;

        bool isWhite = side == White;
        ulong friendlyOcc = isWhite ? occupancies[White] : occupancies[Black];
        ulong enemyOcc = isWhite ? occupancies[Black] : occupancies[White];
        ulong bothOcc = occupancies[Both];

        // For captures-only (quiescence): only target enemy pieces.
        // For full generation: target any square not occupied by a friendly piece.
        ulong targetMask = capturesOnly ? enemyOcc : ~friendlyOcc;

        // Piece codes for the moving side.
        int pawnPiece = isWhite ? P : p;
        int knightPiece = isWhite ? N : n;
        int bishopPiece = isWhite ? B : b;
        int rookPiece = isWhite ? R : r;
        int queenPiece = isWhite ? Q : q;
        int kingPiece = isWhite ? K : k;

        // -----------------------------------------------------------------
        //  Pawns
        //
        //  Pawns are the most complex piece to generate because they have
        //  five distinct move types: single push, double push, capture,
        //  promotion (push or capture), and en passant. We handle them
        //  per-pawn rather than with whole-bitboard shifts because the
        //  per-move encoding (flags, promotion piece) varies per target.
        // -----------------------------------------------------------------

        // Direction of advance: White moves toward lower indices (rank 8→1),
        // Black toward higher indices.
        int pawnDir = isWhite ? -8 : 8;

        // Promotion rank: a7..h7 for White (indices 8..15), a2..h2 for Black (48..55).
        int promoFirst = isWhite ? (int)a7 : (int)a2;

        // Starting rank for double pushes: a2..h2 for White (48..55), a7..h7 for Black (8..15).
        int doubleFirst = isWhite ? (int)a2 : (int)a7;

        // Promotion piece codes (used in the move encoding).
        int qPromo = isWhite ? Q : q;
        int rPromo = isWhite ? R : r;
        int bPromo = isWhite ? B : b;
        int nPromo = isWhite ? N : n;

        // Precompute the en-passant target as a single-bit bitboard. Zero if
        // no EP is available. Hoisted out of the pawn loop so we test it once
        // instead of per-pawn.
        ulong epTarget = enPassant != (int)noSquare ? 1UL << enPassant : 0UL;

        for (ulong x = bitboards[pawnPiece]; x != 0; x &= x - 1)
        {
            int src = TrailingZeroCount(x);

            // --- Pushes (skipped in captures-only mode) ---
            if (!capturesOnly)
            {
                int target = src + pawnDir;

                // (uint) cast handles the bounds check: if target < 0 it wraps
                // to a large unsigned value and fails the < 64 comparison.
                if ((uint)target < 64 && (bothOcc & (1UL << target)) == 0)
                {
                    // Promotion rank check: 8 contiguous squares starting at promoFirst.
                    if ((uint)(src - promoFirst) <= 7)
                    {
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, qPromo, 0, 0, 0, 0));
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, rPromo, 0, 0, 0, 0));
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, bPromo, 0, 0, 0, 0));
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, nPromo, 0, 0, 0, 0));
                    }
                    else
                    {
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, 0, 0, 0, 0, 0));

                        // Double push: only from the starting rank, and only if
                        // the square behind the single-push target is also empty.
                        if ((uint)(src - doubleFirst) <= 7 &&
                            (bothOcc & (1UL << (target + pawnDir))) == 0)
                        {
                            AddMove(ref moveList, EncodeMove(src, target + pawnDir, pawnPiece, 0, 0, 1, 0, 0));
                        }
                    }
                }
            }

            // --- Captures (including promotion captures) ---
            ulong attacks = pawnAttacks[side, src] & enemyOcc;
            while (attacks != 0)
            {
                int target = TrailingZeroCount(attacks);
                attacks &= attacks - 1;

                if ((uint)(src - promoFirst) <= 7)
                {
                    AddMove(ref moveList, EncodeMove(src, target, pawnPiece, qPromo, 1, 0, 0, 0));
                    AddMove(ref moveList, EncodeMove(src, target, pawnPiece, rPromo, 1, 0, 0, 0));
                    AddMove(ref moveList, EncodeMove(src, target, pawnPiece, bPromo, 1, 0, 0, 0));
                    AddMove(ref moveList, EncodeMove(src, target, pawnPiece, nPromo, 1, 0, 0, 0));
                }
                else
                {
                    AddMove(ref moveList, EncodeMove(src, target, pawnPiece, 0, 1, 0, 0, 0));
                }
            }

            // --- En passant ---
            if (epTarget != 0 && (pawnAttacks[side, src] & epTarget) != 0)
                AddMove(ref moveList, EncodeMove(src, enPassant, pawnPiece, 0, 1, 0, 1, 0));
        }

        // -----------------------------------------------------------------
        //  Knights, bishops, rooks, queens, kings
        //
        //  All follow the same pattern: compute the attack bitboard for each
        //  piece, AND with the target mask, iterate over the set bits. The
        //  capture flag is set if the target square is occupied by an enemy.
        // -----------------------------------------------------------------

        for (ulong x = bitboards[knightPiece]; x != 0; x &= x - 1)
        {
            int src = TrailingZeroCount(x);
            ulong attacks = knightAttacks[src] & targetMask;
            while (attacks != 0)
            {
                int target = TrailingZeroCount(attacks);
                attacks &= attacks - 1;
                int cap = GetBit(enemyOcc, target) ? 1 : 0;
                AddMove(ref moveList, EncodeMove(src, target, knightPiece, 0, cap, 0, 0, 0));
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
                int cap = GetBit(enemyOcc, target) ? 1 : 0;
                AddMove(ref moveList, EncodeMove(src, target, bishopPiece, 0, cap, 0, 0, 0));
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
                int cap = GetBit(enemyOcc, target) ? 1 : 0;
                AddMove(ref moveList, EncodeMove(src, target, rookPiece, 0, cap, 0, 0, 0));
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
                int cap = GetBit(enemyOcc, target) ? 1 : 0;
                AddMove(ref moveList, EncodeMove(src, target, queenPiece, 0, cap, 0, 0, 0));
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
                int cap = GetBit(enemyOcc, target) ? 1 : 0;
                AddMove(ref moveList, EncodeMove(src, target, kingPiece, 0, cap, 0, 0, 0));
            }
        }

        // -----------------------------------------------------------------
        //  Castling
        //
        //  Only generated in full mode (not captures-only). Requirements:
        //    1. The relevant castling right bit is still set.
        //    2. All squares between king and rook are unoccupied.
        //    3. The king's current square and all transit squares are not
        //       attacked by the enemy. (The destination square is covered
        //       by the transit check for kingside; for queenside, b1/b8
        //       doesn't need to be attack-checked because the king doesn't
        //       cross it — only the rook does.)
        // -----------------------------------------------------------------

        if (!capturesOnly)
        {
            int enemySide = isWhite ? Black : White;

            if (isWhite)
            {
                if ((castle & (int)wk) != 0 &&
                    (bothOcc & ((1UL << (int)f1) | (1UL << (int)g1))) == 0 &&
                    !IsSquareAttacked((int)e1, enemySide) &&
                    !IsSquareAttacked((int)f1, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e1, (int)g1, K, 0, 0, 0, 0, 1));
                }

                if ((castle & (int)wq) != 0 &&
                    (bothOcc & ((1UL << (int)d1) | (1UL << (int)c1) | (1UL << (int)b1))) == 0 &&
                    !IsSquareAttacked((int)e1, enemySide) &&
                    !IsSquareAttacked((int)d1, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e1, (int)c1, K, 0, 0, 0, 0, 1));
                }
            }
            else
            {
                if ((castle & (int)bk) != 0 &&
                    (bothOcc & ((1UL << (int)f8) | (1UL << (int)g8))) == 0 &&
                    !IsSquareAttacked((int)e8, enemySide) &&
                    !IsSquareAttacked((int)f8, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e8, (int)g8, k, 0, 0, 0, 0, 1));
                }

                if ((castle & (int)bq) != 0 &&
                    (bothOcc & ((1UL << (int)d8) | (1UL << (int)c8) | (1UL << (int)b8))) == 0 &&
                    !IsSquareAttacked((int)e8, enemySide) &&
                    !IsSquareAttacked((int)d8, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e8, (int)c8, k, 0, 0, 0, 0, 1));
                }
            }
        }
    }

    // Captures-only wrapper for quiescence search.
    public static void GenerateCaptureMoves(ref MoveList moveList) =>
        GenerateMoves(ref moveList, capturesOnly: true);

    // =========================================================================
    //  SECTION 3 — Move list helper
    // =========================================================================

    public static void AddMove(ref MoveList moveList, int move)
    {
        moveList.moves[moveList.count] = move;
        moveList.count++;
    }

    // =========================================================================
    //  SECTION 4 — MakeMove
    //
    //  Applies a move to the board state and updates the Zobrist hash
    //  incrementally. Returns 1 if the move is legal (king not in check
    //  after the move), 0 if illegal.
    //
    //  The `moveFlag` parameter controls legality filtering:
    //    • allMoves  — execute the move fully, then check king safety.
    //    • captures  — reject quiet moves early (return 0 without mutating
    //                  the board). Used by quiescence search to skip non-
    //                  captures without paying the full make/unmake cost.
    //
    //  The captured-piece detection loop (iterating enemy piece types to
    //  find which bitboard holds the target square) is O(6) worst case.
    //  Storing the captured piece in the move encoding would eliminate this
    //  loop but requires a wider move format — a future optimization.
    // =========================================================================

    public static int MakeMove(int move, int moveFlag)
    {
        // Captures-only mode: reject quiet moves without touching the board.
        if (moveFlag != (int)MoveFlag.allMoves && GetMoveCapture(move) == 0)
            return 0;

        bool isWhite = side == White;

        // Clear the old en-passant key from the hash (if any). The new EP
        // square (if this move is a double push) will be hashed in later.
        if (enPassant != (int)noSquare)
            Zobrist.hashKey ^= Zobrist.enpassantKeys[enPassant];
        enPassant = (int)noSquare;

        int piece = GetMovePiece(move);
        int capture = GetMoveCapture(move);
        int sourceSquare = GetMoveSource(move);
        int targetSquare = GetMoveTarget(move);

        // Halfmove clock: reset on pawn moves and captures (for 50-move rule).
        if (piece == P || piece == p || capture != 0)
            halfmoveClock = 0;
        else
            halfmoveClock++;

        // --- Move the piece ---
        bitboards[piece] &= ~(1UL << sourceSquare);
        SetBit(ref bitboards[piece], targetSquare);
        Zobrist.hashKey ^= Zobrist.pieceKeys[(piece << 6) | sourceSquare];
        Zobrist.hashKey ^= Zobrist.pieceKeys[(piece << 6) | targetSquare];

        // --- Remove captured piece (not en passant — that's handled below) ---
        if (capture != 0 && GetMoveEnpassant(move) == 0)
        {
            int startPiece = isWhite ? p : P;
            int endPiece = isWhite ? k : K;

            for (int bbPiece = startPiece; bbPiece <= endPiece; bbPiece++)
            {
                if (GetBit(bitboards[bbPiece], targetSquare))
                {
                    bitboards[bbPiece] &= ~(1UL << targetSquare);
                    Zobrist.hashKey ^= Zobrist.pieceKeys[(bbPiece << 6) | targetSquare];
                    break;
                }
            }
        }

        // --- Promotion: replace the pawn with the promoted piece ---
        int promotedPiece = GetMovePromoted(move);
        if (promotedPiece != 0)
        {
            int pawnCode = isWhite ? P : p;
            bitboards[pawnCode] &= ~(1UL << targetSquare);
            Zobrist.hashKey ^= Zobrist.pieceKeys[(pawnCode << 6) | targetSquare];

            SetBit(ref bitboards[promotedPiece], targetSquare);
            Zobrist.hashKey ^= Zobrist.pieceKeys[(promotedPiece << 6) | targetSquare];
        }

        // --- En passant capture: remove the enemy pawn that just double-pushed ---
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

        // --- Double push: set the new en-passant target square ---
        if (GetMoveDouble(move) != 0)
        {
            int epSquare = isWhite ? targetSquare + 8 : targetSquare - 8;
            enPassant = epSquare;
            Zobrist.hashKey ^= Zobrist.enpassantKeys[epSquare];
        }

        // --- Castling: move the rook ---
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

        // --- Update castling rights ---
        Zobrist.hashKey ^= Zobrist.castleKeys[castle];
        castle &= castlingRights[sourceSquare];
        castle &= castlingRights[targetSquare];
        Zobrist.hashKey ^= Zobrist.castleKeys[castle];

        // --- Rebuild occupancy bitboards and flip side ---
        UpdateOccupancies();
        side ^= 1;
        Zobrist.hashKey ^= Zobrist.sideKey;

        // --- Legality check: is the moving side's king in check? ---
        int kingSquare = isWhite
            ? TrailingZeroCount(bitboards[K])
            : TrailingZeroCount(bitboards[k]);

        if (IsSquareAttacked(kingSquare, side))
            return 0;

        return 1;
    }
}