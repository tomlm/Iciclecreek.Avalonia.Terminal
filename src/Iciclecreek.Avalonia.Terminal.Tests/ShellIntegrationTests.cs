using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// OSC 133 shell integration. A shell that emits it says exactly where its prompt ends, which is the one
/// thing the input-start heuristic can only infer — so when it is present it wins outright.
/// </summary>
[TestFixture]
public class ShellIntegrationTests
{
    private const string Osc = "\u001b]";
    private const string St  = "\u0007";
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

    private static void Press(TerminalView v, Key k, KeyModifiers m = KeyModifiers.None)
        => v.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = m });

    /// <summary>
    /// The marker puts the input start exactly where the shell says, with no inference involved — so a
    /// selection to the line start covers the input and none of the prompt.
    /// </summary>
    [AvaloniaTest]
    public async Task Osc133_marks_where_the_input_begins()
    {
        var (view, pty, window) = LiveView();

        view.Terminal.Write(Osc + "133;A" + St);
        view.Terminal.Write("user@host $ ");
        view.Terminal.Write(Osc + "133;B" + St);
        view.Terminal.Write("ls -la");
        await Task.Delay(80);

        Assert.That(view.InputStart, Is.EqualTo((0, 12)), "recorded from the marker, not guessed");

        Press(view, Key.Home, KeyModifiers.Shift);
        await Task.Delay(80);

        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("ls -la"),
            "the command, and none of the prompt");
        Assert.That(pty.Written, Is.Empty);

        window.Close();
    }

    /// <summary>A shell that emits I for the same point is treated the same.</summary>
    [AvaloniaTest]
    public async Task The_I_marker_is_accepted_too()
    {
        var (view, pty, window) = LiveView();

        view.Terminal.Write("> ");
        view.Terminal.Write(Osc + "133;I" + St);
        await Task.Delay(80);

        Assert.That(view.InputStart, Is.EqualTo((0, 2)));
        window.Close();
    }

    /// <summary>
    /// A marker moves the input start on every new prompt, so a second command is bounded by its own
    /// prompt rather than the first one's.
    /// </summary>
    [AvaloniaTest]
    public async Task Each_prompt_moves_the_input_start()
    {
        var (view, pty, window) = LiveView();

        view.Terminal.Write("first$ " + Osc + "133;B" + St + "one\r\n");
        view.Terminal.Write("second-prompt$ " + Osc + "133;B" + St + "two");
        await Task.Delay(80);

        Assert.That(view.InputStart, Is.EqualTo((1, 15)), "the second prompt's edge, on its own row");

        Press(view, Key.Home, KeyModifiers.Shift);
        await Task.Delay(80);
        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("two"));

        window.Close();
    }

    /// <summary>
    /// Markers are not required, and without them the heuristic still finds the prompt edge.
    /// </summary>
    /// <remarks>
    /// Output is pushed through the CONNECTION rather than written straight to the emulator. Only the read
    /// loop arms the heuristic, so a direct Terminal.Write leaves it disarmed and the test would pass while
    /// exercising nothing — which is what the first version of this did.
    /// </remarks>
    [AvaloniaTest]
    public async Task Without_markers_the_heuristic_still_finds_the_prompt()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();

        var pty = new PushConnection();
        view.AttachConnection(pty);
        view.Focus();

        // A prompt with no shell integration at all, arriving the way real output does.
        pty.Push("plain-prompt$ ");
        await WaitUntil(() => view.Terminal.Buffer.X >= 14, "the prompt was written");

        // The heuristic samples at the first keystroke, so type one.
        view.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "x" });
        await Task.Delay(120);

        Assert.That(view.InputStart, Is.EqualTo((0, 14)),
            "inferred from where the cursor stood when typing began");

        pty.Done();
        window.Close();
    }

    private static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out waiting until {because}");
            await Task.Delay(10);
        }
    }
}
