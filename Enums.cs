// =============================================================================
// AnkaX Chess Engine - Board & State Enumerations
//
// Enumerations defining square indices (a8 = 0 to h1 = 63) and castling rights
// bitmasks used across move generation, board representation, and search.
// =============================================================================

// =========================================================================
//  SECTION 1 — Board Squares
// =========================================================================

// Square index mapping corresponding to top-left to bottom-right order.
// Ranks map from 8 down to 1 (0 to 7) and files from A to H (0 to 7).
public enum Square
{
    a8, b8, c8, d8, e8, f8, g8, h8,
    a7, b7, c7, d7, e7, f7, g7, h7,
    a6, b6, c6, d6, e6, f6, g6, h6,
    a5, b5, c5, d5, e5, f5, g5, h5,
    a4, b4, c4, d4, e4, f4, g4, h4,
    a3, b3, c3, d3, e3, f3, g3, h3,
    a2, b2, c2, d2, e2, f2, g2, h2,
    a1, b1, c1, d1, e1, f1, g1, h1,
    noSquare
}

// =========================================================================
//  SECTION 2 — Castling Rights Bitmask
// =========================================================================

// 4-bit mask representing active castling permissions.
// Combined via bitwise OR (e.g. wk | wq | bk | bq = 15 = 0b1111).
public enum CastlingRights
{
    wk = 1, // White Kingside  (0b0001)
    wq = 2, // White Queenside (0b0010)
    bk = 4, // Black Kingside  (0b0100)
    bq = 8  // Black Queenside (0b1000)
}