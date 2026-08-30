using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Once an application has negotiated the Kitty keyboard protocol, the encodings the host would
/// otherwise send are not what it is reading.
/// </summary>
/// <remarks>
/// <para>The Kitty check sits some way down the key handler, and three things above it were still
/// answering for keys after the negotiation: two chord translations that exist to speak to a SHELL,
/// and the Windows Escape special case. Each was correct for a terminal sending legacy sequences and
/// each became a key the application never pressed once it had asked for CSI-u.</para>
/// <para>The fourth is the mirror of the same problem on the way up -- releases sent for presses the
/// host had swallowed, so the application was told about a key going up that it was never told had
/// gone down.</para>
/// </remarks>
[TestFixture]
public class KittyDispatchOrderTests
{
    private static readonly string Esc = ((char)0x1B).ToString();

    /// <summary>Flags 0b1111: every event type, so a release is reportable and CSI-u is in use.</summary>
    private const string NegotiateAll = "[>15u";

    private static (TerminalView view, RecordingConnection pty, Window window) LiveView(bool negotiate = true)
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();

        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        view.Focus();
        Assert.That(view.IsFocused, Is.True, "sanity: the key handlers return early without focus");

        if (negotiate)
        {
            view.Terminal.Write(Esc + NegotiateAll);
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.Terminal.KittyKeyboardActive, Is.True,
                "sanity: the protocol has to be live or every assertion here is vacuous");
        }

        return (view, pty, window);
    }

    private static void Press(TerminalView v, Key k, KeyModifiers m = KeyModifiers.None,
                              PhysicalKey p = PhysicalKey.None, string? symbol = null)
        => v.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = k, KeyModifiers = m, PhysicalKey = p, KeySymbol = symbol,
        });

    private static void Release(TerminalView v, Key k, KeyModifiers m = KeyModifiers.None,
                                PhysicalKey p = PhysicalKey.None, string? symbol = null)
        => v.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyUpEvent,
            Key = k, KeyModifiers = m, PhysicalKey = p, KeySymbol = symbol,
        });

    /// <summary>
    /// Waits until what has been sent stops growing, then returns it.
    /// </summary>
    /// <remarks>
    /// The key handlers are async void and await the write, which completes off the dispatcher -- so
    /// pumping the UI thread does not advance it, and a read straight after raising the event sees
    /// the PREVIOUS keystroke's bytes. That lag does not fail honestly: it reads as "nothing was
    /// sent", which is what several of these tests assert, so an impatient read makes a broken
    /// implementation look correct.
    /// </remarks>
    private static string Settled(RecordingConnection pty)
    {
        var last = pty.Written;
        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(25);
            var now = pty.Written;
            if (now == last && now.Length > 0) return now;
            last = now;
        }
        return last;
    }

    /// <summary>Waits for output to grow past a known completed prefix, then lets it settle.</summary>
    private static string AwaitAfter(RecordingConnection pty, int mark)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && pty.Written.Length <= mark)
            Thread.Sleep(10);

        return Settled(pty);
    }

    private static string Readable(string s)
        => string.Concat(s.Select(c => c < 0x20 || c == 0x7f ? $"\\x{(int)c:x2}" : c.ToString()));

    // ------------------------------------------------- chords meant for a shell

    [AvaloniaTest]
    public void Alt_left_is_not_translated_to_a_readline_binding_once_CSI_u_is_negotiated()
    {
        // ESC-b is what zsh, readline, fish and PSReadLine bind for backward-word, and translating
        // to it is right for a shell reading legacy sequences. An application that asked for CSI-u
        // reads Alt+Left as a modified arrow and binds it itself -- ESC-b is then a key nobody
        // pressed, in an encoding it is no longer reading.
        var (view, pty, window) = LiveView();
        try
        {
            Press(view, Key.Left, KeyModifiers.Alt, PhysicalKey.ArrowLeft);
            var sent = AwaitAfter(pty, 0);

            Assert.That(sent, Does.Not.Contain(Esc + "b"),
                "ESC-b is the shell translation and must not survive the negotiation: " + Readable(sent));
            Assert.That(sent, Does.StartWith(Esc + "["),
                "the chord itself, in CSI-u form: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Alt_left_is_still_translated_when_nothing_has_negotiated()
    {
        // The other half of the same rule, and the reason the gate is on the protocol rather than
        // being a removal: without a negotiation the translation is still the right answer, and
        // zsh still echoes ";3D" into the command line without it.
        var (view, pty, window) = LiveView(negotiate: false);
        try
        {
            Press(view, Key.Left, KeyModifiers.Alt, PhysicalKey.ArrowLeft);
            var sent = AwaitAfter(pty, 0);

            Assert.That(sent, Is.EqualTo(Esc + "b"), "got: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Meta_left_reaches_CSI_u_once_Kitty_is_negotiated()
    {
        // The macOS line-edge alias deliberately stands down under Kitty, but the general Meta
        // passthrough must stand down too. Otherwise the chord falls into the gap between them and
        // produces neither the legacy Home sequence nor the negotiated modified-arrow event.
        var (view, pty, window) = LiveView();
        try
        {
            Press(view, Key.Left, KeyModifiers.Meta, PhysicalKey.ArrowLeft);

            // AwaitAfter, not Settled. Settled gives the FIRST byte one second and then answers with
            // whatever it has -- which for a send that has not started yet is the empty string, and
            // "nothing yet" reads exactly like "nothing sent". A cold macOS runner spinning up a
            // thread pool takes longer than that, so this failed there and nowhere else.
            var sent = AwaitAfter(pty, 0);

            Assert.That(sent, Is.Not.Empty, "the negotiated protocol must receive the Meta chord");
            Assert.That(sent, Does.StartWith(Esc + "["),
                "the chord itself, in CSI-u form: " + Readable(sent));
            Assert.That(sent, Is.Not.EqualTo(Esc + "[H"),
                "the legacy macOS line-edge alias must remain disabled under Kitty");
        }
        finally { window.Close(); }
    }

    // ---------------------------------------------- releases without a press

    [AvaloniaTest]
    public void A_release_is_not_reported_for_a_press_the_host_swallowed()
    {
        // Shift+arrow extends a selection and returns, so the application is never told the key went
        // down. It still produces a key-up, and that used to be encoded and sent -- an application
        // that negotiated event types was told a key it had never seen pressed had been released.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write("hello");
            Thread.Sleep(60);

            Press(view, Key.Left, KeyModifiers.Shift, PhysicalKey.ArrowLeft);
            Thread.Sleep(60);
            Assert.That(view.Terminal.Selection.HasSelection, Is.True,
                "sanity: the press has to be swallowed by the selection for this to be the case under test");
            Assert.That(pty.Written, Is.Empty, "sanity: and nothing was sent for it");

            Release(view, Key.Left, KeyModifiers.Shift, PhysicalKey.ArrowLeft);
            Thread.Sleep(200);

            Assert.That(pty.Written, Is.Empty,
                "a release belongs to the host when its press did: " + Readable(pty.Written));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_release_is_still_reported_for_a_press_that_went_through()
    {
        // The guard is "did this path send the press", not "suppress releases", and the difference
        // is the whole point -- an application that negotiated event types is entitled to the
        // releases for keys it actually received.
        var (view, pty, window) = LiveView();
        try
        {
            Press(view, Key.A, KeyModifiers.None, PhysicalKey.A, "a");
            var afterPress = AwaitAfter(pty, 0);
            Assert.That(afterPress, Is.Not.Empty, "sanity: the press went out");

            Release(view, Key.A, KeyModifiers.None, PhysicalKey.A, "a");
            var afterRelease = AwaitAfter(pty, afterPress.Length);

            Assert.That(afterRelease.Length, Is.GreaterThan(afterPress.Length),
                "the matching release must still be reported: " + Readable(afterRelease));
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------------- Escape on Windows

    [AvaloniaTest]
    public void Escape_reaches_the_negotiated_protocol_rather_than_the_Win32_path()
    {
        // The Windows Escape special case exists because ConPTY has no VT sequence for a plain
        // Escape, so Win32 records are the only way to deliver one. It was applied unconditionally,
        // so on Windows a negotiated application never received CSI 27 u -- the encoding it had
        // asked for, and one the terminal is perfectly able to send.
        //
        // Asserted on every platform. The special case is Windows-only, so elsewhere this is a guard
        // that the ordering has not been rearranged; on Windows it is the regression test.
        var (view, pty, window) = LiveView();
        try
        {
            Press(view, Key.Escape, KeyModifiers.None, PhysicalKey.Escape);
            var sent = AwaitAfter(pty, 0);

            Assert.That(sent, Does.Contain("27"),
                "Escape is keycode 27 in CSI-u: " + Readable(sent));
            Assert.That(sent, Does.Not.EndWith("_"),
                "a trailing _ is a Win32 input record, which is not what was negotiated: " + Readable(sent));
        }
        finally { window.Close(); }
    }
}
