using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The standard terminal keyboard operations in <see cref="TerminalKeyGestures"/>: copy, paste and
/// select-all on their uncontested chords, zoom, and scrolling the scrollback.
///
/// <para>Every test presses the gestures from the table rather than spelling out keys, so the same test
/// exercises Ctrl+Shift+C on Windows and Linux and Cmd+C on macOS — and proves the table and the handler
/// agree, which is the whole point of publishing the table.</para>
/// </summary>
[TestFixture]
public class StandardShortcutTests
{
    private const string Esc = "\u001b";

    private static (TerminalView view, RecordingConnection pty, Window window) LiveView(
        ShortcutMode mode = ShortcutMode.Terminal)
    {
        var view = new TerminalView { Process = "", ShortcutMode = mode };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        view.Focus();
        Assert.That(view.IsFocused, Is.True, "sanity: OnKeyDown returns early without focus");
        return (view, pty, window);
    }

    private static KeyEventArgs Press(TerminalView v, KeyGesture g) => Press(v, g.Key, g.KeyModifiers);

    private static KeyEventArgs Press(TerminalView v, Key k, KeyModifiers m)
    {
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = m };
        v.RaiseEvent(e);
        return e;
    }

    /// <summary>Two hundred lines, so there is a scrollback to move through, with the view at the tail.</summary>
    private static void FillScrollback(TerminalView view)
    {
        for (int i = 0; i < 200; i++)
            view.Terminal.WriteLine($"line {i}");
        view.ViewportY = view.MaxScrollback;
        Assert.That(view.MaxScrollback, Is.GreaterThan(view.ViewportLines * 2), "sanity: more than two pages of scrollback");
    }

    /// <summary>Select the first <paramref name="cells"/> cells of the top row.</summary>
    private static void SelectCells(TerminalView view, int cells)
    {
        view.Terminal.Selection.StartSelection(0, 0, XTerm.Selection.SelectionMode.Normal);
        view.Terminal.Selection.UpdateSelection(cells - 1, 0);
        view.Terminal.Selection.EndSelection();
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: something is selected");
    }

    // ── Clipboard ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Ctrl+Shift+C and Ctrl+Insert (Cmd+C on macOS) copy the selection.</summary>
    [AvaloniaTest]
    public async Task Every_copy_gesture_copies_the_selection()
    {
        foreach (var gesture in TerminalKeyGestures.Copy)
        {
            var (view, pty, window) = LiveView();
            try
            {
                view.Terminal.Write("copy me");
                SelectCells(view, "copy me".Length);
                var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
                await clipboard.SetTextAsync("stale");

                Press(view, gesture);
                await Task.Delay(150);

                Assert.That(await clipboard.TryGetTextAsync(), Is.EqualTo("copy me"), $"{gesture} copied");
                Assert.That(pty.Written, Is.Empty, $"{gesture} sent nothing to the program");
                Assert.That(view.Terminal.Selection.HasSelection, Is.True, "copying leaves the selection in place");
            }
            finally { window.Close(); }
        }
    }

    /// <summary>Ctrl+Shift+V and Shift+Insert (Cmd+V on macOS) paste the clipboard.</summary>
    [AvaloniaTest]
    public async Task Every_paste_gesture_pastes()
    {
        foreach (var gesture in TerminalKeyGestures.Paste)
        {
            var (view, pty, window) = LiveView();
            try
            {
                var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
                await clipboard.SetTextAsync("pasted-text");

                Press(view, gesture);

                Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo("pasted-text"), $"{gesture} pasted");
            }
            finally { window.Close(); }
        }
    }

    /// <summary>Ctrl+Shift+A (Cmd+A on macOS) selects the input, in Terminal mode, without opting in.</summary>
    [AvaloniaTest]
    public async Task Every_select_all_gesture_selects_the_input()
    {
        foreach (var gesture in TerminalKeyGestures.SelectAll)
        {
            var (view, pty, window) = LiveView();
            try
            {
                view.Terminal.Write("hello world");
                await Task.Delay(60);

                Press(view, gesture);
                await Task.Delay(120);

                Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("hello world"),
                    $"{gesture} selected the input, not the whole screen");
            }
            finally { window.Close(); }
        }
    }

    // ── Zoom ────────────────────────────────────────────────────────────────────────────────────

    [AvaloniaTest]
    public void Zoom_defaults_to_one() => Assert.That(new TerminalView().Zoom, Is.EqualTo(1.0));

    /// <summary>Plus, Minus and 0 with the command modifier step the zoom and never reach the program.</summary>
    [AvaloniaTest]
    public async Task Zoom_gestures_step_and_reset()
    {
        var (view, pty, window) = LiveView();
        try
        {
            foreach (var gesture in TerminalKeyGestures.ZoomIn)
            {
                view.ResetZoom();
                var e = Press(view, gesture);
                Assert.That(e.Handled, Is.True, $"{gesture} was claimed");
                Assert.That(view.Zoom, Is.EqualTo(1.1).Within(1e-9), $"{gesture} zoomed in one step");
            }

            foreach (var gesture in TerminalKeyGestures.ZoomOut)
            {
                view.ResetZoom();
                Press(view, gesture);
                Assert.That(view.Zoom, Is.EqualTo(0.9).Within(1e-9), $"{gesture} zoomed out one step");
            }

            view.Zoom = 1.7;
            foreach (var gesture in TerminalKeyGestures.ResetZoom)
            {
                view.Zoom = 1.7;
                Press(view, gesture);
                Assert.That(view.Zoom, Is.EqualTo(1.0), $"{gesture} reset the zoom");
            }

            await Task.Delay(60);
            Assert.That(pty.Written, Is.Empty, "none of it reached the program");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The keypad's + and - zoom as well. Avalonia resolves them to the main row's keys inside
    /// <see cref="KeyGesture.Matches"/>, which is what lets the table list one gesture for each.
    /// </summary>
    [AvaloniaTest]
    public void Keypad_plus_and_minus_zoom()
    {
        var (view, _, window) = LiveView();
        try
        {
            Press(view, Key.Add, TerminalKeyGestures.CommandModifiers);
            Assert.That(view.Zoom, Is.EqualTo(1.1).Within(1e-9), "keypad +");

            view.ResetZoom();
            Press(view, Key.Subtract, TerminalKeyGestures.CommandModifiers);
            Assert.That(view.Zoom, Is.EqualTo(0.9).Within(1e-9), "keypad -");
        }
        finally { window.Close(); }
    }

    /// <summary>Ten steps in and ten out land exactly back on 1.0, not on 1.0000000000000002.</summary>
    [AvaloniaTest]
    public void Zoom_steps_are_exact()
    {
        var view = new TerminalView();
        for (int i = 0; i < 10; i++) view.ZoomIn();
        Assert.That(view.Zoom, Is.EqualTo(2.0));
        for (int i = 0; i < 10; i++) view.ZoomOut();
        Assert.That(view.Zoom, Is.EqualTo(1.0));
    }

    [AvaloniaTest]
    public void Zoom_is_clamped()
    {
        var view = new TerminalView();
        view.Zoom = 100;
        Assert.That(view.Zoom, Is.EqualTo(3.0), "no larger than three times the font");
        view.Zoom = 0;
        Assert.That(view.Zoom, Is.EqualTo(0.5), "no smaller than half of it");
        view.Zoom = double.NaN;
        Assert.That(view.Zoom, Is.EqualTo(1.0), "NaN is not a zoom");
    }

    /// <summary>
    /// Zoom is real: the cell grows and the grid loses columns, so the process is told a new size, exactly
    /// as if FontSize had changed.
    /// </summary>
    [AvaloniaTest]
    public void Zoom_resizes_the_grid()
    {
        var (view, _, window) = LiveView();
        try
        {
            var cellBefore = view.CharWidth;
            var colsBefore = view.Terminal.Cols;

            view.Zoom = 2.0;
            window.UpdateLayout();

            Assert.That(view.CharWidth, Is.GreaterThan(cellBefore * 1.5), "the cell grew with the font");
            Assert.That(view.Terminal.Cols, Is.LessThan(colsBefore * 0.75), "and fewer columns fit");

            view.ResetZoom();
            window.UpdateLayout();

            Assert.That(view.CharWidth, Is.EqualTo(cellBefore).Within(1e-6), "reset restores the cell");
            Assert.That(view.Terminal.Cols, Is.EqualTo(colsBefore), "and the columns");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Zoom sits on top of FontSize rather than replacing it, so the host's font binding keeps working after
    /// the user has zoomed — the failure mode this design exists to avoid.
    /// </summary>
    [AvaloniaTest]
    public void Font_size_changes_still_arrive_after_a_zoom()
    {
        var control = new TerminalControl { Process = "", FontSize = 12 };
        var host = TerminalHost.Show(control);
        try
        {
            var view = control.View();
            control.ZoomIn();
            Assert.That(view.Zoom, Is.EqualTo(1.1).Within(1e-9), "the control forwards the step");

            control.FontSize = 20;
            Assert.That(view.FontSize, Is.EqualTo(20), "the template binding survived the zoom");
            Assert.That(view.Zoom, Is.EqualTo(1.1).Within(1e-9), "and the zoom survived the font change");
        }
        finally { host.Close(); }
    }

    /// <summary>A zoom gesture is not a reason to drop the selection — unlike a typed key.</summary>
    [AvaloniaTest]
    public void Zoom_keeps_the_selection()
    {
        var (view, _, window) = LiveView();
        try
        {
            view.Terminal.Write("keep me");
            SelectCells(view, "keep me".Length);

            Press(view, TerminalKeyGestures.ZoomIn[0]);

            Assert.That(view.Terminal.Selection.HasSelection, Is.True);
        }
        finally { window.Close(); }
    }

    // ── Scrollback ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Shift+Page Up/Down (Cmd on macOS) move a screenful; Ctrl+Shift+Home/End (Cmd) go to the edges.</summary>
    [AvaloniaTest]
    public async Task Scroll_gestures_move_the_viewport_and_send_nothing()
    {
        var (view, pty, window) = LiveView();
        try
        {
            FillScrollback(view);
            var max = view.MaxScrollback;
            var page = view.ViewportLines;

            Press(view, TerminalKeyGestures.ScrollPageUp[0]);
            Assert.That(view.ViewportY, Is.EqualTo(max - page), "one page up");

            Press(view, TerminalKeyGestures.ScrollPageUp[0]);
            Assert.That(view.ViewportY, Is.EqualTo(max - 2 * page), "two pages up");

            Press(view, TerminalKeyGestures.ScrollPageDown[0]);
            Assert.That(view.ViewportY, Is.EqualTo(max - page), "one page back down");

            Press(view, TerminalKeyGestures.ScrollToTop[0]);
            Assert.That(view.ViewportY, Is.Zero, "to the top");

            Press(view, TerminalKeyGestures.ScrollPageUp[0]);
            Assert.That(view.ViewportY, Is.Zero, "clamped at the top");

            Press(view, TerminalKeyGestures.ScrollToBottom[0]);
            Assert.That(view.ViewportY, Is.EqualTo(max), "back to the tail");

            Press(view, TerminalKeyGestures.ScrollPageDown[0]);
            Assert.That(view.ViewportY, Is.EqualTo(max), "clamped at the tail");

            await Task.Delay(60);
            Assert.That(pty.Written, Is.Empty, "scrolling the scrollback is the view's business, not the program's");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A page up must not be undone by the same keystroke. Any non-modifier key jumps the view back to the
    /// tail so a user who scrolled up does not type blind; the scroll gestures are claimed before that.
    /// </summary>
    [AvaloniaTest]
    public void Scrolling_up_stays_up()
    {
        var (view, _, window) = LiveView();
        try
        {
            FillScrollback(view);
            Press(view, TerminalKeyGestures.ScrollPageUp[0]);
            Assert.That(view.ViewportY, Is.LessThan(view.MaxScrollback));
            Assert.That(view.IsFollowingTail, Is.False, "and the follow is paused, as for any scroll-up");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The alternate screen has no scrollback and a full-screen application may bind these chords itself, so
    /// there the view does not claim them: they reach the program, or on macOS stay with the host.
    /// </summary>
    [AvaloniaTest]
    public async Task Scroll_gestures_belong_to_a_full_screen_application()
    {
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?1049h");
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.IsAlternateBuffer, Is.True, "sanity");

            var e = Press(view, TerminalKeyGestures.ScrollPageUp[0]);

            // On macOS the chord is Cmd+Page Up, and a legacy Cmd chord is never encoded for the program:
            // it is left unhandled for the host application, as Terminal.app leaves it. Not claiming it
            // as a scroll is what standing aside means there.
            if (OperatingSystem.IsMacOS())
            {
                Assert.That(e.Handled, Is.False, "the chord was left to the host, not claimed as a scroll");
                Assert.That(pty.Written, Is.Empty);
            }
            else
            {
                Assert.That(await PtyWaits.AwaitOutput(pty), Is.Not.Empty, "the chord went to the application");
            }
        }
        finally { window.Close(); }
    }

    /// <summary>The scroll methods are public for menus and buttons; a page is the viewport's height.</summary>
    [AvaloniaTest]
    public void Scroll_methods_are_the_same_moves()
    {
        var (view, _, window) = LiveView();
        try
        {
            FillScrollback(view);
            var max = view.MaxScrollback;

            view.ScrollPageUp();
            Assert.That(view.ViewportY, Is.EqualTo(max - view.ViewportLines));
            view.ScrollToTop();
            Assert.That(view.ViewportY, Is.Zero);
            view.ScrollPageDown();
            Assert.That(view.ViewportY, Is.EqualTo(view.ViewportLines));
            view.ScrollToBottom();
            Assert.That(view.ViewportY, Is.EqualTo(max));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Unlike FollowTail, which is a resumption and respects AutoScrollToBottom being off, ScrollToBottom is
    /// an explicit request and always moves.
    /// </summary>
    [AvaloniaTest]
    public void Scroll_to_bottom_moves_even_with_auto_scroll_off()
    {
        var (view, _, window) = LiveView();
        try
        {
            view.AutoScrollToBottom = false;
            FillScrollback(view);
            view.ScrollToTop();
            Assert.That(view.ViewportY, Is.Zero);

            view.FollowTail();
            Assert.That(view.ViewportY, Is.Zero, "FollowTail declines with auto-scroll off");

            view.ScrollToBottom();
            Assert.That(view.ViewportY, Is.EqualTo(view.MaxScrollback), "ScrollToBottom does not");
        }
        finally { window.Close(); }
    }

    // ── ShortcutMode.None ───────────────────────────────────────────────────────────────────────

    /// <summary>None means none: the host owns the keyboard, zoom and scrollback gestures included.</summary>
    [AvaloniaTest]
    public void None_leaves_zoom_and_scroll_gestures_unclaimed()
    {
        var (view, _, window) = LiveView(ShortcutMode.None);
        try
        {
            FillScrollback(view);

            var zoom = Press(view, TerminalKeyGestures.ZoomIn[0]);
            Assert.That(view.Zoom, Is.EqualTo(1.0), "no zoom");

            var scroll = Press(view, TerminalKeyGestures.ScrollPageUp[0]);
            Assert.That(view.ViewportY, Is.EqualTo(view.MaxScrollback), "no scroll");

            // The host's own bindings still work, through the methods.
            view.ZoomIn();
            view.ScrollPageUp();
            Assert.That(view.Zoom, Is.EqualTo(1.1).Within(1e-9));
            Assert.That(view.ViewportY, Is.EqualTo(view.MaxScrollback - view.ViewportLines));
        }
        finally { window.Close(); }
    }

    // ── Forwarders ──────────────────────────────────────────────────────────────────────────────

    [AvaloniaTest]
    public void Control_forwards_zoom_and_scroll()
    {
        var control = new TerminalControl { Process = "" };
        var host = TerminalHost.Show(control);
        try
        {
            var view = control.View();
            FillScrollback(view);

            control.ZoomIn();
            Assert.That(view.Zoom, Is.EqualTo(1.1).Within(1e-9));
            Assert.That(control.Zoom, Is.EqualTo(view.Zoom));
            control.Zoom = 1.5;
            Assert.That(view.Zoom, Is.EqualTo(1.5));
            control.ZoomOut();
            Assert.That(view.Zoom, Is.EqualTo(1.4).Within(1e-9));
            control.ResetZoom();
            Assert.That(view.Zoom, Is.EqualTo(1.0));

            control.ScrollToTop();
            Assert.That(view.ViewportY, Is.Zero);
            control.ScrollPageDown();
            Assert.That(view.ViewportY, Is.EqualTo(view.ViewportLines));
            control.ScrollPageUp();
            Assert.That(view.ViewportY, Is.Zero);
            control.ScrollToBottom();
            Assert.That(view.ViewportY, Is.EqualTo(view.MaxScrollback));
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public void Window_forwards_zoom_and_scroll()
    {
        var window = new TerminalWindow { Process = "" }.Realise();
        try
        {
            var view = window.Control().View();
            FillScrollback(view);

            window.ZoomIn();
            Assert.That(view.Zoom, Is.EqualTo(1.1).Within(1e-9));
            Assert.That(window.Zoom, Is.EqualTo(view.Zoom));
            window.ResetZoom();
            Assert.That(view.Zoom, Is.EqualTo(1.0));

            window.ScrollToTop();
            Assert.That(view.ViewportY, Is.Zero);
            window.ScrollToBottom();
            Assert.That(view.ViewportY, Is.EqualTo(view.MaxScrollback));
        }
        finally { window.Close(); }
    }

    /// <summary>Before the template is applied the forwarders are inert rather than throwing.</summary>
    [AvaloniaTest]
    public void Control_forwarders_are_inert_before_realisation()
    {
        var control = new TerminalControl { Process = "" };
        Assert.That(control.Zoom, Is.EqualTo(1.0));
        Assert.DoesNotThrow(() => { control.ZoomIn(); control.ScrollPageUp(); control.Zoom = 2; });
        Assert.That(control.Zoom, Is.EqualTo(1.0), "ignored, not remembered — this is live state");
    }
}
