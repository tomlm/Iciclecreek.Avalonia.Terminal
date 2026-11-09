using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// <see cref="TerminalView.PasteRequested"/>: raised on every paste path with the clipboard's text,
/// able to replace, cancel or hold it, and followed by the terminal's own framing in the program's
/// modes as they are once the handler is done.
/// </summary>
[TestFixture]
public class PasteRequestedTests
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";

    private static async Task<(TerminalView view, RecordingConnection pty, Window window)> LiveView(string clipboardText)
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();

        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        view.Terminal.Options.KeyboardActionModeAllowed = true;
        view.Focus();
        await TopLevel.GetTopLevel(view)!.Clipboard!.SetTextAsync(clipboardText);
        return (view, pty, window);
    }

    private static List<PasteRequestedEventArgs> Record(TerminalView view, Action<PasteRequestedEventArgs>? handle = null)
    {
        var seen = new List<PasteRequestedEventArgs>();
        view.PasteRequested += (_, e) =>
        {
            seen.Add(e);
            handle?.Invoke(e);
        };
        return seen;
    }

    // ---- what the handler sees, and what goes out ----------------------------------------------

    [AvaloniaTest]
    public async Task A_paste_is_offered_to_the_host_and_then_sent()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            var seen = Record(view);

            await view.PasteAsync();

            Assert.That(seen, Has.Count.EqualTo(1));
            Assert.That(seen[0].Text, Is.EqualTo("pasted"));
            Assert.That(seen[0].Source, Is.EqualTo(PasteSource.Api));
            Assert.That(seen[0].Bracketed, Is.False);
            Assert.That(seen[0].Announced, Is.False);
            Assert.That(AwaitOutput(pty), Is.EqualTo("pasted"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task The_host_can_replace_the_text()
    {
        var (view, pty, window) = await LiveView("rm -rf /\r");
        try
        {
            Record(view, e => e.Text = "safe");

            await view.PasteAsync();

            Assert.That(AwaitOutput(pty), Is.EqualTo("safe"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_cancelled_paste_sends_nothing()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            Record(view, e => e.Cancel = true);

            await view.PasteAsync();

            Assert.That(Quiet(pty), Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Replacing_with_nothing_sends_nothing()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            Record(view, e => e.Text = "");

            await view.PasteAsync();

            Assert.That(Quiet(pty), Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_handler_that_throws_cancels_the_paste()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            Record(view, _ => throw new InvalidOperationException("host bug"));

            // Awaited, not Assert.DoesNotThrowAsync: that blocks this UI thread on a paste which needs
            // it to finish. An escaping exception fails the test just the same.
            await view.PasteAsync();
            Assert.That(Quiet(pty), Is.Empty, "a host vetting pastes fails closed");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task The_terminal_brackets_the_text_the_handler_left()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            view.Terminal.Write($"{Esc}[?2004h");
            string? offered = null;
            var seen = Record(view, e => { offered = e.Text; e.Text = "other"; });

            await view.PasteAsync();

            Assert.That(seen[0].Bracketed, Is.True);
            Assert.That(offered, Is.EqualTo("pasted"), "the bare text, not the framed one");
            Assert.That(AwaitOutput(pty), Is.EqualTo($"{Esc}[200~other{Esc}[201~"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task The_replacement_still_replaces_a_keyboard_selection()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            view.Terminal.Write("echo hello world");
            for (var i = 0; i < 5; i++)
                SendKey(view, Key.Left, KeyModifiers.Shift);
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"), "sanity");
            Record(view, e => e.Text = "X");

            await view.PasteAsync();

            var sent = AwaitOutput(pty);
            Assert.That(sent, Does.EndWith("X"));
            Assert.That(sent.Length, Is.GreaterThan(1), "the selection's deletion goes first");
            Assert.That(view.Terminal.Selection.HasSelection, Is.False);
        }
        finally { window.Close(); }
    }

    // ---- deferral --------------------------------------------------------------------------------

    [AvaloniaTest]
    public async Task A_deferral_holds_the_paste_until_it_is_disposed()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            IDisposable? deferral = null;
            PasteRequestedEventArgs? args = null;
            Record(view, e => { args = e; deferral = e.GetDeferral(); });

            var paste = view.PasteAsync();
            Assert.That(Quiet(pty), Is.Empty, "held while the host asks");
            Assert.That(paste.IsCompleted, Is.False);

            args!.Text = "answered";
            deferral!.Dispose();
            await paste;

            Assert.That(AwaitOutput(pty), Is.EqualTo("answered"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Every_deferral_has_to_be_disposed()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            var deferrals = new List<IDisposable>();
            Record(view, e => { deferrals.Add(e.GetDeferral()); deferrals.Add(e.GetDeferral()); });

            var paste = view.PasteAsync();
            deferrals[0].Dispose();
            deferrals[0].Dispose();   // twice counts once
            Assert.That(Quiet(pty), Is.Empty, "one is still out");

            deferrals[1].Dispose();
            await paste;
            Assert.That(AwaitOutput(pty), Is.EqualTo("pasted"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task The_modes_are_read_again_once_the_deferral_ends()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            IDisposable? deferral = null;
            var seen = Record(view, e => deferral = e.GetDeferral());

            var paste = view.PasteAsync();
            view.Terminal.Write($"{Esc}[?2004h");   // the program turns bracketing on while the host asks
            deferral!.Dispose();
            await paste;

            Assert.That(seen[0].Bracketed, Is.False, "as it was when raised");
            Assert.That(AwaitOutput(pty), Is.EqualTo($"{Esc}[200~pasted{Esc}[201~"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_lock_taken_during_the_deferral_still_refuses_the_paste()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            IDisposable? deferral = null;
            Record(view, e => deferral = e.GetDeferral());

            var paste = view.PasteAsync();
            view.Terminal.Write($"{Esc}[2h");
            deferral!.Dispose();
            await paste;

            Assert.That(Quiet(pty), Is.Empty);
        }
        finally { window.Close(); }
    }

    // ---- the session it was meant for --------------------------------------------------------------

    /// <summary>
    /// A paste is for the session it was started in. Held by a host's prompt across a relaunch or a new
    /// connection, the old session's clipboard text, confirmed for that process, must not reach the new one.
    /// </summary>
    [AvaloniaTest]
    public async Task A_paste_held_across_a_new_connection_goes_nowhere()
    {
        var (view, pty, window) = await LiveView("old-session-secret\r");
        try
        {
            IDisposable? deferral = null;
            Record(view, e => deferral = e.GetDeferral());

            var paste = view.PasteAsync();
            var other = new RecordingConnection();
            view.AttachConnection(other);
            deferral!.Dispose();
            await paste;

            Assert.That(Quiet(other), Is.Empty, "the replacement never asked for it");
            Assert.That(pty.Written, Is.Empty, "and the old connection is no longer this view's");
        }
        finally { window.Close(); }
    }

    /// <summary>The same connection object handed back and attached again is a new session too.</summary>
    [AvaloniaTest]
    public async Task A_paste_held_across_a_reattach_of_the_same_connection_goes_nowhere()
    {
        var (view, pty, window) = await LiveView("old-session-secret\r");
        try
        {
            IDisposable? deferral = null;
            Record(view, e => deferral = e.GetDeferral());

            var paste = view.PasteAsync();
            view.DetachConnection();
            view.AttachConnection(pty);
            deferral!.Dispose();
            await paste;

            Assert.That(Quiet(pty), Is.Empty, "a new session, though the object is the same");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Past every earlier check and queued on the writer: the last await before the bytes go out. A
    /// reattach of the same connection object while it waits is a new session all the same.
    /// </summary>
    [AvaloniaTest]
    public async Task A_paste_queued_on_the_writer_across_a_reattach_goes_nowhere()
    {
        var (view, pty, window) = await LiveView("stale-secret\r");
        try
        {
            var writer = (SemaphoreSlim)typeof(TerminalView)
                .GetField("_semaphore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(view)!;
            var queued = false;
            view.InputSent += (_, _) => queued = true;

            await writer.WaitAsync();
            Task paste;
            try
            {
                paste = view.PasteAsync();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!queued && DateTime.UtcNow < deadline)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(10);
                }
                Assert.That(queued, Is.True, "sanity: the paste reached the writer and is waiting on it");

                view.DetachConnection();
                view.AttachConnection(pty);
            }
            finally { writer.Release(); }
            await paste;

            Assert.That(Quiet(pty), Is.Empty, "the new session never asked for it");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task An_announced_paste_held_across_a_new_connection_announces_nothing()
    {
        var (view, pty, window) = await LiveView("old-session-secret");
        try
        {
            view.Terminal.Write($"{Esc}[?5522h");
            IDisposable? deferral = null;
            Record(view, e => deferral = e.GetDeferral());

            var paste = view.PasteAsync();
            var other = new RecordingConnection();
            view.AttachConnection(other);
            view.Terminal.Write($"{Esc}[?5522h");   // the new program asks for announced pastes too
            deferral!.Dispose();
            await paste;

            Assert.That(Quiet(other), Is.Empty);
            Assert.That(pty.Written, Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_paste_held_across_a_new_connection_leaves_the_selection_alone()
    {
        var (view, _, window) = await LiveView("pasted");
        try
        {
            view.Terminal.Write("echo hello world");
            for (var i = 0; i < 5; i++)
                SendKey(view, Key.Left, KeyModifiers.Shift);
            IDisposable? deferral = null;
            Record(view, e => deferral = e.GetDeferral());

            var paste = view.PasteAsync();
            var other = new RecordingConnection();
            view.AttachConnection(other);
            deferral!.Dispose();
            await paste;

            Assert.That(Quiet(other), Is.Empty, "no deletion either");
        }
        finally { window.Close(); }
    }

    // ---- every path ------------------------------------------------------------------------------

    [AvaloniaTest]
    public async Task The_paste_keys_raise_it_as_keyboard()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            var seen = Record(view);

            SendKey(view, Key.V, KeyModifiers.Control | KeyModifiers.Shift);

            Assert.That(AwaitOutput(pty), Is.EqualTo("pasted"));
            Assert.That(seen.Single().Source, Is.EqualTo(PasteSource.Keyboard));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_right_click_with_nothing_selected_raises_it_as_right_click()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            var seen = Record(view);

            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
            view.RaiseEvent(new PointerPressedEventArgs(view, pointer, view, new global::Avalonia.Point(20, 20), 0,
                new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
                KeyModifiers.None)
            {
                RoutedEvent = InputElement.PointerPressedEvent,
            });

            Assert.That(AwaitOutput(pty), Is.EqualTo("pasted"));
            Assert.That(seen.Single().Source, Is.EqualTo(PasteSource.RightClick));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task An_announced_paste_offers_the_text_and_serves_the_replacement()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            view.Terminal.Write($"{Esc}[?5522h");
            string? offered = null;
            var seen = Record(view, e => { offered = e.Text; e.Text = "replaced"; });

            await view.PasteAsync();

            Assert.That(seen.Single().Announced, Is.True);
            Assert.That(offered, Is.EqualTo("pasted"), "the clipboard's text/plain");
            var announce = AwaitOutput(pty);
            Assert.That(announce, Does.Contain("5522;type=read:status=OK"), "announced, not typed");
            Assert.That(announce, Does.Not.Contain("pasted"));

            var start = announce.IndexOf(":pw=") + 4;
            var pw = announce[start..announce.IndexOf('\u001b', start)];
            var mimes = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("text/plain"));
            var before = pty.Written.Length;
            view.Terminal.Write($"{Esc}]5522;type=read:pw={pw}:name=UGFzdGUgZXZlbnQ=;{mimes}{St}");

            var served = AwaitOutput(pty, before)[before..];
            Assert.That(served, Does.Contain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("replaced"))));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_cancelled_announced_paste_announces_nothing()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            view.Terminal.Write($"{Esc}[?5522h");
            Record(view, e => e.Cancel = true);

            await view.PasteAsync();

            Assert.That(Quiet(pty), Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task With_no_handler_a_paste_is_what_it_always_was()
    {
        var (view, pty, window) = await LiveView("pasted");
        try
        {
            view.Terminal.Write($"{Esc}[?2004h");

            await view.PasteAsync();

            Assert.That(AwaitOutput(pty), Is.EqualTo($"{Esc}[200~pasted{Esc}[201~"));
        }
        finally { window.Close(); }
    }

    // ---- the wrappers ----------------------------------------------------------------------------

    [AvaloniaTest]
    public async Task A_control_and_a_window_pass_the_same_args_on()
    {
        var terminalWindow = new TerminalWindow { Process = "" }.Realise();
        try
        {
            var control = terminalWindow.Control();
            var view = control.View();
            var pty = new RecordingConnection();
            view.AttachConnection(pty);
            await TopLevel.GetTopLevel(view)!.Clipboard!.SetTextAsync("pasted");

            object? fromControl = null;
            control.PasteRequested += (s, _) => fromControl = s;
            terminalWindow.PasteRequested += (_, e) => e.Text = "from the window";

            await terminalWindow.PasteAsync();

            Assert.That(fromControl, Is.SameAs(control));
            Assert.That(AwaitOutput(pty), Is.EqualTo("from the window"), "the view acts on what the window set");
        }
        finally { terminalWindow.Close(); }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static void SendKey(TerminalView view, Key key, KeyModifiers modifiers)
    {
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Waits until more than <paramref name="previous"/> characters have been sent.</summary>
    private static string AwaitOutput(RecordingConnection pty, int previous = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && pty.Written.Length <= previous)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        // A beat more, so a write split across two sends is read whole.
        Quiet(pty);
        return pty.Written;
    }

    /// <summary>What has been sent after a short quiet spell, for assertions that expect nothing.</summary>
    private static string Quiet(RecordingConnection pty)
    {
        var until = DateTime.UtcNow.AddMilliseconds(150);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        return pty.Written;
    }
}
