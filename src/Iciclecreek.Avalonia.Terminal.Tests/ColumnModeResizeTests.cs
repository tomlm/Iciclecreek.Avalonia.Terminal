using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// DECCOLM's 80/132 switch reaching the host as a window resize.
/// </summary>
/// <remarks>
/// <para>The emulator has always announced the switch — <c>SetColumnMode</c> re-grids through
/// <c>Terminal.Resize</c>, which raises <c>Resized</c> — but nothing in this control listened, so
/// the buffer became 132 columns wide while the window kept its 80-column pixel width and the
/// columns past the edge were clipped. The application, meanwhile, believed all 132 were visible.</para>
/// <para>The other half of these tests is the resize that must NOT come back: the host re-grids the
/// emulator from its own layout on every arrange, and answering that with a window resize would
/// snap the window to an exact multiple of the cell each time a user dragged its edge.</para>
/// </remarks>
[TestFixture]
public class ColumnModeResizeTests
{
    private const string Esc = "";

    private static (TerminalView view, Window window) Realised()
    {
        // Opted in: the window resize DECCOLM asks for is off by default now, so a test about
        // that resize has to be a host that permits it.
        var view = new TerminalView { Process = "", AllowWindowOps = true };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    [AvaloniaTest]
    public void Switching_to_132_columns_asks_the_host_for_a_window_that_fits_them()
    {
        var (view, window) = Realised();
        try
        {
            var reports = new List<(int Width, int Height)>();
            view.WindowResized += (_, e) => reports.Add((e.Width, e.Height));

            // Mode 40 is the gate DECCOLM swings on, in the emulator and in xterm alike: without it
            // a stray CSI ? 3 h does nothing at all and this test would pass for the wrong reason.
            view.Terminal.Write($"{Esc}[?40h{Esc}[?3h");
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Cols, Is.EqualTo(132),
                "sanity: the emulator re-gridded, which was never the missing half");

            Assert.That(reports, Is.Not.Empty,
                "the switch has to reach the host; silently clipping is the one wrong answer");

            var wanted = (int)System.Math.Ceiling(132 * view.CharWidth + System.Math.Max(0, view.GutterWidth));
            Assert.That(reports[^1].Width, Is.EqualTo(wanted),
                "and it has to ask for the width 132 columns actually need");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Switching_back_to_80_columns_asks_for_the_narrower_window()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[?40h{Esc}[?3h");
            Dispatcher.UIThread.RunJobs();

            var reports = new List<(int Width, int Height)>();
            view.WindowResized += (_, e) => reports.Add((e.Width, e.Height));

            view.Terminal.Write($"{Esc}[?3l");
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Cols, Is.EqualTo(80));

            var wanted = (int)System.Math.Ceiling(80 * view.CharWidth + System.Math.Max(0, view.GutterWidth));
            Assert.That(reports.Select(r => r.Width), Does.Contain(wanted),
                "the switch back is a resize too, not just the switch out");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_request_covers_what_the_host_spends_around_the_control()
    {
        // The request is a WINDOW size but the columns only get what is left after padding, borders
        // and anything else sharing the window. Computing it from the column count alone delivered
        // 130 columns inside an 8px padding to a program that had been told it had 132 -- the same
        // silent shortfall the whole change is about, arriving by another route.
        // Opted in: the window resize DECCOLM asks for is off by default now, so a test about
        // that resize has to be a host that permits it.
        var view = new TerminalView { Process = "", AllowWindowOps = true };
        var host = new Border { Padding = new Thickness(12, 0, 12, 0), Child = view };
        var window = new Window { Width = 800, Height = 600, Content = host };
        view.WindowResized += (_, e) => { window.Width = e.Width; window.Height = e.Height; };
        window.Show();
        window.UpdateLayout();

        try
        {
            view.Terminal.Write($"{Esc}[?40h{Esc}[?3h");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.That(view.Terminal.Cols, Is.EqualTo(132),
                "a program that asked for 132 columns has to end up with 132 of them, padding or no");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_hosts_own_relayout_does_not_bounce_back_as_a_resize_request()
    {
        // The loop this guards against: laying the control out at a new size re-grids the emulator,
        // which raises Resized, which -- unguarded -- would ask the host to resize the window it had
        // just sized itself. Converging or not, it would snap every drag to a whole number of cells.
        var (view, window) = Realised();
        try
        {
            var reports = new List<(int Width, int Height)>();
            view.WindowResized += (_, e) => reports.Add((e.Width, e.Height));

            var before = view.Terminal.Cols;

            window.Width = 640;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Cols, Is.Not.EqualTo(before),
                "sanity: the relayout really did re-grid the emulator");

            Assert.That(reports, Is.Empty,
                "a resize the host itself caused must not come back to it as a request");
        }
        finally { window.Close(); }
    }
}
