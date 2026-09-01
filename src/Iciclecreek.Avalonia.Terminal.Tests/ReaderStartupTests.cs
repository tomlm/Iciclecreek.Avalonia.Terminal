using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// A process that exits almost immediately must still have its output shown.
///
/// <para>The process is already running the moment <c>SpawnAsync</c> returns, so every instant before the
/// read loop's first read is a window in which it can write, finish, and have that output discarded. A shell
/// that exits at once loses everything; one that lives loses its opening banner, which presents as a pane
/// that opened blank.</para>
/// </summary>
[TestFixture]
public class ReaderStartupTests
{
    private static Window Show(Control content)
    {
        var window = new Window { Width = 800, Height = 600, Content = content };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out after {timeoutMs}ms waiting until {because}");
            await Task.Delay(10);
        }
    }

    /// <summary>Everything the emulator has, as text.</summary>
    private static string BufferText(TerminalView view)
    {
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < view.Terminal.Buffer.Length; y++)
        {
            var line = view.Terminal.Buffer.GetLine(y);
            if (line == null) continue;
            for (int x = 0; x < line.Length; x++)
                sb.Append(string.IsNullOrEmpty(line[x].Content) ? " " : line[x].Content);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The failure mechanism, reproduced directly: a SATURATED thread pool.
    ///
    /// <para>A reader started with <c>Task.Run</c> queues behind whatever the pool is already running, and
    /// the read it then performs blocks a pool thread for the life of the process — so under contention the
    /// reader may not start for seconds, and the output of a process that has already exited is gone. A
    /// reader given a dedicated thread cannot be starved this way.</para>
    ///
    /// <para>The pool is deliberately pinned with blocking work before the launch. That is what a contended
    /// CI box does on its own and what an idle developer machine never does, which is why the plain
    /// concurrency test below passes either way and this one does not.</para>
    /// </summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "uses sh; the window under test is not platform-specific")]
    public async Task Output_survives_a_saturated_thread_pool()
    {
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        var release = new ManualResetEventSlim(false);
        try
        {
            // Min threads pinned low so the pool will not readily inject more, then enough occupants to
            // hold what it already has. Both parts are needed and the size is not arbitrary: clamping with
            // SetMaxThreads is refused by the runtime below ProcessorCount, and a lighter crowd
            // (ProcessorCount * 2) stops reproducing the defect at all — verified by running this against
            // the unfixed reader, where it passed.
            ThreadPool.SetMinThreads(1, minIo);
            var hogs = Environment.ProcessorCount * 8;
            for (int i = 0; i < hogs; i++)
                ThreadPool.UnsafeQueueUserWorkItem(_ => release.Wait(10_000), null);

            const string marker = "survivedxyz";
            var view = new TerminalView
            {
                Process = "sh",
                ProcessArgs = new List<string> { "-c", $"echo {marker}" },
            };
            var window = Show(view);

            await view.LaunchProcess();

            try
            {
                await WaitUntil(() => BufferText(view).Contains(marker),
                    "the output of an already-exited process reached the buffer", timeoutMs: 8_000);
            }
            finally { window.Close(); }
        }
        finally
        {
            release.Set();
            ThreadPool.SetMinThreads(minWorker, minIo);
        }
    }

    /// <summary>
    /// Several at once — the ordinary version of the same scenario.
    ///
    /// <para>Stated plainly because it would otherwise be mistaken for a guard: this test passes against the
    /// UNFIXED reader too, verified over repeated runs. The defect never reproduced on an idle machine and
    /// was near-total on a contended one, so concurrency alone does not provoke it — pool saturation does,
    /// which is what the test above exists for.</para>
    ///
    /// <para>It is kept as a regression test: it exercises several readers starting at once over the real
    /// pty layer, which is worth holding still whatever the reader's threading turns out to be.</para>
    /// </summary>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "uses sh; the window under test is not platform-specific")]
    public async Task Concurrent_short_lived_processes_all_deliver_their_output()
    {
        const int count = 8;
        var views = new List<(TerminalView view, Window window, string marker)>();

        for (int i = 0; i < count; i++)
        {
            var marker = $"marker{i}xyz";
            var view = new TerminalView
            {
                Process = "sh",
                ProcessArgs = new List<string> { "-c", $"echo {marker}" },
            };
            views.Add((view, Show(view), marker));
        }

        await Task.WhenAll(views.Select(v => v.view.LaunchProcess()));

        try
        {
            foreach (var (view, _, marker) in views)
                await WaitUntil(() => BufferText(view).Contains(marker), $"'{marker}' reached the buffer");
        }
        finally
        {
            foreach (var (_, window, _) in views) window.Close();
        }

        Assert.Pass($"all {count} short-lived processes delivered their output");
    }
}
