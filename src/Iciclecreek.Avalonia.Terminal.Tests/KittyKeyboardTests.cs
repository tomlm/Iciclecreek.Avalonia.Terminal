using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using System.Threading;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The Kitty keyboard protocol, from the host's side: sending what an application asked for once
/// it has negotiated the protocol, instead of the legacy encodings it has stopped reading.
/// </summary>
/// <remarks>
/// The emulator accepts the negotiation on the host's behalf and records the flags, so an
/// application that sends <c>CSI &gt; flags u</c> believes it is receiving CSI-u encoded keys from
/// that moment. Every keystroke went through the legacy generators regardless, which fails in
/// exactly the applications that ask for the protocol.
/// </remarks>
[TestFixture]
public class KittyKeyboardTests
{
    // Built rather than written as a literal: an ESC byte does not survive every tool that touches
    // a source file, and a silently empty constant turns every sequence below into plain text.
    private static readonly string Esc = ((char)0x1B).ToString();

    private static (TerminalView view, RecordingConnection pty, Window window) LivePty()
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

    /// <summary>Negotiate the protocol the way an application does, asking for event types too.</summary>
    private static void Negotiate(TerminalView view, int flags = 15)
    {
        view.Terminal.Write($"{Esc}[>{flags}u");
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(TerminalView v, Key k, KeyModifiers m = KeyModifiers.None,
                              PhysicalKey p = PhysicalKey.None, string? symbol = null)
        => v.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = k,
            KeyModifiers = m,
            PhysicalKey = p,
            KeySymbol = symbol,
        });

    private static void Release(TerminalView v, Key k, KeyModifiers m = KeyModifiers.None,
                                PhysicalKey p = PhysicalKey.None, string? symbol = null)
        => v.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyUpEvent,
            Key = k,
            KeyModifiers = m,
            PhysicalKey = p,
            KeySymbol = symbol,
        });

    /// <summary>
    /// Waits until what has been sent stops growing, then returns it.
    /// </summary>
    /// <remarks>
    /// The key handlers are async void and await the write, which completes on a thread pool rather
    /// than on the dispatcher -- so pumping the UI thread does not advance it and a read straight
    /// after raising the event sees nothing yet. That does not fail honestly: it reads as "nothing
    /// was sent", which is exactly what several of these tests assert, so an impatient read would
    /// make a broken implementation look correct. Observed as a one-event LAG, where a keystroke's
    /// bytes only appeared once the NEXT keystroke was raised.
    /// </remarks>
    private static string Settle(RecordingConnection pty)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        var seen = pty.Written;
        var stable = 0;

        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);

            var now = pty.Written;
            if (now == seen)
            {
                if (++stable >= 5)
                    break;
            }
            else
            {
                seen = now;
                stable = 0;
            }
        }

        return seen;
    }

    /// <summary>How much has been sent so far, so a test can read only what follows.</summary>
    private static int Mark(RecordingConnection pty) => Settle(pty).Length;

    /// <summary>Everything sent since <paramref name="mark"/>.</summary>
    /// <remarks>
    /// For assertions that expect NOTHING. Settle's quiet window is what bounds how long "nothing"
    /// is watched for; a test expecting output must use <see cref="AwaitSince"/> instead, or a slow
    /// machine turns it into a race -- see there.
    /// </remarks>
    private static string Since(RecordingConnection pty, int mark)
    {
        var all = Settle(pty);
        return mark <= all.Length ? all.Substring(mark) : all;
    }

    /// <summary>Everything sent since <paramref name="mark"/>, waiting for it to BEGIN first.</summary>
    /// <remarks>
    /// Settle answers "has it stopped", and an empty stream that stays empty for its quiet window
    /// answers yes -- so a read that expects output raced the async key handler's FIRST byte, and
    /// lost on a cold CI runner where the thread pool had not spun up. It only ever failed in the
    /// one test with no prior traffic: every other test negotiates first, which primes the same
    /// pipeline it then reads. This waits for growth past the mark before asking Settle when the
    /// growth has finished; the deadline keeps a genuinely-broken implementation failing.
    /// </remarks>
    private static string AwaitSince(RecordingConnection pty, int mark)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && Settle(pty).Length <= mark)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        return Since(pty, mark);
    }

    [AvaloniaTest]
    public void A_negotiated_application_receives_CSI_u_rather_than_the_legacy_encoding()
    {
        var (view, pty, window) = LivePty();
        try
        {
            Negotiate(view);
            var mark = Mark(pty);

            Press(view, Key.Up, p: PhysicalKey.ArrowUp);

            var sent = AwaitSince(pty, mark);
            Assert.That(sent, Does.StartWith($"{Esc}["), $"observed {Escape(sent)}");
            Assert.That(sent, Does.EndWith("u").Or.EndWith("A"),
                $"a CSI-u form, or the CSI-A the protocol keeps for an unmodified arrow: {Escape(sent)}");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Without_the_negotiation_nothing_changes()
    {
        // The legacy path has to stay exactly as it was for every application that never asks.
        var (view, pty, window) = LivePty();
        try
        {
            var mark = Mark(pty);

            Press(view, Key.Up, p: PhysicalKey.ArrowUp);

            Assert.That(AwaitSince(pty, mark), Is.EqualTo($"{Esc}[A"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_release_is_reported_once_the_flags_ask_for_event_types()
    {
        // Releases exist as events at all only under this protocol: the legacy encodings describe
        // presses and repeats, which is why key-up reached nothing but the Win32 path before.
        var (view, pty, window) = LivePty();
        try
        {
            Negotiate(view);
            Press(view, Key.A, p: PhysicalKey.A, symbol: "a");
            Dispatcher.UIThread.RunJobs();
            var mark = Mark(pty);

            Release(view, Key.A, p: PhysicalKey.A, symbol: "a");

            Assert.That(AwaitSince(pty, mark), Is.Not.Empty, "a release the flags asked for must be reported");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_release_is_never_answered_with_a_legacy_encoding()
    {
        // The rule that is easiest to get wrong: a null from the generator means SEND NOTHING, not
        // "try the legacy path". Falling through on a release would turn key-up into a second
        // key-down, which is the original bug wearing a different hat.
        var (view, pty, window) = LivePty();
        try
        {
            // Flags that do NOT include reporting event types, so releases are not wanted.
            Negotiate(view, flags: 1);
            Press(view, Key.Up, p: PhysicalKey.ArrowUp);
            Dispatcher.UIThread.RunJobs();
            var mark = Mark(pty);

            Release(view, Key.Up, p: PhysicalKey.ArrowUp);

            Assert.That(Since(pty, mark), Is.Empty,
                "the release was not asked for, so it sends nothing at all");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_bare_modifier_press_sends_nothing_rather_than_falling_through()
    {
        var (view, pty, window) = LivePty();
        try
        {
            Negotiate(view, flags: 1);
            var mark = Mark(pty);

            Press(view, Key.LeftShift, KeyModifiers.Shift, PhysicalKey.ShiftLeft);

            Assert.That(Since(pty, mark), Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_second_press_of_a_held_key_is_a_repeat_rather_than_another_press()
    {
        // Avalonia's KeyEventArgs carries no repeat flag, so the held keys are tracked here. An
        // application that negotiated event types asked to tell the two apart.
        var (view, pty, window) = LivePty();
        try
        {
            Negotiate(view);
            var start = Mark(pty);

            Press(view, Key.A, p: PhysicalKey.A, symbol: "a");
            var first = AwaitSince(pty, start);

            var mark = Mark(pty);
            Press(view, Key.A, p: PhysicalKey.A, symbol: "a");
            var second = AwaitSince(pty, mark);

            Assert.That(second, Is.Not.EqualTo(first),
                $"a repeat must not encode identically to the press: {Escape(first)} vs {Escape(second)}");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_key_released_and_pressed_again_is_a_press_again()
    {
        var (view, pty, window) = LivePty();
        try
        {
            Negotiate(view);
            var start = Mark(pty);

            Press(view, Key.A, p: PhysicalKey.A, symbol: "a");
            var first = AwaitSince(pty, start);

            // The release REPORTS under these flags, and its bytes land off-thread like any other
            // key's. Marking before they arrive puts them after the mark, and the next read then
            // swallows a release sequence it was never meant to see -- which on a slow runner it
            // did. Drain it first, so the mark truly separates the release from the press.
            var beforeRelease = Mark(pty);
            Release(view, Key.A, p: PhysicalKey.A, symbol: "a");
            Dispatcher.UIThread.RunJobs();
            AwaitSince(pty, beforeRelease);

            var mark = Mark(pty);
            Press(view, Key.A, p: PhysicalKey.A, symbol: "a");

            Assert.That(AwaitSince(pty, mark), Is.EqualTo(first),
                "the key was let go, so this is a fresh press rather than a repeat");
        }
        finally { window.Close(); }
    }

    private static string Escape(string s)
        => s.Replace(((char)0x1B).ToString(), "<ESC>");
}
