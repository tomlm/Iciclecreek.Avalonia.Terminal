using System.Collections.Concurrent;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;
using Porta.Pty;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// <see cref="TerminalView.LaunchBeforeLoaded"/>: spawning the process before the first layout instead of
/// at Loaded, then giving it the laid-out size.
/// </summary>
/// <remarks>
/// <para>"Before the window" here means before the view has been laid out or loaded, which is as early as
/// a view can launch at all. Avalonia initialises a control only once it is attached to a logical root, and
/// a window's content is adopted by its presenter during the first measure -- so OnInitialized, where the
/// emulator is built, runs inside Show's first layout pass, ahead of the view's own measure. Each test
/// therefore records the view's state AT THE MOMENT OF THE SPAWN rather than asserting before Show.</para>
/// <para>Every test but the last drives the launch through the view's spawn seam rather than a real pty.
/// What is under test is ordering -- when the spawn starts, and what happens if Loaded or Dispose or an
/// attach lands while it is still in flight -- and a real spawn finishes on its own schedule, so the
/// in-flight window would be there or not by luck. The seam holds it open for as long as the test
/// needs. The last test carries the real dependency once.</para>
/// </remarks>
[TestFixture]
public class LaunchBeforeLoadedTests
{
    /// <summary>A pty double that records what the view asks of it, and is quiet until killed.</summary>
    private sealed class SpyConnection : IPtyConnection
    {
        private readonly PushStream _reader = new();

        public ConcurrentQueue<(int Cols, int Rows)> Resizes { get; } = new();
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }

        public Stream ReaderStream => _reader;
        public Stream WriterStream { get; } = new MemoryStream();
        public int ExitCode => 0;
        public bool WaitForExit(int milliseconds) => Killed;
        public int Pid => -1;
        public void Kill() { Killed = true; _reader.Done(); }
        public void Resize(int columns, int rows) => Resizes.Enqueue((columns, rows));
        public void Dispose() { Disposed = true; _reader.Done(); }
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
    }

    /// <summary>What the view looked like when it asked for a process.</summary>
    private readonly record struct AtSpawn(PtyOptions Options, bool Measured, bool Arranged, bool Loaded);

    /// <summary>Counts spawns, records the view's state at each, and completes them when told to.</summary>
    private sealed class Spawner
    {
        private readonly TerminalView _view;
        private readonly bool _holdOpen;

        public Spawner(TerminalView view, bool holdOpen)
        {
            _view = view;
            _holdOpen = holdOpen;
            view.SpawnOverride = Spawn;
        }

        public List<AtSpawn> Calls { get; } = new();
        public List<TaskCompletionSource<IPtyConnection>> Pending { get; } = new();
        public List<SpyConnection> Spawned { get; } = new();

        private Task<IPtyConnection> Spawn(PtyOptions options, CancellationToken token)
        {
            Calls.Add(new AtSpawn(options, _view.IsMeasureValid, _view.IsArrangeValid, _view.IsLoaded));
            if (!_holdOpen)
            {
                var connection = new SpyConnection();
                Spawned.Add(connection);
                return Task.FromResult<IPtyConnection>(connection);
            }

            var pending = new TaskCompletionSource<IPtyConnection>();
            Pending.Add(pending);
            return pending.Task;
        }
    }

    private static (TerminalView view, Spawner spawner) NewView(bool launchBeforeLoaded = true, bool holdOpen = false,
                                                              string process = "sh")
    {
        var view = new TerminalView { Process = process, LaunchBeforeLoaded = launchBeforeLoaded };
        return (view, new Spawner(view, holdOpen));
    }

    /// <summary>
    /// Show the view in a window and run the first layout. Deliberately does NOT pump the dispatcher:
    /// Loaded is posted, so when this returns the view has been laid out and is not yet loaded.
    /// </summary>
    private static Window Show(TerminalView view)
    {
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out after {timeoutMs}ms waiting until {because}");
            await Task.Delay(10);
        }
    }

    // ---- the surface ------------------------------------------------------------------------------

    [AvaloniaTest]
    public void Off_by_default_on_every_surface()
    {
        var window = new TerminalWindow { Process = "" };
        try
        {
            Assert.That(new TerminalView().LaunchBeforeLoaded, Is.False, "view");
            Assert.That(new TerminalControl().LaunchBeforeLoaded, Is.False, "control");
            Assert.That(window.LaunchBeforeLoaded, Is.False, "window");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Set_on_a_window_it_reaches_the_view()
    {
        var window = new TerminalWindow { Process = "", LaunchBeforeLoaded = true }.Realise();
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(window.Control().LaunchBeforeLoaded, Is.True, "first hop");
                Assert.That(window.Control().View().LaunchBeforeLoaded, Is.True, "second hop");
            });
        }
        finally { window.Close(); }
    }

    // ---- when it spawns ---------------------------------------------------------------------------

    [AvaloniaTest]
    public async Task Default_off_still_launches_at_Loaded()
    {
        var (view, spawner) = NewView(launchBeforeLoaded: false);
        var window = Show(view);
        try
        {
            Assert.That(spawner.Calls, Is.Empty, "nothing spawns during layout unless asked to");

            await WaitUntil(() => spawner.Calls.Count > 0, "Loaded launched the process");
            Assert.Multiple(() =>
            {
                Assert.That(spawner.Calls, Has.Count.EqualTo(1));
                Assert.That(spawner.Calls[0].Loaded, Is.True, "the launch came from Loaded");
                Assert.That(view.IsLive, Is.True);
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Opted_in_launches_before_the_view_is_laid_out_or_loaded()
    {
        var (view, spawner) = NewView();
        var window = Show(view);
        try
        {
            Assert.That(spawner.Calls, Has.Count.EqualTo(1), "spawned without waiting for Loaded");
            var at = spawner.Calls[0];
            Assert.Multiple(() =>
            {
                Assert.That(at.Measured, Is.False, "before the view's first measure");
                Assert.That(at.Arranged, Is.False, "before its first arrange");
                Assert.That(at.Loaded, Is.False, "before Loaded");
                Assert.That((at.Options.Cols,at.Options.Rows), Is.EqualTo((80, 24)),
                    "at the provisional size, 80x24 when Options does not say otherwise");
                Assert.That(view.IsLive, Is.True, "and the connection is installed");
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_provisional_size_comes_from_Options_when_set()
    {
        var view = new TerminalView
        {
            Process = "sh",
            LaunchBeforeLoaded = true,
            Options = new XTerm.Options.TerminalOptions { Cols = 132, Rows = 50 },
        };
        var spawner = new Spawner(view, holdOpen: false);
        var window = Show(view);
        try
        {
            Assert.That((spawner.Calls[0].Options.Cols, spawner.Calls[0].Options.Rows), Is.EqualTo((132, 50)));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Opted_in_launches_when_Process_arrives_after_initialisation()
    {
        var (view, spawner) = NewView(process: "");
        var window = Show(view);
        try
        {
            Assert.That(spawner.Calls, Is.Empty, "no process to run yet");

            view.Process = "sh";
            Dispatcher.UIThread.RunJobs();

            Assert.That(spawner.Calls, Has.Count.EqualTo(1), "exactly one launch, whichever path took it");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The queued launch meeting Loaded, with the spawn held open so a second launch could not hide behind
    /// an already-installed connection.
    /// </summary>
    [AvaloniaTest]
    public async Task A_queued_launch_and_Loaded_spawn_once_between_them()
    {
        var (view, spawner) = NewView(process: "", holdOpen: true);
        var window = Show(view);
        try
        {
            view.Process = "sh";
            await WaitUntil(() => view.IsLoaded, "Loaded fired");
            Dispatcher.UIThread.RunJobs();

            Assert.That(spawner.Calls, Has.Count.EqualTo(1), "one spawn, whichever of the two got there first");
        }
        finally { window.Close(); }
    }

    // ---- the size it ends up with -----------------------------------------------------------------

    [AvaloniaTest]
    public void The_first_layout_resizes_the_early_process()
    {
        var (view, spawner) = NewView();
        var window = Show(view);
        try
        {
            var laidOut = (view.Terminal.Cols, view.Terminal.Rows);
            Assert.That(laidOut, Is.Not.EqualTo((80, 24)), "sanity: the window is not exactly 80x24 cells");

            Assert.That(spawner.Spawned.Single().Resizes, Does.Contain(laidOut), "the pty was told the laid-out size");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The first layout landing while the spawn is still in flight: ArrangeOverride re-grids the emulator
    /// but has no connection to resize, so the launch must catch the process up once it is installed.
    /// </summary>
    [AvaloniaTest]
    public async Task Layout_during_the_spawn_still_reaches_the_process()
    {
        var (view, spawner) = NewView(holdOpen: true);
        var window = Show(view);
        try
        {
            Assert.That(spawner.Pending, Has.Count.EqualTo(1), "sanity: the spawn is still in flight");
            var laidOut = (view.Terminal.Cols, view.Terminal.Rows);
            Assert.That(laidOut, Is.Not.EqualTo((80, 24)), "sanity: layout re-gridded the emulator meanwhile");

            var connection = new SpyConnection();
            spawner.Pending[0].SetResult(connection);

            await WaitUntil(() => view.IsLive, "the early spawn was installed");
            Assert.That(connection.Resizes, Does.Contain(laidOut));
        }
        finally { window.Close(); }
    }

    // ---- exactly one process ----------------------------------------------------------------------

    /// <summary>
    /// The double-spawn guard. Loaded arrives while the early spawn has not returned, so there is no
    /// connection yet -- the null check OnLoaded always had cannot tell this from "nothing launched".
    /// </summary>
    [AvaloniaTest]
    public async Task Loaded_during_the_early_spawn_does_not_spawn_again()
    {
        var (view, spawner) = NewView(holdOpen: true);
        var window = Show(view);
        try
        {
            Assert.That(spawner.Calls, Has.Count.EqualTo(1), "sanity: the early spawn started");

            await WaitUntil(() => view.IsLoaded, "Loaded fired");
            Dispatcher.UIThread.RunJobs();
            Assert.That(spawner.Calls, Has.Count.EqualTo(1), "Loaded left the in-flight spawn alone");

            var connection = new SpyConnection();
            spawner.Pending[0].SetResult(connection);
            await WaitUntil(() => view.IsLive, "the early spawn was installed");

            Assert.Multiple(() =>
            {
                Assert.That(spawner.Calls, Has.Count.EqualTo(1), "still exactly one spawn");
                Assert.That(connection.Killed, Is.False, "and it was not superseded");
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Loaded_after_the_early_spawn_does_not_spawn_again()
    {
        var (view, spawner) = NewView();
        var window = Show(view);
        try
        {
            await WaitUntil(() => view.IsLoaded, "Loaded fired");
            Dispatcher.UIThread.RunJobs();

            Assert.That(spawner.Calls, Has.Count.EqualTo(1));
            Assert.That(spawner.Spawned.Single().Killed, Is.False);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Moved off the tree and back while the early spawn is in flight. The detach cleans up, so the spawn
    /// comes back superseded and kills itself; the re-attach's Loaded must not defer to it.
    /// </summary>
    [AvaloniaTest]
    public async Task Detach_and_reattach_during_the_early_spawn_still_ends_with_a_process()
    {
        var (view, spawner) = NewView(holdOpen: true);
        var window = Show(view);
        try
        {
            Assert.That(spawner.Calls, Has.Count.EqualTo(1), "sanity: the early spawn started");

            window.Content = null;
            window.Content = view;
            window.UpdateLayout();
            await WaitUntil(() => view.IsLoaded, "the re-attached view loaded");
            Dispatcher.UIThread.RunJobs();

            Assert.That(spawner.Calls, Has.Count.EqualTo(2), "Loaded launched in place of the superseded spawn");

            var early = new SpyConnection();
            var replacement = new SpyConnection();
            spawner.Pending[1].SetResult(replacement);
            spawner.Pending[0].SetResult(early);
            await WaitUntil(() => view.IsLive, "the replacement was installed");
            Dispatcher.UIThread.RunJobs();

            Assert.Multiple(() =>
            {
                Assert.That(early.Killed, Is.True, "the superseded early spawn is not left running");
                Assert.That(replacement.Killed, Is.False);
                Assert.That(view.DetachConnection(), Is.SameAs(replacement), "the view ends with a process");
            });
        }
        finally { window.Close(); }
    }

    /// <summary>The same move under BeginReparent, which keeps the process: still exactly one.</summary>
    [AvaloniaTest]
    public async Task Reparenting_during_the_early_spawn_keeps_the_one_process()
    {
        var (view, spawner) = NewView(holdOpen: true);
        var window = Show(view);
        try
        {
            view.BeginReparent();
            window.Content = null;
            window.Content = view;
            view.EndReparent();
            window.UpdateLayout();
            await WaitUntil(() => view.IsLoaded, "the re-parented view loaded");
            Dispatcher.UIThread.RunJobs();

            var early = new SpyConnection();
            spawner.Pending[0].SetResult(early);
            await WaitUntil(() => view.IsLive, "the early spawn was installed");

            Assert.Multiple(() =>
            {
                Assert.That(spawner.Calls, Has.Count.EqualTo(1));
                Assert.That(early.Killed, Is.False);
            });
        }
        finally { window.Close(); }
    }

    // ---- what shapes the launch -------------------------------------------------------------------

    /// <summary>
    /// Avalonia raises Initialized straight after OnInitialized, and a host's Initialized handler is an
    /// ordinary place to finish configuring a view. Its values must reach the early launch.
    /// </summary>
    [AvaloniaTest]
    public void Settings_from_an_Initialized_handler_shape_the_early_launch()
    {
        var directory = Path.GetTempPath();
        var (view, spawner) = NewView();
        view.Initialized += (_, _) =>
        {
            view.StartingDirectory = directory;
            view.ProcessArgs = new List<string> { "-c", "echo hi" };
            view.EnvironmentVariables = new Dictionary<string, string> { ["EARLY_PROBE"] = "1" };
        };

        var window = Show(view);
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.That(spawner.Calls, Has.Count.EqualTo(1));
            var at = spawner.Calls[0];
            Assert.Multiple(() =>
            {
                Assert.That(at.Options.Cwd, Is.EqualTo(directory), "StartingDirectory");
                Assert.That(at.Options.CommandLine, Is.EqualTo(new[] { "-c", "echo hi" }), "ProcessArgs");
                Assert.That(at.Options.Environment, Does.ContainKey("EARLY_PROBE"), "EnvironmentVariables");
                Assert.That(at.Arranged, Is.False, "and still before the view's first arrange");
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Process_set_in_an_Initialized_handler_launches_once_before_layout()
    {
        var (view, spawner) = NewView(process: "");
        view.Initialized += (_, _) => view.Process = "sh";

        var window = Show(view);
        try
        {
            Assert.That(spawner.Calls, Has.Count.EqualTo(1), "launched without waiting for a dispatcher turn");
            Assert.That(spawner.Calls[0].Arranged, Is.False);

            Dispatcher.UIThread.RunJobs();
            Assert.That(spawner.Calls, Has.Count.EqualTo(1), "and only once");
        }
        finally { window.Close(); }
    }

    // ---- disposal and handover --------------------------------------------------------------------

    [AvaloniaTest]
    public void Disposed_before_the_spawn_returns_kills_what_it_spawned()
    {
        var (view, spawner) = NewView(holdOpen: true);
        var window = Show(view);
        try
        {
            Assert.That(view.IsLoaded, Is.False, "sanity: never loaded");
            view.Dispose();

            var connection = new SpyConnection();
            spawner.Pending[0].SetResult(connection);
            Dispatcher.UIThread.RunJobs();

            Assert.Multiple(() =>
            {
                Assert.That(connection.Killed, Is.True, "a disposed view must not leave its process running");
                Assert.That(connection.Disposed, Is.True);
                Assert.That(view.IsLive, Is.False);
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Disposed_before_Loaded_kills_the_early_process()
    {
        var (view, spawner) = NewView();
        var window = Show(view);
        try
        {
            Assert.That(view.IsLoaded, Is.False, "sanity: never loaded");
            view.Dispose();

            Assert.That(spawner.Spawned.Single().Killed, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void An_attach_during_the_spawn_wins_and_the_early_process_is_killed()
    {
        var (view, spawner) = NewView(holdOpen: true);
        var window = Show(view);
        try
        {
            var attached = new SpyConnection();
            view.AttachConnection(attached);

            var early = new SpyConnection();
            spawner.Pending[0].SetResult(early);
            Dispatcher.UIThread.RunJobs();

            Assert.Multiple(() =>
            {
                Assert.That(early.Killed, Is.True, "the superseded spawn is not left running");
                Assert.That(attached.Killed, Is.False, "the attached connection is untouched");
                Assert.That(view.DetachConnection(), Is.SameAs(attached), "and it is still the live one");
                Assert.That(spawner.Calls, Has.Count.EqualTo(1), "and Loaded did not launch anything");
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_connection_attached_before_initialisation_means_no_spawn()
    {
        var (view, spawner) = NewView();
        var attached = new SpyConnection();
        view.AttachConnection(attached);

        var window = Show(view);
        try
        {
            Dispatcher.UIThread.RunJobs();

            Assert.That(spawner.Calls, Is.Empty);
            Assert.That(view.DetachConnection(), Is.SameAs(attached));
        }
        finally { window.Close(); }
    }

    // ---- over a real pty --------------------------------------------------------------------------

    /// <summary>
    /// The one test over a real pty: a shell spawned early ends up with the laid-out size, not the
/// provisional 80x24 it was started at.
    /// </summary>
    /// <remarks>
    /// Asked with <c>stty size</c> once the test says so, rather than from a WINCH trap. The resize lands
    /// microseconds after the fork, usually before the shell has run far enough to install a trap, so a
    /// trap-based check would be a race; the kernel raises SIGWINCH on every TIOCSWINSZ that changes the
    /// size, so the size the tty reports is the evidence that matters.
    /// </remarks>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "uses sh and stty")]
    public async Task A_real_shell_spawned_early_ends_up_with_the_laid_out_size()
    {
        var view = new TerminalView
        {
            Process = "sh",
            ProcessArgs = new List<string> { "-c", "echo armed; read x; stty size; exec cat" },
            LaunchBeforeLoaded = true,
        };
        var window = Show(view);
        try
        {
            var expected = $"{view.Terminal.Rows} {view.Terminal.Cols}";
            Assert.That(expected, Is.Not.EqualTo("24 80"), "sanity: the layout changed the size");

            await WaitUntil(() => BufferText(view).Contains("armed"), "the shell started");
            await view.SendInputAsync("\r");

            await WaitUntil(() => BufferText(view).Contains(expected), $"the shell's tty reported '{expected}'");
        }
        finally
        {
            window.Close();
            view.Dispose();
        }
    }

    private static string BufferText(TerminalView view)
    {
        var sb = new StringBuilder();
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
}
