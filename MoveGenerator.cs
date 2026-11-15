using static Board;
using static CastlingRights;
using static MoveEncoding;
using static PieceAttacks;
using static Square;
using static BitboardOperations;

public static class MoveGenerator
{
    // Maps each square to a bitmask of castling rights that SURVIVE a move
    // from/to that square. Used in MakeMove to strip rights when a king or
    // rook moves (or is captured).
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

    public const int White = 0;
    public const int Black = 1;
    public const int Both = 2;

    // =========================================================================
    //  Move Generation
    // =========================================================================

    /// <summary>
    /// Generates all pseudo-legal moves (or captures only) for the current position.
    /// Legality filtering (king left in check) happens later in MakeMove.
    /// </summary>
    /// <param name="capturesOnly">
    /// When true, only captures and promotions are generated.
    /// Used by quiescence search to avoid the horizon effect.
    /// </param>
    public static void GenerateMoves(ref MoveList moveList, bool capturesOnly = false)
    {
        moveList.count = 0;

        bool isWhite = side == White;
        ulong friendlyOcc = isWhite ? occupancies[White] : occupancies[Black];
        ulong enemyOcc = isWhite ? occupancies[Black] : occupancies[White];
        ulong bothOcc = occupancies[Both];

        // In captures-only mode, targets are restricted to enemy pieces.
        // In all-moves mode, targets are any square not occupied by a friendly piece.
        ulong targetMask = capturesOnly ? enemyOcc : ~friendlyOcc;

        // ----- Side-dependent piece constants -----
        int pawnPiece = isWhite ? P : p;
        int knightPiece = isWhite ? N : n;
        int bishopPiece = isWhite ? B : b;
        int rookPiece = isWhite ? R : r;
        int queenPiece = isWhite ? Q : q;
        int kingPiece = isWhite ? K : k;

        // =====================================================================
        //  Pawns (unified for both sides via direction and rank parameters)
        // =====================================================================
        //
        //  White pawns move "up" the board (square index decreases by 8).
        //  Black pawns move "down" (square index increases by 8).
        //
        //  Promotion ranks:  White = rank 7 (a7..h7, indices 8..15)
        //                    Black = rank 2 (a2..h2, indices 48..55)
        //  Double-push ranks: White = rank 2 (a2..h2)
        //                     Black = rank 7 (a7..h7)

        int pawnDir = isWhite ? -8 : 8;
        int promoFirst = isWhite ? (int)a7 : (int)a2;
        int promoLast = isWhite ? (int)h7 : (int)h2;
        int doubleFirst = isWhite ? (int)a2 : (int)a7;
        int doubleLast = isWhite ? (int)h2 : (int)h7;
        int qPromo = isWhite ? Q : q;
        int rPromo = isWhite ? R : r;
        int bPromo = isWhite ? B : b;
        int nPromo = isWhite ? N : n;

        ulong pawns = bitboards[pawnPiece];
        while (pawns != 0)
        {
            int src = GetLs1bIndex(pawns);

            // --- Pushes (skipped in captures-only mode) ---
            if (!capturesOnly)
            {
                int target = src + pawnDir;

                if (target >= 0 && target < 64 && !GetBit(bothOcc, target))
                {
                    if (src >= promoFirst && src <= promoLast)
                    {
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, qPromo, 0, 0, 0, 0));
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, rPromo, 0, 0, 0, 0));
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, bPromo, 0, 0, 0, 0));
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, nPromo, 0, 0, 0, 0));
                    }
                    else
                    {
                        AddMove(ref moveList, EncodeMove(src, target, pawnPiece, 0, 0, 0, 0, 0));

                        // Double push from starting rank
                        if (src >= doubleFirst && src <= doubleLast &&
                            !GetBit(bothOcc, target + pawnDir))
                        {
                            AddMove(ref moveList, EncodeMove(src, target + pawnDir, pawnPiece, 0, 0, 1, 0, 0));
                        }
                    }
                }
            }

            // --- Captures ---
            ulong attacks = pawnAttacks[side, src] & enemyOcc;
            while (attacks != 0)
            {
                int target = GetLs1bIndex(attacks);

                if (src >= promoFirst && src <= promoLast)
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

                PopBit(ref attacks, target);
            }

            // --- En passant ---
            if (enPassant != (int)noSquare)
            {
                ulong epAttacks = pawnAttacks[side, src] & (1UL << enPassant);
                if (epAttacks != 0)
                    AddMove(ref moveList, EncodeMove(src, enPassant, pawnPiece, 0, 1, 0, 1, 0));
            }

            PopBit(ref pawns, src);
        }

        // =====================================================================
        //  Knights
        // =====================================================================

        ulong knights = bitboards[knightPiece];
        while (knights != 0)
        {
            int src = GetLs1bIndex(knights);
            ulong attacks = knightAttacks[src] & targetMask;

            while (attacks != 0)
            {
                int target = GetLs1bIndex(attacks);
                int cap = GetBit(enemyOcc, target) ? 1 : 0;
                AddMove(ref moveList, EncodeMove(src, target, knightPiece, 0, cap, 0, 0, 0));
                PopBit(ref attacks, target);
            }

            PopBit(ref knights, src);
        }

        // =====================================================================
        //  Bishops
        // =====================================================================

        ulong bishops = bitboards[bishopPiece];
        while (bishops != 0)
        {
            int src = GetLs1bIndex(bishops);
            ulong attacks = GetBishopAttacks(src, bothOcc) & targetMask;

            while (attacks != 0)
            {
                int target = GetLs1bIndex(attacks);
                int cap = GetBit(enemyOcc, target) ? 1 : 0;
                AddMove(ref moveList, EncodeMove(src, target, bishopPiece, 0, cap, 0, 0, 0));
                PopBit(ref attacks, target);
            }

            PopBit(ref bishops, src);
        }

        // =====================================================================
        //  Rooks
        // =====================================================================

        ulong rooks = bitboards[rookPiece];
        while (rooks != 0)
        {
            int src = GetLs1bIndex(rooks);
            ulong attacks = GetRookAttacks(src, bothOcc) & targetMask;

            while (attacks != 0)
            {
                int target = GetLs1bIndex(attacks);
                int cap = GetBit(enemyOcc, target) ? 1 : 0;
                AddMove(ref moveList, EncodeMove(src, target, rookPiece, 0, cap, 0, 0, 0));
                PopBit(ref attacks, target);
            }

            PopBit(ref rooks, src);
        }

        // =====================================================================
        //  Queens
        // =====================================================================

        ulong queens = bitboards[queenPiece];
        while (queens != 0)
        {
            int src = GetLs1bIndex(queens);
            ulong attacks = GetQueenAttacks(src, bothOcc) & targetMask;

            while (attacks != 0)
            {
                int target = GetLs1bIndex(attacks);
                int cap = GetBit(enemyOcc, target) ? 1 : 0;
                AddMove(ref moveList, EncodeMove(src, target, queenPiece, 0, cap, 0, 0, 0));
                PopBit(ref attacks, target);
            }

            PopBit(ref queens, src);
        }

        // =====================================================================
        //  King moves
        // =====================================================================

        ulong kings = bitboards[kingPiece];
        while (kings != 0)
        {
            int src = GetLs1bIndex(kings);
            ulong attacks = kingAttacks[src] & targetMask;

            while (attacks != 0)
            {
                int target = GetLs1bIndex(attacks);
                int cap = GetBit(enemyOcc, target) ? 1 : 0;
                AddMove(ref moveList, EncodeMove(src, target, kingPiece, 0, cap, 0, 0, 0));
                PopBit(ref attacks, target);
            }

            PopBit(ref kings, src);
        }

        // =====================================================================
        //  Castling (only in all-moves mode — castling is never a capture)
        // =====================================================================

        if (!capturesOnly)
        {
            int enemySide = isWhite ? Black : White;

            if (isWhite)
            {
                // Kingside: e1-g1, rook h1-f1
                if ((castle & (int)wk) != 0 &&
                    !GetBit(bothOcc, (int)f1) &&
                    !GetBit(bothOcc, (int)g1) &&
                    !IsSquareAttacked((int)e1, enemySide) &&
                    !IsSquareAttacked((int)f1, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e1, (int)g1, K, 0, 0, 0, 0, 1));
                }

                // Queenside: e1-c1, rook a1-d1
                if ((castle & (int)wq) != 0 &&
                    !GetBit(bothOcc, (int)d1) &&
                    !GetBit(bothOcc, (int)c1) &&
                    !GetBit(bothOcc, (int)b1) &&
                    !IsSquareAttacked((int)e1, enemySide) &&
                    !IsSquareAttacked((int)d1, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e1, (int)c1, K, 0, 0, 0, 0, 1));
                }
            }
            else
            {
                // Kingside: e8-g8, rook h8-f8
                if ((castle & (int)bk) != 0 &&
                    !GetBit(bothOcc, (int)f8) &&
                    !GetBit(bothOcc, (int)g8) &&
                    !IsSquareAttacked((int)e8, enemySide) &&
                    !IsSquareAttacked((int)f8, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e8, (int)g8, k, 0, 0, 0, 0, 1));
                }

                // Queenside: e8-c8, rook a8-d8
                if ((castle & (int)bq) != 0 &&
                    !GetBit(bothOcc, (int)d8) &&
                    !GetBit(bothOcc, (int)c8) &&
                    !GetBit(bothOcc, (int)b8) &&
                    !IsSquareAttacked((int)e8, enemySide) &&
                    !IsSquareAttacked((int)d8, enemySide))
                {
                    AddMove(ref moveList, EncodeMove((int)e8, (int)c8, k, 0, 0, 0, 0, 1));
                }
            }
        }
    }

    /// <summary>
    /// Captures-only wrapper for quiescence search.
    /// </summary>
    public static void GenerateCaptureMoves(ref MoveList moveList) =>
        GenerateMoves(ref moveList, capturesOnly: true);

    // =========================================================================
    //  Move List Helper
    // =========================================================================

    public static void AddMove(ref MoveList moveList, int move)
    {
        moveList.moves[moveList.count] = move;
        moveList.count++;
    }

    // =========================================================================
    //  Make Move
    // =========================================================================
    //
    //  Applies a move to the board and updates Zobrist hash incrementally.
    //  Returns 1 if the move is legal (own king not left in check), 0 otherwise.
    //  When returning 0, the board state is NOT restored — the caller must
    //  TakeBack using the BoardState snapshot taken before MakeMove.

    public static int MakeMove(int move, int moveFlag)
    {
        bool isWhite = side == White;

        // Remove the old en passant from hash (if it existed)
        if (enPassant != (int)noSquare)
            Zobrist.hashKey ^= Zobrist.enpassantKeys[enPassant];

        // Then clear it
        enPassant = (int)noSquare;

        if (moveFlag == (int)MoveFlag.allMoves)
        {
            int piece = GetMovePiece(move);
            int capture = GetMoveCapture(move);

            // 50-move rule clock:
            // reset on pawn move or capture, otherwise increment
            if (piece == P || piece == p || capture != 0)
                halfmoveClock = 0;
            else
                halfmoveClock++;

            int sourceSquare = GetMoveSource(move);
            int targetSquare = GetMoveTarget(move);

            PopBit(ref bitboards[piece], sourceSquare);
            SetBit(ref bitboards[piece], targetSquare);

            Zobrist.hashKey ^= Zobrist.pieceKeys[piece, sourceSquare];
            Zobrist.hashKey ^= Zobrist.pieceKeys[piece, targetSquare];

            if (capture != 0 && GetMoveEnpassant(move) == 0)
            {
                int startPiece = isWhite ? p : P;
                int endPiece = isWhite ? k : K;

                for (int bbPiece = startPiece; bbPiece <= endPiece; bbPiece++)
                {
                    if (GetBit(bitboards[bbPiece], targetSquare))
                    {
                        PopBit(ref bitboards[bbPiece], targetSquare);
                        Zobrist.hashKey ^= Zobrist.pieceKeys[bbPiece, targetSquare];
                        break;
                    }
                }
            }

            int promotedPiece = GetMovePromoted(move);
            if (promotedPiece != 0)
            {
                if (isWhite)
                {
                    PopBit(ref bitboards[P], targetSquare);
                    Zobrist.hashKey ^= Zobrist.pieceKeys[P, targetSquare];
                }
                else
                {
                    PopBit(ref bitboards[p], targetSquare);
                    Zobrist.hashKey ^= Zobrist.pieceKeys[p, targetSquare];
                }

                SetBit(ref bitboards[promotedPiece], targetSquare);
                Zobrist.hashKey ^= Zobrist.pieceKeys[promotedPiece, targetSquare];
            }

            if (GetMoveEnpassant(move) != 0)
            {
                if (isWhite)
                {
                    PopBit(ref bitboards[p], targetSquare + 8);
                    Zobrist.hashKey ^= Zobrist.pieceKeys[p, targetSquare + 8];
                }
                else
                {
                    PopBit(ref bitboards[P], targetSquare - 8);
                    Zobrist.hashKey ^= Zobrist.pieceKeys[P, targetSquare - 8];
                }
            }

            int doublePush = GetMoveDouble(move);
            if (doublePush != 0)
            {
                if (isWhite)
                {
                    enPassant = targetSquare + 8;
                    Zobrist.hashKey ^= Zobrist.enpassantKeys[targetSquare + 8];
                }
                else
                {
                    enPassant = targetSquare - 8;
                    Zobrist.hashKey ^= Zobrist.enpassantKeys[targetSquare - 8];
                }
            }

            if (GetMoveCastling(move) != 0)
            {
                switch ((Square)targetSquare)
                {
                    case g1:
                        PopBit(ref bitboards[R], (int)h1);
                        SetBit(ref bitboards[R], (int)f1);
                        Zobrist.hashKey ^= Zobrist.pieceKeys[R, (int)h1];
                        Zobrist.hashKey ^= Zobrist.pieceKeys[R, (int)f1];
                        break;
                    case c1:
                        PopBit(ref bitboards[R], (int)a1);
                        SetBit(ref bitboards[R], (int)d1);
                        Zobrist.hashKey ^= Zobrist.pieceKeys[R, (int)a1];
                        Zobrist.hashKey ^= Zobrist.pieceKeys[R, (int)d1];
                        break;
                    case g8:
                        PopBit(ref bitboards[r], (int)h8);
                        SetBit(ref bitboards[r], (int)f8);
                        Zobrist.hashKey ^= Zobrist.pieceKeys[r, (int)h8];
                        Zobrist.hashKey ^= Zobrist.pieceKeys[r, (int)f8];
                        break;
                    case c8:
                        PopBit(ref bitboards[r], (int)a8);
                        SetBit(ref bitboards[r], (int)d8);
                        Zobrist.hashKey ^= Zobrist.pieceKeys[r, (int)a8];
                        Zobrist.hashKey ^= Zobrist.pieceKeys[r, (int)d8];
                        break;
                }
            }

            Zobrist.hashKey ^= Zobrist.castleKeys[castle];

            castle &= castlingRights[sourceSquare];
            castle &= castlingRights[targetSquare];

            Zobrist.hashKey ^= Zobrist.castleKeys[castle];

            UpdateOccupancies();

            side ^= 1;
            Zobrist.hashKey ^= Zobrist.sideKey;

            int kingSquare = isWhite
                ? GetLs1bIndex(bitboards[K])
                : GetLs1bIndex(bitboards[k]);

            if (IsSquareAttacked(kingSquare, side))
                return 0;

            return 1;
        }
        else
        {
            if (GetMoveCapture(move) == 0)
                return 0;

            return MakeMove(move, (int)MoveFlag.allMoves);
        }
    }
}