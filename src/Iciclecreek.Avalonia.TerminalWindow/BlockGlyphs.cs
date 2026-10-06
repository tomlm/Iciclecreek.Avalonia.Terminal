using System;
using System.Collections.Generic;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// The block characters drawn as rectangles and polygons rather than from the font.
    /// </summary>
    /// <remarks>
    /// <para>These characters exist to be tiled: half blocks, quadrants, sextants and the
    /// smooth-mosaic wedges are how programs like notcurses and chafa draw pictures out of text, and
    /// notcurses stacks wedges into shapes two rows tall -- where a glyph falling a few pixels short of
    /// its cell's bottom drew a band through every one. That only works if each one
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
        /// <summary>
        /// One filled rectangle, in 24ths of the cell, drawn at <see cref="Alpha"/> of the foreground --
        /// or, when <see cref="Points"/> is set, the polygon through those x,y pairs instead.
        /// </summary>
        internal readonly record struct Shape(byte X0, byte Y0, byte X1, byte Y1, float Alpha = 1f,
                                              byte[]? Points = null)
        {
            public bool IsPolygon => Points is not null;
        }

        internal const int Units = 24;

        private static readonly Dictionary<int, Shape[]> Shapes = Build();

        /// <summary>Whether <paramref name="codePoint"/> is drawn by this table.</summary>
        public static bool IsBlock(int codePoint)
            => (codePoint >= 0x2580 && codePoint <= 0x259F) || (codePoint >= 0x1FB00 && codePoint <= 0x1FB8B);

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

            // U+1FB3C..1FB67 smooth-mosaic wedges: a corner of the cell cut off along a diagonal
            // between the sextant grid's points, drawn as polygons. Extracted mechanically from
            // WezTerm's customglyph.rs, which defines each by name; the comment is that name.
            void Wedge(int codePoint, params byte[] points) => map[codePoint] = new[] { new Shape(0, 0, 0, 0, 1f, points) };
            Wedge(0x1FB3C, 0, 16, 0, 24, 12, 24); // [🬼] LOWER LEFT BLOCK DIAGONAL LOWER MIDDLE LEFT TO LOWER CENTRE
            Wedge(0x1FB3D, 0, 16, 0, 24, 24, 24); // [🬽] LOWER LEFT BLOCK DIAGONAL LOWER MIDDLE LEFT TO LOWER RIGHT
            Wedge(0x1FB3E, 0, 8, 0, 24, 12, 24); // [🬾] LOWER LEFT BLOCK DIAGONAL UPPER MIDDLE LEFT TO LOWER CENTRE
            Wedge(0x1FB3F, 0, 8, 0, 24, 24, 24); // [🬿] LOWER LEFT BLOCK DIAGONAL UPPER MIDDLE LEFT TO LOWER RIGHT
            Wedge(0x1FB40, 0, 0, 0, 24, 12, 24); // [🭀] LOWER LEFT BLOCK DIAGONAL UPPER LEFT TO LOWER CENTRE
            Wedge(0x1FB41, 12, 0, 24, 0, 24, 24, 0, 24, 0, 8); // [🭁] LOWER RIGHT BLOCK DIAGONAL UPPER MIDDLE LEFT TO UPPER CENTRE
            Wedge(0x1FB42, 24, 0, 24, 24, 0, 24, 0, 8); // [🭂] LOWER RIGHT BLOCK DIAGONAL UPPER MIDDLE LEFT TO UPPER RIGHT
            Wedge(0x1FB43, 12, 0, 24, 0, 24, 24, 0, 24, 0, 16); // [🭃] LOWER RIGHT BLOCK DIAGONAL LOWER MIDDLE LEFT TO UPPER CENTRE
            Wedge(0x1FB44, 24, 0, 24, 24, 0, 24, 0, 16); // [🭄] LOWER RIGHT BLOCK DIAGONAL LOWER MIDDLE LEFT TO UPPER RIGHT
            Wedge(0x1FB45, 12, 0, 24, 0, 24, 24, 0, 24); // [🭅] LOWER RIGHT BLOCK DIAGONAL UPPER LEFT TO UPPER CENTRE
            Wedge(0x1FB46, 0, 16, 24, 8, 24, 24, 0, 24); // [🭆] LOWER RIGHT BLOCK DIAGONAL LOWER MIDDLE LEFT TO UPPER MIDDLE RIGHT
            Wedge(0x1FB47, 12, 24, 24, 16, 24, 24); // [🭇] LOWER RIGHT BLOCK DIAGONAL LOWER CENTRE TO LOWER MIDDLE RIGHT
            Wedge(0x1FB48, 0, 24, 24, 16, 24, 24); // [🭈] LOWER RIGHT BLOCK DIAGONAL LOWER LEFT TO LOWER MIDDLE RIGHT
            Wedge(0x1FB49, 12, 24, 24, 8, 24, 24); // [🭉] LOWER RIGHT BLOCK DIAGONAL LOWER CENTRE TO UPPER MIDDLE RIGHT
            Wedge(0x1FB4A, 0, 24, 24, 8, 24, 24); // [🭊] LOWER RIGHT BLOCK DIAGONAL LOWER LEFT TO UPPER MIDDLE RIGHT
            Wedge(0x1FB4B, 12, 24, 24, 0, 24, 24); // [🭋] LOWER RIGHT BLOCK DIAGONAL LOWER CENTRE TO UPPER RIGHT
            Wedge(0x1FB4C, 0, 0, 12, 0, 24, 8, 24, 24, 0, 24); // [🭌] LOWER LEFT BLOCK DIAGONAL UPPER CENTRE TO UPPER MIDDLE RIGHT
            Wedge(0x1FB4D, 0, 0, 24, 8, 24, 24, 0, 24); // [🭍] LOWER LEFT BLOCK DIAGONAL UPPER LEFT TO UPPER MIDDLE RIGHT
            Wedge(0x1FB4E, 0, 0, 12, 0, 24, 16, 24, 24, 0, 24); // [🭎] LOWER LEFT BLOCK DIAGONAL UPPER CENTRE TO LOWER MIDDLE RIGHT
            Wedge(0x1FB4F, 0, 0, 24, 16, 24, 24, 0, 24); // [🭏] LOWER LEFT BLOCK DIAGONAL UPPER LEFT TO LOWER MIDDLE RIGHT
            Wedge(0x1FB50, 0, 0, 12, 0, 24, 24, 0, 24); // [🭐] LOWER LEFT BLOCK DIAGONAL UPPER CENTRE TO LOWER RIGHT
            Wedge(0x1FB51, 0, 8, 24, 16, 24, 24, 0, 24); // [🭑] LOWER LEFT BLOCK DIAGONAL UPPER MIDDLE LEFT TO LOWER MIDDLE RIGHT
            Wedge(0x1FB52, 0, 0, 24, 0, 24, 24, 12, 24, 0, 16); // [🭒] UPPER RIGHT BLOCK DIAGONAL LOWER MIDDLE LEFT TO LOWER CENTRE
            Wedge(0x1FB53, 0, 0, 24, 0, 24, 24, 0, 16); // [🭓] UPPER RIGHT BLOCK DIAGONAL LOWER MIDDLE LEFT TO LOWER RIGHT
            Wedge(0x1FB54, 0, 0, 24, 0, 24, 24, 12, 24, 0, 8); // [🭔] UPPER RIGHT BLOCK DIAGONAL UPPER MIDDLE LEFT TO LOWER CENTRE
            Wedge(0x1FB55, 0, 0, 24, 0, 24, 24, 0, 8); // [🭕] UPPER RIGHT BLOCK DIAGONAL UPPER MIDDLE LEFT TO LOWER RIGHT
            Wedge(0x1FB56, 0, 0, 24, 0, 24, 24, 12, 24); // [🭖] UPPER RIGHT BLOCK DIAGONAL UPPER LEFT TO LOWER CENTRE
            Wedge(0x1FB57, 0, 0, 12, 0, 0, 8); // [🭗] UPPER LEFT BLOCK DIAGONAL UPPER MIDDLE LEFT TO UPPER CENTRE
            Wedge(0x1FB58, 0, 0, 24, 0, 0, 8); // [🭘] UPPER LEFT BLOCK DIAGONAL UPPER MIDDLE LEFT TO UPPER RIGHT
            Wedge(0x1FB59, 0, 0, 12, 0, 0, 16); // [🭙] UPPER LEFT BLOCK DIAGONAL LOWER MIDDLE LEFT TO UPPER CENTRE
            Wedge(0x1FB5A, 0, 0, 24, 0, 0, 16); // [🭚] UPPER LEFT BLOCK DIAGONAL LOWER MIDDLE LEFT TO UPPER RIGHT
            Wedge(0x1FB5B, 0, 0, 12, 0, 0, 24); // [🭛] UPPER LEFT BLOCK DIAGONAL LOWER LEFT TO UPPER CENTRE
            Wedge(0x1FB5C, 0, 0, 24, 0, 24, 8, 0, 16); // [🭜] UPPER LEFT BLOCK DIAGONAL LOWER MIDDLE LEFT TO UPPER MIDDLE RIGHT
            Wedge(0x1FB5D, 0, 0, 24, 0, 24, 16, 12, 24, 0, 24); // [🭝] UPPER LEFT BLOCK DIAGONAL LOWER CENTRE TO LOWER MIDDLE RIGHT
            Wedge(0x1FB5E, 0, 0, 24, 0, 24, 16, 0, 24); // [🭞] UPPER LEFT BLOCK DIAGONAL LOWER LEFT TO LOWER MIDDLE RIGHT
            Wedge(0x1FB5F, 0, 0, 24, 0, 24, 8, 12, 24, 0, 24); // [🭟] UPPER LEFT BLOCK DIAGONAL LOWER CENTRE TO UPPER MIDDLE RIGHT
            Wedge(0x1FB60, 0, 0, 24, 0, 24, 8, 0, 24); // [🭠] UPPER LEFT BLOCK DIAGONAL LOWER LEFT TO UPPER MIDDLE RIGHT
            Wedge(0x1FB61, 0, 0, 24, 0, 12, 24, 0, 24); // [🭡] UPPER LEFT BLOCK DIAGONAL LOWER CENTRE TO UPPER RIGHT
            Wedge(0x1FB62, 12, 0, 24, 0, 24, 8); // [🭢] UPPER RIGHT BLOCK DIAGONAL UPPER CENTRE TO UPPER MIDDLE RIGHT
            Wedge(0x1FB63, 0, 0, 24, 0, 24, 8); // [🭣] UPPER RIGHT BLOCK DIAGONAL UPPER LEFT TO UPPER MIDDLE RIGHT
            Wedge(0x1FB64, 12, 0, 24, 0, 24, 16); // [🭤] UPPER RIGHT BLOCK DIAGONAL UPPER CENTRE TO LOWER MIDDLE RIGHT
            Wedge(0x1FB65, 0, 0, 24, 0, 24, 16); // [🭥] UPPER RIGHT BLOCK DIAGONAL UPPER LEFT TO LOWER MIDDLE RIGHT
            Wedge(0x1FB66, 12, 0, 24, 0, 24, 24); // [🭦] UPPER RIGHT BLOCK DIAGONAL UPPER CENTRE TO LOWER RIGHT
            Wedge(0x1FB67, 0, 0, 24, 0, 24, 16, 0, 8); // [🭧] UPPER RIGHT BLOCK DIAGONAL UPPER MIDDLE LEFT TO LOWER MIDDLE RIGHT

            // U+1FB68..1FB6F: triangles meeting at the centre, one per edge, alone or three together.
            byte[] upper = { 0, 0, Units, 0, H, H }, right = { Units, 0, Units, Units, H, H };
            byte[] lower = { 0, Units, Units, Units, H, H }, left = { 0, 0, 0, Units, H, H };
            Shape Tri(byte[] p) => new(0, 0, 0, 0, 1f, p);
            map[0x1FB68] = new[] { Tri(upper), Tri(right), Tri(lower) };   // 🭨
            map[0x1FB69] = new[] { Tri(left), Tri(lower), Tri(right) };    // 🭩
            map[0x1FB6A] = new[] { Tri(upper), Tri(left), Tri(lower) };    // 🭪
            map[0x1FB6B] = new[] { Tri(left), Tri(upper), Tri(right) };    // 🭫
            map[0x1FB6C] = new[] { Tri(left) };                             // 🭬
            map[0x1FB6D] = new[] { Tri(upper) };                            // 🭭
            map[0x1FB6E] = new[] { Tri(right) };                            // 🭮
            map[0x1FB6F] = new[] { Tri(lower) };                            // 🭯

            // U+1FB70..1FB8B: the eighths Block Elements lacked, in eighths (3 units each).
            Shape Columns(int from, int to) => new((byte)(from * 3), 0, (byte)(to * 3), Units);
            Shape Rows(int from, int to) => new(0, (byte)(from * 3), Units, (byte)(to * 3));
            for (var n = 1; n <= 6; n++)
            {
                map[0x1FB70 + n - 1] = new[] { Columns(n, n + 1) };         // 🭰..🭵 vertical eighth n+1
                map[0x1FB76 + n - 1] = new[] { Rows(n, n + 1) };            // 🭶..🭻 horizontal eighth n+1
            }
            map[0x1FB7C] = new[] { Columns(0, 1), Rows(7, 8) };             // 🭼 left and lower
            map[0x1FB7D] = new[] { Columns(0, 1), Rows(0, 1) };             // 🭽 left and upper
            map[0x1FB7E] = new[] { Columns(7, 8), Rows(0, 1) };             // 🭾 right and upper
            map[0x1FB7F] = new[] { Columns(7, 8), Rows(7, 8) };             // 🭿 right and lower
            map[0x1FB80] = new[] { Rows(0, 1), Rows(7, 8) };                // 🮀 upper and lower
            map[0x1FB81] = new[] { Rows(0, 1), Rows(2, 3), Rows(4, 5), Rows(7, 8) }; // 🮁 eighths 1358
            int[] upperEighths = { 2, 3, 5, 6, 7 };                          // 🮂🮃🮄🮅🮆
            for (var i = 0; i < upperEighths.Length; i++)
                map[0x1FB82 + i] = new[] { Rows(0, upperEighths[i]) };
            for (var i = 0; i < upperEighths.Length; i++)                     // 🮇🮈🮉🮊🮋
                map[0x1FB87 + i] = new[] { Columns(8 - upperEighths[i], 8) };

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
