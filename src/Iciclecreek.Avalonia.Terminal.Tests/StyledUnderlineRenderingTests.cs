using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// What the DrawingContext path decides to draw for a styled underline.
/// </summary>
/// <remarks>
/// <para>These exist because of a bug the whole suite missed. Underline used to be drawn through
/// Avalonia's <c>TextDecorations</c>; taking it off that path and drawing it by hand — Avalonia has
/// no curly decoration — meant wiring the new drawing into BOTH places a line is painted. A line is
/// painted by the build path on the frame it changes and by the cached replay afterwards, and only
/// the replay was wired. Every newly written underline was missing until something else happened to
/// invalidate the line.</para>
///
/// <para>324 tests passed while that pane drew no underlines at all, because nothing asserted on
/// decorations here. A side-by-side against the Skia renderer is what caught it. These assert the
/// run list, which is where the decision is observable.</para>
/// </remarks>
[TestFixture]
public class StyledUnderlineRenderingTests
{
    private const string Esc = "\u001b";

    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    /// <summary>Render a frame and hand back the runs the first row decided on.</summary>
    private static IReadOnlyList<TerminalView.CachedTextRun> RunsForFirstRow(TerminalView view)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            view.Render(context);
        }

        var line = view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY];
        Assert.That(line, Is.Not.Null);

        var runs = line!.Cache as List<TerminalView.CachedTextRun>;
        Assert.That(runs, Is.Not.Null, "the row produced no cached runs");
        return runs!;
    }

    [AvaloniaTest]
    [TestCase("4", XTerm.Common.UnderlineStyle.Single)]
    [TestCase("4:2", XTerm.Common.UnderlineStyle.Double)]
    [TestCase("4:3", XTerm.Common.UnderlineStyle.Curly)]
    [TestCase("4:4", XTerm.Common.UnderlineStyle.Dotted)]
    [TestCase("4:5", XTerm.Common.UnderlineStyle.Dashed)]
    public void The_run_carries_the_style_it_will_draw(string sgr, XTerm.Common.UnderlineStyle expected)
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[{sgr}mabc");

            var run = RunsForFirstRow(view).First(r => r.UnderlineStyle != XTerm.Common.UnderlineStyle.None);
            Assert.That(run.UnderlineStyle, Is.EqualTo(expected));
            Assert.That(run.UnderlineBrush, Is.Not.Null, "an underline needs something to draw with");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Text_without_an_underline_carries_none()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("plain");

            Assert.That(RunsForFirstRow(view).All(r => r.UnderlineStyle == XTerm.Common.UnderlineStyle.None),
                Is.True);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// SGR 58 is the point of the feature: a red squiggle under text that stays its normal colour.
    /// </summary>
    [AvaloniaTest]
    public void The_underline_takes_its_own_colour()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[4:3;58:2::255:0:0merror");

            var run = RunsForFirstRow(view).First(r => r.UnderlineStyle != XTerm.Common.UnderlineStyle.None);
            Assert.That(run.UnderlineBrush, Is.InstanceOf<ImmutableSolidColorBrush>());

            var brush = (ImmutableSolidColorBrush)run.UnderlineBrush!;
            Assert.That(brush.Color, Is.EqualTo(Color.FromRgb(255, 0, 0)));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Without_SGR58_the_underline_follows_the_text()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[4:3mplain");

            var run = RunsForFirstRow(view).First(r => r.UnderlineStyle != XTerm.Common.UnderlineStyle.None);
            Assert.That(run.UnderlineBrush, Is.Not.Null);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The underline must actually be DRAWN on the frame the line is built.
    /// </summary>
    /// <remarks>
    /// <para>This is the assertion the first version of these tests was missing. Asserting the run
    /// list proves only that the view decided on an underline — the run carried the right style the
    /// whole time the bug existed. What was absent was the draw call, on the path that paints a line
    /// the frame it changes rather than replaying it from cache.</para>
    /// <para>So this looks at what was drawn, and uses a colour no other part of the frame uses so
    /// the match cannot be an accident.</para>
    /// </remarks>
    [AvaloniaTest]
    public void The_underline_is_drawn_on_the_frame_the_line_is_built()
    {
        var (view, window) = Realised();
        try
        {
            // A colour nothing else on screen draws in, so finding it proves the underline drew it.
            view.Terminal.Write($"{Esc}[4:3;58:2::255:0:255mfresh");

            var group = new DrawingGroup();
            using (var context = group.Open())
            {
                view.Render(context);
            }

            var magenta = Color.FromRgb(255, 0, 255);
            var drawn = group.Children.OfType<GeometryDrawing>()
                .Any(d => (d.Brush is ISolidColorBrush fill && fill.Color == magenta)
                          || (d.Pen?.Brush is ISolidColorBrush stroke && stroke.Color == magenta));

            Assert.That(drawn, Is.True,
                "nothing was drawn in the underline's colour, so the underline was decided on and "
                + "then never painted");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// And on the frames after it, which replay the line from cache rather than rebuilding it.
    /// </summary>
    [AvaloniaTest]
    public void The_underline_is_drawn_again_when_the_line_is_replayed()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[4:3;58:2::255:0:255mcached");

            // First render builds the line and populates its cache.
            var first = new DrawingGroup();
            using (var context = first.Open())
                view.Render(context);

            // Second render replays it.
            var second = new DrawingGroup();
            using (var context = second.Open())
                view.Render(context);

            var magenta = Color.FromRgb(255, 0, 255);
            var drawn = second.Children.OfType<GeometryDrawing>()
                .Any(d => (d.Brush is ISolidColorBrush fill && fill.Color == magenta)
                          || (d.Pen?.Brush is ISolidColorBrush stroke && stroke.Color == magenta));

            Assert.That(drawn, Is.True, "the replayed frame lost the underline");
        }
        finally { window.Close(); }
    }
}
