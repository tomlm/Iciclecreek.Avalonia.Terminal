using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The host's say over selection: whether typing drops it (<see cref="TerminalView.ClearSelectionOnTyping"/>),
/// and where a word ends for the keyboard's word-wise selection (XTerm.NET's <c>WordSeparators</c>, the
/// same rule double-click uses).
/// </summary>
[TestFixture]
public class SelectionOptionsTests
{
    private static (TerminalView view, RecordingConnection pty, Window window) LiveView()
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

    // ---- ClearSelectionOnTyping ------------------------------------------------------------------

    [AvaloniaTest]
    public void By_default_typed_text_drops_the_selection()
    {
        var (view, _, window) = LiveView();
        try
        {
            Assert.That(view.ClearSelectionOnTyping, Is.True, "the default is unchanged");
            view.Terminal.Write("some output");
            view.Terminal.Selection.SelectAll();

            TypeText(view, "a");

            Assert.That(view.Terminal.Selection.HasSelection, Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Turned_off_typed_text_leaves_the_selection_and_still_types()
    {
        var (view, pty, window) = LiveView();
        try
        {
            view.ClearSelectionOnTyping = false;
            view.Terminal.Write("some output");
            view.Terminal.Selection.SelectAll();

            TypeText(view, "a");

            Assert.That(view.Terminal.Selection.HasSelection, Is.True);
            Assert.That(AwaitOutput(pty), Is.EqualTo("a"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void By_default_a_key_drops_the_selection()
    {
        var (view, _, window) = LiveView();
        try
        {
            view.Terminal.Write("some output");
            view.Terminal.Selection.SelectAll();

            SendKey(view, Key.A, KeyModifiers.None, "a");

            Assert.That(view.Terminal.Selection.HasSelection, Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Turned_off_a_key_leaves_the_selection()
    {
        var (view, _, window) = LiveView();
        try
        {
            view.ClearSelectionOnTyping = false;
            view.Terminal.Write("some output");
            view.Terminal.Selection.SelectAll();

            SendKey(view, Key.A, KeyModifiers.None, "a");

            Assert.That(view.Terminal.Selection.HasSelection, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_setting_reaches_the_view_from_a_control_and_a_window()
    {
        var control = new TerminalControl { Process = "", ClearSelectionOnTyping = false };
        var host = TerminalHost.Show(control);
        var terminalWindow = new TerminalWindow { Process = "", ClearSelectionOnTyping = false }.Realise();
        try
        {
            Assert.That(control.View().ClearSelectionOnTyping, Is.False);
            Assert.That(terminalWindow.Control().View().ClearSelectionOnTyping, Is.False);
        }
        finally
        {
            host.Close();
            terminalWindow.Close();
        }
    }

    // ---- WordSeparators --------------------------------------------------------------------------

    [AvaloniaTest]
    public void By_default_a_word_ends_at_punctuation()
    {
        var (view, _, window) = LiveView();
        try
        {
            view.Terminal.Write("echo foo-bar");

            SendKey(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);

            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("bar"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Word_separators_decide_where_the_keyboard_word_ends()
    {
        var (view, _, window) = LiveView();
        try
        {
            view.Terminal.Options.WordSeparators = " ";
            view.Terminal.Write("echo foo-bar");

            SendKey(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);

            Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("foo-bar"),
                "the same rule double-click uses: '-' is not a separator here");
        }
        finally { window.Close(); }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static void TypeText(TerminalView view, string text)
    {
        view.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
        Dispatcher.UIThread.RunJobs();
    }

    private static void SendKey(TerminalView view, Key key, KeyModifiers modifiers, string? symbol = null)
    {
        view.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, KeySymbol = symbol,
        });
        Dispatcher.UIThread.RunJobs();
    }

    private static string AwaitOutput(RecordingConnection pty)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && pty.Written.Length == 0)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        return pty.Written;
    }
}
