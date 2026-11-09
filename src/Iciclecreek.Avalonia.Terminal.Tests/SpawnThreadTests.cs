using System.Collections.Concurrent;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;
using Porta.Pty;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The spawn itself -- <see cref="PtyProvider.SpawnAsync"/>, which on every platform does all its work before
/// returning a completed task -- runs on the thread pool, in both launch modes, and nothing the view did
/// around it moves.
/// </summary>
/// <remarks>
/// <para>These go through the seam BELOW the thread-pool hop (<c>PtySpawnOverride</c>), so the launch takes
/// the same path a real one does. <see cref="LaunchBeforeLoadedTests"/> replaces the whole spawn step to hold
/// it open, and so never sees the hop.</para>
/// <para>Each early-launch test blocks the UI thread while it waits for the spawn. That is the assertion,
/// not a shortcut: Loaded is posted and the test is not pumping, so the view cannot load meanwhile, and a
/// spawn that needed the UI thread would never arrive.</para>
/// </remarks>
[TestFixture]
public class SpawnThreadTests
{
    private sealed class Connection : IPtyConnection
    {
        public PushStream Reader { get; } = new();
        public ConcurrentQueue<(int Cols, int Rows)> Resizes { get; } = new();

        public Stream ReaderStream => Reader;
        public Stream WriterStream { get; } = new MemoryStream();
        public int ExitCode => 0;
        public bool WaitForExit(int milliseconds) => false;
        public int Pid => -1;
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public void Kill() { Killed = true; Reader.Done(); }
        public void Resize(int columns, int rows) => Resizes.Enqueue((columns, rows));
        public void Dispose() { Disposed = true; Reader.Done(); }
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
    }

    /// <summary>What was true where and when the pty layer was called.</summary>
    private sealed class Spawn
    {
        public readonly ManualResetEventSlim Called = new();
        public readonly Connection Connection = new();
        public bool OnUiThread;
        public bool Loaded;
        public PtyOptions? Options;
    }

    private static (TerminalView view, Spawn spawn) NewView(bool launchBeforeLoaded, Action<Connection>? beforeReturning = null)
    {
        var view = new TerminalView { Process = "sh", LaunchBeforeLoaded = launchBeforeLoaded };
        var spawn = new Spawn();
        view.PtySpawnOverride = (options, _) =>
        {
            spawn.OnUiThread = Dispatcher.UIThread.CheckAccess();
            spawn.Loaded = view.IsLoaded;
            spawn.Options = options;
            beforeReturning?.Invoke(spawn.Connection);
            spawn.Called.Set();
            return Task.FromResult<IPtyConnection>(spawn.Connection);
        };
        return (view, spawn);
    }

    /// <summary>First layout only; Loaded is posted, so it has not fired when this returns.</summary>
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

    [AvaloniaTest]
    public void Opted_in_the_spawn_runs_off_the_UI_thread_before_Loaded()
    {
        var (view, spawn) = NewView(launchBeforeLoaded: true);
        var window = Show(view);
        try
        {
            Assert.That(spawn.Called.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "the spawn ran while the UI thread was blocked here, so it did not need it");
            Assert.Multiple(() =>
            {
                Assert.That(spawn.OnUiThread, Is.False, "the pty layer was called on the thread pool");
                Assert.That(spawn.Loaded, Is.False, "and before Loaded");
            });
        }
        finally
        {
            window.Close();
            view.Dispose();
        }
    }

    [AvaloniaTest]
    public async Task Default_the_spawn_runs_off_the_UI_thread_after_Loaded()
    {
        var (view, spawn) = NewView(launchBeforeLoaded: false);
        var window = Show(view);
        try
        {
            Assert.That(spawn.Called.IsSet, Is.False, "nothing spawns during layout unless asked to");

            await WaitUntil(() => spawn.Called.IsSet, "Loaded launched the process");
            Assert.Multiple(() =>
            {
                Assert.That(spawn.OnUiThread, Is.False, "the pty layer was called on the thread pool");
                Assert.That(spawn.Loaded, Is.True, "from Loaded, as before");
            });
            await WaitUntil(() => view.IsLive, "the connection was installed");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// With the spawn on the pool the first layout always lands before the connection is installed, so the
    /// resize to the laid-out size is the launch catching the process up, never ArrangeOverride.
    /// </summary>
    [AvaloniaTest]
    public async Task The_provisional_size_is_followed_by_the_laid_out_size()
    {
        var (view, spawn) = NewView(launchBeforeLoaded: true);
        var window = Show(view);
        try
        {
            Assert.That(spawn.Called.Wait(TimeSpan.FromSeconds(5)), Is.True, "sanity: the spawn ran");
            var laidOut = (view.Terminal.Cols, view.Terminal.Rows);
            Assert.That((spawn.Options!.Cols, spawn.Options.Rows), Is.EqualTo((80, 24)), "spawned at the provisional size");
            Assert.That(laidOut, Is.Not.EqualTo((80, 24)), "sanity: the layout changed the size");

            await WaitUntil(() => view.IsLive, "the connection was installed");
            Assert.That(spawn.Connection.Resizes, Does.Contain(laidOut), "then told the laid-out size");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A host's own <c>await LaunchProcess()</c> made before Loaded. The spawn now yields, so Loaded lands in
    /// the middle of it with no connection to see; it must leave the launch alone rather than start a second
    /// one that supersedes it -- which would kill the first process and return the caller to a view with none.
    /// </summary>
    [AvaloniaTest]
    public async Task Loaded_during_an_explicit_launch_does_not_spawn_again()
    {
        var view = new TerminalView { Process = "" };
        var gate = new ManualResetEventSlim();
        var calls = 0;
        var connection = new Connection();
        view.PtySpawnOverride = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            gate.Wait(TimeSpan.FromSeconds(5));
            return Task.FromResult<IPtyConnection>(connection);
        };
        var window = Show(view);
        try
        {
            Assert.That(view.IsLoaded, Is.False, "sanity: not loaded yet");
            var launch = view.LaunchProcess(null, "sh");

            await WaitUntil(() => view.IsLoaded, "Loaded fired while the spawn was held");
            Dispatcher.UIThread.RunJobs();
            gate.Set();
            await launch;

            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(1), "Loaded left the in-flight launch alone");
                Assert.That(view.IsLive, Is.True, "and the caller's await ended with the connection installed");
                Assert.That(view.Pid, Is.EqualTo(connection.Pid));
            });
        }
        finally
        {
            gate.Set();
            window.Close();
        }
    }

    /// <summary>
    /// <c>DetachConnection()</c> while the spawn is on the pool finds no connection to hand back. It must
    /// still retire the launch, or the late result installs a process in a view the host was just told has
    /// nothing attached.
    /// </summary>
    [AvaloniaTest]
    public async Task DetachConnection_during_the_spawn_kills_the_late_process()
    {
        var view = new TerminalView { Process = "" };
        var entered = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        var connection = new Connection();
        view.PtySpawnOverride = (_, _) =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(5));
            return Task.FromResult<IPtyConnection>(connection);
        };
        var window = Show(view);
        try
        {
            await WaitUntil(() => view.IsLoaded, "loaded, so only the explicit launch can spawn");
            var launch = view.LaunchProcess(null, "sh");

            // Detach only once the spawn is really running. Earlier, the pool has not started it, and the
            // cancelled launch never spawns at all -- also right, but not the late result under test.
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "sanity: the spawn is under way");
            Assert.That(view.DetachConnection(), Is.Null, "nothing attached yet");
            gate.Set();
            await launch;
            Dispatcher.UIThread.RunJobs();

            Assert.Multiple(() =>
            {
                Assert.That(view.IsLive, Is.False, "the detached view stays empty");
                Assert.That(connection.Killed, Is.True, "the late process is not left running");
                Assert.That(connection.Disposed, Is.True);
            });
        }
        finally
        {
            gate.Set();
            window.Close();
        }
    }

    [AvaloniaTest]
    public Task Opted_in_output_written_before_the_view_is_ready_is_not_lost() => AssertNoOutputLost(launchBeforeLoaded: true);

    [AvaloniaTest]
    public Task Default_output_written_before_the_view_is_ready_is_not_lost() => AssertNoOutputLost(launchBeforeLoaded: false);

    /// <summary>
    /// The child writes before the spawn has even returned to the view -- before the connection is installed,
    /// the reader started, Loaded, and for an early launch before layout -- and every byte still arrives, in
    /// order, ahead of what it writes later.
    /// </summary>
    private static async Task AssertNoOutputLost(bool launchBeforeLoaded)
    {
        var (view, spawn) = NewView(launchBeforeLoaded, c =>
        {
            c.Reader.Push("banner-");
            c.Reader.Push("one ");
        });
        var window = Show(view);
        try
        {
            await WaitUntil(() => view.IsLive, "the connection was installed");
            spawn.Connection.Reader.Push("prompt-two");

            await WaitUntil(() => BufferText(view).Contains("prompt-two"), "the later output arrived");
            Assert.That(BufferText(view), Does.Contain("banner-one prompt-two"), "the early output came first, whole");
        }
        finally { window.Close(); }
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
