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
    /// Stands in for the font lookup, which the headless platform cannot do: its font manager matches no
    /// font for a character and its stand-in font maps nothing. Everything after the lookup is the real path.
    /// </summary>
    private static void FallbackFontHas(TerminalView view, int codePoint, double inkLeftEm, double inkWidthEm)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var record = typeof(TerminalView).GetNestedType("FallbackGlyph", System.Reflection.BindingFlags.NonPublic)!;
        var glyph = Activator.CreateInstance(record, new Typeface(view.FontFamily).GlyphTypeface, (ushort)1, inkLeftEm, inkWidthEm);
        var known = (System.Collections.IDictionary)typeof(TerminalView).GetField("_fallbackGlyphs", flags)!.GetValue(view)!;
        known[(codePoint, FontStyle.Normal, FontWeight.Normal)] = glyph;
    }

    /// <summary>
    /// The fallback glyph is drawn on the grid: one cell's advance, the grid's baseline, and its ink centred
    /// in the cell, at a smaller size only if it would not otherwise fit. Widths are in cells; the headless
    /// cell is exactly FontSize wide, so a cell of ink is 1 em.
    /// </summary>
    [TestCase(1.5, 1.0)]
    [TestCase(1.0, 1.0)]
    [TestCase(0.5, 0.5)]
    [AvaloniaTest]
    public void A_fallback_glyph_is_fitted_to_its_cell(double inkCells, double drawnCells)
    {
        const double inkLeftEm = 0.1;
        TerminalView.PrimaryGlyphProbe = cp => cp != 0x23F5;
        var (view, window) = Realised();
        try
        {
            var inkWidthEm = inkCells * view.CharWidth / view.FontSize;
            FallbackFontHas(view, 0x23F5, inkLeftEm, inkWidthEm);

            view.Terminal.Write("a⏵b");

            var runs = TextRuns(view);
            var fallback = runs.Single(r => r.StartX == 1);
            Assert.That(fallback.CellCount, Is.EqualTo(1), "a character the font lacks is a run of its own");
            Assert.That(fallback.Glyphs, Is.Not.Null, "the fallback cell did not take the glyph path");

            var glyphs = fallback.Glyphs!;
            var info = glyphs.GlyphInfos.Single();
            var size = glyphs.FontRenderingEmSize;
            Assert.That(info.GlyphAdvance, Is.EqualTo(view.CharWidth).Within(1e-9));
            Assert.That(inkWidthEm * size, Is.EqualTo(drawnCells * view.CharWidth).Within(1e-9),
                "ink wider than the cell is shrunk to it, and ink that fits keeps the text's size");

            var baseline = (double)typeof(TerminalView)
                .GetField("_baseline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(view)!;
            Assert.That(baseline, Is.GreaterThan(0), "a baseline of 0 could not tell the grid's from a default");
            Assert.That(glyphs.BaselineOrigin.Y, Is.EqualTo(baseline).Within(1e-9), "on the grid's baseline");

            var inkLeft = info.GlyphOffset.X + inkLeftEm * size;
            var inkRight = inkLeft + inkWidthEm * size;
            Assert.That(inkLeft, Is.EqualTo(view.CharWidth - inkRight).Within(1e-9), "the ink is centred in the cell");
        }
        finally { window.Close(); }
    }
}
