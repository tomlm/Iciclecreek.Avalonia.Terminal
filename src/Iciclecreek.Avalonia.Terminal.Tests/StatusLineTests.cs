using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The DEC status line's row: where it sits, and what it costs the grid.
/// </summary>
/// <remarks>
/// <para>Two layers of tests, matching the two halves of the split. The geometry and drawing
/// tests drive the view through <c>SetStatusLine</c> with a row borrowed from a scratch terminal —
/// they were written and pinned before the emulator half existed, and still isolate this side. The
/// wiring tests at the bottom drive the emulator itself with DECSSDT/DECSASD (XTerm.NET#148) and
/// assert the row arrives, the indicator type does not, and deselecting gives the row back.</para>
/// <para>The geometry half matters more than the drawing half. An application told it has N rows
/// must have N rows it can write to; a status line carved out of the grid after the fact leaves it
/// addressing a row that is not there, and every pixel-geometry report is computed from
/// <c>_terminal.Rows</c>.</para>
/// </remarks>
[TestFixture]
public class StatusLineTests
{
    private static (TerminalView view, Window window) Realised()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    /// <summary>A real BufferLine with real attributes, from a terminal nobody is looking at.</summary>
    private static XTerm.Buffer.BufferLine RowOf(string text)
    {
        var scratch = new XTerm.Terminal(
            new XTerm.Options.TerminalOptions { Cols = 80, Rows = 2, Scrollback = 0 });
        scratch.Write(text);
        return scratch.Buffer.GetLine(0)!;
    }

    [AvaloniaTest]
    public void A_status_line_costs_the_grid_exactly_one_row()
    {
        var (view, window) = Realised();
        try
        {
            var before = view.Terminal.Rows;

            view.SetStatusLine(RowOf("ready"));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Rows, Is.EqualTo(before - 1),
                "the row comes out of the height before the grid is counted, so the grid is one shorter");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Clearing_it_gives_the_row_back()
    {
        var (view, window) = Realised();
        try
        {
            var before = view.Terminal.Rows;

            view.SetStatusLine(RowOf("ready"));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Terminal.Rows, Is.EqualTo(before - 1), "sanity: it was taken");

            view.SetStatusLine(null);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Rows, Is.EqualTo(before),
                "a status line that goes away stops costing anything");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_row_is_not_one_of_the_terminals_rows()
    {
        // The failure this guards against: an application is told it has N rows, writes to the last
        // one, and finds the status line sitting there instead. Rows is what every pixel-geometry
        // report and the pty size are computed from, so getting this right gets those right for free.
        var (view, window) = Realised();
        try
        {
            view.SetStatusLine(RowOf("ready"));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var rows = view.Terminal.Rows;

            // Fill the grid's last row through the emulator and check it is still the emulator's.
            view.Terminal.Write($"[{rows};1HLAST");
            Dispatcher.UIThread.RunJobs();

            var top = view.Terminal.Buffer.ViewportY;
            var lastRow = (view.Terminal.Buffer.GetLine(top + rows - 1)?.TranslateToString(true) ?? "").TrimEnd();

            Assert.That(lastRow, Is.EqualTo("LAST"),
                "the bottom row of the grid belongs to the application, not to the status line");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_grid_fits_the_height_that_is_left()
    {
        // Not merely "one fewer" -- the row's height has to be a whole cell, or the arithmetic drifts
        // and a tall enough window loses two rows instead of one.
        var (view, window) = Realised();
        try
        {
            view.SetStatusLine(RowOf("ready"));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var used = (view.Terminal.Rows + 1) * view.CharHeight;

            Assert.That(used, Is.LessThanOrEqualTo(view.Bounds.Height + 0.5),
                "the grid plus the status row must fit inside the control");
            Assert.That(used + view.CharHeight, Is.GreaterThan(view.Bounds.Height),
                "and must not leave a whole unused row behind");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Setting_the_same_presence_again_does_not_re_grid()
    {
        // Content changing is a repaint, not a layout: a status line whose text moves must not
        // resize the terminal underneath the application on every update.
        var (view, window) = Realised();
        try
        {
            view.SetStatusLine(RowOf("first"));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var rows = view.Terminal.Rows;

            view.SetStatusLine(RowOf("second"));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Rows, Is.EqualTo(rows),
                "only appearing or vanishing changes the geometry");
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------- the emulator end of the seam

    private const string Esc = "\u001b";

    [AvaloniaTest]
    public void The_emulators_status_line_reaches_the_screen()
    {
        // The whole path: DECSSDT 2 selects a host-writable status line, DECSASD 1 sends the next
        // writes into it, DECSASD 0 hands the cursor back -- exactly what vttest 11.2.6.2 does.
        var (view, window) = Realised();
        try
        {
            var before = view.Terminal.Rows;

            view.Terminal.Write($"{Esc}[2$~{Esc}[1$}}TEXT IN THE STATUS LINE{Esc}[0$}}");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Rows, Is.EqualTo(before - 1),
                "selecting a host-writable status line costs the grid its row");

            var shown = (typeof(TerminalView)
                .GetField("_statusLine", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(view) as XTerm.Buffer.BufferLine)?.TranslateToString(true)?.TrimEnd();
            Assert.That(shown, Is.EqualTo("TEXT IN THE STATUS LINE"),
                "and the text written under DECSASD 1 is what the row shows");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_indicator_type_draws_nothing()
    {
        // DECSSDT 1 selects the INDICATOR status line: the terminal's own status, which DEC
        // hardware showed because it was the whole screen. A control embedded in someone's
        // application has no status of its own worth a row of their layout, so it draws nothing --
        // and a program loses nothing, because DECSASD 1 is refused without a host-writable line.
        var (view, window) = Realised();
        try
        {
            var before = view.Terminal.Rows;

            view.Terminal.Write($"{Esc}[1$~");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Rows, Is.EqualTo(before),
                "the indicator costs the application nothing");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Deselecting_gives_the_row_back_through_the_emulator()
    {
        var (view, window) = Realised();
        try
        {
            var before = view.Terminal.Rows;

            view.Terminal.Write($"{Esc}[2$~");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Terminal.Rows, Is.EqualTo(before - 1), "sanity: the row was taken");

            view.Terminal.Write($"{Esc}[0$~");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Rows, Is.EqualTo(before),
                "DECSSDT 0 hands the application its row back");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_blinking_status_line_is_invalidated_by_the_blink_tick()
    {
        // The blink tick's cache walk covers the viewport's rows, and the status line is not one of
        // them -- so SGR 5 written into it (vttest does) cached one phase and froze there.
        var (view, window) = Realised();
        try
        {
            view.CursorBlink = true;
            view.SetStatusLine(RowOf($"{Esc}[5mALERT{Esc}[0m"));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var line = typeof(TerminalView)
                .GetField("_statusLine", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(view) as XTerm.Buffer.BufferLine;
            Assert.That(line, Is.Not.Null);

            // Something has to be IN the cache before dropping it can be observed.
            line!.Cache = new object();
            view.Focus();
            typeof(TerminalView).GetMethod("OnCursorBlinkTick",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, new object?[] { null, EventArgs.Empty });

            Assert.That(line.Cache, Is.Null,
                "a blinking status line must have its cached runs dropped, or it freezes half-lit");
        }
        finally { window.Close(); }
    }
}
