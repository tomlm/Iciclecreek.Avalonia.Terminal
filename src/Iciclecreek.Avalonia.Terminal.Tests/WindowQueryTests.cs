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
    /// GEOMETRY the view itself knows is answered even with no handler: cell and text-area sizes
    /// are the view's own numbers, and silence there makes an image client fall back to guessing
    /// a cell size the emulator's tiling then disagrees with — which shears every placeholder
    /// picture into repeated bands. The answer must be the same metric the emulator tiles with.
    /// </summary>
    [AvaloniaTest]
    public async Task A_geometry_query_is_answered_even_with_no_handler()
    {
        var (view, pty, window) = LiveView();

        view.Terminal.Write(Esc + "[16t");

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.Not.Empty,
            "the view knows its own cell size and must say so");
        Assert.That(pty.Written, Does.StartWith(Esc + "[6;"), "CSI 6 ; height ; width t");

        window.Close();
    }

    /// <summary>
    /// What the view CANNOT know on its own — where the window sits on the screen — still stays
    /// unanswered without a handler: there the terminal genuinely has nothing to say, and
    /// inventing an answer is worse than silence.
    /// </summary>
    [AvaloniaTest]
    public async Task An_unhandled_query_the_view_cannot_answer_stays_unanswered()
    {
        var (view, pty, window) = LiveView();

        view.Terminal.Write(Esc + "[13t");
        await Task.Delay(150);

        Assert.That(pty.Written, Is.Empty);

        window.Close();
    }

    /// <summary>
    /// For facts only the host can know -- where the window sits -- the values the host supplied
    /// are the ones that go out. Pixel geometry no longer consults handlers at all: those answers
    /// come inline from the view's own published metrics, on the reader's thread, because waiting
    /// on the UI thread for a number the view already knows stalled the reader for up to a quarter
    /// of a second whenever the UI was busy.
    /// </summary>
    [AvaloniaTest]
    public async Task The_answer_carries_what_the_host_said()
    {
        var (view, pty, window) = LiveView();
        AnswerWith(view, 640, 480);

        view.Terminal.Write(Esc + "[13t");

        Assert.That(await PtyWaits.AwaitOutput(pty), Does.Contain("20").And.Contain("10"),
            "CSI 3 ; x ; y t with the handler's coordinates");

        window.Close();
    }

    /// <summary>
    /// Pixel geometry is answered on the READER's thread from the published metrics -- no UI-thread
    /// round trip, no quarter-second stall when the UI is busy, and the same numbers the emulator
    /// tiles images by.
    /// </summary>
    [AvaloniaTest]
    public async Task Pixel_geometry_reports_the_views_own_metrics_not_a_handlers()
    {
        var (view, pty, window) = LiveView();
        AnswerWith(view, 640, 480);   // a handler claiming other numbers is not consulted

        view.Terminal.Write(Esc + "[16t");

        var written = await PtyWaits.AwaitOutput(pty);
        Assert.That(written, Does.StartWith(Esc + "[6;"));
        Assert.That(written, Does.Not.Contain("640").And.Not.Contain("480"),
            "the view's real cell size goes out, not the handler's fiction");

        window.Close();
    }
}
