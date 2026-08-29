using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// In-band resize reports (DEC private mode 2048) for the case <c>Resize</c> cannot see: the PIXEL
/// size of the text area changing while the grid stays the shape it was.
/// </summary>
/// <remarks>
/// <para>A DPI switch from dragging the window to another monitor leaves the terminal 80x24 while
/// every pixel dimension the application was told about becomes wrong. Anything sizing images or
/// Sixel output from the reported geometry goes on drawing at the old scale.</para>
/// <para>The scaling change is what makes these tests mean anything. A FONT change would report
/// too, but for the wrong reason — a bigger cell box usually changes the row and column counts as
/// well, so <c>Resize</c> sends its own report and a test cannot tell the two apart. Changing only
/// the render scaling holds the grid still, which is exactly the case the host has to notice for
/// itself.</para>
/// </remarks>
[TestFixture]
public class InBandResizeTests
{
    private const string Esc = "";

    private static (TerminalView view, Window window) Realised()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    /// <summary>The in-band reports an application asked for, in the order they were sent.</summary>
    private static List<string> Reports(TerminalView view)
    {
        var reports = new List<string>();
        view.Terminal.DataReceived += (_, e) =>
        {
            if (e.Data.StartsWith($"{Esc}[48;"))
                reports.Add(e.Data);
        };
        return reports;
    }

    private static void Rescale(Window window, TerminalView view, double scaling)
    {
        window.SetRenderScaling(scaling);
        view.InvalidateMeasure();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTest]
    public void A_scaling_change_reports_although_the_grid_did_not_move()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[?2048h");        // the application asks for reports
            Dispatcher.UIThread.RunJobs();

            var cols = view.Terminal.Cols;
            var rows = view.Terminal.Rows;
            var reports = Reports(view);

            Rescale(window, view, 2.0);

            Assert.Multiple(() =>
            {
                Assert.That(view.Terminal.Cols, Is.EqualTo(cols), "the grid must not have moved");
                Assert.That(view.Terminal.Rows, Is.EqualTo(rows), "the grid must not have moved");
                Assert.That(reports, Is.Not.Empty,
                    "the text area is twice the pixels it was, and the application asked to be told");
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_scaling_change_moves_the_cell_metrics_the_emulator_holds()
    {
        // The report is only worth sending because the numbers behind it moved. Sixel and Kitty
        // both size themselves from these.
        var (view, window) = Realised();
        try
        {
            var width = view.Terminal.Options.CellWidthPixels;
            var height = view.Terminal.Options.CellHeightPixels;

            Rescale(window, view, 2.0);

            Assert.Multiple(() =>
            {
                Assert.That(view.Terminal.Options.CellWidthPixels, Is.GreaterThan(width));
                Assert.That(view.Terminal.Options.CellHeightPixels, Is.GreaterThan(height));
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Laying_the_view_out_again_at_the_same_size_reports_nothing()
    {
        // The publish runs from UpdateTextMetrics, which MeasureOverride calls on EVERY layout
        // pass. Reporting unconditionally there would hand an application a resize report every
        // time the view was measured -- a flood rather than a notification, and the reason the
        // metrics are compared before anything is sent.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[?2048h");
            Dispatcher.UIThread.RunJobs();

            var reports = Reports(view);

            for (var i = 0; i < 5; i++)
            {
                view.InvalidateMeasure();
                window.UpdateLayout();
            }
            Dispatcher.UIThread.RunJobs();

            Assert.That(reports, Is.Empty,
                "nothing about the text area changed, so there was nothing to report");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void An_application_that_did_not_ask_is_told_nothing()
    {
        // Mode 2048 is the request. Without it the emulator drops the notification, which is what
        // makes it safe for the host to call this wherever font metrics are recomputed.
        var (view, window) = Realised();
        try
        {
            var reports = Reports(view);

            Rescale(window, view, 2.0);

            Assert.That(reports, Is.Empty);
        }
        finally { window.Close(); }
    }
}
