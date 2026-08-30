using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Characters that are produced by something other than one key press.
/// </summary>
/// <remarks>
/// AltGr, dead keys and IMEs all make a character the keyboard has no key for, and all of them
/// reached this control looking like something else: AltGr as a Ctrl+Alt chord, composed text as a
/// TextInput with no key event behind it. Each was then handled as the thing it resembled, and the
/// character was lost.
/// </remarks>
[TestFixture]
public class AltGrAndComposedInputTests
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
        Assert.That(view.IsFocused, Is.True, "sanity: the key handlers return early without focus");
        return (view, pty, window);
    }

    private static void Press(TerminalView v, Key k, KeyModifiers m, string? symbol)
        => v.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = k, KeyModifiers = m, KeySymbol = symbol,
        });

    private static void Release(TerminalView v, Key k, KeyModifiers m, string? symbol)
        => v.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyUpEvent,
            Key = k, KeyModifiers = m, KeySymbol = symbol,
        });

    private static TextInputEventArgs TypeText(TerminalView v, string text)
    {
        var e = new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text };
        v.RaiseEvent(e);
        return e;
    }

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

    private static string Readable(string s)
        => string.Concat(s.Select(c => c < 0x20 || c == 0x7f ? $"\\x{(int)c:x2}" : c.ToString()));

    // --------------------------------------------------------------- AltGr

    [AvaloniaTest]
    public void AltGr_sends_the_character_it_composed()
    {
        // Neither Windows nor X11 has an AltGr modifier: both report it as Ctrl+Alt. So AltGr+Q on a
        // German layout -- which types @ -- arrived indistinguishable from a Ctrl+Alt chord and was
        // turned into a control code. Whole layouts could not type their own symbols.
        var (view, pty, window) = LiveView();
        try
        {
            Press(view, Key.Q, KeyModifiers.Control | KeyModifiers.Alt, "@");
            var sent = Settled(pty);

            Assert.That(sent, Is.EqualTo("@"), "got: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_real_ctrl_alt_chord_is_still_a_chord()
    {
        // The half that must not change, and the reason the test is on the CHARACTER rather than on
        // the modifiers: a genuine Ctrl+Alt+Q reports the key's own letter, because nothing was
        // composed. That is what tells the two apart.
        var (view, pty, window) = LiveView();
        try
        {
            Press(view, Key.Q, KeyModifiers.Control | KeyModifiers.Alt, "q");
            var sent = Settled(pty);

            Assert.That(sent, Is.Not.EqualTo("q"), "a chord must not arrive as a plain letter");
            Assert.That(sent, Does.Contain(Esc), "it is Alt-prefixed, as a chord is: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_plain_ctrl_chord_is_untouched()
    {
        var (view, pty, window) = LiveView();
        try
        {
            Press(view, Key.C, KeyModifiers.Control, "c");
            var sent = Settled(pty);

            Assert.That(sent, Is.EqualTo(((char)0x03).ToString()), "Ctrl+C is still ETX: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------- composed text under Win32

    [AvaloniaTest]
    public void Composed_text_reaches_a_process_reading_input_records()
    {
        // An IME commits its composition through TextInput, and so does the second press of a
        // dead-key pair -- one character, arriving with no key event carrying it. Win32 input mode
        // discarded every TextInput on the grounds that keys are reported from KeyDown, which is true
        // for keys and not for these. Under cmd.exe nothing composed could be typed at all.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?9001h");   // Win32 input mode
            Dispatcher.UIThread.RunJobs();

            TypeText(view, "は");
            var sent = Settled(pty);

            // VK_PACKET is 0xE7 = 231, and the character is its unicode field.
            Assert.That(sent, Does.Contain($"{Esc}[231;0;{(int)'は'};1;0;1_"),
                "expected a VK_PACKET record pair: " + Readable(sent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void An_ordinary_key_is_not_sent_twice_under_Win32()
    {
        // The reason the blanket skip existed. A key that produced a record from KeyDown must not
        // have its text sent again from the TextInput behind it.
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?9001h");
            Dispatcher.UIThread.RunJobs();

            Press(view, Key.A, KeyModifiers.None, "a");
            var afterKey = Settled(pty);

            var textInput = TypeText(view, "a");
            Thread.Sleep(200);

            Assert.That(pty.Written, Is.EqualTo(afterKey),
                "the keystroke was already reported; its text must not be sent again");
            Assert.That(textInput.Handled, Is.True,
                "duplicate text was consumed by the terminal and must not bubble to a parent");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_non_text_key_does_not_hide_a_later_IME_commit_under_Win32()
    {
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?9001h");
            Dispatcher.UIThread.RunJobs();

            Press(view, Key.Left, KeyModifiers.None, null);
            Release(view, Key.Left, KeyModifiers.None, null);
            _ = Settled(pty);
            var beforeComposition = pty.Written;

            TypeText(view, "は");
            var composed = Settled(pty)[beforeComposition.Length..];

            Assert.That(composed, Does.Contain($"{Esc}[231;0;{(int)'は'};1;0;1_"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Non_BMP_composed_text_uses_UTF16_Win32_records()
    {
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write($"{Esc}[?9001h");
            Dispatcher.UIThread.RunJobs();

            TypeText(view, "😀");
            var sent = Settled(pty);

            Assert.That(sent, Does.Contain($"{Esc}[231;0;{(int)'\ud83d'};1;0;1_"));
            Assert.That(sent, Does.Contain($"{Esc}[231;0;{(int)'\ude00'};1;0;1_"));
            Assert.That(sent, Does.Not.Contain("128512"), "UnicodeChar is a 16-bit WCHAR");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Composed_text_is_still_plain_text_outside_Win32_mode()
    {
        var (view, pty, window) = LiveView();
        try
        {
            TypeText(view, "は");
            var sent = Settled(pty);

            Assert.That(sent, Is.EqualTo("は"));
        }
        finally { window.Close(); }
    }

    // --------------------------------------------------- the Win32 key state

    [AvaloniaTest]
    public void The_right_hand_modifiers_are_reported_as_right_hand()
    {
        // Avalonia's KeyModifiers carries no handedness -- Alt is Alt whichever one is down -- so a
        // right modifier was reported as the left one, and a program watching for RIGHT_ALT (which is
        // how Windows spells AltGr) never saw it.
        Assert.That(State(KeyModifiers.Alt, Key.RightAlt) & 0x0001, Is.Not.Zero, "RightAltPressed");
        Assert.That(State(KeyModifiers.Alt, Key.RightAlt) & 0x0002, Is.Zero, "and not LeftAltPressed");

        Assert.That(State(KeyModifiers.Control, Key.RightCtrl) & 0x0004, Is.Not.Zero, "RightCtrlPressed");
        Assert.That(State(KeyModifiers.Control, Key.RightCtrl) & 0x0008, Is.Zero, "and not LeftCtrlPressed");
    }

    [AvaloniaTest]
    public void The_left_hand_modifiers_are_still_reported_as_left_hand()
    {
        Assert.That(State(KeyModifiers.Alt, Key.LeftAlt) & 0x0002, Is.Not.Zero, "LeftAltPressed");
        Assert.That(State(KeyModifiers.Control, Key.LeftCtrl) & 0x0008, Is.Not.Zero, "LeftCtrlPressed");
    }

    private static int State(KeyModifiers modifiers, Key key)
    {
        var m = typeof(TerminalView).GetMethod("GetWin32ControlKeyState",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.That(m, Is.Not.Null, "GetWin32ControlKeyState has been renamed; this test needs updating");
        return (int)m!.Invoke(null, new object[] { modifiers, key })!;
    }

    // -------------------------------------------------- the IME's index space

    [AvaloniaTest]
    public void The_IME_caret_indexes_the_text_the_IME_was_given()
    {
        // The surrounding text was built a cell at a time while the caret was reported as a COLUMN,
        // and the two agree only while every cell holds exactly one char. A grapheme cluster does
        // not, so from the first one on the line the IME was told the caret was somewhere it was not.
        var (view, pty, window) = LiveView();
        try
        {
            // e + combining acute: one cell, two chars.
            view.Terminal.Write("éx");
            Dispatcher.UIThread.RunJobs();

            var client = InputMethodClient(view);
            var text = (string)client.GetType().GetProperty("SurroundingText")!.GetValue(client)!;
            var selection = client.GetType().GetProperty("Selection")!.GetValue(client)!;
            var start = (int)selection.GetType().GetProperty("Start")!.GetValue(selection)!;

            Assert.That(start, Is.LessThanOrEqualTo(text.Length),
                "a caret past the end of the text it indexes is not a position in it");
            Assert.That(text, Does.StartWith("é"), "sanity: the cluster is one cell of two chars");

            // Two cells written, so the caret is at cell 2 -- which is offset 3 in the text, not 2.
            Assert.That(start, Is.EqualTo(3),
                "the caret must be an offset into the surrounding text, not a column number");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_wide_glyph_has_no_phantom_character_in_IME_text()
    {
        var (view, pty, window) = LiveView();
        try
        {
            view.Terminal.Write("界");
            Dispatcher.UIThread.RunJobs();

            var client = InputMethodClient(view);
            var text = (string)client.GetType().GetProperty("SurroundingText")!.GetValue(client)!;
            var selection = client.GetType().GetProperty("Selection")!.GetValue(client)!;
            var start = (int)selection.GetType().GetProperty("Start")!.GetValue(selection)!;

            Assert.That(text, Does.StartWith("界"));
            Assert.That(start, Is.EqualTo(1), "the continuation cell contributes no text");
        }
        finally { window.Close(); }
    }

    private static object InputMethodClient(TerminalView view)
    {
        var f = typeof(TerminalView).GetField("_inputMethodClient",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(f, Is.Not.Null, "_inputMethodClient has been renamed; this test needs updating");
        var client = f!.GetValue(view);
        Assert.That(client, Is.Not.Null, "the view has no IME client");
        return client!;
    }
}
