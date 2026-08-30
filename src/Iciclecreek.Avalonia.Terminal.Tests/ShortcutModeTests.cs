using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// <see cref="TerminalView.ShortcutMode"/> — which convention Ctrl+A, Ctrl+C, Ctrl+V and Ctrl+X follow.
/// </summary>
[TestFixture]
public class ShortcutModeTests
{
    private const string Esc = "\u001b";
    private static (TerminalView view, RecordingConnection pty, Window window) LiveView(ShortcutMode mode)
    {
        var view = new TerminalView { Process = "", ShortcutMode = mode };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        view.Focus();
        return (view, pty, window);
    }

    private static void Press(TerminalView v, Key k, KeyModifiers m)
        => v.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = m });

    private static bool OnMac => System.Runtime.InteropServices.RuntimeInformation
        .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);

    /// <summary>The Ctrl map is Windows/Linux only; macOS gets the Cmd gestures instead.</summary>
    private static void SkipUnlessCtrlPlatform()
    {
        if (OnMac) Assert.Ignore("the Ctrl map is deliberately not applied on macOS");
    }

    // ── Off by default: nothing changes ─────────────────────────────────────────────────────────

    [AvaloniaTest]
    public void It_defaults_to_terminal() => Assert.That(new TerminalView().ShortcutMode, Is.EqualTo(ShortcutMode.Terminal));

    /// <summary>
    /// With the option off, Ctrl+V and Ctrl+X still reach the program — quoted-insert and readline's prefix
    /// are exactly what upstream kept them for.
    /// </summary>
    [TestCase(Key.V)]
    [TestCase(Key.X)]
    [AvaloniaTest]
    public async Task Terminal_mode_leaves_the_keys_with_the_program(Key key)
    {
        var (view, pty, window) = LiveView(ShortcutMode.Terminal);
        view.Terminal.Write("hello");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Control);

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.Not.Empty,
            $"Ctrl+{key} still belongs to the program");
        window.Close();
    }

    // ── On: the desktop map ─────────────────────────────────────────────────────────────────────

    /// <summary>Ctrl+A selects everything. It is beginning-of-line otherwise, which is why this is opt-in.</summary>
    [AvaloniaTest]
    public async Task Ctrl_a_selects_all()
    {
        SkipUnlessCtrlPlatform();
        var (view, pty, window) = LiveView(ShortcutMode.Desktop);
        view.Terminal.Write("hello world");
        await Task.Delay(60);

        Press(view, Key.A, KeyModifiers.Control);
        await Task.Delay(120);

        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("hello world"),
            "the input, not the whole screen");
        window.Close();
    }

    /// <summary>Ctrl+V pastes the clipboard rather than reaching the program as quoted-insert.</summary>
    [AvaloniaTest]
    public async Task Ctrl_v_pastes()
    {
        SkipUnlessCtrlPlatform();
        var (view, pty, window) = LiveView(ShortcutMode.Desktop);

        var clipboard = TopLevel.GetTopLevel(view)?.Clipboard;
        Assert.That(clipboard, Is.Not.Null, "sanity: the headless top level has a clipboard");
        await clipboard!.SetTextAsync("pasted-text");

        Press(view, Key.V, KeyModifiers.Control);

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo("pasted-text"),
            "the clipboard reached the shell, not the quoted-insert control character");
        window.Close();
    }

    /// <summary>
    /// Shift carries what the unshifted chord used to send, so nothing is lost — only moved.
    /// </summary>
    [TestCase(Key.V, "\u0016")]
    [TestCase(Key.X, "\u0018")]
    [AvaloniaTest]
    public async Task Shift_carries_the_literal_control_character(Key key, string expected)
    {
        SkipUnlessCtrlPlatform();
        var (view, pty, window) = LiveView(ShortcutMode.Desktop);

        Press(view, key, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo(expected),
            "the character the unshifted chord used to send");
        window.Close();
    }

    /// <summary>
    /// Ctrl+X with nothing selected is not a cut, so it falls through and readline's prefix keeps working.
    /// </summary>
    [AvaloniaTest]
    public async Task Ctrl_x_with_no_selection_reaches_the_program()
    {
        SkipUnlessCtrlPlatform();
        var (view, pty, window) = LiveView(ShortcutMode.Desktop);

        Press(view, Key.X, KeyModifiers.Control);

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo("\u0018"),
            "Ctrl+X Ctrl+E and friends still work");
        window.Close();
    }

    // ── Cut, end to end ─────────────────────────────────────────────────────────────────────────

    private static async Task<(TerminalView view, Window window)> RealShell(ShortcutMode mode)
    {
        var view = new TerminalView
        {
            Process = "bash",
            Args = new List<string> { "--norc" },
            ShortcutMode = mode,
        };
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

    private static string CursorRow(TerminalView v)
    {
        var line = v.Terminal.Buffer.GetLine(v.Terminal.Buffer.YBase + v.Terminal.Buffer.Y);
        if (line == null) return "";
        var sb = new System.Text.StringBuilder();
        for (int x = 0; x < line.Length; x++) sb.Append(string.IsNullOrEmpty(line[x].Content) ? " " : line[x].Content);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Cut is copy AND removal — both halves asserted, because either alone would look like it worked.
    /// </summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Ctrl_x_cuts_the_selection()
    {
        SkipUnlessCtrlPlatform();
        var (view, window) = await RealShell(ShortcutMode.Desktop);
        try
        {
            TypeText(view, "hello world");
            await Task.Delay(700);

            Press(view, Key.Left, KeyModifiers.Shift);
            Press(view, Key.Left, KeyModifiers.Shift);
            Press(view, Key.Left, KeyModifiers.Shift);
            await Task.Delay(300);
            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("rld"), "sanity");

            Press(view, Key.X, KeyModifiers.Control);
            await Task.Delay(900);

            Assert.That(CursorRow(view), Does.EndWith("hello wo"), "the selection was removed");

            var clipboard = TopLevel.GetTopLevel(view)?.Clipboard;
            Assert.That(await clipboard!.TryGetTextAsync(), Is.EqualTo("rld"), "and it went to the clipboard");
        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>Cmd+X is the macOS spelling, and does not need the opt-in — the chord is unbound there.</summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Cmd_x_cuts_in_terminal_mode_too()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation
                .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            Assert.Ignore("Cmd+X is a macOS gesture");

        var (view, window) = await RealShell(ShortcutMode.Terminal);
        try
        {
            TypeText(view, "hello world");
            await Task.Delay(700);

            Press(view, Key.Left, KeyModifiers.Shift);
            Press(view, Key.Left, KeyModifiers.Shift);
            await Task.Delay(300);

            Press(view, Key.X, KeyModifiers.Meta);
            await Task.Delay(900);

            Assert.That(CursorRow(view), Does.EndWith("hello wor"));
        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>
    /// On macOS the option must NOT claim Ctrl+V: paste is Cmd+V there, and Ctrl+V is readline's
    /// quoted-insert. Taking it would break the shell to duplicate a gesture that already works.
    /// </summary>
    [AvaloniaTest]
    public async Task On_mac_desktop_mode_leaves_ctrl_alone()
    {
        if (!OnMac) Assert.Ignore("about macOS specifically");

        var (view, pty, window) = LiveView(ShortcutMode.Desktop);

        Press(view, Key.V, KeyModifiers.Control);
        await Task.Delay(150);

        Assert.That(pty.Written, Is.EqualTo("\u0016"),
            "Ctrl+V still reaches the program as quoted-insert");
        window.Close();
    }

    /// <summary>Cmd+A selects all on macOS, without the opt-in — the chord is unbound there.</summary>
    [AvaloniaTest]
    public async Task Cmd_a_selects_all_in_terminal_mode_too()
    {
        if (!OnMac) Assert.Ignore("Cmd+A is a macOS gesture");

        var (view, pty, window) = LiveView(ShortcutMode.Terminal);
        view.Terminal.Write("hello world");
        await Task.Delay(60);

        Press(view, Key.A, KeyModifiers.Meta);
        await Task.Delay(120);

        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("hello world"),
            "the input, not the whole screen");
        window.Close();
    }

    // ── The alternate screen suspends Desktop mode ──────────────────────────────────────────────

    private static void EnterAlternateScreen(TerminalView v) => v.Terminal.Write(Esc + "[?1049h");

    /// <summary>
    /// A full-screen application owns its own keys — vim's Ctrl+V is blockwise-visual, not paste — so
    /// Desktop mode stands aside while the alternate screen is up and the chord reaches the program.
    /// </summary>
    [TestCase(Key.V, "\u0016")]
    [TestCase(Key.X, "\u0018")]
    [TestCase(Key.A, "\u0001")]
    [AvaloniaTest]
    public async Task The_alternate_screen_hands_the_keys_back(Key key, string expected)
    {
        SkipUnlessCtrlPlatform();
        var (view, pty, window) = LiveView(ShortcutMode.Desktop);

        EnterAlternateScreen(view);
        await Task.Delay(80);
        Assert.That(view.Terminal.IsAlternateBufferActive, Is.True, "sanity");

        Press(view, key, KeyModifiers.Control);
        await Task.Delay(150);

        Assert.That(pty.Written, Is.EqualTo(expected),
            $"Ctrl+{key} belongs to the full-screen application, not to us");
        window.Close();
    }

    /// <summary>
    /// Copying still works in there, though: Ctrl+Shift+C is a terminal binding no application claims, so
    /// text can be taken out of a full-screen program.
    /// </summary>
    [AvaloniaTest]
    public async Task Copy_still_works_in_the_alternate_screen()
    {
        SkipUnlessCtrlPlatform();
        var (view, pty, window) = LiveView(ShortcutMode.Desktop);

        EnterAlternateScreen(view);
        view.Terminal.Write("selected text");
        await Task.Delay(80);
        view.Terminal.Selection.SelectAll();

        Press(view, Key.C, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(200);

        var clipboard = TopLevel.GetTopLevel(view)?.Clipboard;
        Assert.That((await clipboard!.TryGetTextAsync())?.Trim(), Does.Contain("selected text"));
        window.Close();
    }

    /// <summary>Leaving the alternate screen gives the keys back to Desktop mode.</summary>
    [AvaloniaTest]
    public async Task Leaving_the_alternate_screen_restores_desktop_mode()
    {
        SkipUnlessCtrlPlatform();
        var (view, pty, window) = LiveView(ShortcutMode.Desktop);

        EnterAlternateScreen(view);
        await Task.Delay(60);
        view.Terminal.Write(Esc + "[?1049l");            // back to the normal buffer
        await Task.Delay(80);
        Assert.That(view.Terminal.IsAlternateBufferActive, Is.False, "sanity");

        view.Terminal.Write("hello");
        await Task.Delay(60);
        Press(view, Key.A, KeyModifiers.Control);
        await Task.Delay(120);

        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("hello"), "Ctrl+A selects again");
        window.Close();
    }

    // ── None: the program gets everything ───────────────────────────────────────────────────────

    /// <summary>
    /// None hands the whole keyboard to the program. Ctrl+C is plain SIGINT because nothing intercepts it,
    /// and even the terminal's own Ctrl+Shift+C and Ctrl+Shift+V go through untouched.
    /// </summary>
    [TestCase(Key.C, KeyModifiers.Control, "\u0003")]
    [TestCase(Key.A, KeyModifiers.Control, "\u0001")]
    [TestCase(Key.V, KeyModifiers.Control, "\u0016")]
    [TestCase(Key.X, KeyModifiers.Control, "\u0018")]
    [AvaloniaTest]
    public async Task None_sends_every_chord_to_the_program(Key key, KeyModifiers mods, string expected)
    {
        var (view, pty, window) = LiveView(ShortcutMode.None);
        view.Terminal.Write("hello");
        await Task.Delay(60);
        view.Terminal.Selection.SelectAll();      // even with a selection, Ctrl+C must not copy

        Press(view, key, mods);
        await Task.Delay(150);

        Assert.That(pty.Written, Is.EqualTo(expected));
        window.Close();
    }

    /// <summary>
    /// Ctrl+C is SIGINT in None mode even when there IS a selection — which is the difference from
    /// Terminal mode, where a selection makes it copy instead.
    /// </summary>
    [AvaloniaTest]
    public async Task None_makes_ctrl_c_sigint_even_with_a_selection()
    {
        var (view, pty, window) = LiveView(ShortcutMode.None);
        view.Terminal.Write("hello");
        await Task.Delay(60);
        view.Terminal.Selection.SelectAll();
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: there is something to copy");

        // The selection is cleared by any keystroke whatever the mode, so it cannot tell a copy from a
        // non-copy. The clipboard can.
        var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
        const string sentinel = "untouched";
        await clipboard.SetTextAsync(sentinel);

        Press(view, Key.C, KeyModifiers.Control);
        await Task.Delay(150);

        Assert.That(pty.Written, Is.EqualTo("\u0003"), "SIGINT, not a copy");
        Assert.That(await clipboard.TryGetTextAsync(), Is.EqualTo(sentinel),
            "and nothing was copied — the clipboard is untouched");
        window.Close();
    }

    /// <summary>The terminal's own copy and paste chords are not exempt.</summary>
    [TestCase(Key.C)]
    [TestCase(Key.V)]
    [AvaloniaTest]
    public async Task None_does_not_keep_the_terminal_chords(Key key)
    {
        var (view, pty, window) = LiveView(ShortcutMode.None);
        view.Terminal.Write("hello");
        await Task.Delay(60);
        view.Terminal.Selection.SelectAll();

        Press(view, key, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(150);

        Assert.That(pty.Written, Is.Not.Empty, $"Ctrl+Shift+{key} reaches the program too");
        window.Close();
    }

    /// <summary>Cmd chords go with it on macOS: None means none.</summary>
    [AvaloniaTest]
    public async Task None_suppresses_the_mac_gestures_too()
    {
        if (!OnMac) Assert.Ignore("about macOS specifically");

        var (view, pty, window) = LiveView(ShortcutMode.None);
        view.Terminal.Write("hello");
        await Task.Delay(60);
        view.Terminal.Selection.SelectAll();

        var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
        const string sentinel = "untouched";
        await clipboard.SetTextAsync(sentinel);

        Press(view, Key.C, KeyModifiers.Meta);
        await Task.Delay(200);

        Assert.That(await clipboard.TryGetTextAsync(), Is.EqualTo(sentinel),
            "Cmd+C did not copy — the selection being cleared is the generic keystroke behaviour, not a copy");
        window.Close();
    }

    /// <summary>
    /// Select-all takes the INPUT, not the buffer — a prompt's worth of typing, not the scrollback above it
    /// nor the screenful of blanks around it.
    /// </summary>
    /// <remarks>
    /// A real shell, because the input has to be ECHOED to be in the buffer at all. Driving this through a
    /// scripted connection tests nothing: the keystrokes go out to the pty and nothing comes back, so there
    /// is no input on screen to select and the assertion passes for the wrong reason.
    /// </remarks>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Select_all_takes_the_input_not_the_scrollback()
    {
        var (view, window) = await RealShell(ShortcutMode.Desktop);
        try
        {
            // Put output above the prompt, so there is scrollback that must NOT be selected.
            TypeText(view, "echo earlier-output\r");
            await Task.Delay(900);

            TypeText(view, "my command");
            await Task.Delay(700);

            if (OnMac) Press(view, Key.A, KeyModifiers.Meta);
            else Press(view, Key.A, KeyModifiers.Control);
            await Task.Delay(400);

            var selected = (view.Terminal.Selection.GetSelectionText() ?? "").Trim();
            Assert.That(selected, Does.Not.Contain("earlier-output"), "not the scrollback");
            Assert.That(selected, Does.Not.Contain("$"), "not the prompt");
            Assert.That(selected, Is.EqualTo("my command"), "just what was typed");
        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>Select-all then typing replaces the whole command, as it would in any text field.</summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Select_all_then_typing_replaces_the_command()
    {
        var (view, window) = await RealShell(ShortcutMode.Desktop);
        try
        {
            TypeText(view, "the wrong command");
            await Task.Delay(700);

            if (OnMac) Press(view, Key.A, KeyModifiers.Meta);
            else Press(view, Key.A, KeyModifiers.Control);
            await Task.Delay(400);

            TypeText(view, "ok");
            await Task.Delay(900);

            Assert.That(CursorRow(view), Does.EndWith("$ ok"),
                "the old command went and the new one took its place");
        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>Select-all with nothing typed selects nothing, rather than the whole blank screen.</summary>
    [AvaloniaTest]
    public async Task Select_all_at_an_empty_prompt_selects_nothing()
    {
        var (view, pty, window) = LiveView(ShortcutMode.Desktop);

        var pushed = new PushConnection();
        view.AttachConnection(pushed);
        pushed.Push("prompt$ ");
        await Task.Delay(200);

        if (OnMac) Press(view, Key.A, KeyModifiers.Meta);
        else Press(view, Key.A, KeyModifiers.Control);
        await Task.Delay(200);

        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "nothing typed, nothing to select");

        pushed.Done();
        window.Close();
    }

    /// <summary>
    /// Select-all leaves the caret indeterminate — the whole input is selected, so neither end is more the
    /// cursor than the other. It is hidden rather than parked at one end, which is what every editor that
    /// can select all does.
    /// </summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Select_all_hides_the_caret()
    {
        var (view, window) = await RealShell(ShortcutMode.Desktop);
        try
        {
            TypeText(view, "hello world");
            await Task.Delay(700);
            Assert.That(view.CaretHidden, Is.False, "sanity: the caret is drawn while editing");

            if (OnMac) Press(view, Key.A, KeyModifiers.Meta);
            else Press(view, Key.A, KeyModifiers.Control);
            await Task.Delay(400);

            Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: something is selected");
            Assert.That(view.CaretHidden, Is.True, "nowhere meaningful to draw it");
        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>Steering an edge afterwards makes the caret meaningful again, so it comes back.</summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Steering_an_edge_brings_the_caret_back()
    {
        var (view, window) = await RealShell(ShortcutMode.Desktop);
        try
        {
            TypeText(view, "hello world");
            await Task.Delay(700);

            if (OnMac) Press(view, Key.A, KeyModifiers.Meta);
            else Press(view, Key.A, KeyModifiers.Control);
            await Task.Delay(400);
            Assert.That(view.CaretHidden, Is.True, "sanity");

            Press(view, Key.Right, KeyModifiers.Shift);
            await Task.Delay(300);

            Assert.That(view.CaretHidden, Is.False, "the user is steering an edge again");
        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>And typing over the selection retires it, caret included.</summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Typing_after_select_all_brings_the_caret_back()
    {
        var (view, window) = await RealShell(ShortcutMode.Desktop);
        try
        {
            TypeText(view, "hello world");
            await Task.Delay(700);

            if (OnMac) Press(view, Key.A, KeyModifiers.Meta);
            else Press(view, Key.A, KeyModifiers.Control);
            await Task.Delay(400);

            TypeText(view, "x");
            await Task.Delay(700);

            Assert.That(view.CaretHidden, Is.False, "the selection is gone, so the caret is back");
        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }

    /// <summary>
    /// Copying does not clear the selection. Every other application works that way — copying is not a
    /// destructive act — and a selection you can no longer see is one you cannot copy again, extend, or
    /// replace.
    /// </summary>
    [TestCase(Key.C, KeyModifiers.Control | KeyModifiers.Shift)]
    [AvaloniaTest]
    public async Task Copy_leaves_the_selection_in_place(Key key, KeyModifiers mods)
    {
        var (view, pty, window) = LiveView(ShortcutMode.Terminal);
        view.Terminal.Write("hello world");
        await Task.Delay(60);
        view.Terminal.Selection.SelectAll();
        var before = view.Terminal.Selection.GetSelectionText();

        Press(view, key, mods);
        await Task.Delay(200);

        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "the selection survives a copy");
        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo(before), "and is unchanged");

        var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
        Assert.That((await clipboard.TryGetTextAsync())?.Trim(), Does.Contain("hello world"),
            "sanity: it really did copy");
        window.Close();
    }

    /// <summary>The macOS spelling behaves the same.</summary>
    [AvaloniaTest]
    public async Task Cmd_c_leaves_the_selection_in_place()
    {
        if (!OnMac) Assert.Ignore("Cmd+C is a macOS gesture");

        var (view, pty, window) = LiveView(ShortcutMode.Terminal);
        view.Terminal.Write("hello world");
        await Task.Delay(60);
        view.Terminal.Selection.SelectAll();

        Press(view, Key.C, KeyModifiers.Meta);
        await Task.Delay(200);

        Assert.That(view.Terminal.Selection.HasSelection, Is.True);
        window.Close();
    }

    /// <summary>
    /// Copying after select-all keeps the caret hidden too: nothing about the selection changed, so
    /// nothing about the caret should either.
    /// </summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Copying_a_select_all_keeps_the_caret_hidden()
    {
        var (view, window) = await RealShell(ShortcutMode.Desktop);
        try
        {
            TypeText(view, "hello world");
            await Task.Delay(700);

            if (OnMac) Press(view, Key.A, KeyModifiers.Meta);
            else Press(view, Key.A, KeyModifiers.Control);
            await Task.Delay(400);
            Assert.That(view.CaretHidden, Is.True, "sanity");

            if (OnMac) Press(view, Key.C, KeyModifiers.Meta);
            else Press(view, Key.C, KeyModifiers.Control | KeyModifiers.Shift);
            await Task.Delay(400);

            Assert.That(view.Terminal.Selection.HasSelection, Is.True, "still selected");
            Assert.That(view.CaretHidden, Is.True, "so still hidden");
        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }


    // ── Cut only claims what it can do ──────────────────────────────────────────────────────────

    /// <summary>
    /// A selection cut cannot remove — one made with the mouse, or sitting in the scrollback — is left
    /// entirely alone. Copying it instead would read as a completed cut: the selection clears, the
    /// clipboard fills, and the source is still there.
    /// </summary>
    [AvaloniaTest]
    public async Task Cut_does_nothing_when_it_cannot_remove()
    {
        var (view, pty, window) = LiveView(ShortcutMode.Desktop);
        view.Terminal.Write("scrollback text");
        await Task.Delay(60);

        // A selection made without the keyboard gesture: no anchor, so nothing is removable.
        view.Terminal.Selection.SelectAll();
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity");

        var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
        const string sentinel = "untouched";
        await clipboard.SetTextAsync(sentinel);

        var cut = await view.CutAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(cut, Is.False, "it says so, rather than claiming a cut it did not make");
            Assert.That(view.Terminal.Selection.HasSelection, Is.True, "the selection is left standing");
            Assert.That(await clipboard.TryGetTextAsync(), Is.EqualTo(sentinel),
                "and the clipboard is not touched — a copy here would look like a cut");
        });

        window.Close();
    }

    /// <summary>
    /// Ctrl+X over such a selection is not swallowed either: it goes to the program, where it is readline's
    /// prefix and worth more than a cut that silently became a copy.
    /// </summary>
    [AvaloniaTest]
    public async Task Ctrl_x_falls_through_when_it_cannot_cut()
    {
        SkipUnlessCtrlPlatform();
        var (view, pty, window) = LiveView(ShortcutMode.Desktop);
        view.Terminal.Write("scrollback text");
        await Task.Delay(60);
        view.Terminal.Selection.SelectAll();

        Press(view, Key.X, KeyModifiers.Control);
        await Task.Delay(200);

        Assert.That(pty.Written, Is.EqualTo("\u0018"), "the chord reached the program");
        window.Close();
    }

    /// <summary>And a cut it CAN make still happens, so the guard has not simply disabled the feature.</summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "drives a real bash")]
    public async Task Cut_still_works_on_an_editable_selection()
    {
        var (view, window) = await RealShell(ShortcutMode.Desktop);
        try
        {
            TypeText(view, "hello world");
            await Task.Delay(700);

            Press(view, Key.Left, KeyModifiers.Shift);
            Press(view, Key.Left, KeyModifiers.Shift);
            Press(view, Key.Left, KeyModifiers.Shift);
            await Task.Delay(300);

            Assert.That(await view.CutAsync(), Is.True, "this one is removable");
            await Task.Delay(900);

            Assert.That(CursorRow(view), Does.EndWith("hello wo"));
        }
        finally
        {
            view.Kill();
            window.Close();
        }
    }
}
