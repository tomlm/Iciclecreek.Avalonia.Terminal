using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Whether the program may rearrange the window it is running in.
/// </summary>
/// <remarks>
/// <para>The XTERM window-operation family — move, resize, minimise, maximise, restore, raise,
/// lower, fullscreen — plus DECCOLM's 80/132 switch, which asks for a resize by the same route.
/// This host forwarded all of them unconditionally; xterm gates the same set behind a resource of
/// this name and defaults it OFF. It is left ON here so hosts gain a control rather than lose
/// behaviour.</para>
/// <para>Refusal is silent: nothing is raised, so no host acts, and a bare view with its own
/// handler is covered as well as <c>TerminalWindow</c>.</para>
/// </remarks>
[TestFixture]
public class AllowWindowOpsTests
{
    private const string Esc = "";

    /// <summary>
    /// A view whose EMULATOR permits window manipulation, which is a separate gate and off by
    /// default: XTerm.NET's WindowOptions has one flag per operation and TerminalView enables only
    /// the four report flags. Without this the ops never reach the host at all and every assertion
    /// below would pass whether or not AllowWindowOps did anything.
    /// </summary>
    private static (TerminalView view, Window window) Realised()
    {
        var options = new XTerm.Options.TerminalOptions();
        options.WindowOptions.SetWinSizeChars = true;
        options.WindowOptions.GetWinSizeChars = true;

        var view = new TerminalView { Process = "", Options = options };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    [AvaloniaTest]
    public void Window_operations_are_refused_by_default()
    {
        var (view, window) = Realised();
        try
        {
            Assert.That(view.AllowWindowOps, Is.False,
                "opt-in, like xterm's resource and like every flag in the emulator's WindowOptions");

            var asked = false;
            view.WindowResized += (_, _) => asked = true;

            view.Terminal.Write($"{Esc}[8;30;100t");
            Dispatcher.UIThread.RunJobs();

            Assert.That(asked, Is.False, "CSI 8 t does not reach the host until a host says it may");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Turning_it_on_lets_a_resize_request_through()
    {
        var (view, window) = Realised();
        try
        {
            view.AllowWindowOps = true;

            var asked = false;
            view.WindowResized += (_, _) => asked = true;

            view.Terminal.Write($"{Esc}[8;30;100t");
            Dispatcher.UIThread.RunJobs();

            Assert.That(asked, Is.True,
                "a host that opts in gets the request it asked for");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void DECCOLM_answers_to_the_same_switch()
    {
        // DECCOLM asks by the same route, so it answers to the same switch.
        var (view, window) = Realised();
        try
        {
            var asked = false;
            view.WindowResized += (_, _) => asked = true;

            view.Terminal.Write($"{Esc}[?40h{Esc}[?3h");
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Cols, Is.EqualTo(132),
                "the emulator still re-grids -- that gate is upstream, behind a mode the program sets "
                + "for itself, so refusing here does not undo it and the extra columns clip");

            Assert.That(asked, Is.False, "but the window is not asked to grow");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Reports_still_tell_the_truth_when_operations_are_refused()
    {
        // Refusing to be MOVED is not a reason to lie about where you are. xterm keeps its reports
        // working with allowWindowOps off, and a program that cannot trust the answer has no way to
        // lay itself out at all.
        var (view, window) = Realised();
        try
        {
            view.AllowWindowOps = false;

            // Asserted on the REPLY the program receives, not on a routed event: the view answers
            // size questions itself rather than passing them out, which is the behaviour that has
            // to survive.
            var reply = "";
            view.Terminal.DataReceived += (_, e) => reply += e.Data;

            view.Terminal.Write($"{Esc}[18t");
            Dispatcher.UIThread.RunJobs();

            Assert.That(reply, Does.Contain("8;"),
                "CSI 18 t still reports the text area -- refusing to be moved is not a reason to lie, "
                + $"and a program that cannot trust the answer cannot lay itself out (got '{reply}')");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_switch_is_live()
    {
        var (view, window) = Realised();
        try
        {
            var count = 0;
            view.WindowResized += (_, _) => count++;

            view.Terminal.Write($"{Esc}[8;30;100t");
            Dispatcher.UIThread.RunJobs();
            Assert.That(count, Is.EqualTo(0), "off is where it starts");

            view.AllowWindowOps = true;
            view.Terminal.Write($"{Esc}[8;31;101t");
            Dispatcher.UIThread.RunJobs();
            Assert.That(count, Is.EqualTo(1), "on takes effect without a rebuild");

            view.AllowWindowOps = false;
            view.Terminal.Write($"{Esc}[8;32;102t");
            Dispatcher.UIThread.RunJobs();
            Assert.That(count, Is.EqualTo(1), "and off again means off again");
        }
        finally { window.Close(); }
    }
}
