using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Keyboard Action Mode (KAM, <c>CSI 2 h</c>): an application that is allowed to may lock the keyboard,
/// and while it is locked nothing the user types reaches the process.
/// </summary>
/// <remarks>
/// <para>The emulator gates its own generators, but this view encodes much of what the keyboard sends
/// itself -- Win32 records, AltGr and composed characters, IME commits, the classic paste, the
/// word-motion aliases -- and every one of those used to go straight past the lock. So each test here
/// drives a path the view encodes, not just one the emulator would have caught anyway.</para>
/// <para>xterm's KAM locks only the keyboard: mouse reports and focus events keep flowing, and so does
/// the host's own <see cref="TerminalView.SendInputAsync"/>.</para>
/// </remarks>
[TestFixture]
public class KeyboardLockTests
{
    private static readonly string Esc = ((char)0x1B).ToString();

    /// <summary>"Nothing was sent" can only mean "nothing within this long" -- see <see cref="PtyWaits"/>.</summary>
    private const int QuietMs = 150;

    private static (TerminalView view, RecordingConnection pty, Window window) LiveView(bool allowKam = true)
    {
        var view = new TerminalView { Process = "" };
        var other = new Button { Content = "elsewhere" };
        var window = new Window
        {
            Width = 800, Height = 600,
            Content = new DockPanel { Children = { other, view } },
        };
        DockPanel.SetDock(other, Dock.Top);
        window.Show();
        window.UpdateLayout();

        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        view.Terminal.Options.KeyboardActionModeAllowed = allowKam;
        view.Focus();
        Assert.That(view.IsFocused, Is.True, "sanity: the key handlers return early without focus");
        return (view, pty, window);
    }

    private static void Lock(TerminalView view)
    {
        view.Terminal.Write($"{Esc}[2h");
        Dispatcher.UIThread.RunJobs();
        Assert.That(view.Terminal.KeyboardLocked, Is.True, "sanity: SM 2 locked the keyboard");
    }

    private static KeyEventArgs Press(TerminalView v, Key k, KeyModifiers m = KeyModifiers.None,
                                      string? symbol = null, PhysicalKey p = PhysicalKey.None)
    {
        var e = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = k, KeyModifiers = m, KeySymbol = symbol, PhysicalKey = p,
        };
        v.RaiseEvent(e);
        return e;
    }

    private static void Release(TerminalView v, Key k, KeyModifiers m = KeyModifiers.None,
                                string? symbol = null, PhysicalKey p = PhysicalKey.None)
        => v.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyUpEvent,
            Key = k, KeyModifiers = m, KeySymbol = symbol, PhysicalKey = p,
        });

    private static TextInputEventArgs TypeText(TerminalView v, string text)
    {
        var e = new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text };
        v.RaiseEvent(e);
        return e;
    }

    private static async Task<string> Quiet(RecordingConnection pty)
    {
        await Task.Delay(QuietMs);
        Dispatcher.UIThread.RunJobs();
        return pty.Written;
    }

    private static string Readable(string s)
        => string.Concat(s.Select(c => c < 0x20 || c == 0x7f ? $"\\x{(int)c:x2}" : c.ToString()));

    // ------------------------------------------------------------------ keys

    [AvaloniaTest]
    public async Task Locked_keys_send_nothing()
    {
        var (view, pty, window) = LiveView();
        try
        {
            Lock(view);

            // One at a time, each checked on its own, so a failure names the path that leaked. Tab LAST:
            // were it left unhandled it would move focus away, and every key after it would be dropped
            // for want of focus -- passing for the wrong reason.
            (string name, Key key, KeyModifiers mods, string? symbol)[] keys =
            [
                ("Enter", Key.Enter, KeyModifiers.None, null),
                ("Backspace", Key.Back, KeyModifiers.None, null),
                ("a", Key.A, KeyModifiers.None, "a"),
                ("Ctrl+C", Key.C, KeyModifiers.Control, "c"),                       // SIGINT, nothing selected
                ("Alt+x", Key.X, KeyModifiers.Alt, "x"),                            // Alt-prefixed character
                ("Alt+Left", Key.Left, KeyModifiers.Alt, null),                     // the ESC-b word-motion alias
                ("AltGr+Q", Key.Q, KeyModifiers.Control | KeyModifiers.Alt, "@"),   // @ on a German layout
            ];
            foreach (var (name, key, mods, symbol) in keys)
            {
                Press(view, key, mods, symbol);
                var leaked = await Quiet(pty);
                Assert.That(leaked, Is.Empty, $"{name} reached the process: {Readable(leaked)}");
                Assert.That(view.IsFocused, Is.True, $"sanity: still focused after {name}");
            }

            var tab = Press(view, Key.Tab);
            Assert.That(await Quiet(pty), Is.Empty, "Tab reached the process");
            Assert.That(tab.Handled, Is.True, "a locked Tab is still the terminal's: it must not move focus");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Locked_text_input_and_IME_commits_send_nothing()
    {
        var (view, pty, window) = LiveView();
        try
        {
            Lock(view);

            var typed = TypeText(view, "a");
            TypeText(view, "は");    // an IME commit arrives as TextInput with no key behind it

            var sent = await Quiet(pty);
            Assert.That(sent, Is.Empty, "got: " + Readable(sent));
            Assert.That(typed.Handled, Is.True, "the text was the terminal's to drop, not a parent's to take");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Locked_keys_send_no_Win32_records()
    {
        // Win32 input records are encoded by this view for both halves of a key, and for composed
        // text, so none of them pass through a gated generator.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?9001h");
            Lock(view);

            Press(view, Key.A, symbol: "a");
            Release(view, Key.A, symbol: "a");
            TypeText(view, "は");

            var sent = await Quiet(pty);
            Assert.That(sent, Is.Empty, "got: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Locked_keys_send_nothing_under_Kitty()
    {
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[>15u");    // every flag, event types and all keys included
            Lock(view);

            Press(view, Key.A, symbol: "a", p: PhysicalKey.A);
            Release(view, Key.A, symbol: "a", p: PhysicalKey.A);
            Press(view, Key.LeftShift, p: PhysicalKey.ShiftLeft);
            Release(view, Key.LeftShift, p: PhysicalKey.ShiftLeft);
            Press(view, Key.Left, KeyModifiers.Meta);

            var sent = await Quiet(pty);
            Assert.That(sent, Is.Empty, "got: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Unlocked, Cmd+Left/Right is the terminal's own Home/End alias, not a chord for the host's bindings,
    /// so locking the keyboard must not start handing it to them. Macs only: elsewhere it is no alias.
    /// </summary>
    [AvaloniaTest]
    [TestCase(Key.Left)]
    [TestCase(Key.Right)]
    public async Task A_locked_Cmd_arrow_stays_handled_and_sends_nothing(Key key)
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Ignore("Cmd+Left/Right is the macOS Home/End alias; elsewhere it is a legacy Meta chord, and bubbles");

        var (view, pty, window) = LiveView();
        try
        {
            Lock(view);

            var arrow = Press(view, key, KeyModifiers.Meta);

            Assert.That(arrow.Handled, Is.True, "the alias is the terminal's whether or not it may send");
            Assert.That(await Quiet(pty), Is.Empty);
        }
        finally { window.Close(); }
    }

    /// <summary>The legacy Meta chords stay the host's under the lock. Only the alias changed sides.</summary>
    [AvaloniaTest]
    public async Task A_locked_Meta_chord_still_bubbles_to_the_host()
    {
        var (view, pty, window) = LiveView();
        try
        {
            Lock(view);

            var cmdK = Press(view, Key.K, KeyModifiers.Meta, "k");

            Assert.That(cmdK.Handled, Is.False);
            Assert.That(await Quiet(pty), Is.Empty);
        }
        finally { window.Close(); }
    }

    // ------------------------------------- releases of keys pressed before the lock

    [AvaloniaTest]
    public async Task A_Kitty_release_finishes_a_press_sent_before_the_lock()
    {
        // KAM's ordinary use: the application reads a key, then locks while it acts on it. The release
        // finishes a keystroke it already saw; dropping it leaves Enter held down as far as it knows.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[>15u");    // event types included, so releases are reported
            Press(view, Key.Enter, p: PhysicalKey.Enter);
            var pressed = (await PtyWaits.AwaitOutput(pty)).Length;
            Lock(view);

            Release(view, Key.Enter, p: PhysicalKey.Enter);

            var release = (await PtyWaits.AwaitOutput(pty, pressed))[pressed..];
            Assert.That(release, Is.EqualTo($"{Esc}[13;1:3u"), "got: " + Readable(release));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_Win32_key_up_finishes_a_press_sent_before_the_lock()
    {
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?9001h");
            Press(view, Key.A, symbol: "a", p: PhysicalKey.A);
            var pressed = (await PtyWaits.AwaitOutput(pty)).Length;
            Lock(view);

            Release(view, Key.A, symbol: "a", p: PhysicalKey.A);

            // VK_A is 65; the fourth field is bKeyDown, 0 for the release.
            var release = (await PtyWaits.AwaitOutput(pty, pressed))[pressed..];
            Assert.That(release, Does.Match($@"^\x1b\[65;\d+;\d+;0;\d+;\d+_$"), "got: " + Readable(release));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_key_pressed_during_the_lock_sends_no_release_after_it()
    {
        // The other half: a press that was itself dropped has no release to finish, even once the lock
        // has lifted by the time the key comes up -- and the next press is a press, not a repeat.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[>15u");
            Lock(view);
            Press(view, Key.A, symbol: "a", p: PhysicalKey.A);
            Release(view, Key.A, symbol: "a", p: PhysicalKey.A);
            Assert.That(await Quiet(pty), Is.Empty, "sanity: locked");

            view.Terminal.Write($"{Esc}[2l");
            Press(view, Key.B, symbol: "b", p: PhysicalKey.B);
            var sent = await PtyWaits.AwaitOutput(pty);
            Assert.That(sent, Does.Not.Contain(":2u"), "a fresh press, not a repeat: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_finished_Win32_keystroke_does_not_unlock_a_later_release()
    {
        // A key pressed and released before the lock is no longer down, so pressing it again during the
        // lock is a new keystroke with nothing to finish.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?9001h");
            Press(view, Key.A, symbol: "a", p: PhysicalKey.A);
            Release(view, Key.A, symbol: "a", p: PhysicalKey.A);
            var before = await PtyWaits.AwaitOutput(pty);
            Lock(view);

            Press(view, Key.A, symbol: "a", p: PhysicalKey.A);
            Release(view, Key.A, symbol: "a", p: PhysicalKey.A);

            var after = await Quiet(pty);
            Assert.That(after[before.Length..], Is.Empty, "got: " + Readable(after[before.Length..]));
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------- state across the lock

    [AvaloniaTest]
    public async Task A_deletion_pending_when_the_lock_lands_is_not_sent_after_it()
    {
        // The lock flips on the reader thread, so it can land after a key-down took a selection's
        // deletion and before the TextInput that would have sent it. Through raised events the key-down
        // always sends its own deletion, so the in-between state is set directly: the question is only
        // what the locked TextInput does with it.
        var (view, pty, window) = LiveView();
        try
        {
            var pending = typeof(TerminalView).GetField("_pendingReplaceKeys",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(pending, Is.Not.Null, "_pendingReplaceKeys has been renamed; this test needs updating");
            pending!.SetValue(view, "\x7f\x7f");
            Lock(view);

            TypeText(view, "x");
            view.Terminal.Write($"{Esc}[2l");
            TypeText(view, "は");    // an IME commit, which has no key-down to reassign the field

            var sent = await PtyWaits.AwaitOutput(pty);
            Assert.That(sent, Is.EqualTo("は"), "a stale deletion rode out with the commit: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------------- keyboard selection

    [AvaloniaTest]
    public async Task Keyboard_selection_still_extends_while_locked()
    {
        // Shift+arrow only moves the selection on the view, so it is not the keyboard the lock is for.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write("echo hello world");
            Lock(view);

            KeyEventArgs last = null!;
            for (var i = 0; i < 3; i++)
                last = Press(view, Key.Left, KeyModifiers.Shift);

            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("rld"));
            Assert.That(last.Handled, Is.True);
            Assert.That(await Quiet(pty), Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_locked_Cmd_X_is_left_to_the_host()
    {
        // CanCut is asked before the chord is claimed, so it has to agree with CutAsync, which refuses
        // under the lock. Claimed and then not cut, Cmd+X is a swallowed chord. Under Win32 input mode,
        // where the deletion is records this view encodes: the legacy Backspace comes from the emulator's
        // gated generator, so CanCut would already be false there with or without its own check.
        if (!OperatingSystem.IsMacOS())
            Assert.Ignore("Cmd+X is the macOS cut chord; elsewhere the cut chords are Ctrl-based and the lock consumes them");

        var (view, pty, window) = LiveView();
        try
        {
            var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
            await clipboard.SetTextAsync("previous");
            view.Terminal.Write("echo hello world");
            view.Terminal.Write($"{Esc}[?9001h");
            for (var i = 0; i < 5; i++)
                Press(view, Key.Left, KeyModifiers.Shift);
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"), "sanity");
            Lock(view);

            var cmdX = Press(view, Key.X, KeyModifiers.Meta, "x");

            Assert.That(cmdX.Handled, Is.False, "nothing was cut, so the chord is not the terminal's to claim");
            Assert.That(await Quiet(pty), Is.Empty);
            Assert.That(await clipboard.TryGetTextAsync(), Is.EqualTo("previous"));
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"));
        }
        finally { window.Close(); }
    }

    // ----------------------------------------------------------------- paste

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task A_locked_paste_sends_nothing(bool bracketed)
    {
        var (view, pty, window) = LiveView();
        try
        {
            var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
            await clipboard.SetTextAsync("pasted");
            if (bracketed)
                view.Terminal.Write($"{Esc}[?2004h");
            Lock(view);

            // PasteAsync's own check: called directly, nothing in OnKeyDown stands in front of it.
            await view.PasteAsync();
            var direct = await Quiet(pty);
            Assert.That(direct, Is.Empty, "PasteAsync sent: " + Readable(direct));

            // The standard chords are below the key handler's lock, which is what stops them.
            Press(view, Key.V, KeyModifiers.Control | KeyModifiers.Shift, "V");
            Press(view, Key.Insert, KeyModifiers.Shift);

            var sent = await Quiet(pty);
            Assert.That(sent, Is.Empty, "the paste chords sent: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_locked_announced_paste_sends_nothing()
    {
        // Mode 5522 announces through Terminal.Paste, which declines by itself -- but a keyboard
        // selection's deletion is sent ahead of it, by this view. Under Win32 input mode, because there
        // the deletion is records this view encodes; the legacy Backspace comes from the emulator's own
        // gated generator and would be empty with or without the guard.
        var (view, pty, window) = LiveView();
        try
        {
            var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
            await clipboard.SetTextAsync("pasted");
            view.Terminal.Write("echo hello world");
            view.Terminal.Write($"{Esc}[?5522h{Esc}[?9001h");
            for (var i = 0; i < 5; i++)
                Press(view, Key.Left, KeyModifiers.Shift);
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"), "sanity");
            Lock(view);

            await view.PasteAsync();

            var sent = await Quiet(pty);
            Assert.That(sent, Is.Empty, "got: " + Readable(sent));
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"),
                "nothing replaced the selection, so it is still there");
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------------- cut, select-all

    [AvaloniaTest]
    public async Task A_locked_cut_is_refused_rather_than_becoming_a_copy()
    {
        var (view, pty, window) = LiveView();
        try
        {
            var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
            await clipboard.SetTextAsync("previous");
            view.Terminal.Write("echo hello world");
            for (var i = 0; i < 5; i++)
                Press(view, Key.Left, KeyModifiers.Shift);
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"), "sanity");
            Lock(view);

            var cut = await view.CutAsync();

            Assert.That(cut, Is.False);
            Assert.That(await Quiet(pty), Is.Empty, "the deletion is keystrokes, and the keyboard is locked");
            Assert.That(await clipboard.TryGetTextAsync(), Is.EqualTo("previous"), "and the clipboard is untouched");
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_locked_select_input_selects_nothing()
    {
        // Select-input anchors its selection on the End key it sends. Locked, that key never goes, and a
        // selection anchored as though it had would delete the wrong characters once the lock lifts.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write("echo hello");
            Press(view, Key.Left);   // samples the input start, as typing would
            _ = await PtyWaits.AwaitOutput(pty);
            var before = pty.Written;
            Lock(view);

            Assert.That(await view.SelectInputAsync(), Is.False);
            Assert.That(view.Terminal.Selection.HasSelection, Is.False);
            Assert.That(await Quiet(pty), Is.EqualTo(before));
        }
        finally { window.Close(); }
    }

    // --------------------------------------------- what the lock does not stop

    [AvaloniaTest]
    public async Task Mouse_reports_still_flow_while_locked()
    {
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?1000h");
            Lock(view);

            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
            view.RaiseEvent(new PointerPressedEventArgs(view, pointer, view, new global::Avalonia.Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None)
            {
                RoutedEvent = InputElement.PointerPressedEvent,
            });
            Dispatcher.UIThread.RunJobs();

            var sent = await PtyWaits.AwaitOutput(pty);
            Assert.That(sent, Does.StartWith($"{Esc}[M"), "a press report: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Focus_events_still_flow_while_locked()
    {
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?1004h");
            Lock(view);

            ((DockPanel)window.Content!).Children.OfType<Button>().Single().Focus();
            Dispatcher.UIThread.RunJobs();
            view.Focus();
            Dispatcher.UIThread.RunJobs();

            var sent = await PtyWaits.AwaitOutput(pty, alreadyWritten: 3);
            Assert.That(sent, Is.EqualTo($"{Esc}[O{Esc}[I"), "got: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task SendInput_is_the_hosts_channel_and_is_not_locked()
    {
        var (view, pty, window) = LiveView();
        try
        {
            Lock(view);

            await view.SendInputAsync("ls\r");

            Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo("ls\r"));
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------------ unlocking, and opt-in

    [AvaloniaTest]
    public async Task RM_2_unlocks_the_keyboard()
    {
        var (view, pty, window) = LiveView();
        try
        {
            Lock(view);
            Press(view, Key.A, symbol: "a");
            Assert.That(await Quiet(pty), Is.Empty, "sanity: locked");

            view.Terminal.Write($"{Esc}[2l");
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Terminal.KeyboardLocked, Is.False);

            Press(view, Key.B, symbol: "b");
            TypeText(view, "c");

            Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo("bc"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task SM_2_does_nothing_unless_the_host_allowed_it()
    {
        var (view, pty, window) = LiveView(allowKam: false);
        try
        {
            view.Terminal.Write($"{Esc}[2h");
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Terminal.KeyboardLocked, Is.False);

            Press(view, Key.A, symbol: "a");
            TypeText(view, "b");

            Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo("ab"));
        }
        finally { window.Close(); }
    }
}
