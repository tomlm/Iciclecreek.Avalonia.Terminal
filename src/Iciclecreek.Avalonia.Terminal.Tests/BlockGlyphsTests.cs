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

    [Test]
    public void Ordinary_characters_are_not_blocks()
    {
        Assert.That(BlockGlyphs.IsBlock('A'), Is.False);
        Assert.That(BlockGlyphs.IsBlock(0x2500), Is.False, "box drawing lines are still drawn from the font");
        Assert.That(BlockGlyphs.IsBlock(0x1FB3C), Is.False, "the legacy-computing wedges after the sextants are not covered");
        Assert.That(BlockGlyphs.TryGet('A', out _), Is.False);
    }

    // ------------------------------------------------------------- the run split

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
