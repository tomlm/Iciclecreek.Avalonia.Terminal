using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// A program asking about the window has to get an answer back.
/// </summary>
/// <remarks>
/// <para>The reply used to never arrive. The emulator raises the request synchronously and reads
/// <c>Handled</c> the moment the handler returns; the handler posted the work to the UI thread and returned
/// immediately, so <c>Handled</c> was still false and nothing was sent. The answer was computed correctly
/// and written after the only reader of it had given up.</para>
/// <para>Asserted on what reaches the pty, because that is the part a program can see. A handler that sets
/// the right values on the wrong thread looks identical from inside.</para>
/// </remarks>
[TestFixture]
public class WindowQueryTests
{
    private const string Esc = "\u001b";

    private static (TerminalView view, RecordingConnection pty, Window window) LiveView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 900, Height = 400, Content = view };
        window.Show();
        window.UpdateLayout();

        // The reports are opt-in; a host says which it will answer.
        var options = view.Terminal.Options.WindowOptions;
        options.GetWinSizeChars = true;
        options.GetWinSizePixels = true;
        options.GetCellSizePixels = true;
        options.GetWinPosition = true;

        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        return (view, pty, window);
    }

    /// <summary>Answer the request, the way a host embedding the terminal does.</summary>
    private static void AnswerWith(TerminalView view, int width, int height)
    {
        TerminalView.AddWindowInfoRequestedHandler(view, (_, e) =>
        {
            e.WidthPixels = width;
            e.HeightPixels = height;
            e.CellWidth = 8;
            e.CellHeight = 16;
            e.X = 10;
            e.Y = 20;
            e.Handled = true;
        });
    }

    [TestCase("[14t", TestName = "pixel size")]
    [TestCase("[16t", TestName = "cell size")]
    [TestCase("[13t", TestName = "window position")]
    [AvaloniaTest]
    public async Task A_window_query_is_answered(string query)
    {
        var (view, pty, window) = LiveView();
        AnswerWith(view, 640, 480);

        view.Terminal.Write(Esc + query);

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.Not.Empty,
            "nothing was sent back — the handler's answer arrived after the emulator had stopped listening");
        Assert.That(pty.Written, Does.StartWith(Esc + "["), "and it should be a CSI report");

        window.Close();
    }

    /// <summary>
    /// A host that does NOT handle the request must not produce a reply — the terminal has nothing to say
    /// and inventing an answer is worse than silence.
    /// </summary>
    [AvaloniaTest]
    public async Task An_unhandled_query_stays_unanswered()
    {
        var (view, pty, window) = LiveView();

        view.Terminal.Write(Esc + "[14t");
        await Task.Delay(150);

        Assert.That(pty.Written, Is.Empty);

        window.Close();
    }

    /// <summary>The values the host supplied are the ones that go out, not defaults.</summary>
    [AvaloniaTest]
    public async Task The_answer_carries_what_the_host_said()
    {
        var (view, pty, window) = LiveView();
        AnswerWith(view, 640, 480);

        view.Terminal.Write(Esc + "[14t");

        Assert.That(await PtyWaits.AwaitOutput(pty), Does.Contain("480").And.Contain("640"));

        window.Close();
    }
}
