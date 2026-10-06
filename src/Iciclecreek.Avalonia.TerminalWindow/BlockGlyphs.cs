using System;
using System.Collections.Generic;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// The block characters drawn as rectangles rather than from the font.
    /// </summary>
    /// <remarks>
    /// <para>These characters exist to be tiled: half blocks, quadrants and sextants are how
    /// programs like notcurses and chafa draw pictures out of text. That only works if each one
    /// fills its share of the cell EXACTLY, and a font cannot promise that -- its glyph is drawn to
    /// its own metrics, not to a cell rounded to whole device pixels, so neighbouring blocks overlap
    /// by a fraction or leave a hairline between them. Windows Terminal, kitty, WezTerm and foot all
    /// draw them themselves for the same reason.</para>
    /// <para>Shapes are in 24ths of a cell on each axis: 24 is the smallest unit that holds both the
    /// eighths of the Block Elements and the thirds of the sextants. Renderers snap every edge to
    /// device pixels against the cell's own snapped edges, so a shape ending at 24 meets the next
    /// cell's shape starting at 0 on the same pixel.</para>
    /// </remarks>
    internal static class BlockGlyphs
    {
        /// <summary>One filled rectangle, in 24ths of the cell, drawn at <see cref="Alpha"/> of the foreground.</summary>
        internal readonly record struct Shape(byte X0, byte Y0, byte X1, byte Y1, float Alpha = 1f);

        internal const int Units = 24;

        private static readonly Dictionary<int, Shape[]> Shapes = Build();

        /// <summary>Whether <paramref name="codePoint"/> is drawn by this table.</summary>
        public static bool IsBlock(int codePoint)
            => (codePoint >= 0x2580 && codePoint <= 0x259F) || (codePoint >= 0x1FB00 && codePoint <= 0x1FB3B);

        /// <summary>The rectangles that make up <paramref name="codePoint"/>, if it is a block character.</summary>
        public static bool TryGet(int codePoint, out Shape[] shapes)
        {
            if (IsBlock(codePoint) && Shapes.TryGetValue(codePoint, out shapes!))
                return true;
            shapes = Array.Empty<Shape>();
            return false;
        }

        private static Dictionary<int, Shape[]> Build()
        {
            var map = new Dictionary<int, Shape[]>();
            const byte H = Units / 2;

            // U+2580..259F Block Elements.
            map[0x2580] = new[] { new Shape(0, 0, Units, H) };                     // ▀ upper half
            for (var eighths = 1; eighths <= 7; eighths++)                           // ▁..▇ lower 1/8..7/8
                map[0x2580 + eighths] = new[] { new Shape(0, (byte)(Units - eighths * 3), Units, Units) };
            map[0x2588] = new[] { new Shape(0, 0, Units, Units) };                 // █ full
            for (var eighths = 7; eighths >= 1; eighths--)                           // ▉..▏ left 7/8..1/8
                map[0x2589 + (7 - eighths)] = new[] { new Shape(0, 0, (byte)(eighths * 3), Units) };
            map[0x2590] = new[] { new Shape(H, 0, Units, Units) };                 // ▐ right half
            map[0x2591] = new[] { new Shape(0, 0, Units, Units, 0.25f) };          // ░ light shade
            map[0x2592] = new[] { new Shape(0, 0, Units, Units, 0.50f) };          // ▒ medium shade
            map[0x2593] = new[] { new Shape(0, 0, Units, Units, 0.75f) };          // ▓ dark shade
            map[0x2594] = new[] { new Shape(0, 0, Units, 3) };                     // ▔ upper 1/8
            map[0x2595] = new[] { new Shape(Units - 3, 0, Units, Units) };         // ▕ right 1/8

            // Quadrants, as bits: 1 upper left, 2 upper right, 4 lower left, 8 lower right.
            int[] quadrants =
            {
                4,          // ▖ U+2596
                8,          // ▗
                1,          // ▘
                1 | 4 | 8,  // ▙
                1 | 8,      // ▚
                1 | 2 | 4,  // ▛
                1 | 2 | 8,  // ▜
                2,          // ▝
                2 | 4,      // ▞
                2 | 4 | 8,  // ▟ U+259F
            };
            for (var i = 0; i < quadrants.Length; i++)
                map[0x2596 + i] = Grid(quadrants[i], columns: 2, rows: 2);

            // U+1FB00..1FB3B sextants: every pattern of a 2x3 grid except the ones Unicode already
            // had -- empty (space), full (█), left column (▌, pattern 21) and right column (▐, 42).
            // Sextant n is bit n-1, numbered left to right then top to bottom.
            for (var i = 0; i < 60; i++)
            {
                var pattern = i + 1;
                if (pattern >= 21) pattern++;
                if (pattern >= 42) pattern++;
                map[0x1FB00 + i] = Grid(pattern, columns: 2, rows: 3);
            }

            return map;
        }

        /// <summary>A shape per set bit of a grid, bits numbered left to right then top to bottom.</summary>
        private static Shape[] Grid(int bits, int columns, int rows)
        {
            var shapes = new List<Shape>();
            int cw = Units / columns, rh = Units / rows;
            for (var row = 0; row < rows; row++)
                for (var col = 0; col < columns; col++)
                    if ((bits & (1 << (row * columns + col))) != 0)
                        shapes.Add(new Shape((byte)(col * cw), (byte)(row * rh),
                                             (byte)((col + 1) * cw), (byte)((row + 1) * rh)));
            return shapes.ToArray();
        }
    }
}
