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

    [AvaloniaTest]
    public void A_fractional_scale_draws_small()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}]66;n=1:d=2;h{St}");
            var scaled = ScaledGlyphs(view).Where(g => g.ScaleX is > 0.4 and < 0.6).ToList();

            Assert.That(scaled, Is.Not.Empty, "no glyph drew at 1/2x");
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
