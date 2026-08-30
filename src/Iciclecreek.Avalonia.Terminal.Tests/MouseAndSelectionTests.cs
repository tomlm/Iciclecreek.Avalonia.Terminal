using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Selections and mouse reports that outlive the thing they were about.
/// </summary>
/// <remarks>
/// Four of these share a shape: a position recorded in one frame of reference and used in another
/// after something moved underneath it -- the viewport scrolled, or the whole screen was swapped.
/// The fifth is the opposite complaint, a report sent far more often than anything changed.
/// </remarks>
[TestFixture]
public class MouseAndSelectionTests
{
    private static readonly string Esc = ((char)0x1B).ToString();

    private static (TerminalView view, RecordingConnection pty, Window window) LiveView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();

        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        view.Focus();
        return (view, pty, window);
    }

    // ------------------------------------------------------- the screen switch

    [AvaloniaTest]
    public void Switching_to_the_alternate_screen_drops_the_selection()
    {
        // The two buffers are different content at the same coordinates. A selection left standing
        // across the switch still highlights the same rectangle -- of whatever is there now -- so
        // copying it returned a full-screen application's text from a selection that looked like it
        // was over the shell output the user had actually chosen.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write("some shell output\r\n");
            Dispatcher.UIThread.RunJobs();

            view.Terminal.Selection.SelectAll();
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: there is a selection");

            view.Terminal.Write($"{Esc}[?1049h");   // to the alternate screen
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Selection.HasSelection, Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Switching_back_to_the_normal_screen_drops_it_too()
    {
        // Wrong in both directions, so cleared in both.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?1049h");
            Dispatcher.UIThread.RunJobs();

            view.Terminal.Write("full screen app");
            Dispatcher.UIThread.RunJobs();
            view.Terminal.Selection.SelectAll();
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity");

            view.Terminal.Write($"{Esc}[?1049l");
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Selection.HasSelection, Is.False);
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------ the anchor that drifted

    [AvaloniaTest]
    public void A_click_anchors_the_selection_where_it_was_clicked_even_if_output_scrolls()
    {
        // A single click defers starting the selection until the pointer moves, so the anchor sits
        // in a field for a while. It was stored as a VIEWPORT row -- and output arriving in that gap
        // scrolls the viewport, so the row named different content by the time it was used. The
        // selection then began somewhere the user had not pressed.
        var (view, pty, window) = LiveView();
        try
        {
            // Scrolled BEFORE the press, which is what makes the two readings differ at all: with the
            // viewport still at buffer row 0 an absolute row and a viewport row are the same number,
            // and the test would agree with either implementation.
            for (var i = 0; i < view.Terminal.Rows + 10; i++)
                view.Terminal.Write($"line {i}\r\n");
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Terminal.Buffer.ViewportY, Is.GreaterThan(0),
                "sanity: without a scrolled viewport this proves nothing");

            // The row is derived from the view's OWN cell height rather than assumed, so the test does
            // not depend on what font the platform resolved.
            var pressY = CharHeight(view) * 2 + 1;
            var anchorBefore = view.Terminal.Buffer.ViewportY + (int)(pressY / CharHeight(view));

            Press(view, 5, pressY);

            // And more output after it, so the viewport moves again while the anchor is pending --
            // the gap the bug actually lived in.
            var atPress = view.Terminal.Buffer.ViewportY;
            for (var i = 0; i < 20; i++)
                view.Terminal.Write($"filler {i}\r\n");
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Terminal.Buffer.ViewportY, Is.GreaterThan(atPress), "sanity: it scrolled again");

            Assert.That(PendingAnchorRow(view), Is.EqualTo(anchorBefore),
                "the anchor must still name the row that was under the pointer at the press");
        }
        finally { window.Close(); }
    }

    private static int PendingAnchorRow(TerminalView view)
    {
        var f = typeof(TerminalView).GetField("_pendingSelectionStart",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(f, Is.Not.Null, "_pendingSelectionStart has been renamed; this test needs updating");

        var value = f!.GetValue(view);
        Assert.That(value, Is.Not.Null, "no selection is pending, so there is nothing to check");

        // Item2 rather than Row: the tuple's element names exist only at compile time, so there is
        // no Row property to reflect over and asking for one gets null back.
        return (int)value!.GetType().GetField("Item2")!.GetValue(value)!;
    }

    // ------------------------------------------- one report per cell, not per event

    [AvaloniaTest]
    public void Motion_inside_one_cell_is_reported_once()
    {
        // The protocol reports positions in CELLS, so every event inside one cell produces an
        // identical sequence -- and a pointer fires them at several hundred hertz. A program tracking
        // the mouse was reading hundreds of copies of "still at 40,12" a second.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?1003h");   // report any motion
            Dispatcher.UIThread.RunJobs();

            Move(view, 40.0, 40.0);
            var afterFirst = AwaitOutput(pty).Length;
            Assert.That(afterFirst, Is.GreaterThan(0), "sanity: motion is being reported at all");

            // Four more events, all landing in the same cell.
            for (var i = 0; i < 4; i++)
                Move(view, 40.0 + i, 40.0 + i);
            Thread.Sleep(120);

            Assert.That(pty.Written.Length, Is.EqualTo(afterFirst),
                "nothing changed in cell terms, so nothing more should have been sent");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Crossing_into_another_cell_is_still_reported()
    {
        // The coalescing must not become silence. This is the half that would break if the guard
        // were keyed on anything coarser than the cell.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?1003h");
            Dispatcher.UIThread.RunJobs();

            Move(view, 40.0, 40.0);
            var afterFirst = AwaitOutput(pty).Length;

            Move(view, 400.0, 200.0);

            Assert.That(AwaitOutput(pty, afterFirst).Length, Is.GreaterThan(afterFirst));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_move_while_tracking_is_off_does_not_hide_the_first_tracked_move()
    {
        // Coalescing belongs to reports, not raw pointer events. If an unreported move is remembered
        // while tracking is off, enabling mode 1003 and moving again inside that cell produces no
        // first report, because the coordinates appear unchanged to the cache.
        var (view, pty, window) = LiveView();
        try
        {
            Move(view, 40.0, 40.0);             // tracking off: nothing is sent
            Assert.That(pty.Written, Is.Empty);

            view.Terminal.Write($"{Esc}[?1003h");
            Dispatcher.UIThread.RunJobs();

            Move(view, 40.0, 40.0);             // same cell, now meaningful

            Assert.That(AwaitOutput(pty), Is.Not.Empty,
                "the first motion after tracking is enabled must reach the application");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_modifier_change_in_one_cell_is_still_a_distinct_report()
    {
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?1003h");
            Dispatcher.UIThread.RunJobs();

            Move(view, 40.0, 40.0);
            var afterPlain = AwaitOutput(pty).Length;
            Assert.That(afterPlain, Is.GreaterThan(0), "sanity: the first move was reported");

            Move(view, 40.0, 40.0, KeyModifiers.Shift);

            Assert.That(AwaitOutput(pty, afterPlain).Length, Is.GreaterThan(afterPlain),
                "modifier state is part of the mouse report even when the cell is unchanged");
        }
        finally { window.Close(); }
    }

    // -------------------------------------------------- Shift owns the wheel

    [AvaloniaTest]
    public void Shift_wheel_scrolls_the_terminal_rather_than_the_application()
    {
        // Shift is the host's override, and it worked for Shift+click and not for Shift+wheel -- so
        // the one way to reach a terminal's own scrollback while a full-screen application had the
        // mouse did nothing at all.
        var (view, pty, window) = LiveView();
        try
        {
            for (var i = 0; i < view.Terminal.Rows + 30; i++)
                view.Terminal.Write($"line {i}\r\n");
            Dispatcher.UIThread.RunJobs();

            view.Terminal.Write($"{Esc}[?1000h");   // the application takes the mouse
            Dispatcher.UIThread.RunJobs();

            var before = view.ViewportY;
            var mark = pty.Written.Length;

            Wheel(view, 3.0, KeyModifiers.Shift);
            Thread.Sleep(120);

            Assert.That(view.ViewportY, Is.LessThan(before), "the terminal's own viewport must move");
            Assert.That(pty.Written.Length, Is.EqualTo(mark),
                "and the application must not be told about a gesture the host claimed");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void An_unshifted_wheel_still_belongs_to_the_application()
    {
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?1000h");
            Dispatcher.UIThread.RunJobs();

            var mark = pty.Written.Length;
            Wheel(view, 3.0, KeyModifiers.None);

            Assert.That(AwaitOutput(pty, mark).Length, Is.GreaterThan(mark));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Waits until more than <paramref name="previous"/> bytes have been sent, then returns them.
    /// </summary>
    /// <remarks>
    /// For assertions that expect OUTPUT. The send path is async and completes off the dispatcher, so
    /// a fixed sleep is a bet on how fast the machine is -- and it is a bet that loses silently,
    /// because "nothing has arrived yet" and "nothing was sent" look identical from here. It lost on
    /// the macOS CI runner while passing on every developer machine it was written on.
    ///
    /// A test expecting NOTHING still uses a fixed wait, because there is no growth to wait for.
    /// </remarks>
    private static string AwaitOutput(RecordingConnection pty, int previous = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && pty.Written.Length <= previous)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        return pty.Written;
    }

    // ----------------------------------------------------------------- input

    private static double CharHeight(TerminalView view)
    {
        var f = typeof(TerminalView).GetField("_charHeight",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(f, Is.Not.Null, "_charHeight has been renamed; this test needs updating");
        var h = (double)f!.GetValue(view)!;
        Assert.That(h, Is.GreaterThan(0), "the view has not measured its font yet");
        return h;
    }

    private static void Press(TerminalView view, double x, double y)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                                               PointerUpdateKind.LeftButtonPressed);
        view.RaiseEvent(new PointerPressedEventArgs(view, pointer, view,
            new global::Avalonia.Point(x, y), 0, props, KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerPressedEvent,
        });
        Dispatcher.UIThread.RunJobs();
    }

    private static void Move(TerminalView view, double x, double y,
                             KeyModifiers modifiers = KeyModifiers.None)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        view.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, view, pointer, view,
            new global::Avalonia.Point(x, y), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            modifiers));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Wheel(TerminalView view, double deltaY, KeyModifiers modifiers)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        view.RaiseEvent(new PointerWheelEventArgs(view, pointer, view,
            new global::Avalonia.Point(40, 40), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            modifiers, new global::Avalonia.Vector(0, deltaY))
        {
            RoutedEvent = InputElement.PointerWheelChangedEvent,
        });
        Dispatcher.UIThread.RunJobs();
    }
}
