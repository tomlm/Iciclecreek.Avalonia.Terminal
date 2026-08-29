using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// What <see cref="TerminalView.Render"/> draws for OSC 66 sized text: the block's glyph under a
/// scale transform, after every row's background, aligned per the sizing — asserted by recording
/// the draw calls, the same technique the background tests use.
/// </summary>
[TestFixture]
public class SizedTextRenderingTests
{
    private const string Esc = "\u001b";
    private const string St = "\u001b\\";

    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    private static DrawingGroup Capture(TerminalView view)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            view.Render(context);
        }
        return group;
    }

    /// <summary>Every drawing in the frame with the scale its enclosing transforms multiply to.</summary>
    private static IEnumerable<(Drawing Drawing, double ScaleX, double OffsetY)> Flatten(
        DrawingGroup group, double scaleX = 1.0, double offsetY = 0.0)
    {
        foreach (var child in group.Children)
        {
            var sx = scaleX;
            var oy = offsetY;
            if (child is DrawingGroup g)
            {
                if (g.Transform is not null)
                {
                    var m = g.Transform.Value;
                    sx *= m.M11;
                    oy = oy * m.M22 + m.M32;
                }
                foreach (var inner in Flatten(g, sx, oy))
                    yield return inner;
            }
            else
            {
                yield return (child, sx, oy);
            }
        }
    }

    private static List<(GlyphRunDrawing Glyphs, double ScaleX, double OffsetY)> ScaledGlyphs(TerminalView view)
        => Flatten(Capture(view))
            .Where(d => d.Drawing is GlyphRunDrawing)
            .Select(d => ((GlyphRunDrawing)d.Drawing, d.ScaleX, d.OffsetY))
            .ToList();

    [AvaloniaTest]
    public void A_scaled_block_draws_its_glyph_under_the_scale_transform()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}]66;s=2;H{St}");
            var scaled = ScaledGlyphs(view).Where(g => g.ScaleX > 1.5).ToList();

            Assert.That(scaled, Is.Not.Empty, "no glyph drew at 2x — the block did not render scaled");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A fractional block is always s=1, so every cell it claims is one column wide and carries the
    /// SGR in force when it was printed. Text in FRONT of it, in the same SGR, is therefore the case
    /// that discriminates: the row pass collects width-1 cells by attribute, and a collector that
    /// does not stop at the run boundary walks straight through it and draws the whole line at base
    /// size. Written at column 0 this passes either way, which is why it used to be.
    /// </summary>
    [AvaloniaTest]
    public void A_fractional_scale_draws_small()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"small: {Esc}]66;n=1:d=2;h{St}");
            var scaled = ScaledGlyphs(view).Where(g => g.ScaleX is > 0.4 and < 0.6).ToList();

            Assert.That(scaled, Is.Not.Empty, "no glyph drew at 1/2x");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The same boundary, the other way round: the text in front of the block must still be drawn,
    /// and at base size. Stopping the run collector must SPLIT the line, not swallow the label.
    /// </summary>
    [AvaloniaTest]
    public void The_text_in_front_of_a_block_still_draws_at_base_size()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"small: {Esc}]66;n=1:d=2;h{St}");
            var label = ScaledGlyphs(view)
                .Where(g => g.ScaleX > 0.9 && g.Glyphs.GlyphRun is { } run
                            && run.Characters.ToString()!.Contains("small"));

            Assert.That(label, Is.Not.Empty, "the label in front of the block was lost");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The reason the pass is deferred: rows render top to bottom, and a block two rows tall
    /// must not have its lower half painted over by the next row's background. Ordering in the
    /// captured drawing list IS paint order.
    /// </summary>
    [AvaloniaTest]
    public void A_tall_block_paints_after_the_row_below_it()
    {
        var (view, window) = Realised();
        try
        {
            // A block on row 0, and a red-background run on row 1 sharing its columns' region.
            view.Terminal.Write($"{Esc}]66;s=2;H{St}");
            view.Terminal.Write($"{Esc}[2;1H{Esc}[41m   {Esc}[0m");

            var frame = Flatten(Capture(view)).ToList();
            var glyphAt = frame.FindLastIndex(d => d.Drawing is GlyphRunDrawing && d.ScaleX > 1.5);
            var lastFillAt = frame.FindLastIndex(d =>
                d.Drawing is GeometryDrawing { Brush: ISolidColorBrush b } && b.Color.R > 100 && b.Color.G < 50);

            Assert.That(glyphAt, Is.GreaterThan(-1), "scaled glyph missing");
            Assert.That(lastFillAt, Is.GreaterThan(-1), "red fill missing");
            Assert.That(glyphAt, Is.GreaterThan(lastFillAt),
                "the block painted before the lower row's background, which will overpaint its bottom half");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Vertical_alignment_moves_the_glyph_down()
    {
        var (view, window) = Realised();
        try
        {
            // Same fractional block, top-aligned then bottom-aligned; the bottom one must land lower.
            view.Terminal.Write($"{Esc}]66;n=1:d=2:v=0;a{St}");
            var top = ScaledGlyphs(view).Where(g => g.ScaleX < 0.9).Select(g => g.OffsetY).First();

            view.Terminal.Write($"{Esc}c");
            view.Terminal.Write($"{Esc}]66;n=1:d=2:v=1;a{St}");
            var bottom = ScaledGlyphs(view).Where(g => g.ScaleX < 0.9).Select(g => g.OffsetY).First();

            Assert.That(bottom, Is.GreaterThan(top));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_normal_pass_does_not_also_draw_the_block_text()
    {
        // The anchor cell's Width spans the whole block; drawn by the normal run path it would
        // appear at base size in the corner. Exactly one glyph draw for the block's character.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}]66;s=2;Z{St}");
            var zDraws = ScaledGlyphs(view)
                .Where(g => g.Glyphs.GlyphRun is { } run && run.Characters.ToString()!.Contains('Z'))
                .ToList();

            Assert.That(zDraws.Count(g => g.ScaleX > 1.5), Is.EqualTo(1), "the block glyph drew more or less than once");
            Assert.That(zDraws.Count(g => g.ScaleX < 1.5), Is.EqualTo(0),
                "the Z also drew at base scale — the normal pass did not skip the block");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A block whose anchor has scrolled above the top of the viewport must be CLIPPED, not lost.
    /// The rows it covers are blank in the buffer — SkipCellsCoveredFromAbove steered text around
    /// them — so if the deferred pass is fed only from rows inside the viewport, scrolling one line
    /// through any output holding a tall heading blanks the heading.
    /// </summary>
    [AvaloniaTest]
    public void A_tall_block_anchored_above_the_viewport_still_draws()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}]66;s=2;H{St}");

            // Park on the last row and feed a newline: the screen scrolls by one, so the block's
            // anchor is now the row immediately ABOVE the viewport and only its lower half is on
            // screen.
            view.Terminal.Write($"{Esc}[{view.Terminal.Rows};1H\n");

            Assert.That(view.Terminal.Buffer.ViewportY, Is.EqualTo(1), "the buffer did not scroll");
            Assert.That(ScaledGlyphs(view).Count(g => g.ScaleX > 1.5), Is.EqualTo(1),
                "the block vanished rather than being clipped to the top of the viewport");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The deferred pass is the only thing that paints inside a run — the row pass skipped every
    /// column of it — so a space's background is this pass's to draw. Skipping the cell outright
    /// left an unpainted notch between the words of a coloured heading.
    /// </summary>
    [AvaloniaTest]
    public void A_space_inside_a_block_keeps_its_background()
    {
        var (view, window) = Realised();
        try
        {
            // w=0, so each grapheme is its own block: 'A', ' ', 'B', three of them in red.
            view.Terminal.Write($"{Esc}[41m{Esc}]66;s=2;A B{St}{Esc}[0m");

            var reds = Flatten(Capture(view))
                .Where(d => d.Drawing is GeometryDrawing { Brush: ISolidColorBrush b }
                            && b.Color.R > 100 && b.Color.G < 50)
                .ToList();

            Assert.That(reds.Count, Is.EqualTo(3),
                "the space's own block went unfilled — the heading renders with a notch in it");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_second_render_still_draws_the_block()
    {
        // Sized lines bypass the run cache; a stale cache would drop the block on replay.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}]66;s=2;H{St}");
            Assert.That(ScaledGlyphs(view).Count(g => g.ScaleX > 1.5), Is.EqualTo(1));
            Assert.That(ScaledGlyphs(view).Count(g => g.ScaleX > 1.5), Is.EqualTo(1),
                "the replayed frame lost the block");
        }
        finally { window.Close(); }
    }
}
