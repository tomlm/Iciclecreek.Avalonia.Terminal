using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Avalonia.Terminal;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Five places where a screen position was worked out against the wrong frame of reference.
/// </summary>
/// <remarks>
/// They are unrelated as code and identical as mistakes: a buffer row used where a viewport row was
/// meant, a viewport row used where a buffer row was meant, a live scroll position used where a
/// snapshotted one was meant, and two pointer coordinates used with no bounds at all. Each is
/// invisible on a fresh terminal, because before anything scrolls every one of these frames of
/// reference agrees.
/// </remarks>
[TestFixture]
public class RendererViewportTests
{
    private static readonly string Esc = ((char)0x1B).ToString();

    private static (TerminalView view, Window window) Realised()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    /// <summary>Fills the buffer past the viewport, so scrollback exists and the frames diverge.</summary>
    private static void ScrollPast(TerminalView view)
    {
        for (var i = 0; i < view.Terminal.Rows + 20; i++)
            view.Terminal.Write($"filler {i}\r\n");
        Dispatcher.UIThread.RunJobs();

        Assert.That(view.Terminal.Buffer.ViewportY, Is.GreaterThan(0),
            "the whole point is that the viewport is no longer at buffer row 0");
    }

    // ------------------------------------------------------------ blinking

    [AvaloniaTest]
    public void Blinking_text_keeps_being_invalidated_once_there_is_scrollback()
    {
        // The blink tick dropped the cached run for every line carrying SGR 5 -- looked up by BUFFER
        // row 0..Rows, which is the oldest scrollback rather than what is on screen. So it cleared
        // caches nobody was looking at and left the visible blinking line cached, and blinking
        // stopped for the rest of the session the moment anything scrolled off the top.
        var (view, window) = Realised();
        try
        {
            view.CursorBlink = true;
            ScrollPast(view);

            view.Terminal.Write($"{Esc}[5mblinking{Esc}[0m");
            Dispatcher.UIThread.RunJobs();

            var row = view.Terminal.Buffer.ViewportY + view.Terminal.Buffer.Y;
            var line = view.Terminal.Buffer.GetLine(row);
            Assert.That(line, Is.Not.Null);

            // Something has to be IN the cache before dropping it can be observed.
            line!.Cache = new object();
            view.Focus();
            Tick(view);

            Assert.That(line.Cache, Is.Null,
                "the visible blinking line must have its cached run dropped, or it never repaints");
        }
        finally { window.Close(); }
    }

    /// <summary>Fires the cursor-blink timer the way the dispatcher would.</summary>
    /// <remarks>
    /// Reached by reflection rather than by waiting out the real interval: the handler is private and
    /// the alternative is a test that sleeps half a second to observe one tick.
    /// </remarks>
    private static void Tick(TerminalView view)
    {
        var tick = typeof(TerminalView).GetMethod("OnCursorBlinkTick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(tick, Is.Not.Null, "OnCursorBlinkTick has been renamed; this test needs updating");
        tick!.Invoke(view, new object?[] { null, EventArgs.Empty });
    }

    // ------------------------------------------------------------- conceal

    [AvaloniaTest]
    public void Concealed_text_is_drawn_in_nothing()
    {
        // SGR 8 has been recorded by the emulator since the parser was written and read by nothing
        // here, so a password prompt that echoes was drawn in full.
        var (view, window) = Realised();
        try
        {
            var cell = CellAfter(view, $"{Esc}[8mhunter2");
            Assert.That(cell.Attributes.IsInvisible(), Is.True,
                "sanity: the emulator recorded the attribute, which was never the missing half");

            var concealed = cell.ApplyConceal(Brushes.Red);
            Assert.That(((ISolidColorBrush)concealed).Color.A, Is.EqualTo(0));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Text_that_is_not_concealed_keeps_the_brush_it_was_given()
    {
        var (view, window) = Realised();
        try
        {
            var cell = CellAfter(view, "hunter2");

            Assert.That(cell.ApplyConceal(Brushes.Red), Is.SameAs(Brushes.Red));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Conceal_takes_the_foreground_AFTER_an_inverse_swap_not_before()
    {
        // The ordering is the substance of this one. Inverse exchanges the two brushes, so
        // concealing first hands back a transparent BACKGROUND -- which conceals nothing and punches
        // a hole in the fill instead. The renderer passes the swapped pair, and this is the contract
        // that says so.
        var (view, window) = Realised();
        try
        {
            var cell = CellAfter(view, $"{Esc}[7;8mhunter2");
            Assert.That(cell.Attributes.IsInverse(), Is.True, "sanity: both attributes are set");
            Assert.That(cell.Attributes.IsInvisible(), Is.True);

            // What the renderer does: resolve, swap, then conceal.
            var palette = view.Terminal.Colors.Take();
            IBrush foreground = cell.GetForegroundBrush(palette, Brushes.White);
            IBrush background = cell.GetBackgroundBrush(palette, Brushes.Black);
            (foreground, background) = (background, foreground);

            foreground = cell.ApplyConceal(foreground);

            Assert.That(((ISolidColorBrush)foreground).Color.A, Is.EqualTo(0),
                "the glyph is what disappears");
            Assert.That(((ISolidColorBrush)background).Color.A, Is.Not.EqualTo(0),
                "and the fill behind it is still painted");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Conceal_is_applied_everywhere_a_cell_is_drawn_not_only_the_run_path()
    {
        // There are three places a cell's text is shaped: the ordinary run path, DECDWL/DECDHL rows,
        // and OSC 66 sized blocks. Conceal landed in the first only, so a concealed password showed
        // in full on any line a program happened to double.
        //
        // Counted rather than exercised through each renderer, which needs pixels this platform has
        // no backend for. What it guards is the thing that actually went wrong: a fourth draw site
        // appearing without the call.
        // TerminalView is partial classes now, and the call sites this counts live in more than
        // one of its files -- so read them ALL, or a move-only split flips this test.
        var source = string.Concat(
            System.IO.Directory.GetFiles(
                    System.IO.Path.GetDirectoryName(SourcePath("TerminalView.cs"))!, "TerminalView*.cs")
                .OrderBy(f => f)
                .Select(System.IO.File.ReadAllText));

        Assert.That(Occurrences(source, "ApplyConceal(foreground)"), Is.EqualTo(3),
            "every path that shapes a cell's text must conceal it; add the call, then update this count");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>The library source file, found relative to the test assembly.</summary>
    private static string SourcePath(string file)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        Assert.That(dir, Is.Not.Null, "could not find the repo root from " + AppContext.BaseDirectory);
        var path = System.IO.Path.Combine(dir!.FullName, "src", "Iciclecreek.Avalonia.TerminalWindow", file);
        Assert.That(System.IO.File.Exists(path), Is.True, "not where this test expected: " + path);
        return path;
    }

    private static XTerm.Buffer.BufferCell CellAfter(TerminalView view, string write)
    {
        view.Terminal.Write(write);
        Dispatcher.UIThread.RunJobs();
        var line = view.Terminal.Buffer.GetLine(view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y);
        Assert.That(line, Is.Not.Null);
        return line![0];
    }

    [AvaloniaTest]
    public void Concealed_text_still_occupies_its_cells()
    {
        // Concealed means unreadable, not absent. The characters stay in the buffer, so the cursor
        // sits where it should and a copy still finds them -- which is why the glyph is drawn in a
        // transparent brush rather than skipped.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[8mhunter2");
            Dispatcher.UIThread.RunJobs();

            var line = view.Terminal.Buffer.GetLine(view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y);
            Assert.That(line!.TranslateToString(true), Does.StartWith("hunter2"));
            Assert.That(view.Terminal.Buffer.X, Is.EqualTo(7), "and the cursor advanced over them");
        }
        finally { window.Close(); }
    }

    // -------------------------------------------------------- pointer bounds

    [AvaloniaTest]
    public void A_pointer_above_the_control_does_not_report_a_negative_row()
    {
        // Every caller divided by the row height and used the result unchecked. A drag that leaves
        // the control at speed reports well outside it, and the capture keeps delivering those
        // events -- so a negative row reached the selection and the application as a position.
        var (view, window) = Realised();
        try
        {
            Assert.That(Row(view, -500), Is.EqualTo(0));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_pointer_below_the_last_line_does_not_report_a_row_past_the_end()
    {
        var (view, window) = Realised();
        try
        {
            Assert.That(Row(view, 100_000), Is.EqualTo(view.Terminal.Rows - 1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_pointer_at_the_right_edge_does_not_report_a_column_past_the_last()
    {
        // The control is wider than a whole number of cells, so the strip of padding at the right
        // edge resolved to a column that does not exist.
        var (view, window) = Realised();
        try
        {
            Assert.That(Column(view, 100_000), Is.EqualTo(view.Terminal.Cols - 1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_pointer_inside_the_control_still_reports_where_it_is()
    {
        // The clamp must not have flattened the ordinary case.
        var (view, window) = Realised();
        try
        {
            Assert.That(Row(view, 0), Is.EqualTo(0));
            Assert.That(Column(view, 0), Is.EqualTo(0));
            Assert.That(Row(view, 100_000), Is.Not.EqualTo(Row(view, 0)),
                "sanity: the two ends are still distinguishable");
        }
        finally { window.Close(); }
    }

    private static int Row(TerminalView view, double y) => Invoke<int>(view, "PointerRow", y);
    private static int Column(TerminalView view, double x) => Invoke<int>(view, "PointerColumn", x);

    private static T Invoke<T>(TerminalView view, string name, double arg)
    {
        var m = typeof(TerminalView).GetMethod(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(m, Is.Not.Null, $"{name} has been renamed; this test needs updating");
        return (T)m!.Invoke(view, new object[] { arg })!;
    }
}
