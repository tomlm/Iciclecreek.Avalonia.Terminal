using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Shift + navigation extends a selection in the buffer instead of sending the modified-cursor sequence.
///
/// <para>No interactive shell binds ESC[1;2C, so what the emulator would otherwise send comes back as the
/// literal text ";2C" in the command line — the same failure as the word-motion keys, one modifier over.</para>
/// </summary>
[TestFixture]
public class ShiftSelectionTests
{
    private const string Esc = "\u001b";

    private static (TerminalView view, RecordingConnection pty, Window window) LiveView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        view.Focus();
        Assert.That(view.IsFocused, Is.True, "sanity: OnKeyDown returns early without focus");
        return (view, pty, window);
    }

    private static void Press(TerminalView v, Key k, KeyModifiers m = KeyModifiers.None)
        => v.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = m });

    /// <summary>
    /// One Shift+Left selects exactly ONE cell. That is the whole reason anchor and focus are caret
    /// boundaries rather than cell indices — counting cells makes the first press select two.
    /// </summary>
    /// <remarks>
    /// Measured leftwards over written text. Rightwards from a fresh view there is nothing to select: the
    /// grid past the input is blank, and a selection is now bounded by the end of the input.
    /// </remarks>
    [AvaloniaTest]
    public async Task Shift_left_selects_exactly_one_cell()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "a selection started");
        Assert.That(pty.Written, Is.Empty, "and nothing was sent to the shell");
        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("o"),
            "exactly one cell, not two");

        window.Close();
    }

    /// <summary>Collapsing back onto the anchor clears the selection, the way an editor does.</summary>
    [AvaloniaTest]
    public async Task Collapsing_back_onto_the_anchor_clears_it()
    {
        var (view, pty, window) = LiveView();

        Type(view, "hello");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: something is selected");

        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(40);

        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "back at the anchor means no selection");
        window.Close();
    }

    /// <summary>
    /// The point of the change: these keys must not reach the shell. Previously each sent a modified-cursor
    /// sequence that no default keymap binds, so zsh echoed its tail into the command line.
    /// </summary>
    [TestCase(Key.Left)]
    [TestCase(Key.Right)]
    [TestCase(Key.Up)]
    [TestCase(Key.Down)]
    [TestCase(Key.Home)]
    [TestCase(Key.End)]
    [AvaloniaTest]
    public async Task Shift_navigation_is_not_sent_to_the_shell(Key key)
    {
        var (view, pty, window) = LiveView();
        Press(view, key, KeyModifiers.Shift);
        await Task.Delay(60);
        Assert.That(pty.Written, Is.Empty, $"Shift+{key} extends a selection; it is not shell input");
        window.Close();
    }

    /// <summary>
    /// Left alone in the alternate buffer: full-screen apps draw their own selection and several bind
    /// Shift+arrow themselves, so there the sequence still belongs to the app.
    /// </summary>
    [AvaloniaTest]
    public async Task The_alternate_buffer_still_gets_the_sequence()
    {
        var (view, pty, window) = LiveView();

        view.Terminal.Write("\u001b[?1049h");     // switch to the alternate buffer
        await Task.Delay(60);
        Assert.That(view.Terminal.IsAlternateBufferActive, Is.True, "sanity: in the alternate buffer");

        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(pty.Written, Is.Not.Empty, "a full-screen app reads the real sequence itself");
        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "and no buffer selection was made");

        window.Close();
    }

    /// <summary>A plain keystroke drops the selection, and the next Shift+arrow re-anchors at the cursor.</summary>
    [AvaloniaTest]
    public async Task A_plain_keystroke_drops_the_selection()
    {
        var (view, pty, window) = LiveView();

        Type(view, "hello");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity");

        Press(view, Key.A);
        await Task.Delay(40);

        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "typing clears it");
        window.Close();
    }

    // ── Word-wise extension (#63) ───────────────────────────────────────────────────────────────

    /// <summary>Put known text in the buffer and leave the cursor at the end of it.</summary>
    private static void Type(TerminalView view, string text)
    {
        view.Terminal.Write(text);
    }

    /// <summary>
    /// Ctrl+Shift+Left extends the selection by a WORD, the way it does in every text field. Reported as
    /// #63: it moved to the word boundary but dropped the selection, because Control|Shift matched neither
    /// the Shift-selection gate nor the word-motion gate and fell through to the blanket selection-clear.
    /// </summary>
    [AvaloniaTest]
    public async Task Ctrl_shift_left_extends_the_selection_by_a_word()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "the selection must survive");
        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"),
            "one word back from the cursor");
        Assert.That(pty.Written, Is.Empty, "and nothing reaches the shell");

        window.Close();
    }

    /// <summary>
    /// What counts as a word is XTerm.NET's definition, the same one double-click expansion uses.
    /// </summary>
    /// <remarks>
    /// "hello world" cannot show this: whitespace-delimited and letter-delimited agree on it. A hyphen is
    /// where they part company — it is not whitespace, but it is not a word character either. The keyboard
    /// gesture used to answer "foo-bar" where a double-click answered "bar", so the same terminal gave two
    /// different answers about the same text depending on which hand you used.
    /// </remarks>
    [AvaloniaTest]
    public async Task A_word_ends_where_double_click_says_it_does()
    {
        var (view, pty, window) = LiveView();
        Type(view, "foo-bar");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("bar"),
            "the hyphen ends the word, as it does for double-click");

        // The other half of the claim: the mouse gesture, over the same text, agrees.
        view.Terminal.Selection.ClearSelection();
        view.Terminal.Selection.StartSelection(5, 0, XTerm.Selection.SelectionMode.Word);
        view.Terminal.Selection.EndSelection();

        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("bar"),
            "sanity: this is the definition being matched, not a coincidence");

        window.Close();
    }

    /// <summary>A second press keeps growing it, rather than re-anchoring.</summary>
    [AvaloniaTest]
    public async Task Repeated_ctrl_shift_left_keeps_growing_the_selection()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(40);
        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(40);

        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("hello world"));
        window.Close();
    }

    /// <summary>And back the other way, collapsing as it returns to the anchor.</summary>
    [AvaloniaTest]
    public async Task Ctrl_shift_right_extends_back_toward_the_anchor()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"), "sanity");

        Press(view, Key.Right, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(40);

        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "back at the anchor clears it");
        window.Close();
    }

    /// <summary>Alt+Shift is the same gesture on macOS; it must behave identically.</summary>
    [AvaloniaTest]
    public async Task Alt_shift_left_extends_by_a_word_too()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Alt | KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"));
        Assert.That(pty.Written, Is.Empty);
        window.Close();
    }

    /// <summary>
    /// Option+Shift is the macOS word-selection gesture, and on that platform it is the ONLY one — Ctrl+arrow
    /// belongs to Mission Control, so Ctrl+Shift+arrow never reaches the app. Both are accepted so the same
    /// binding works everywhere.
    /// </summary>
    [TestCase(Key.Left, "world")]
    [TestCase(Key.Right, "")]
    [AvaloniaTest]
    public async Task Option_shift_is_the_mac_gesture(Key key, string expected)
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Alt | KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.GetSelectionText() ?? "", Is.EqualTo(expected));
        Assert.That(pty.Written, Is.Empty, "a selection gesture is not shell input");
        window.Close();
    }

    /// <summary>
    /// Bare Option+arrow keeps meaning word-motion IN the shell, unchanged from #49. Pinned here because the
    /// selection gesture claims Option+SHIFT, one modifier away — it must not swallow this one.
    /// </summary>
    [TestCase(Key.Left, "b")]
    [TestCase(Key.Right, "f")]
    [AvaloniaTest]
    public async Task Bare_option_arrow_still_moves_the_shell_cursor(Key key, string letter)
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Alt);
        await Task.Delay(60);

        Assert.That(pty.Written, Is.EqualTo(Esc + letter), "still ESC-b / ESC-f to the shell");
        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "and no selection is made");
        window.Close();
    }

    /// <summary>
    /// Ctrl+arrow the same way, for a shell reading VT sequences.
    /// </summary>
    [TestCase(Key.Left, "b")]
    [TestCase(Key.Right, "f")]
    [AvaloniaTest]
    public async Task Bare_ctrl_arrow_moves_the_shell_cursor_by_a_word(Key key, string letter)
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Control);
        await Task.Delay(60);

        Assert.That(pty.Written, Is.EqualTo(Esc + letter));
        Assert.That(view.Terminal.Selection.HasSelection, Is.False);
        window.Close();
    }

    /// <summary>
    /// But NOT when the process is reading Win32 input records — there the real key event has to go through.
    /// </summary>
    /// <remarks>
    /// <para>cmd.exe turns that mode on as it starts (<c>CSI ?9001h</c>, visible in the first bytes of any
    /// session it runs in). Both it and PSReadLine already move by word on a real Ctrl+Left, and neither
    /// binds ESC-b — so translating the chord replaced one they understand with one they ignore, and on
    /// Windows the key did nothing whatsoever.</para>
    /// <para>Asserted as "not the translation" rather than against an exact Win32 record, because the record
    /// encodes scan codes and repeat counts that are not this test's business.</para>
    /// </remarks>
    [TestCase(Key.Left)]
    [TestCase(Key.Right)]
    [AvaloniaTest]
    public async Task Ctrl_arrow_is_not_translated_under_win32_input_mode(Key key)
    {
        var (view, pty, window) = LiveView();
        view.Terminal.Write(Esc + "[?9001h");      // what cmd.exe sends on startup
        await Task.Delay(60);
        Assert.That(view.Terminal.Win32InputMode, Is.True, "sanity: the mode is on");

        Press(view, key, KeyModifiers.Control);
        await Task.Delay(60);

        Assert.That(pty.Written, Is.Not.Empty, "the keystroke still has to reach the process");
        Assert.That(pty.Written, Is.Not.EqualTo(Esc + "b"), "ESC-b is a binding cmd.exe does not have");
        Assert.That(pty.Written, Is.Not.EqualTo(Esc + "f"));
        Assert.That(pty.Written, Does.EndWith("_"), "a Win32 input record, which it does understand");

        window.Close();
    }

    /// <summary>Same for bare Ctrl+arrow, which is the gesture on Windows and Linux.</summary>
    [TestCase(Key.Left, "b")]
    [TestCase(Key.Right, "f")]
    [AvaloniaTest]
    public async Task Bare_ctrl_arrow_still_moves_the_shell_cursor(Key key, string letter)
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Control);
        await Task.Delay(60);

        Assert.That(pty.Written, Is.EqualTo(Esc + letter));
        Assert.That(view.Terminal.Selection.HasSelection, Is.False);
        window.Close();
    }

    // ── Line-edge gestures (#63 follow-up) ──────────────────────────────────────────────────────

    private static bool OnMac => System.Runtime.InteropServices.RuntimeInformation
        .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);

    /// <summary>Shift+Home / Shift+End select to the line edge — the Windows and Linux gesture.</summary>
    [TestCase(Key.Home, "hello world")]
    [TestCase(Key.End, "")]
    [AvaloniaTest]
    public async Task Shift_home_and_end_select_to_the_line_edge(Key key, string expected)
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.GetSelectionText() ?? "", Is.EqualTo(expected));
        Assert.That(pty.Written, Is.Empty, "a selection gesture is not shell input");
        window.Close();
    }

    /// <summary>
    /// A Mac keyboard has no Home/End, so Cmd+arrow is the platform's line-start/line-end — and until now it
    /// did nothing at all, swallowed by the Meta passthrough. It sends exactly what Home and End send.
    /// </summary>
    [TestCase(Key.Left, "[H")]
    [TestCase(Key.Right, "[F")]
    [AvaloniaTest]
    public async Task Cmd_arrow_is_the_mac_line_edge(Key key, string tail)
    {
        if (!OnMac) Assert.Ignore("Cmd+arrow is a macOS gesture");

        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Meta);
        await Task.Delay(60);

        Assert.That(pty.Written, Is.EqualTo(Esc + tail), "the same sequence Home and End send");
        window.Close();
    }

    /// <summary>And with Shift held it selects to that edge, like Shift+Home / Shift+End.</summary>
    [TestCase(Key.Left, "hello world")]
    [TestCase(Key.Right, "")]
    [AvaloniaTest]
    public async Task Cmd_shift_arrow_selects_to_the_mac_line_edge(Key key, string expected)
    {
        if (!OnMac) Assert.Ignore("Cmd+Shift+arrow is a macOS gesture");

        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Meta | KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.GetSelectionText() ?? "", Is.EqualTo(expected));
        Assert.That(pty.Written, Is.Empty, "a selection gesture is not shell input");
        window.Close();
    }

    // ── Where the caret is drawn ────────────────────────────────────────────────────────────────

    /// <summary>The caret follows the selection's moving edge, as it does in every text field.</summary>
    [AvaloniaTest]
    public async Task The_caret_follows_the_selection_edge()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        var atCursor = view.CaretPosition;

        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(40);

        Assert.That(view.CaretPosition, Is.Not.EqualTo(atCursor), "it moved with the selection");
        Assert.That(view.CaretPosition.Column, Is.EqualTo(atCursor.Column - "world".Length),
            "to the start of the selected word");

        window.Close();
    }

    /// <summary>
    /// A gesture can leave the anchor set having selected NOTHING — Shift+End at the end of a line. Release
    /// it anyway, or the caret stays pinned to a boundary the cursor has since moved away from, and typed
    /// characters append somewhere the caret is not. That is what was reported against the sample.
    /// </summary>
    [AvaloniaTest]
    public async Task A_gesture_that_selects_nothing_still_releases_the_caret()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello");
        await Task.Delay(60);

        Press(view, Key.End, KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "sanity: nothing was selected");

        Press(view, Key.X);
        await Task.Delay(40);

        // The shell echoing input moves the real cursor on; the caret has to go with it.
        Type(view, "world");
        await Task.Delay(60);

        Assert.That(view.CaretPosition, Is.EqualTo((view.Terminal.Buffer.X,
                                                    view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y)),
            "the caret is back on the shell's cursor, not pinned to the retired gesture");

        window.Close();
    }

    /// <summary>The same for the collapse path: un-selecting also retires the gesture.</summary>
    [AvaloniaTest]
    public async Task Collapsing_a_selection_releases_the_caret()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Shift);
        await Task.Delay(40);
        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "sanity: collapsed");

        Type(view, "world");
        await Task.Delay(60);

        Assert.That(view.CaretPosition, Is.EqualTo((view.Terminal.Buffer.X,
                                                    view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y)));
        window.Close();
    }


    // ── Typing over a selection replaces it (against a real shell) ──────────────────────────────

    private static string CursorRow(TerminalView v)
    {
        var line = v.Terminal.Buffer.GetLine(v.Terminal.Buffer.YBase + v.Terminal.Buffer.Y);
        if (line == null) return "";
        var sb = new System.Text.StringBuilder();
        for (int x = 0; x < line.Length; x++) sb.Append(string.IsNullOrEmpty(line[x].Content) ? " " : line[x].Content);
        return sb.ToString().TrimEnd();
    }

    private static async Task<(TerminalView view, Window window)> RealShell()
    {
        var view = new TerminalView { Process = "bash", Args = new List<string> { "--norc" } };
        var window = new Window { Width = 900, Height = 400, Content = view };
        window.Show();
        window.UpdateLayout();
        await view.LaunchProcess();
        view.Focus();
        await Task.Delay(1500);
        return (view, window);
    }

    private static void TypeText(TerminalView v, string text)
    {
        foreach (var ch in text)
            v.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = ch.ToString() });
    }

    /// <summary>
    /// Typing over a selection replaces it, the way it does in every text field. The view cannot edit the
    /// line — the shell owns it — so the selection becomes the keystrokes that would have removed it.
    /// </summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Typing_over_a_backwards_selection_replaces_it()
    {
        var (view, window) = await RealShell();
        try
        {

            TypeText(view, "hello world");
            await Task.Delay(700);
            Assert.That(CursorRow(view), Does.EndWith("hello world"), "sanity");

            Press(view, Key.Left, KeyModifiers.Shift);
            Press(view, Key.Left, KeyModifiers.Shift);
            Press(view, Key.Left, KeyModifiers.Shift);
            await Task.Delay(300);
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("rld"), "sanity: selected the tail");

            TypeText(view, "Z");
            await Task.Delay(900);

            Assert.That(CursorRow(view), Does.EndWith("hello woZ"),
                "the selected text is gone and the typed character took its place");

        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>A word-wise selection replaces the same way.</summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Typing_over_a_word_selection_replaces_it()
    {
        var (view, window) = await RealShell();
        try
        {

            TypeText(view, "hello world");
            await Task.Delay(700);

            Press(view, Key.Left, KeyModifiers.Alt | KeyModifiers.Shift);
            await Task.Delay(300);
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"), "sanity");

            TypeText(view, "there");
            await Task.Delay(900);

            Assert.That(CursorRow(view), Does.EndWith("hello there"));

        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>
    /// A selection stops where the editable input starts. Running back over the prompt is never what the
    /// user meant — the prompt is not theirs to edit, and readline will not delete it either, so a selection
    /// covering it could not be replaced.
    /// </summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task A_selection_stops_at_the_prompt_edge()
    {
        var (view, window) = await RealShell();
        try
        {

            TypeText(view, "hello world");
            await Task.Delay(700);
            var row = CursorRow(view);
            Assert.That(row, Does.Contain("$ hello world"), "sanity: there is a prompt in front of the input");

            Press(view, Key.Home, KeyModifiers.Shift);
            await Task.Delay(300);

            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("hello world"),
                "the input, and none of the prompt");

        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>Word-wise too: walking left word by word stops at the same edge.</summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Word_selection_stops_at_the_prompt_edge()
    {
        var (view, window) = await RealShell();
        try
        {

            TypeText(view, "hello world");
            await Task.Delay(700);

            for (int i = 0; i < 6; i++)   // more presses than there are words
            {
                Press(view, Key.Left, KeyModifiers.Alt | KeyModifiers.Shift);
                await Task.Delay(80);
            }

            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("hello world"),
                "it stops at the input, however many times the chord is pressed");

        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>
    /// Backspace and Delete both remove a selection — either key means "get rid of what is selected" in any
    /// text field, rather than "act on one character".
    /// </summary>
    [TestCase(Key.Back)]
    [TestCase(Key.Delete)]
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Erasing_a_backwards_selection_removes_all_of_it(Key key)
    {
        var (view, window) = await RealShell();
        try
        {

            TypeText(view, "hello world");
            await Task.Delay(700);

            Press(view, Key.Left, KeyModifiers.Shift);
            Press(view, Key.Left, KeyModifiers.Shift);
            Press(view, Key.Left, KeyModifiers.Shift);
            await Task.Delay(300);
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("rld"), "sanity");

            Press(view, key);
            await Task.Delay(900);

            Assert.That(CursorRow(view), Does.EndWith("hello wo"),
                $"{key} removed the selection, and nothing more");

        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>The same for a word-wise selection, which is the longer one.</summary>
    [TestCase(Key.Back)]
    [TestCase(Key.Delete)]
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Erasing_a_word_selection_removes_the_word(Key key)
    {
        var (view, window) = await RealShell();
        try
        {

            TypeText(view, "hello world");
            await Task.Delay(700);

            Press(view, Key.Left, KeyModifiers.Alt | KeyModifiers.Shift);
            await Task.Delay(300);
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"), "sanity");

            Press(view, key);
            await Task.Delay(900);

            // Typing a marker afterwards, because the row helper trims: "hello " and "hello" are otherwise
            // indistinguishable, and the difference is exactly whether the separating space survived.
            TypeText(view, "X");
            await Task.Delay(700);

            Assert.That(CursorRow(view), Does.EndWith("hello X"),
                "the word went and the space before it stayed");

        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>With nothing selected, Backspace still deletes exactly one character.</summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Backspace_without_a_selection_is_unchanged()
    {
        var (view, window) = await RealShell();
        try
        {

            TypeText(view, "hello world");
            await Task.Delay(700);

            Press(view, Key.Back);
            await Task.Delay(900);

            Assert.That(CursorRow(view), Does.EndWith("hello worl"), "one character, as before");

        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    // ── Review findings ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A wide glyph occupies two cells: the character, then a width-0 placeholder. Selecting to the line
    /// end must land past the WHOLE glyph — recording only the column it starts in leaves the boundary in
    /// the middle of one character, and the selection covers half of it.
    /// </summary>
    /// <remarks>
    /// Driven through a real shell because the cursor has to be somewhere other than the end of the line
    /// for Shift+End to have anywhere to go, and only the shell can move it there.
    /// </remarks>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Shift_end_covers_a_whole_wide_glyph()
    {
        var (view, window) = await RealShell();
        try
        {

            TypeText(view, "ab\u4e16\u754c");
            await Task.Delay(700);

            Press(view, Key.Home);              // move the SHELL's cursor to the line start
            await Task.Delay(400);

            Press(view, Key.End, KeyModifiers.Shift);
            await Task.Delay(300);

            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("ab\u4e16\u754c"),
                "the last glyph is selected whole, not cut in half");

        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>
    /// Word movement must not stop between the two cells of one glyph. The placeholder reads as empty
    /// content, so treating it as a separator ends the word inside the character.
    /// </summary>
    [AvaloniaTest]
    public async Task Word_selection_does_not_split_a_wide_glyph()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hi \u4e16\u754c");
        await Task.Delay(80);

        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(80);

        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("\u4e16\u754c"),
            "both glyphs, not one and a half");
        window.Close();
    }

    /// <summary>
    /// Copying clears the selection, so it retires the gesture too. Leaving the anchor set pins the caret
    /// to a boundary the shell's cursor has since moved past.
    /// </summary>
    [AvaloniaTest]
    public async Task Copying_a_selection_releases_the_caret()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity");

        Press(view, Key.C, KeyModifiers.Control | KeyModifiers.Shift);   // copy, which clears
        await Task.Delay(120);

        Type(view, "world");
        await Task.Delay(60);

        Assert.That(view.CaretPosition, Is.EqualTo((view.Terminal.Buffer.X,
                                                    view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y)),
            "the caret is back on the shell's cursor");
        window.Close();
    }

    /// <summary>
    /// The word gesture is horizontal. Ctrl+Shift with a vertical key or a line-edge key belongs to the
    /// application, and must still reach it rather than being swallowed as a local selection.
    /// </summary>
    [TestCase(Key.Up)]
    [TestCase(Key.Down)]
    [AvaloniaTest]
    public async Task Ctrl_shift_vertical_is_not_claimed_as_a_word_gesture(Key key)
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(80);

        Assert.That(pty.Written, Is.Not.Empty, $"Ctrl+Shift+{key} still reaches the application");
        window.Close();
    }

    private static async Task<(TerminalView view, Window window)> RealZsh()
    {
        var view = new TerminalView
        {
            Process = "zsh",
            Args = new List<string> { "-f" },
            EnvironmentVariables = new Dictionary<string, string> { ["PROMPT"] = "zsh$ " },
        };
        var window = new Window { Width = 900, Height = 400, Content = view };
        window.Show();
        window.UpdateLayout();
        await view.LaunchProcess();
        view.Focus();
        await Task.Delay(1500);
        return (view, window);
    }


    /// <summary>
    /// A selection cannot run off the end of the input into blank screen. The grid is padded to full width
    /// with blanks, so without a ceiling Shift+Right walks into the empty rest of the screen a cell at a
    /// time — selecting nothing, and giving the replace nothing it could do.
    /// </summary>
    [AvaloniaTest]
    public async Task Selection_stops_at_the_end_of_the_input()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hi");
        await Task.Delay(60);

        for (int i = 0; i < 8; i++)
        {
            Press(view, Key.Right, KeyModifiers.Shift);
            await Task.Delay(20);
        }

        Assert.That(view.Terminal.Selection.GetSelectionText() ?? "", Is.Empty,
            "there is nothing past the input to select");
        window.Close();
    }

    /// <summary>
    /// A FORWARD selection — focus past the anchor — is removed by walking the cursor right and deleting
    /// backwards, never by forward-delete, which is not reliably bound: zsh with no rc does not know ESC[3~
    /// and types the tilde instead.
    /// </summary>
    /// <remarks>
    /// The first version of this test pressed Shift+Left and then Shift+Home, which leaves the focus BELOW
    /// the anchor — a backwards selection, taking the Backspace-only path. It asserted the absence of a
    /// forward-delete sequence that was never going to be emitted, and would have passed against the bug.
    /// Home first, so the shell's cursor is at the input start and Shift+Right can select forwards from it.
    /// </remarks>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task A_forward_selection_is_erased_without_forward_delete()
    {
        var (view, window) = await RealShell();
        try
        {
            TypeText(view, "hello world");
            await Task.Delay(700);

            Press(view, Key.Home);          // move the SHELL's cursor to the input start
            await Task.Delay(400);

            Press(view, Key.Right, KeyModifiers.Shift);
            Press(view, Key.Right, KeyModifiers.Shift);
            Press(view, Key.Right, KeyModifiers.Shift);
            await Task.Delay(300);
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("hel"),
                "sanity: a forward selection, focus past the anchor");

            Press(view, Key.Back);
            await Task.Delay(900);

            Assert.That(CursorRow(view), Does.EndWith("lo world"),
                "the selected text went, and nothing else with it");
        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }
}
