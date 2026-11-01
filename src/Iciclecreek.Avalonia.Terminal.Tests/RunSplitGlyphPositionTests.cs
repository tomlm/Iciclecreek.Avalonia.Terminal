using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// A glyph is drawn at its column, wherever the line happens to be split into runs.
/// </summary>
/// <remarks>
/// <para>Each run used to draw its glyphs from its snapped origin, while the glyphs inside it step by the
/// unsnapped cell width. With a fractional cell width (Consolas at 13px is 7.15px) a colour change in the
/// middle of a line moved every glyph after it by up to half a pixel, so a line jittered while an
/// application re-coloured part of it -- Claude Code highlighting its own mouse selection, for one.</para>
/// <para>Backgrounds stay snapped, so neighbouring cells still meet on a whole pixel; only where the
/// glyphs go is asserted here.</para>
/// </remarks>
[TestFixture]
public class RunSplitGlyphPositionTests
{
    private const string Esc = "\u001b";

    /// <summary>
    /// The headless font's cell is exactly FontSize wide, so a fractional size is what gives the fractional
    /// cell a real font has. At 13.3 the runs at columns 2 and 5 snap 0.4 and 0.5 pixels away from them.
    /// </summary>
    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "", FontSize = 13.3 };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    /// <summary>
    /// The headless platform's font maps nothing, so without the probe every cell is a fallback run of
    /// its own and there is no run for a colour change to split.
    /// </summary>
    [SetUp]
    public void UseProbe() => TerminalView.PrimaryGlyphProbe = _ => true;

    [TearDown]
    public void ClearProbe() => TerminalView.PrimaryGlyphProbe = null;

    /// <summary>
    /// The x of every glyph the frame draws, through the transforms around it, left to right.
    /// </summary>
    private static List<double> GlyphXs(TerminalView view)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
            view.Render(context);

        var xs = new List<double>();
        Collect(group, Matrix.Identity, xs);
        xs.Sort();
        return xs;
    }

    private static void Collect(DrawingGroup group, Matrix parent, List<double> xs)
    {
        var matrix = group.Transform is { } transform ? transform.Value * parent : parent;
        foreach (var child in group.Children)
        {
            if (child is DrawingGroup inner)
            {
                Collect(inner, matrix, xs);
            }
            else if (child is GlyphRunDrawing { GlyphRun: { } run })
            {
                var x = run.BaselineOrigin.X;
                foreach (var info in run.GlyphInfos)
                {
                    xs.Add(matrix.Transform(new Point(x + info.GlyphOffset.X, 0)).X);
                    x += info.GlyphAdvance;
                }
            }
        }
    }

    [AvaloniaTest]
    public void A_colour_change_mid_line_does_not_move_the_glyphs_after_it()
    {
        var (view, window) = Realised();
        try
        {
            Assume.That(view.CharWidth % 1, Is.Not.EqualTo(0).Within(1e-9),
                "a whole-pixel cell width snaps to itself, so there would be nothing to move");

            view.Terminal.Write("abcdefgh");
            var whole = GlyphXs(view);

            view.Terminal.Write($"\r{Esc}[0mab{Esc}[7mcde{Esc}[0mfgh");
            var split = GlyphXs(view);

            var runs = (List<TerminalView.CachedTextRun>)view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY]!.Cache!;
            Assume.That(runs.Count(r => r.Text is not null || r.Glyphs is not null), Is.GreaterThanOrEqualTo(3),
                "the inverse cells are not a run of their own, so nothing was split");

            Assert.That(whole, Has.Count.EqualTo(view.Terminal.Cols), "one glyph per cell, the trailing blanks included");
            Assert.That(split, Is.EqualTo(whole).Within(1e-6),
                "the inverse run in the middle must not shift any glyph on the line");
            Assert.That(whole[7], Is.EqualTo(7 * view.CharWidth).Within(1e-6),
                "and each glyph sits at its column");
        }
        finally { window.Close(); }
    }
}
