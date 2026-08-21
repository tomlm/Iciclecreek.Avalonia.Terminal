using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// What <see cref="TerminalView.ProcessExited"/> reports has to be what the process actually returned. It is
/// the only thing a host has to go on — the buffer's own "Process exited with code" line is frequently cleared
/// by the host on the way back to an idle state — so a wrong code here becomes a UI that tells the user their
/// build succeeded when it failed.
///
/// <para>Repeated deliberately. Two paths can report an exit (the pty layer's own event, and the read loop's
/// EOF fallback) racing behind one interlock, and which of them wins depends on scheduling — so a single pass
/// proves nothing. The batch is what makes the assertion mean "always" rather than "this time".</para>
/// </summary>
[TestFixture]
public class ProcessExitCodeTests
{
    private static bool Posix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // Asserts on the codes that were REPORTED, not on how many arrived in the window. Spawning a batch of PTYs
    // on a two-core runner starves some of them — measured, 7 of 32 never reported inside 10s on ubuntu-latest,
    // while every code that DID arrive was correct. Failing on that would be reporting the runner's throughput
    // as a product defect.
    //
    // Contention is what surfaces the race: it first appeared in a full-assembly run, not alone. Even so this
    // reproduction is STATISTICAL, so the batch size was chosen by MEASURING it rather than by taste. Against
    // the unfixed code:
    //
    //     12 spawns (2 x 6)   caught it 0 times in 1 run   <- looked like a guard, was not one
    //     48 spawns (4 x 12)  caught it 3 times in 3 runs
    //
    // and 48 stays green 4 runs in 4 against the fix, so it is not merely trading a false negative for a false
    // positive. The first shape is worth recording because it PASSED with the bug reinstated — a test that
    // exercises the right code path and still cannot fail is the most expensive kind to trust.
    //
    // A DETERMINISTIC version — handing the view a connection whose child has exited but not yet been reaped,
    // so the window is modelled rather than raced for — needs a way to inject an IPtyConnection. TerminalView
    // spawns through the static Porta.Pty PtyProvider, so there is no seam for that today. Adding one (an
    // AttachConnection(IPtyConnection) overload, say) would make this a tight guard instead of a probabilistic
    // one, and I'm happy to offer that separately if it's wanted.
    private const int Rounds = 4;
    private const int Concurrent = 12;
    private static readonly TimeSpan ReportWindow = TimeSpan.FromSeconds(30);

    /// <summary>Every exit code reported across all rounds, in completion order.</summary>
    private static async Task<List<int?>> ReportedExitCodes(int code)
    {
        var seen = new List<int?>();
        for (var round = 0; round < Rounds; round++)
        {
            var batch = Enumerable.Range(0, Concurrent).Select(_ => ReportedExitCode(code)).ToArray();
            seen.AddRange(await Task.WhenAll(batch));
        }

        return seen;
    }

    /// <summary>Run a shell that exits with <paramref name="code"/> and return what the view reported — or
    /// null if it never reported at all.</summary>
    private static async Task<int?> ReportedExitCode(int code)
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 400, Height = 200, Content = view };
        window.Show();

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

        await view.LaunchProcess(Path.GetTempPath(), "/bin/sh", "-c", $"exit {code}");

        var done = await Task.WhenAny(exited.Task, Task.Delay(ReportWindow));
        window.Close();
        return done == exited.Task ? exited.Task.Result : null;
    }

    private static string Detail(List<int?> seen) =>
        " Saw: " + string.Join(",", seen.Select(c => c?.ToString() ?? "none"));

    /// <summary>
    /// The defect this guards: EOF on the pty master means the child closed its end, which can beat the child
    /// being REAPED — and until it is reaped, ExitCode is still its default 0. Reading it straight away reported
    /// a clean exit for a process that had failed. Measured before the fix: 20 runs of `sh -c "exit 3"` reported
    /// 0 once, which is the worst possible failure rate — frequent enough to reach a user, rare enough to be
    /// dismissed as a fluke.
    /// </summary>
    [AvaloniaTest]
    public async Task A_nonzero_exit_code_is_reported_faithfully()
    {
        if (!Posix) Assert.Ignore("POSIX only");

        var seen = await ReportedExitCodes(3);

        Assert.That(seen.Any(c => c.HasValue), Is.True, "the exit path has to work at all." + Detail(seen));
        Assert.That(seen.Where(c => c.HasValue), Is.All.EqualTo(3),
            "a reported code that is not 3 is a host being told the wrong outcome." + Detail(seen));
    }

    /// <summary>The other half: waiting for the reap must not turn a genuine success into anything else.</summary>
    [AvaloniaTest]
    public async Task A_clean_exit_is_reported_as_zero()
    {
        if (!Posix) Assert.Ignore("POSIX only");

        var seen = await ReportedExitCodes(0);

        Assert.That(seen.Any(c => c.HasValue), Is.True, "the exit path has to work at all." + Detail(seen));
        Assert.That(seen.Where(c => c.HasValue), Is.All.EqualTo(0), Detail(seen));
    }
}
