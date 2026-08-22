using System.Runtime.InteropServices;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The documented members that only mean anything with a real process behind them: ExitCode, Pid, and the
/// ProcessExited event as it surfaces on <see cref="TerminalControl"/> and <see cref="TerminalWindow"/>.
///
/// <para>Deliberately small, and deliberately NOT batched. ProcessExitCodeTests spawns 48 PTYs per assertion
/// because it is reproducing a scheduling race and needs the contention to do it. Nothing here is racing
/// anything — these assert that a value reaches the right property — so one shell each is the honest cost.
/// Copying the batching would multiply CI time for no additional coverage.</para>
///
/// <para>ProcessExited is a plain CLR event with no public raise path, so unlike the window-command events
/// it cannot be synthesised; a real child is the only way in.</para>
/// </summary>
[TestFixture]
public class TerminalProcessLifetimeTests
{
    private static bool Posix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static readonly TimeSpan ReportWindow = TimeSpan.FromSeconds(30);

    /// <summary>Wait for the control to report an exit, or null if it never does.</summary>
    private static async Task<int?> Exited(TerminalControl control, Func<Task> launch)
    {
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

        await launch();

        var done = await Task.WhenAny(exited.Task, Task.Delay(ReportWindow));
        return done == exited.Task ? exited.Task.Result : null;
    }

    /// <summary>
    /// README: ProcessExited "receives ProcessExitedEventArgs with the process ExitCode". Asserted on the
    /// CONTROL rather than the view, because the forwarding hop is this library's own code and is the part
    /// a consumer of TerminalControl actually subscribes to.
    /// </summary>
    [AvaloniaTest]
    public async Task ProcessExited_reaches_the_control_with_the_real_exit_code()
    {
        if (!Posix) Assert.Ignore("POSIX only");

        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            var code = await Exited(control, () => control.LaunchProcess(Path.GetTempPath(), "/bin/sh", "-c", "exit 5"));

            Assert.That(code, Is.Not.Null, "the control never reported an exit at all");
            Assert.That(code, Is.EqualTo(5), $"observed {code}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>README: ExitCode is "the exit code of the launched process after it has terminated".</summary>
    [AvaloniaTest]
    public async Task ExitCode_reflects_what_the_process_returned()
    {
        if (!Posix) Assert.Ignore("POSIX only");

        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            var code = await Exited(control, () => control.LaunchProcess(Path.GetTempPath(), "/bin/sh", "-c", "exit 5"));
            Assert.That(code, Is.Not.Null, "the control never reported an exit at all");

            Assert.That(control.ExitCode, Is.EqualTo(5),
                $"the event said {code} but the property says {control.ExitCode}; they describe the same process");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>README: Pid is "the operating system process identifier of the launched terminal process".</summary>
    [AvaloniaTest]
    public async Task Pid_identifies_the_launched_process()
    {
        if (!Posix) Assert.Ignore("POSIX only");

        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            await control.LaunchProcess(Path.GetTempPath(), "/bin/sh", "-c", "sleep 2");

            Assert.That(control.Pid, Is.GreaterThan(0), $"observed {control.Pid}");

            control.Kill();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// README: CurrentDirectory is "reported by the running terminal session". The launch itself seeds it
    /// from StartingDirectory, which is observable without depending on the shell emitting OSC 7 — plain
    /// bash on a CI box does not.
    /// </summary>
    [AvaloniaTest]
    public async Task CurrentDirectory_is_seeded_from_the_starting_directory()
    {
        if (!Posix) Assert.Ignore("POSIX only");

        var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            await control.LaunchProcess(dir, "/bin/sh", "-c", "sleep 2");

            Assert.That(control.CurrentDirectory, Is.EqualTo(dir),
                $"observed '{control.CurrentDirectory ?? "null"}'");

            control.Kill();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// README states the ordering explicitly: "If CloseOnProcessExit is true, the event is raised before the
    /// window closes." A host that inspects the window from its ProcessExited handler depends on that.
    /// </summary>
    [AvaloniaTest]
    public async Task CloseOnProcessExit_closes_the_window_after_raising_the_event()
    {
        if (!Posix) Assert.Ignore("POSIX only");

        var window = new TerminalWindow { Process = "", CloseOnProcessExit = true }.Realise();

        var openAtRaise = (bool?)null;
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ProcessExited += (_, e) =>
        {
            openAtRaise = window.IsVisible;
            exited.TrySetResult(e.ExitCode);
        };

        await window.LaunchProcess(Path.GetTempPath(), "/bin/sh", "-c", "exit 0");

        var done = await Task.WhenAny(exited.Task, Task.Delay(ReportWindow));

        Assert.That(done, Is.SameAs(exited.Task), "the window never reported an exit");
        Assert.That(openAtRaise, Is.True,
            "the event has to arrive while the window is still open, or a handler cannot inspect it");
    }

    /// <summary>The opt-out: the window stays open, and this test is responsible for closing it.</summary>
    [AvaloniaTest]
    public async Task CloseOnProcessExit_false_leaves_the_window_open()
    {
        if (!Posix) Assert.Ignore("POSIX only");

        var window = new TerminalWindow { Process = "", CloseOnProcessExit = false }.Realise();

        try
        {
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

            await window.LaunchProcess(Path.GetTempPath(), "/bin/sh", "-c", "exit 0");

            var done = await Task.WhenAny(exited.Task, Task.Delay(ReportWindow));
            Assert.That(done, Is.SameAs(exited.Task), "the window never reported an exit");

            Assert.That(window.IsVisible, Is.True,
                "the host opted out of automatic closing and the window closed anyway");
        }
        finally
        {
            window.Close();
        }
    }
}
