using System;
using System.Collections.Generic;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// The block characters, and the Powerline arrows, drawn as rectangles and polygons rather than
    /// from the font.
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
        /// <remarks>
        /// The box-drawing lines (U+2500..257F) are in the table except the arcs and diagonals, which
        /// stay with the font. A line glyph from the font is drawn to the font's advance, not to the
        /// cell's whole device pixels, so it reaches a pixel past its cell: a picture that later
        /// replaces the cell is clipped to the cell and that pixel stays behind as a sliver.
        /// <para>The four Powerline arrows (U+E0B0..E0B3) are in it too. They are private-use, so a
        /// font without them falls back to whichever installed font puts something else at those
        /// codepoints -- on Windows an icon font, which drew a small curl where the arrow should join
        /// two coloured segments. Windows Terminal and xterm.js draw them themselves.</para>
        /// </remarks>
        public static bool IsBlock(int codePoint)
            => (codePoint >= 0x2580 && codePoint <= 0x259F) || (codePoint >= 0x1FB00 && codePoint <= 0x1FB8B)
               || (codePoint >= 0x2500 && codePoint <= 0x257F && Shapes.ContainsKey(codePoint))
               || (codePoint >= 0xE0B0 && codePoint <= 0xE0B3);

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

            BoxDrawing(map);
            Powerline(map);
            return map;
        }

        /// <summary>
        /// U+E0B0..E0B3, the Powerline arrows: a solid triangle pointing right and left, each spanning
        /// the cell's full height so it meets the segments either side edge to edge, and the thin
        /// chevrons of the same outline.
        /// </summary>
        /// <remarks>
        /// A chevron is one concave polygon -- the triangle's two slanted sides with a copy of each
        /// moved 2 units in from its open edge -- rather than two strokes, so the point where the arms
        /// meet is filled once and not antialiased twice.
        /// </remarks>
        private static void Powerline(Dictionary<int, Shape[]> map)
        {
            static Shape[] Polygon(params byte[] points) => new[] { new Shape(0, 0, 0, 0, 1f, points) };
            map[0xE0B0] = Polygon(0, 0, 24, 12, 0, 24);                       // right solid
            map[0xE0B1] = Polygon(0, 0, 24, 12, 0, 24, 0, 22, 20, 12, 0, 2);  // right chevron
            map[0xE0B2] = Polygon(24, 0, 0, 12, 24, 24);                      // left solid
            map[0xE0B3] = Polygon(24, 0, 0, 12, 24, 24, 24, 22, 4, 12, 24, 2); // left chevron
        }

        // Arm weights of a box-drawing glyph.
        private const byte None = 0, Light = 1, Heavy = 2;

        /// <summary>
        /// U+2500..257F Box Drawing, as strokes along the cell's centre lines: light strokes 2 units
        /// wide, heavy 6, double two light strokes 4 apart. An arm runs from its cell edge to the far
        /// side of the centre stroke, so the arms of a joint overlap into one shape. The arcs
        /// (U+256D..2570) and the diagonals (U+2571..2573) are left to the font.
        /// </summary>
        /// <remarks>
        /// At small cells a stroke is thinner than a device pixel; the renderers give a stroke that
        /// snaps to nothing one pixel, so a line is never lost and a double stays two lines with a gap.
        /// </remarks>
        private static void BoxDrawing(Dictionary<int, Shape[]> map)
        {
            // Horizontal and vertical strokes between two coordinates along the other axis.
            static Shape H(int y0, int y1, int x0, int x1) => new((byte)x0, (byte)y0, (byte)x1, (byte)y1);
            static Shape V(int x0, int x1, int y0, int y1) => new((byte)x0, (byte)y0, (byte)x1, (byte)y1);

            // A glyph from its four arms (left, right, up, down), each none, light or heavy.
            static Shape[] Arms(byte left, byte right, byte up, byte down)
            {
                var shapes = new List<Shape>(4);
                static (int Near, int Far) Stroke(byte weight) => weight == Heavy ? (9, 15) : (11, 13);
                if (left != None) { var (n, f) = Stroke(left); shapes.Add(H(n, f, 0, f)); }
                if (right != None) { var (n, f) = Stroke(right); shapes.Add(H(n, f, n, Units)); }
                if (up != None) { var (n, f) = Stroke(up); shapes.Add(V(n, f, 0, f)); }
                if (down != None) { var (n, f) = Stroke(down); shapes.Add(V(n, f, n, Units)); }
                return shapes.ToArray();
            }

            // U+2500..254B in Unicode order: every light/heavy combination of the four arms.
            (byte L, byte R, byte U, byte D)[] arms =
            {
                (Light, Light, None, None),   // ─ 2500
                (Heavy, Heavy, None, None),   // ━
                (None, None, Light, Light),   // │
                (None, None, Heavy, Heavy),   // ┃
                default, default, default, default, default, default, default, default, // dashes, below
                (None, Light, None, Light),   // ┌ 250C
                (None, Heavy, None, Light),   // ┍
                (None, Light, None, Heavy),   // ┎
                (None, Heavy, None, Heavy),   // ┏
                (Light, None, None, Light),   // ┐
                (Heavy, None, None, Light),   // ┑
                (Light, None, None, Heavy),   // ┒
                (Heavy, None, None, Heavy),   // ┓
                (None, Light, Light, None),   // └
                (None, Heavy, Light, None),   // ┕
                (None, Light, Heavy, None),   // ┖
                (None, Heavy, Heavy, None),   // ┗
                (Light, None, Light, None),   // ┘
                (Heavy, None, Light, None),   // ┙
                (Light, None, Heavy, None),   // ┚
                (Heavy, None, Heavy, None),   // ┛
                (None, Light, Light, Light),  // ├ 251C
                (None, Heavy, Light, Light),  // ┝
                (None, Light, Heavy, Light),  // ┞
                (None, Light, Light, Heavy),  // ┟
                (None, Light, Heavy, Heavy),  // ┠
                (None, Heavy, Heavy, Light),  // ┡
                (None, Heavy, Light, Heavy),  // ┢
                (None, Heavy, Heavy, Heavy),  // ┣
                (Light, None, Light, Light),  // ┤ 2524
                (Heavy, None, Light, Light),  // ┥
                (Light, None, Heavy, Light),  // ┦
                (Light, None, Light, Heavy),  // ┧
                (Light, None, Heavy, Heavy),  // ┨
                (Heavy, None, Heavy, Light),  // ┩
                (Heavy, None, Light, Heavy),  // ┪
                (Heavy, None, Heavy, Heavy),  // ┫
                (Light, Light, None, Light),  // ┬ 252C
                (Heavy, Light, None, Light),  // ┭
                (Light, Heavy, None, Light),  // ┮
                (Heavy, Heavy, None, Light),  // ┯
                (Light, Light, None, Heavy),  // ┰
                (Heavy, Light, None, Heavy),  // ┱
                (Light, Heavy, None, Heavy),  // ┲
                (Heavy, Heavy, None, Heavy),  // ┳
                (Light, Light, Light, None),  // ┴ 2534
                (Heavy, Light, Light, None),  // ┵
                (Light, Heavy, Light, None),  // ┶
                (Heavy, Heavy, Light, None),  // ┷
                (Light, Light, Heavy, None),  // ┸
                (Heavy, Light, Heavy, None),  // ┹
                (Light, Heavy, Heavy, None),  // ┺
                (Heavy, Heavy, Heavy, None),  // ┻
                (Light, Light, Light, Light), // ┼ 253C
                (Heavy, Light, Light, Light), // ┽
                (Light, Heavy, Light, Light), // ┾
                (Heavy, Heavy, Light, Light), // ┿
                (Light, Light, Heavy, Light), // ╀
                (Light, Light, Light, Heavy), // ╁
                (Light, Light, Heavy, Heavy), // ╂
                (Heavy, Light, Heavy, Light), // ╃
                (Light, Heavy, Heavy, Light), // ╄
                (Heavy, Light, Light, Heavy), // ╅
                (Light, Heavy, Light, Heavy), // ╆
                (Heavy, Heavy, Heavy, Light), // ╇
                (Heavy, Heavy, Light, Heavy), // ╈
                (Heavy, Light, Heavy, Heavy), // ╉
                (Light, Heavy, Heavy, Heavy), // ╊
                (Heavy, Heavy, Heavy, Heavy), // ╋ 254B
            };
            for (var i = 0; i < arms.Length; i++)
            {
                var (l, r, u, d) = arms[i];
                if (l != None || r != None || u != None || d != None)
                    map[0x2500 + i] = Arms(l, r, u, d);
            }

            // Dashed lines: the stroke cut into equal dashes with gaps between them.
            static Shape[] Dashes(bool horizontal, byte weight, int count)
            {
                var (n, f) = weight == Heavy ? (9, 15) : (11, 13);
                var period = Units / count;
                var gap = count == 2 ? 4 : 2;
                var shapes = new Shape[count];
                for (var i = 0; i < count; i++)
                {
                    var from = i * period + (i == 0 ? 0 : gap / 2);
                    var to = (i + 1) * period - (i == count - 1 ? 0 : gap - gap / 2);
                    shapes[i] = horizontal ? H(n, f, from, to) : V(n, f, from, to);
                }
                return shapes;
            }
            map[0x2504] = Dashes(true, Light, 3);   // ┄
            map[0x2505] = Dashes(true, Heavy, 3);   // ┅
            map[0x2506] = Dashes(false, Light, 3);  // ┆
            map[0x2507] = Dashes(false, Heavy, 3);  // ┇
            map[0x2508] = Dashes(true, Light, 4);   // ┈
            map[0x2509] = Dashes(true, Heavy, 4);   // ┉
            map[0x250A] = Dashes(false, Light, 4);  // ┊
            map[0x250B] = Dashes(false, Heavy, 4);  // ┋
            map[0x254C] = Dashes(true, Light, 2);   // ╌
            map[0x254D] = Dashes(true, Heavy, 2);   // ╍
            map[0x254E] = Dashes(false, Light, 2);  // ╎
            map[0x254F] = Dashes(false, Heavy, 2);  // ╏

            // Double lines, U+2550..256C: the two strokes at 8..10 and 14..16. At a joint the outer
            // stroke of each arm runs through and the inner one stops at the gap, as the glyphs do.
            map[0x2550] = new[] { H(8, 10, 0, 24), H(14, 16, 0, 24) };                                     // ═
            map[0x2551] = new[] { V(8, 10, 0, 24), V(14, 16, 0, 24) };                                     // ║
            map[0x2552] = new[] { V(11, 13, 8, 24), H(8, 10, 11, 24), H(14, 16, 11, 24) };                 // ╒
            map[0x2553] = new[] { V(8, 10, 11, 24), V(14, 16, 11, 24), H(11, 13, 8, 24) };                 // ╓
            map[0x2554] = new[] { V(8, 10, 8, 24), V(14, 16, 14, 24), H(8, 10, 8, 24), H(14, 16, 14, 24) }; // ╔
            map[0x2555] = new[] { V(11, 13, 8, 24), H(8, 10, 0, 13), H(14, 16, 0, 13) };                   // ╕
            map[0x2556] = new[] { V(8, 10, 11, 24), V(14, 16, 11, 24), H(11, 13, 0, 16) };                 // ╖
            map[0x2557] = new[] { V(14, 16, 8, 24), V(8, 10, 14, 24), H(8, 10, 0, 16), H(14, 16, 0, 10) }; // ╗
            map[0x2558] = new[] { V(11, 13, 0, 16), H(8, 10, 11, 24), H(14, 16, 11, 24) };                 // ╘
            map[0x2559] = new[] { V(8, 10, 0, 13), V(14, 16, 0, 13), H(11, 13, 8, 24) };                   // ╙
            map[0x255A] = new[] { V(8, 10, 0, 16), V(14, 16, 0, 10), H(14, 16, 8, 24), H(8, 10, 14, 24) }; // ╚
            map[0x255B] = new[] { V(11, 13, 0, 16), H(8, 10, 0, 13), H(14, 16, 0, 13) };                   // ╛
            map[0x255C] = new[] { V(8, 10, 0, 13), V(14, 16, 0, 13), H(11, 13, 0, 16) };                   // ╜
            map[0x255D] = new[] { V(14, 16, 0, 16), V(8, 10, 0, 10), H(14, 16, 0, 16), H(8, 10, 0, 10) }; // ╝
            map[0x255E] = new[] { V(11, 13, 0, 24), H(8, 10, 11, 24), H(14, 16, 11, 24) };                 // ╞
            map[0x255F] = new[] { V(8, 10, 0, 24), V(14, 16, 0, 24), H(11, 13, 14, 24) };                  // ╟
            map[0x2560] = new[] { V(8, 10, 0, 24), V(14, 16, 0, 10), V(14, 16, 14, 24), H(8, 10, 14, 24), H(14, 16, 14, 24) }; // ╠
            map[0x2561] = new[] { V(11, 13, 0, 24), H(8, 10, 0, 13), H(14, 16, 0, 13) };                   // ╡
            map[0x2562] = new[] { V(8, 10, 0, 24), V(14, 16, 0, 24), H(11, 13, 0, 10) };                   // ╢
            map[0x2563] = new[] { V(14, 16, 0, 24), V(8, 10, 0, 10), V(8, 10, 14, 24), H(8, 10, 0, 10), H(14, 16, 0, 10) }; // ╣
            map[0x2564] = new[] { H(8, 10, 0, 24), H(14, 16, 0, 24), V(11, 13, 14, 24) };                  // ╤
            map[0x2565] = new[] { H(11, 13, 0, 24), V(8, 10, 11, 24), V(14, 16, 11, 24) };                 // ╥
            map[0x2566] = new[] { H(8, 10, 0, 24), H(14, 16, 0, 10), H(14, 16, 14, 24), V(8, 10, 14, 24), V(14, 16, 14, 24) }; // ╦
            map[0x2567] = new[] { H(8, 10, 0, 24), H(14, 16, 0, 24), V(11, 13, 0, 10) };                   // ╧
            map[0x2568] = new[] { H(11, 13, 0, 24), V(8, 10, 0, 13), V(14, 16, 0, 13) };                   // ╨
            map[0x2569] = new[] { H(14, 16, 0, 24), H(8, 10, 0, 10), H(8, 10, 14, 24), V(8, 10, 0, 10), V(14, 16, 0, 10) }; // ╩
            map[0x256A] = new[] { V(11, 13, 0, 24), H(8, 10, 0, 24), H(14, 16, 0, 24) };                   // ╪
            map[0x256B] = new[] { H(11, 13, 0, 24), V(8, 10, 0, 24), V(14, 16, 0, 24) };                   // ╫
            map[0x256C] = new[]                                                                            // ╬
            {
                V(8, 10, 0, 10), V(8, 10, 14, 24), V(14, 16, 0, 10), V(14, 16, 14, 24),
                H(8, 10, 0, 10), H(8, 10, 14, 24), H(14, 16, 0, 10), H(14, 16, 14, 24),
            };

            // Half lines, U+2574..257F: one arm to the centre, and the two-weight pairs.
            map[0x2574] = new[] { H(11, 13, 0, 12) };                   // ╴
            map[0x2575] = new[] { V(11, 13, 0, 12) };                   // ╵
            map[0x2576] = new[] { H(11, 13, 12, 24) };                  // ╶
            map[0x2577] = new[] { V(11, 13, 12, 24) };                  // ╷
            map[0x2578] = new[] { H(9, 15, 0, 12) };                    // ╸
            map[0x2579] = new[] { V(9, 15, 0, 12) };                    // ╹
            map[0x257A] = new[] { H(9, 15, 12, 24) };                   // ╺
            map[0x257B] = new[] { V(9, 15, 12, 24) };                   // ╻
            map[0x257C] = new[] { H(11, 13, 0, 12), H(9, 15, 12, 24) }; // ╼
            map[0x257D] = new[] { V(11, 13, 0, 12), V(9, 15, 12, 24) }; // ╽
            map[0x257E] = new[] { H(9, 15, 0, 12), H(11, 13, 12, 24) }; // ╾
            map[0x257F] = new[] { V(9, 15, 0, 12), V(11, 13, 12, 24) }; // ╿
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
