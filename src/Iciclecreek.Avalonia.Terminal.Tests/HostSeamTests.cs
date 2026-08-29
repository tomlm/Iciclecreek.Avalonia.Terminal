using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using NUnit.Framework;
using XT = global::XTerm;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The host seams wired to the emulator's clipboard, notification, attention and pointer-shape
/// events: what the view does when the RUNNING PROGRAM asks for things only the host can do.
/// </summary>
/// <remarks>
/// Every seam is raised from <c>Terminal.Write</c>, which the read loop calls on the pty READER
/// thread, so each handler marshals to the UI thread. That is why these tests pump the dispatcher
/// with <c>RunJobs</c> after a write instead of asserting straight away, and why
/// <see cref="The_seams_survive_being_driven_off_the_UI_thread"/> drives the terminal the way the
/// control actually does rather than from the headless UI thread.
/// </remarks>
[TestFixture]
public class HostSeamTests
{
    private const string Esc = "\u001b";
    private const string Bel = "\u0007";
    private const string St = "\u001b\\";

    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    private static string B64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    // ---- notifications --------------------------------------------------------------------

    [AvaloniaTest]
    public void An_OSC9_notification_surfaces_as_the_routed_event()
    {
        var (view, window) = Realised();
        try
        {
            TerminalNotificationEventArgs? seen = null;
            view.NotificationRequested += (_, e) => seen = e;

            view.Terminal.Write($"{Esc}]9;Build finished{Bel}");
            Dispatcher.UIThread.RunJobs();

            Assert.That(seen, Is.Not.Null);
            Assert.That(seen!.Notification.Text, Is.EqualTo("Build finished"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_routed_event_bubbles_to_the_control_and_window()
    {
        // The wrappers forward by re-exposing the routed event, so a handler attached at
        // either level must see a notification raised deep inside the view.
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            var seen = 0;
            control.NotificationRequested += (_, _) => seen++;

            control.View().Terminal.Write($"{Esc}]9;hi{Bel}");
            Dispatcher.UIThread.RunJobs();

            Assert.That(seen, Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_structured_OSC99_notification_carries_its_fields()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Options.KittyNotificationsEnabled = true;
            TerminalNotificationEventArgs? seen = null;
            view.NotificationRequested += (_, e) => seen = e;

            view.Terminal.Write($"{Esc}]99;i=b1:p=title;Deploy done{St}");
            Dispatcher.UIThread.RunJobs();

            Assert.That(seen, Is.Not.Null);
            Assert.That(seen!.Notification.Title, Is.EqualTo("Deploy done"));
            Assert.That(seen.Notification.Identifier, Is.EqualTo("b1"));
        }
        finally { window.Close(); }
    }

    // ---- attention ------------------------------------------------------------------------

    [AvaloniaTest]
    public void RequestAttention_surfaces_with_its_action_verbatim()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Options.WindowOptions.RequestAttention = true;
            var actions = new List<string>();
            view.AttentionRequested += (_, e) => actions.Add(e.Action);

            view.Terminal.Write($"{Esc}]1337;RequestAttention=yes{Bel}");
            view.Terminal.Write($"{Esc}]1337;RequestAttention=no{Bel}");
            Dispatcher.UIThread.RunJobs();

            // "no" reaches the application too: cancelling a pending request is ITS decision,
            // because it owns the dock or taskbar.
            Assert.That(actions, Is.EqualTo(new[] { "yes", "no" }));
        }
        finally { window.Close(); }
    }

    // ---- pointer shapes -------------------------------------------------------------------

    [AvaloniaTest]
    public void A_pointer_shape_becomes_the_controls_cursor_and_reset_restores_it()
    {
        var (view, window) = Realised();
        try
        {
            var before = view.Cursor;

            view.Terminal.Write($"{Esc}]22;wait{Bel}");
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Cursor, Is.Not.EqualTo(before), "the shape did not take");

            view.Terminal.Write($"{Esc}]22;{Bel}");
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Cursor, Is.EqualTo(before), "the reset did not restore the default");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A reset restores the cursor the EMBEDDER set, not null. SetCurrentValue overwrites the local
    /// value, so restoring MapPointerShape(null) left a <c>&lt;TerminalView Cursor="IBeam"/&gt;</c> with
    /// no cursor at all the first time a program set a shape and let it go.
    /// </summary>
    [AvaloniaTest]
    public void A_reset_restores_the_cursor_the_embedder_set()
    {
        var (view, window) = Realised();
        try
        {
            var mine = new Cursor(StandardCursorType.Ibeam);
            view.Cursor = mine;

            view.Terminal.Write($"{Esc}]22;wait{Bel}");
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Cursor, Is.Not.SameAs(mine), "sanity: the program's shape took");

            view.Terminal.Write($"{Esc}]22;{Bel}");
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Cursor, Is.SameAs(mine), "the embedder's own cursor has to come back");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void An_unmapped_shape_falls_back_to_the_default_pointer()
    {
        // kitty accepts names Avalonia has no cursor for (e.g. zoom-in). A wrong cursor
        // misleads; the default merely underwhelms — so unmapped names read as reset.
        var (view, window) = Realised();
        try
        {
            var before = view.Cursor;
            view.Terminal.Write($"{Esc}]22;zoom-in{Bel}");
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Cursor, Is.EqualTo(before));
        }
        finally { window.Close(); }
    }

    // ---- clipboard ------------------------------------------------------------------------

    [AvaloniaTest]
    public void An_OSC52_write_lands_on_the_host_clipboard()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}]52;c;{B64("from the program")}{Bel}");
            Dispatcher.UIThread.RunJobs();

            var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
            var text = clipboard.TryGetTextAsync().GetAwaiter().GetResult();
            Assert.That(text, Is.EqualTo("from the program"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void An_OSC52_read_answers_through_the_deferral_once_enabled()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Options.ClipboardReadEnabled = true;
            var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
            clipboard.SetTextAsync("host secret").GetAwaiter().GetResult();

            var responses = new List<string>();
            view.Terminal.DataReceived += (_, e) => responses.Add(e.Data);

            view.Terminal.Write($"{Esc}]52;c;?{Bel}");
            Dispatcher.UIThread.RunJobs();   // let the deferred clipboard fetch complete

            Assert.That(responses, Does.Contain($"{Esc}]52;c;{B64("host secret")}{Bel}"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void An_OSC52_read_stays_silent_by_default()
    {
        var (view, window) = Realised();
        try
        {
            var responses = new List<string>();
            view.Terminal.DataReceived += (_, e) => responses.Add(e.Data);

            view.Terminal.Write($"{Esc}]52;c;?{Bel}");
            Dispatcher.UIThread.RunJobs();

            Assert.That(responses, Is.Empty);
        }
        finally { window.Close(); }
    }

    // ---- thread affinity --------------------------------------------------------------------

    /// <summary>
    /// Drives the terminal the way the control does — from a thread that is not the UI thread —
    /// because that is the only way the seams are reached in a real session.
    /// </summary>
    private static void WriteOffThread(TerminalView view, params string[] chunks)
    {
        Exception? thrown = null;
        var writer = new Thread(() =>
        {
            try
            {
                foreach (var chunk in chunks)
                    view.Terminal.Write(chunk);
            }
            catch (Exception ex) { thrown = ex; }
        });
        writer.Start();
        writer.Join();

        Assert.That(thrown, Is.Null,
            "a seam threw on the reader thread; the read loop's catch-all would have ended the loop "
            + "and the terminal would show no further output for the rest of its life");
    }

    /// <summary>
    /// The read loop calls <c>Terminal.Write</c> off the UI thread, so every seam is raised there.
    /// Before they marshalled, OSC 22 threw out of <c>SetCurrentValue</c>'s VerifyAccess and killed
    /// the read loop, while the notification and attention events ran an application's handlers on
    /// the reader thread — where anything UI-shaped they did would throw and kill it too.
    /// </summary>
    [AvaloniaTest]
    public void The_seams_survive_being_driven_off_the_UI_thread()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Options.WindowOptions.RequestAttention = true;
            var onUiThread = new List<bool>();
            view.NotificationRequested += (_, _) => onUiThread.Add(Dispatcher.UIThread.CheckAccess());
            view.AttentionRequested += (_, _) => onUiThread.Add(Dispatcher.UIThread.CheckAccess());

            var before = view.Cursor;
            WriteOffThread(view,
                $"{Esc}]9;from the reader thread{Bel}",
                $"{Esc}]1337;RequestAttention=yes{Bel}",
                $"{Esc}]22;wait{Bel}",
                $"{Esc}]52;c;{B64("off-thread copy")}{Bel}");

            Dispatcher.UIThread.RunJobs();

            Assert.That(onUiThread, Is.EqualTo(new[] { true, true }),
                "the application's handlers run on the UI thread, which is the whole point of the events");
            Assert.That(view.Cursor, Is.Not.EqualTo(before), "the pointer shape still took effect");

            var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
            Assert.That(clipboard.TryGetTextAsync().GetAwaiter().GetResult(), Is.EqualTo("off-thread copy"),
                "the set path is thread-affine on Windows; off-thread it failed into a discarded task");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The deferred read has to resume somewhere the clipboard can be touched. Off the UI thread it
    /// resumed on a thread-pool thread with no SynchronizationContext, where Windows' clipboard
    /// declines outright — so OSC 52 read never worked from a real pty even with the opt-in set.
    /// </summary>
    [AvaloniaTest]
    public void A_clipboard_read_driven_off_the_UI_thread_still_answers()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Options.ClipboardReadEnabled = true;
            var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
            clipboard.SetTextAsync("host secret").GetAwaiter().GetResult();

            var responses = new List<string>();
            var answeredOnUiThread = new List<bool>();
            view.Terminal.DataReceived += (_, e) =>
            {
                lock (responses)
                {
                    responses.Add(e.Data);
                    answeredOnUiThread.Add(Dispatcher.UIThread.CheckAccess());
                }
            };

            WriteOffThread(view, $"{Esc}]52;c;?{Bel}");
            Dispatcher.UIThread.RunJobs();

            lock (responses)
            {
                Assert.That(responses, Does.Contain($"{Esc}]52;c;{B64("host secret")}{Bel}"));
                // Respond is what raises DataReceived, so this is where the answer was emitted from.
                // The headless clipboard has no thread affinity, so the Windows decline cannot be
                // reproduced here — the thread the answer lands on can, and it is the contract.
                Assert.That(answeredOnUiThread, Is.All.True,
                    "Respond has to be called from the thread the terminal is driven on");
            }
        }
        finally { window.Close(); }
    }
}
