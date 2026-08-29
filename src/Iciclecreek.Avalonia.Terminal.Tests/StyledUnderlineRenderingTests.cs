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

    /// <summary>
    /// Every GeometryDrawing in the frame, at any depth. The curly underline draws inside a
    /// translation group (its geometry is cached relative to the run and moved into place), and
    /// double-width lines draw inside their scale transform, so a flat search misses both.
    /// </summary>
    private static IEnumerable<GeometryDrawing> AllGeometry(DrawingGroup group)
        => group.Children.OfType<GeometryDrawing>()
            .Concat(group.Children.OfType<DrawingGroup>().SelectMany(AllGeometry));

    private static bool DrawnIn(DrawingGroup group, Color colour)
        => AllGeometry(group).Any(d => (d.Brush is ISolidColorBrush fill && fill.Color == colour)
                                       || (d.Pen?.Brush is ISolidColorBrush stroke && stroke.Color == colour));

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
    [TestCase("4")]
    [TestCase("4:2")]
    [TestCase("4:3")]
    [TestCase("4:4")]
    [TestCase("4:5")]
    public void Every_style_is_drawn_on_the_frame_the_line_is_built(string sgr)
    {
        var (view, window) = Realised();
        try
        {
            // A colour nothing else on screen draws in, so finding it proves the underline drew it.
            view.Terminal.Write($"{Esc}[{sgr};58:2::255:0:255mfresh");

            var group = new DrawingGroup();
            using (var context = group.Open())
            {
                view.Render(context);
            }

            Assert.That(DrawnIn(group, Color.FromRgb(255, 0, 255)), Is.True,
                $"nothing was drawn in the underline's colour for {sgr}, so the underline was "
                + "decided on and then never painted");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Double means TWO lines. An Any() check would pass a Double that quietly drew one, which is
    /// exactly the kind of silent downgrade the drawn-assertions exist to catch.
    /// </summary>
    [AvaloniaTest]
    public void Double_draws_two_lines()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[4:2;58:2::255:0:255mtwo");

            var group = new DrawingGroup();
            using (var context = group.Open())
                view.Render(context);

            var magenta = Color.FromRgb(255, 0, 255);
            var strokes = AllGeometry(group)
                .Count(d => d.Brush is ISolidColorBrush fill && fill.Color == magenta);

            Assert.That(strokes, Is.EqualTo(2), "a Double underline is a pair, not a line");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Double-width and double-height lines draw their underlines too. This is the path the review
    /// caught losing them entirely: taking underline out of TextDecorations removed it from every
    /// caller, and only the normal-line renderer gained the by-hand replacement — so plain SGR 4 on
    /// a DECDWL line, which underlined before, silently drew nothing.
    /// </summary>
    [AvaloniaTest]
    [TestCase("#6")]   // DECDWL — double width
    [TestCase("#3")]   // DECDHL top half
    [TestCase("#4")]   // DECDHL bottom half
    public void A_double_width_line_keeps_its_underline(string lineAttr)
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}{lineAttr}{Esc}[4;58:2::255:0:255mwide");

            var group = new DrawingGroup();
            using (var context = group.Open())
                view.Render(context);

            Assert.That(DrawnIn(group, Color.FromRgb(255, 0, 255)), Is.True,
                $"an underline on a {lineAttr} line drew nothing — the double-width renderer "
                + "lost the by-hand underline call");
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

            // The same gap this file's other test was written to close, one level up: without
            // these two assertions the test passes whether or not the replay path was taken. If
            // anything nulled the cache between renders, the second render would BUILD again,
            // still find magenta, and still go green — while the replay wiring went untested.
            var line = view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY];
            Assert.That(line!.Cache, Is.Not.Null,
                "the first render did not cache, so this is not testing replay");
            var cachedRuns = line.Cache;

            // Second render replays it.
            var second = new DrawingGroup();
            using (var context = second.Open())
                view.Render(context);

            Assert.That(ReferenceEquals(line.Cache, cachedRuns), Is.True,
                "the second render rebuilt the line instead of replaying it — the build path "
                + "cannot prove the replay path draws");

            Assert.That(DrawnIn(second, Color.FromRgb(255, 0, 255)), Is.True,
                "the replayed frame lost the underline");
        }
        finally { window.Close(); }
    }
}
