using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using NUnit.Framework;
using XTerm.Search;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The renderer half of scrollback search: highlights as an overlay, navigation that moves the
/// viewport, and all of it reachable from the host without touching the inner view.
/// </summary>
[TestFixture]
public class SearchSurfaceTests
{
    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    [AvaloniaTest]
    public void Finding_reports_the_count_a_find_box_shows()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("error one\r\nfine\r\nerror two\r\n");

            Assert.That(view.FindInBuffer("error"), Is.EqualTo(2));
            Assert.That(view.SearchHitCount, Is.EqualTo(2));
            Assert.That(view.SearchCurrentIndex, Is.EqualTo(-1), "no match chosen until the box steps");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Stepping_moves_the_viewport_to_an_offscreen_match()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("needle\r\n");
            for (var i = 0; i < 200; i++)
                view.Terminal.Write($"filler {i}\r\n");

            view.FindInBuffer("needle");
            var bottom = view.Terminal.Buffer.ViewportY;

            Assert.That(view.FindNext(), Is.True);
            Assert.That(view.Terminal.Buffer.ViewportY, Is.LessThan(bottom),
                        "the viewport should have moved up to show the match");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Stepping_with_no_search_is_left_unhandled()
    {
        var (view, window) = Realised();
        try
        {
            Assert.That(view.FindNext(), Is.False);
            Assert.That(view.FindPrevious(), Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Clearing_removes_the_result()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("error\r\n");
            view.FindInBuffer("error");
            view.ClearSearch();

            Assert.That(view.SearchHitCount, Is.Zero);
            Assert.That(view.SearchCurrentIndex, Is.EqualTo(-1));
        }
        finally { window.Close(); }
    }

    /// <summary>A frame with hits paints without throwing; the overlay path is exercised.</summary>
    [AvaloniaTest]
    public void A_frame_with_matches_renders()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("error error error\r\n");
            view.FindInBuffer("error");
            view.FindNext();

            var group = new DrawingGroup();
            using (var context = group.Open())
                view.Render(context);

            Assert.That(view.SearchHitCount, Is.EqualTo(3));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Setting a brush on the CONTROL must change what the view paints with. SurfaceParityTests
    /// proves the member exists; this proves the wiring — which is exactly the half the review
    /// found missing.
    /// </summary>
    [AvaloniaTest]
    public void Brushes_set_on_the_control_reach_the_view()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            control.SearchHighlightBrush = Brushes.HotPink;
            control.SearchCurrentBrush = Brushes.Lime;
            window.UpdateLayout();

            var view = control.View();
            Assert.That(view.SearchHighlightBrush, Is.EqualTo(Brushes.HotPink));
            Assert.That(view.SearchCurrentBrush, Is.EqualTo(Brushes.Lime));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Highlights from a finished search follow their lines up as the top of a full buffer is
    /// trimmed, and the ones whose lines went are dropped.
    /// </summary>
    [AvaloniaTest]
    public void Highlights_follow_their_lines_when_the_top_is_trimmed()
    {
        var control = new TerminalControl { Process = "", BufferSize = 500 };
        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            var view = control.View();

            // Full, so every line written from here on trims one off the top.
            for (var i = 0; i < 700; i++)
                view.Terminal.Write(i % 10 == 0 ? $"needle {i}\r\n" : $"line {i}\r\n");

            view.FindInBuffer("needle");
            var rowsBefore = HitRows(view);

            const int trimmed = 25;
            for (var i = 0; i < trimmed; i++)
                view.Terminal.Write($"more {i}\r\n");

            var rowsAfter = HitRows(view);
            Assert.That(rowsAfter, Is.EqualTo(rowsBefore.Select(r => r - trimmed).Where(r => r >= 0).ToList()));
            Assert.That(rowsAfter, Has.Count.LessThan(rowsBefore.Count), "a match should have gone with the trim");
            foreach (var row in rowsAfter)
                Assert.That(view.Terminal.Buffer.GetLine(row)!.TranslateToString(true), Does.StartWith("needle"),
                            $"the highlight on row {row} is not on a match");

            view.FindInBuffer("needle");
            Assert.That(HitRows(view), Is.EqualTo(rowsAfter), "a fresh search should agree");
        }
        finally { window.Close(); }
    }

    private static List<int> HitRows(TerminalView view)
    {
        var rows = new List<int>();
        for (var row = 0; row < view.Terminal.Buffer.Lines.Length; row++)
            foreach (var hit in view.SearchResults!.HitsOnRow(row))
                rows.Add(hit.BufferRow);
        return rows;
    }

    [AvaloniaTest]
    public void Options_pass_through()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("Error error\r\n");

            Assert.That(view.FindInBuffer("error", new SearchOptions { CaseSensitive = true }),
                        Is.EqualTo(1));
        }
        finally { window.Close(); }
    }
}
