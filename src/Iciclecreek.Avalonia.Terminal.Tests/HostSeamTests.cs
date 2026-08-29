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
[TestFixture]
public class HostSeamTests
{
    private const string Esc = "\u001b";
    private const string Bel = "";

    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

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

            view.Terminal.Write($"{Esc}]99;i=b1:p=title;Deploy done{Esc}\\");

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
            view.Terminal.Options.PointerShapesEnabled = true;
            var before = view.Cursor;

            view.Terminal.Write($"{Esc}]22;wait{Bel}");
            Assert.That(view.Cursor, Is.Not.EqualTo(before), "the shape did not take");

            view.Terminal.Write($"{Esc}]22;{Bel}");
            Assert.That(view.Cursor, Is.EqualTo(before), "the reset did not restore the default");
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
            view.Terminal.Options.PointerShapesEnabled = true;
            var before = view.Cursor;
            view.Terminal.Write($"{Esc}]22;zoom-in{Bel}");
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
            var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("from the program"));
            view.Terminal.Write($"{Esc}]52;c;{payload}{Bel}");
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

            var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("host secret"));
            Assert.That(responses, Does.Contain($"{Esc}]52;c;{expected}{Bel}"));
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
}
