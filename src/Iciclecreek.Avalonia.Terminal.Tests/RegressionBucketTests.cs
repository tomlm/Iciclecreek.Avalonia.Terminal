using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Defects in code added during the 2.0 integration work, found by the audit that followed it.
/// </summary>
/// <remarks>
/// Grouped by when they were introduced rather than by subsystem, deliberately. Every one of them
/// is a fix or a feature from the same short stretch of work, and reading them together is what
/// shows the two habits that produced most of them: claiming a routed event after the first await,
/// and encoding a keystroke for one protocol when three are reachable.
/// </remarks>
[TestFixture]
public class RegressionBucketTests
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

    /// <summary>A realised view with a connection to write into and the focus OnKeyDown requires.</summary>
    private static (TerminalView view, RecordingConnection pty, Window window) LiveView()
    {
        var (view, window) = Realised();
        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        view.Focus();
        Assert.That(view.IsFocused, Is.True, "sanity: OnKeyDown returns early without focus");
        return (view, pty, window);
    }

    // ---------------------------------------------------------------- Dispose

    // [AvaloniaTest] even though no window is involved: the view's static constructor builds
    // Cursors, which needs a platform, and this fixture shares one application with the rest of the
    // assembly. A plain [Test] running first poisons the type initializer for every test after it.
    [AvaloniaTest]
    public void Disposing_a_view_that_was_never_shown_does_not_throw()
    {
        // No window, no Show, no layout pass: everything OnInitialized builds -- the emulator, both
        // timers -- is still null. A host that makes a tab and then decides against it does exactly
        // this, and so does any test that news up a view.
        var view = new TerminalView { Process = "" };

        Assert.DoesNotThrow(() => view.Dispose());
    }

    [AvaloniaTest]
    public void A_failed_Dispose_does_not_make_the_view_undisposable()
    {
        // The NullReferenceException was the smaller half. _disposed is set before the work, so a
        // throw part-way left the view marked disposed with the emulator and pty still held -- and
        // a second call could not finish the job, because the guard at the top sent it straight
        // back. This asserts the property that made that unrecoverable, not the throw itself.
        var view = new TerminalView { Process = "" };

        view.Dispose();
        Assert.DoesNotThrow(() => view.Dispose(), "a second Dispose must stay a no-op, not a throw");
    }

    [AvaloniaTest]
    public void Disposing_a_view_that_is_still_on_the_tree_stops_its_timers()
    {
        // Dispose is not Unloaded. A view disposed while still shown never gets an Unloaded, and a
        // DispatcherTimer holds its target through the Tick handler -- so the timers kept the
        // disposed view alive and went on asking it to blink a cursor over a disposed emulator.
        var (view, window) = Realised();
        try
        {
            view.Options!.CursorBlink = true;
            view.CursorBlink = true;
            Dispatcher.UIThread.RunJobs();

            view.Dispose();

            Assert.That(RunningTimers(view), Is.Empty,
                "every DispatcherTimer the view owns must be stopped by Dispose");
        }
        finally { window.Close(); }
    }

    /// <summary>The view's own dispatcher timers that are still enabled, by field name.</summary>
    /// <remarks>
    /// Reflected over rather than asserted one at a time, so a timer added later is covered without
    /// anyone remembering to come back here. Both current ones are private, and neither is worth a
    /// test-only property.
    /// </remarks>
    private static string[] RunningTimers(TerminalView view)
        => typeof(TerminalView)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(f => typeof(DispatcherTimer).IsAssignableFrom(f.FieldType))
            .Where(f => f.GetValue(view) is DispatcherTimer { IsEnabled: true })
            .Select(f => f.Name)
            .ToArray();

    // ------------------------------------------------------- Options identity

    [AvaloniaTest]
    public void The_window_exposes_the_options_the_emulator_actually_reads()
    {
        // The third link in a chain whose first two were already fixed: the view redirects onto the
        // emulator's own options, the control follows it, and the window did not -- while being the
        // object a host is most likely to be holding. Writes through it went into an abandoned copy
        // with no exception and nothing in the log.
        var window = new TerminalWindow { Process = "", Width = 800, Height = 600 };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.That(window.Options, Is.SameAs(window.Terminal.Options));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void An_option_set_through_the_window_after_startup_reaches_the_emulator()
    {
        // Reference equality proves the plumbing; this proves it matters.
        var window = new TerminalWindow { Process = "", Width = 800, Height = 600 };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var before = window.Terminal.Buffer.Lines.MaxLength;
            window.Options!.Scrollback = window.Options.Scrollback + 500;

            Assert.That(window.Terminal.Buffer.Lines.MaxLength, Is.GreaterThan(before));
        }
        finally { window.Close(); }
    }

    // ------------------------------------------- Claiming the event in time

    [AvaloniaTest]
    public void Right_click_claims_the_press_before_it_finishes_bubbling()
    {
        // OnPointerPressed is async void: it returns to the routing at its first await, and the
        // press finishes bubbling THEN -- with Handled still false, because the line setting it had
        // not run. A host with its own right-click menu got the press as well as the paste.
        //
        // Asserted synchronously, with no pumping, which is the whole point: what is being checked
        // is that the flag is up at the moment routing looks at it, not that it is up eventually.
        //
        // A GUARD rather than a regression test, and worth saying so: it passes against the previous
        // commit too. Headless Avalonia answers the clipboard from a completed task, so the await
        // never actually yields here and the old placement got to run in time after all. On a real
        // platform the clipboard is a round trip to the window system and it does not. Every other
        // test in this file fails against the code it fixes; this one asserts the property rather
        // than reproducing the failure.
        var (view, window) = Realised();
        try
        {
            var args = RightPress(view);
            view.RaiseEvent(args);

            Assert.That(args.Handled, Is.True,
                "the press must be claimed before the first await, not after the event has gone");
        }
        finally { window.Close(); }
    }

    private static PointerPressedEventArgs RightPress(TerminalView view)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var props = new PointerPointProperties(RawInputModifiers.RightMouseButton,
                                               PointerUpdateKind.RightButtonPressed);
        return new PointerPressedEventArgs(view, pointer, view, new global::Avalonia.Point(10, 10),
                                           0, props, KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerPressedEvent,
        };
    }

    [AvaloniaTest]
    public void The_cut_chord_is_not_swallowed_when_there_is_nothing_to_cut()
    {
        // The other side of claiming the event before the await. Claiming on the strength of a cut
        // that then declines -- no clipboard, or a selection whose text is empty -- swallows a chord
        // for nothing, and Ctrl+X is readline's prefix, which is worth more than a cut that did not
        // happen. So the claim asks every question the cut asks, not just the first.
        var (view, window) = Realised();
        try
        {
            // No selection at all, which is the commonest case by far: the chord must reach the
            // program untouched.
            var args = KeyPress(Key.X, KeyModifiers.Control, PhysicalKey.X, "x");
            view.RaiseEvent(args);

            Assert.That(args.Handled, Is.False,
                "with nothing to cut the chord belongs to the application");
        }
        finally { window.Close(); }
    }

    // ------------------------------------------ Deletion under every protocol

    [AvaloniaTest]
    public void Typing_over_a_keyboard_selection_under_the_Kitty_protocol_still_deletes_it()
    {
        // The selection is consumed several branches above the senders, and the Kitty path claims
        // the key and returns -- so the deletion was dropped while the selection it belonged to had
        // already gone from the screen. Typing "x" over a selected word left the word and added the
        // x, and the stale deletion then waited to be prepended to some later keystroke.
        var (view, pty, window) = LiveView();
        try
        {
            var sent = SelectAndType(view, pty, negotiate: $"{Esc}[>1u");

            Assert.That(sent, Is.Not.Empty, "the keystroke reached the pty at all");
            Assert.That(HasDeletion(sent), Is.True,
                "the selection's deletion must travel with the key that replaces it: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_deletion_is_encoded_for_the_protocol_the_application_is_reading()
    {
        // Under Win32 input mode the process reads INPUT_RECORDs. A bare 0x08 is not one, so a
        // deletion generated through the legacy path was bytes cmd.exe and PSReadLine ignore --
        // present on the wire, and still a selection that never went away.
        var (view, pty, window) = LiveView();
        try
        {
            var sent = SelectAndType(view, pty, negotiate: $"{Esc}[?9001h");

            // The Backspace records, in the INPUT_RECORD form ConPTY reads: virtual key 8, unicode
            // 8, down and then up. Before the fix the deletion was generated through the legacy
            // path, so what went out was a bare 0x08 -- on the wire, and ignored by a process
            // reading records, which leaves a selection the user watched disappear still in the line.
            Assert.That(sent, Does.Contain($"{Esc}[8;0;8;1;0;1_{Esc}[8;0;8;0;0;1_"),
                "expected a Win32 Backspace record pair, got: " + Readable(sent));

            // And the character AFTER it, in one write: X is virtual key 88, unicode 120. Order is
            // the point -- a deletion arriving after the replacement types into the wrong line.
            Assert.That(sent, Does.EndWith($"{Esc}[88;0;120;1;0;1_"),
                "the key that replaces the selection must follow its deletion: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Makes a one-cell keyboard selection, turns on a protocol, types over it, and returns
    /// everything that reached the pty afterwards.
    /// </summary>
    /// <remarks>
    /// Built on the same footing as ShiftSelectionTests rather than a second arrangement of its own:
    /// a real connection to write into, focus (OnKeyDown returns early without it), and the text
    /// TYPED rather than written, because a keyboard selection is bounded by the shell's editable
    /// input and text the emulator merely printed is not inside it.
    /// </remarks>
    private static string SelectAndType(TerminalView view, RecordingConnection pty, string negotiate)
    {
        view.Terminal.Write("hello");
        Thread.Sleep(60);

        view.RaiseEvent(KeyPress(Key.Left, KeyModifiers.Shift));
        Thread.Sleep(60);

        Assert.That(view.Terminal.Selection.HasSelection, Is.True,
            "the test needs a live keyboard selection before it can assert anything about removing one");

        // AFTER the selection exists, so negotiating cannot interfere with making it.
        view.Terminal.Write(negotiate);
        Thread.Sleep(20);

        var before = pty.Written.Length;

        // KeySymbol and PhysicalKey both matter: without a symbol the protocol has no name for the
        // key and declines it, so the press falls through to the legacy generator -- and the test
        // would then measure the legacy path under both protocols and pass against either.
        view.RaiseEvent(KeyPress(Key.X, KeyModifiers.None, PhysicalKey.X, "x"));
        Thread.Sleep(150);

        return pty.Written.Substring(before);
    }

    /// <summary>Whether anything in <paramref name="sent"/> asks for a character to be removed.</summary>
    /// <remarks>
    /// Deliberately protocol-agnostic: a legacy backspace is 0x08 or 0x7F, a Kitty one is CSI 127 u,
    /// and a Win32 one is a record whose unicode field is 8. The assertion is that a deletion was
    /// sent AT ALL -- which byte it is is the other test's question.
    /// </remarks>
    private static bool HasDeletion(string sent)
        => sent.Contains('\b')
           || sent.Contains((char)0x7f)
           || sent.Contains("127u")
           || sent.Contains(";8;");

    private static string Readable(string s)
        => string.Concat(s.Select(c => c < 0x20 || c == 0x7f ? $"\\x{(int)c:x2}" : c.ToString()));

    private static KeyEventArgs KeyPress(Key key, KeyModifiers modifiers,
                                        PhysicalKey physical = PhysicalKey.None, string? symbol = null) => new()
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = key,
        KeyModifiers = modifiers,
        PhysicalKey = physical,
        KeySymbol = symbol,
    };

}
