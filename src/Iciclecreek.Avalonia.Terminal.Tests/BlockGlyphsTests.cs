using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Block characters drawn as rectangles snapped to the cell instead of from the font.
/// </summary>
/// <remarks>
/// <para>notcurses draws pictures out of half blocks, quadrants and sextants. Drawn from the font,
/// each glyph kept the font's metrics rather than the cell's, so neighbouring blocks overlapped by a
/// fraction or left a hairline between them -- visible seams through every such picture. The pixel
/// result was checked with a Skia render; what is asserted here is the geometry table and the run
/// split that routes these cells to it.</para>
/// </remarks>
[TestFixture]
public class BlockGlyphsTests
{
    private const string Esc = "\u001b";

    private static int Area(int codePoint)
    {
        Assert.That(BlockGlyphs.TryGet(codePoint, out var shapes), Is.True, $"U+{codePoint:X4} should be a block");
        var area = 0;
        foreach (var s in shapes)
            area += (s.X1 - s.X0) * (s.Y1 - s.Y0);
        return area;
    }

    private const int Cell = BlockGlyphs.Units * BlockGlyphs.Units;

    [Test]
    public void Halves_cover_exactly_half_the_cell_on_the_right_side()
    {
        Assert.That(BlockGlyphs.TryGet(0x2580, out var upper), Is.True);    // ▀
        Assert.That(upper, Is.EqualTo(new[] { new BlockGlyphs.Shape(0, 0, 24, 12) }));
        Assert.That(BlockGlyphs.TryGet(0x2584, out var lower), Is.True);    // ▄
        Assert.That(lower, Is.EqualTo(new[] { new BlockGlyphs.Shape(0, 12, 24, 24) }));
        Assert.That(BlockGlyphs.TryGet(0x258C, out var left), Is.True);     // ▌
        Assert.That(left, Is.EqualTo(new[] { new BlockGlyphs.Shape(0, 0, 12, 24) }));
        Assert.That(BlockGlyphs.TryGet(0x2590, out var right), Is.True);    // ▐
        Assert.That(right, Is.EqualTo(new[] { new BlockGlyphs.Shape(12, 0, 24, 24) }));
    }

    [Test]
    public void Eighths_grow_from_the_bottom_and_shrink_from_the_right()
    {
        for (var eighths = 1; eighths <= 7; eighths++)
        {
            Assert.That(Area(0x2580 + eighths), Is.EqualTo(Cell * eighths / 8), $"lower {eighths}/8");
            Assert.That(Area(0x2590 - eighths), Is.EqualTo(Cell * eighths / 8), $"left {eighths}/8");
        }
    }

    [Test]
    public void Quadrants_cover_one_to_three_quarters()
    {
        Assert.That(Area(0x2596), Is.EqualTo(Cell / 4));        // ▖
        Assert.That(Area(0x259A), Is.EqualTo(Cell / 2));        // ▚
        Assert.That(Area(0x2599), Is.EqualTo(Cell * 3 / 4));    // ▙

        Assert.That(BlockGlyphs.TryGet(0x2598, out var upperLeft), Is.True);   // ▘
        Assert.That(upperLeft, Is.EqualTo(new[] { new BlockGlyphs.Shape(0, 0, 12, 12) }));
    }

    [Test]
    public void Shades_fill_the_cell_at_a_fraction()
    {
        Assert.That(BlockGlyphs.TryGet(0x2591, out var light), Is.True);
        Assert.That(light[0].Alpha, Is.EqualTo(0.25f));
        Assert.That(BlockGlyphs.TryGet(0x2593, out var dark), Is.True);
        Assert.That(dark[0].Alpha, Is.EqualTo(0.75f));
    }

    /// <summary>
    /// Sextants skip the two patterns Unicode already had as half blocks, so the codepoint is not
    /// simply the bit pattern. The ends and both sides of each gap pin the mapping; names are from
    /// UnicodeData.txt, against which all 60 were also checked when this was written.
    /// </summary>
    [TestCase(0x1FB00, new[] { 1 })]                  // SEXTANT-1
    [TestCase(0x1FB13, new[] { 3, 5 })]               // SEXTANT-35, just before the ▌ (135) gap
    [TestCase(0x1FB14, new[] { 2, 3, 5 })]            // SEXTANT-235, just after it
    [TestCase(0x1FB27, new[] { 1, 4, 6 })]            // SEXTANT-146, just before the ▐ (246) gap
    [TestCase(0x1FB28, new[] { 1, 2, 4, 6 })]         // SEXTANT-1246, just after it
    [TestCase(0x1FB3B, new[] { 2, 3, 4, 5, 6 })]      // SEXTANT-23456, the last
    public void Sextants_follow_the_unicode_numbering(int codePoint, int[] sextants)
    {
        Assert.That(BlockGlyphs.TryGet(codePoint, out var shapes), Is.True);

        var expected = new List<BlockGlyphs.Shape>();
        foreach (var n in sextants)
        {
            var col = (n - 1) % 2;
            var row = (n - 1) / 2;
            expected.Add(new BlockGlyphs.Shape((byte)(col * 12), (byte)(row * 8),
                                               (byte)((col + 1) * 12), (byte)((row + 1) * 8)));
        }

        Assert.That(shapes, Is.EqualTo(expected));
    }

    /// <summary>
    /// notcurses stacks wedges into two-row shapes, so a wedge that reaches its cell's bottom edge
    /// must reach it exactly -- falling short was a band through every such shape.
    /// </summary>
    [Test]
    public void Wedges_are_polygons_reaching_the_cell_edges()
    {
        Assert.That(BlockGlyphs.TryGet(0x1FB3C, out var lowerLeft), Is.True);   // 🬼
        Assert.That(lowerLeft.Single().Points, Is.EqualTo(new byte[] { 0, 16, 0, 24, 12, 24 }),
                    "lower middle left to lower centre");

        Assert.That(BlockGlyphs.TryGet(0x1FB52, out var upperRight), Is.True);  // 🭒
        Assert.That(upperRight.Single().Points, Is.EqualTo(new byte[] { 0, 0, 24, 0, 24, 24, 12, 24, 0, 16 }));
    }

    [Test]
    public void Every_codepoint_in_the_covered_ranges_has_a_shape()
    {
        foreach (var (from, to) in new[] { (0x2580, 0x259F), (0x1FB00, 0x1FB8B) })
            for (var cp = from; cp <= to; cp++)
                Assert.That(BlockGlyphs.TryGet(cp, out var shapes) && shapes.Length > 0, Is.True, $"U+{cp:X4}");
    }

    [Test]
    public void Triangles_meet_at_the_centre_and_eighths_are_eighths()
    {
        Assert.That(BlockGlyphs.TryGet(0x1FB6D, out var upper), Is.True);       // 🭭
        Assert.That(upper.Single().Points, Is.EqualTo(new byte[] { 0, 0, 24, 0, 12, 12 }));
        Assert.That(BlockGlyphs.TryGet(0x1FB68, out var threeQuarters), Is.True); // 🭨
        Assert.That(threeQuarters, Has.Length.EqualTo(3));

        Assert.That(Area(0x1FB82), Is.EqualTo(Cell / 4), "🮂 upper one quarter");
        Assert.That(Area(0x1FB8B), Is.EqualTo(Cell * 7 / 8), "🮋 right seven eighths");
        Assert.That(BlockGlyphs.TryGet(0x1FB70, out var vertical), Is.True);     // 🭰 vertical eighth 2
        Assert.That(vertical.Single(), Is.EqualTo(new BlockGlyphs.Shape(3, 0, 6, 24)));
    }

    [Test]
    public void Ordinary_characters_are_not_blocks()
    {
        Assert.That(BlockGlyphs.IsBlock('A'), Is.False);
        Assert.That(BlockGlyphs.IsBlock(0x256D), Is.False, "the arcs are still drawn from the font");
        Assert.That(BlockGlyphs.IsBlock(0x2571), Is.False, "and so are the diagonals");
        Assert.That(BlockGlyphs.IsBlock(0x1FB8C), Is.False, "the shaded halves after the eighths are not covered");
        Assert.That(BlockGlyphs.TryGet('A', out _), Is.False);
    }

    /// <summary>
    /// Box-drawing lines are drawn from the table: a line glyph from the font reaches a pixel past
    /// its cell, and a picture replacing the cell is clipped to the cell, which left that pixel as a
    /// sliver wherever a dialog's border had been.
    /// </summary>
    [Test]
    public void Box_drawing_lines_are_strokes_along_the_centre_lines()
    {
        for (var cp = 0x2500; cp <= 0x257F; cp++)
        {
            var fromFont = cp is >= 0x256D and <= 0x2573;
            Assert.That(BlockGlyphs.IsBlock(cp), Is.EqualTo(!fromFont), $"U+{cp:X4}");
            if (!fromFont)
                Assert.That(BlockGlyphs.TryGet(cp, out var shapes) && shapes.Length > 0, Is.True, $"U+{cp:X4}");
        }

        Assert.That(BlockGlyphs.TryGet(0x2500, out var horizontal), Is.True);              // ─
        Assert.That(horizontal, Is.EquivalentTo(new[]
        {
            new BlockGlyphs.Shape(0, 11, 13, 13), new BlockGlyphs.Shape(11, 11, 24, 13),
        }), "two light arms meeting at the centre, edge to edge");

        Assert.That(BlockGlyphs.TryGet(0x250F, out var heavyCorner), Is.True);             // ┏
        Assert.That(heavyCorner, Is.EquivalentTo(new[]
        {
            new BlockGlyphs.Shape(9, 9, 24, 15), new BlockGlyphs.Shape(9, 9, 15, 24),
        }), "heavy arms six units wide, overlapping at the corner");

        Assert.That(BlockGlyphs.TryGet(0x2551, out var doubleVertical), Is.True);          // ║
        Assert.That(doubleVertical.Select(s => (s.X0, s.X1)), Is.EquivalentTo(new[] { (8, 10), (14, 16) }),
            "two light strokes four units apart");

        Assert.That(BlockGlyphs.TryGet(0x256C, out var doubleCross), Is.True);             // ╬
        Assert.That(doubleCross, Has.Length.EqualTo(8), "every arm's two strokes stop at the gap");
        Assert.That(doubleCross.All(s => s.X1 <= 10 || s.X0 >= 14 || s.Y1 <= 10 || s.Y0 >= 14), Is.True,
            "nothing is drawn in the centre gap");

        Assert.That(BlockGlyphs.TryGet(0x2504, out var dashes), Is.True);                  // ┄
        Assert.That(dashes, Has.Length.EqualTo(3));
        Assert.That(dashes[0].X0, Is.Zero);
        Assert.That(dashes[2].X1, Is.EqualTo(24), "the dashes reach the cell edges so lines continue across cells");
    }

    /// <summary>
    /// The Powerline arrows are private-use, so a font without them fell back to an icon font with
    /// something else at those codepoints: Consolas drew a small curl between oh-my-posh's segments.
    /// </summary>
    [Test]
    public void Powerline_arrows_span_the_full_cell_height()
    {
        for (var cp = 0xE0B0; cp <= 0xE0B3; cp++)
        {
            Assert.That(BlockGlyphs.TryGet(cp, out var shapes), Is.True, $"U+{cp:X4}");
            var points = shapes.Single().Points!;
            var ys = points.Where((_, i) => i % 2 == 1).ToArray();
            Assert.That(ys.Min(), Is.Zero, $"U+{cp:X4} reaches the top edge");
            Assert.That(ys.Max(), Is.EqualTo(24), $"U+{cp:X4} reaches the bottom edge");
        }

        Assert.That(BlockGlyphs.TryGet(0xE0B0, out var right), Is.True);
        Assert.That(right.Single().Points, Is.EqualTo(new byte[] { 0, 0, 24, 12, 0, 24 }),
                    "flat against the left edge, pointing at the right edge's centre");
        Assert.That(BlockGlyphs.TryGet(0xE0B2, out var left), Is.True);
        Assert.That(left.Single().Points, Is.EqualTo(new byte[] { 24, 0, 0, 12, 24, 24 }));

        Assert.That(BlockGlyphs.IsBlock(0xE0AF), Is.False);
        Assert.That(BlockGlyphs.IsBlock(0xE0B4), Is.False, "the rounded separators are still drawn from the font");
    }

    // ------------------------------------------------------------- the run split

    /// <summary>The prompt from the issue: each arrow is its own run, drawn from the table.</summary>
    [AvaloniaTest]
    public void Powerline_arrows_in_a_prompt_are_drawn_from_the_table()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        try
        {
            var view = control.View();
            view.Terminal.Write($"{Esc}[44m main {Esc}[34;42m{Esc}[30m dirty {Esc}[0;32m{Esc}[0m");

            var group = new DrawingGroup();
            using (var context = group.Open())
                view.Render(context);

            var runs = (List<TerminalView.CachedTextRun>)view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY]!.Cache!;
            var arrows = runs.Where(r => r.IsBlockArt).ToList();

            Assert.That(arrows.Select(r => r.StartX), Is.EqualTo(new[] { 6, 14 }));
            Assert.That(arrows.All(r => r.Blocks!.SequenceEqual(new[] { 0xE0B0 })), Is.True);
            Assert.That(arrows[0].Background, Is.Not.Null, "the first arrow sits on the green segment's fill");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Block_cells_get_a_run_of_their_own_drawn_from_the_table()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        try
        {
            var view = control.View();
            view.Terminal.Write($"ab▀▄\U0001FB00cd");

            var group = new DrawingGroup();
            using (var context = group.Open())
                view.Render(context);

            var runs = (List<TerminalView.CachedTextRun>)view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY]!.Cache!;
            var block = runs.Single(r => r.IsBlockArt);

            Assert.That(block.StartX, Is.EqualTo(2));
            Assert.That(block.CellCount, Is.EqualTo(3));
            Assert.That(block.Blocks, Is.EqualTo(new[] { 0x2580, 0x2584, 0x1FB00 }));
            Assert.That(block.Text, Is.Null, "a block run is never shaped");
            Assert.That(block.Glyphs, Is.Null);

            Assert.That(runs.Where(r => !r.IsBlockArt && (r.Text is not null || r.Glyphs is not null))
                            .Select(r => r.StartX), Is.EqualTo(new[] { 0, 5 }),
                        "the text either side stays in runs of its own");
        }
        finally { window.Close(); }
    }
}
