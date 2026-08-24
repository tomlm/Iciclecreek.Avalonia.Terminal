using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// What the terminal sends to the process for modified keys.
///
/// <para>Both cases here end the same way — a shell receiving characters nobody asked it to receive — but
/// they arrive by different routes: one falls through to the printable-character path, the other generates
/// a sequence no shell binds.</para>
/// </summary>
[TestFixture]
public class KeyboardChordTests
{
    private const string Esc = "\u001b";   // what a shell binds word-motion to: ESC-b / ESC-f

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

    private static async Task<string> PressAndSettle(TerminalView view, Key key, KeyModifiers mods, RecordingConnection pty)
    {
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods });
        await Task.Delay(150);   // OnKeyDown is async void; let any write land
        return pty.Written;
    }

    /// <summary>
    /// Cmd+C and Cmd+V are handled, but every OTHER Meta chord falls out of that block and carries on to the
    /// printable-character path — so the shell receives a literal character for a chord that belongs to the
    /// application. In a host that binds Cmd+K, the shell quietly gets a stray "k".
    /// </summary>
    [TestCase(Key.K)]
    [TestCase(Key.N)]
    [TestCase(Key.T)]
    [AvaloniaTest]
    public async Task A_meta_chord_is_not_typed_into_the_process(Key key)
    {
        var (view, pty, window) = LiveView();
        var written = await PressAndSettle(view, key, KeyModifiers.Meta, pty);
        Assert.That(written, Is.Empty, $"Cmd+{key} belongs to the application, not the shell");
        window.Close();
    }

    /// <summary>
    /// Alt/Ctrl + Left/Right means "move by word". What the emulator generates for them is a modified-cursor
    /// sequence that no default shell keymap binds, so zsh echoes its tail straight into the command line.
    /// ESC-b / ESC-f (backward-word / forward-word) is what zsh, bash's readline, fish and PSReadLine's
    /// default emacs mode all bind out of the box.
    /// </summary>
    [TestCase(Key.Left, KeyModifiers.Alt, "b")]
    [TestCase(Key.Right, KeyModifiers.Alt, "f")]
    [TestCase(Key.Left, KeyModifiers.Control, "b")]
    [TestCase(Key.Right, KeyModifiers.Control, "f")]
    [AvaloniaTest]
    public async Task Word_motion_sends_what_a_shell_actually_binds(Key key, KeyModifiers mods, string letter)
    {
        var (view, pty, window) = LiveView();
        var written = await PressAndSettle(view, key, mods, pty);
        Assert.That(written, Is.EqualTo(Esc + letter),
            "the modified-cursor sequence is bound by no default shell keymap and gets echoed as text");
        window.Close();
    }
}
