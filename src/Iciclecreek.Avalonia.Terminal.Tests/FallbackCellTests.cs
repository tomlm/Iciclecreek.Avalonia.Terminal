using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// A character the primary font lacks is drawn in a run of its own, fitted to its cell.
/// </summary>
/// <remarks>
/// <para>It used to cost the whole run the glyph fast path, and FormattedText laid the run out with each
/// font's own advances: U+23F5 from Segoe UI Symbol is nearly two Consolas cells wide, so every column
/// after it on the line moved right, and the fallback font's taller line moved the run's baseline down.
/// The pixels were checked with a Skia render; what is asserted here is the run split and the run's
/// geometry, as FontFallbackTests asserts the chain rather than what a machine's fonts draw.</para>
/// </remarks>
[TestFixture]
public class FallbackCellTests
{
    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    /// <summary>
    /// The headless platform's font maps nothing, so the split is driven through the probe: every
    /// codepoint but U+0378 counts as one the primary font has.
    /// </summary>
    [SetUp]
    public void UseProbe() => TerminalView.PrimaryGlyphProbe = cp => cp != 0x0378;

    [TearDown]
    public void ClearProbe() => TerminalView.PrimaryGlyphProbe = null;

    private static List<TerminalView.CachedTextRun> TextRuns(TerminalView view)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
            view.Render(context);

        var runs = (List<TerminalView.CachedTextRun>)view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY]!.Cache!;
        return runs.Where(r => r.Text is not null || r.Glyphs is not null).ToList();
    }

    [AvaloniaTest]
    public void A_character_the_font_lacks_is_a_run_of_its_own()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("ab͸cd|");

            var runs = TextRuns(view);

            // The last run carries on through the blanks to the end of the line.
            Assert.That(runs.Select(r => r.StartX), Is.EqualTo(new[] { 0, 2, 3 }),
                "the text either side keeps its own runs, so nothing after the gap can drift");
            Assert.That(runs[0].CellCount, Is.EqualTo(2));
            Assert.That(runs[1].CellCount, Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Neighbouring_missing_characters_each_get_a_cell_of_their_own()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("͸͸ x");

            var runs = TextRuns(view);
            Assert.That(runs.Select(r => r.StartX), Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(runs[0].CellCount + runs[1].CellCount, Is.EqualTo(2));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Where the platform has a font for it, the fallback glyph is drawn on the grid: one cell's
    /// advance, the grid's baseline, and ink no wider than the cell.
    /// </summary>
    [AvaloniaTest]
    public void A_fallback_glyph_is_fitted_to_its_cell()
    {
        TerminalView.PrimaryGlyphProbe = null;
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("a⏵b");

            var runs = TextRuns(view);
            var fallback = runs.SingleOrDefault(r => r.StartX == 1);
            Assume.That(fallback?.CellCount, Is.EqualTo(1), "the primary font has U+23F5, so there is nothing to fall back from");
            Assume.That(fallback!.Glyphs, Is.Not.Null, "no font on this machine has U+23F5");

            var glyphs = fallback.Glyphs!;
            Assert.That(glyphs.GlyphInfos.Single().GlyphAdvance, Is.EqualTo(view.CharWidth).Within(1e-9));
            Assert.That(glyphs.FontRenderingEmSize, Is.LessThanOrEqualTo(view.FontSize));
            if (runs[0].Glyphs is { } beside)
                Assert.That(glyphs.BaselineOrigin.Y, Is.EqualTo(beside.BaselineOrigin.Y).Within(1e-9),
                    "on the same baseline as the text beside it");
            if (glyphs.BuildGeometry() is { } ink)
                Assert.That(ink.Bounds.Width, Is.LessThanOrEqualTo(view.CharWidth + 1e-6));
        }
        finally { window.Close(); }
    }
}
