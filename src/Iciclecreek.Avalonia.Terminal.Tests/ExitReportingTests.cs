using Porta.Pty;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The exit paths, tested deterministically rather than by racing a real shell.
///
/// <para>These are what <see cref="TerminalView.AttachConnection"/> makes possible. Each hands the view a
/// connection that models the exact window under test — a child that has exited but not been reaped, one that
/// will not reap at all, one whose reader is still parked when a relaunch replaces it — so the assertion is
/// about behaviour rather than about whether the scheduler happened to cooperate. The integration test
/// alongside them needs 48 concurrent spawns before it can catch the same bug even once.</para>
/// </summary>
[TestFixture]
public class ExitReportingTests
{
    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    // [AvaloniaTest] already runs the body on the headless UI thread, so this just unwraps the lambda the
    // bodies below are written as.
    private static Task RunAsync(Func<Task> body) => body();

    /// <summary>Host the view in a real window and lay it out, so the visual tree exists.</summary>
    private static Window Show(Control content)
    {
        var window = new Window { Width = 520, Height = 900, Content = content };
        window.Show();
        Pump(window);
        return window;
    }

    /// <summary>Force a layout + render pass so freshly-raised changes are reflected.</summary>
    private static void Pump(Window window)
    {
        window.UpdateLayout();
        global::Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    /// <summary>Wait for a real signal rather than sleeping a guessed interval.</summary>
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

    // ── The deterministic guard ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A connection whose child has EXITED but not yet been REAPED — which is precisely the window
    /// the bug lived in. Its reader stream is already at EOF, its ProcessExited never fires (so the
    /// EOF path in the view is forced to be the one that reports), and its ExitCode is the default
    /// 0 until <see cref="WaitForExit"/> is called, exactly as a real connection behaves.
    ///
    /// <para>Racing a real shell only reproduces this half the time. Modelling the window directly
    /// turns "we got unlucky enough to see it" into a guard that cannot pass against the bug.</para>
    /// </summary>
    private sealed class ExitedButNotYetReaped : IPtyConnection
    {
        private readonly int _realExitCode;
        private readonly bool _everReaps;
        private readonly int _reapsOnCall;
        private int _waitCalls;
        private bool _reaped;

        /// <param name="reapsOnCall">
        /// Which <see cref="WaitForExit"/> call finally succeeds, 1-based. 1 is the ordinary case —
        /// the child is already dead and reaps immediately. A HIGHER value models the pathological
        /// one this class exists for: the read loop's grace period expires without a reap, and the
        /// child reaps a moment later. That is not hypothetical — it is what a CI box under enough
        /// load does, and it is the case that used to end in no exit event at all.
        /// </param>
        public ExitedButNotYetReaped(int realExitCode, bool everReaps = true, int reapsOnCall = 1)
        {
            _realExitCode = realExitCode;
            _everReaps = everReaps;
            _reapsOnCall = reapsOnCall;
        }


        public bool WasWaitedOn { get; private set; }

        /// <summary>0 until reaped — the whole point. Reading this too early is the defect.</summary>
        public int ExitCode => _reaped ? _realExitCode : 0;

        public bool WaitForExit(int milliseconds)
        {
            WasWaitedOn = true;
            _waitCalls++;
            _reaped = _everReaps && _waitCalls >= _reapsOnCall;
            return _reaped;
        }

        // An empty stream reads 0 bytes immediately, which is EOF.
        public Stream ReaderStream { get; } = new MemoryStream(Array.Empty<byte>());
        public Stream WriterStream { get; } = new MemoryStream();

        public int Pid => -1;
        public void Kill() { }
        public void Resize(int columns, int rows) { }
        public void Dispose() { }

        /// <summary>Never raised: EOF alone has to drive these cases, which is the point.</summary>
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
    }

    // ── A read that fails ───────────────────────────────────────────────────────────────────────

    /// <summary>Everything the emulator has, rows run together so a line that wrapped still reads whole.</summary>
    private static string BufferText(TerminalView view)
    {
        var text = new System.Text.StringBuilder();
        for (var y = 0; y < view.Terminal.Buffer.Length; y++)
        {
            var line = view.Terminal.Buffer.GetLine(y);
            if (line == null) continue;
            for (var x = 0; x < line.Length; x++)
                text.Append(string.IsNullOrEmpty(line[x].Content) ? " " : line[x].Content);
        }
        return text.ToString();
    }

    /// <summary>
    /// EIO on the master is the ordinary end of a process on Linux, not an exceptional one. With no event
    /// from the pty layer the read loop is the only thing left that can report it, and it has to report
    /// everything the EOF path would: a host told nothing cannot leave the state it entered when the
    /// process started.
    /// </summary>
    [AvaloniaTest]
    public Task A_read_that_fails_because_the_child_is_gone_reports_the_exit() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var exited = new TaskCompletionSource<ProcessExitedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.ProcessExited += (_, e) => exited.TrySetResult(e);

        var connection = new ReadFailureConnection();
        view.AttachConnection(connection);
        var session = view.SessionId;

        connection.ChildExits(3);
        connection.Fail(new IOException("Input/output error"));

        var done = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.That(done, Is.SameAs(exited.Task), "EIO is how the platform ended this process; silence strands the host");

        var report = exited.Task.Result;
        Assert.Multiple(() =>
        {
            Assert.That(report.ExitCode, Is.EqualTo(3), "the real code, read after the reap");
            Assert.That(report.SessionId, Is.EqualTo(session));
            Assert.That(view.IsLive, Is.False, "the view stops claiming a live process once the exit is reported");
        });

        await WaitUntil(() => BufferText(view).Contains("Process exited with code: 3"), "the exit notice was written");
        Assert.That(BufferText(view), Does.Not.Contain("Error reading from process"),
            "this is the end of a process, not a failure");

        window.Close();
    });

    /// <summary>
    /// A genuine I/O error on a running child prints the error, and "we could not read" must not become
    /// "you are never told": the exit is still reported when the child does die, though the read loop has
    /// long since ended.
    /// </summary>
    [AvaloniaTest]
    public Task A_read_error_on_a_live_child_prints_and_still_reports_the_later_exit() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

        var connection = new ReadFailureConnection();
        view.AttachConnection(connection);
        connection.Fail(new IOException("Input/output error"));

        await WaitUntil(() => BufferText(view).Contains("Error reading from process: Input/output error"),
            "the error was printed");
        Assert.Multiple(() =>
        {
            Assert.That(view.IsLive, Is.True, "the child is still running");
            Assert.That(exited.Task.IsCompleted, Is.False, "nothing has ended yet");
        });

        connection.ChildExits(7);

        var done = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.That(done, Is.SameAs(exited.Task), "the exit must be handed off, not abandoned with the error message");
        Assert.Multiple(() =>
        {
            Assert.That(exited.Task.Result, Is.EqualTo(7));
            Assert.That(view.IsLive, Is.False);
        });

        window.Close();
    });

    /// <summary>
    /// Establishing whether the child is gone takes up to the reap grace, and a relaunch can land in it.
    /// The stream closing underneath is how the loop got here, so what it would say about the old process
    /// must not reach the terminal that now belongs to the new one.
    /// </summary>
    [AvaloniaTest]
    public Task A_read_failure_says_nothing_once_a_successor_arrived_during_the_reap_wait() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var exits = 0;
        view.ProcessExited += (_, _) => Interlocked.Increment(ref exits);

        var stale = new ReadFailureConnection();
        view.AttachConnection(stale);
        stale.Fail(new IOException("Input/output error"));
        Assert.That(stale.AwaitWaitForExit(TimeSpan.FromSeconds(5)), Is.True, "sanity: the loop reached the reap wait");

        var successor = new RecordingConnection();
        view.AttachConnection(successor);
        var session = view.SessionId;

        // Past the grace, so the wait has answered "not gone" and the loop has decided what to do about it.
        await Task.Delay(1200);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        stale.ChildExits(9);
        await Task.Delay(300);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(BufferText(view), Does.Not.Contain("Error reading from process"));
            Assert.That(Volatile.Read(ref exits), Is.EqualTo(0), "the old process must not speak for its successor");
            Assert.That(view.IsLive, Is.True);
            Assert.That(view.SessionId, Is.EqualTo(session));
        });

        window.Close();
    });

    /// <summary>
    /// The pty layer's own event and the read failure are two reports of one exit, and on a real Unix pty
    /// they arrive together. Whichever is first, the host hears once and sees no error.
    /// </summary>
    [AvaloniaTest]
    public Task A_read_failure_after_the_exit_was_reported_is_silent() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var exits = 0;
        view.ProcessExited += (_, _) => Interlocked.Increment(ref exits);

        var connection = new ReadFailureConnection();
        view.AttachConnection(connection);

        connection.RaiseProcessExited(5);
        await WaitUntil(() => Volatile.Read(ref exits) == 1, "the event was reported");

        connection.Fail(new IOException("Input/output error"));
        await Task.Delay(300);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(Volatile.Read(ref exits), Is.EqualTo(1));
            Assert.That(BufferText(view), Does.Not.Contain("Error reading from process"));
        });

        window.Close();
    });

    /// <summary>
    /// The ceiling that ends EOF's wait is for a child EOF says is dead. A read error says no such thing,
    /// so a child outliving the ceiling is not reported as ended: that would turn IsLive false for a
    /// running process, and the claimed interlock would then lock out its real exit and code.
    /// </summary>
    [AvaloniaTest]
    public Task A_live_child_past_the_ceiling_is_not_reported_until_it_really_exits() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "", ExitReapCeilingMs = 100 };
        var window = Show(view);

        var reported = new List<ProcessExitedEventArgs>();
        view.ProcessExited += (_, e) => reported.Add(e);

        var connection = new ReadFailureConnection();
        view.AttachConnection(connection);
        var session = view.SessionId;
        connection.Fail(new IOException("Input/output error"));

        await WaitUntil(() => BufferText(view).Contains("Error reading from process"), "the error was printed");
        await Task.Delay(600);   // well past the ceiling
        await WaitUntil(() => connection.WaitCalls > 1, "it is being watched, not abandoned");

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.Empty, "the child is still running");
            Assert.That(view.IsLive, Is.True);
        });

        connection.ChildExits(7);
        await WaitUntil(() => reported.Count > 0, "the real exit was reported");
        await Task.Delay(300);   // room for a second report, if there were one

        Assert.Multiple(() =>
        {
            Assert.That(reported, Has.Count.EqualTo(1));
            Assert.That(reported[0].ExitCode, Is.EqualTo(7), "the real code");
            Assert.That(reported[0].ExitCodeKnown, Is.True);
            Assert.That(reported[0].SessionId, Is.EqualTo(session));
            Assert.That(view.IsLive, Is.False);
        });

        window.Close();
    });

    /// <summary>The pty layer's own event beats the watch to it, and the watch then stops looking.</summary>
    [AvaloniaTest]
    public Task The_pty_layers_event_during_the_watch_is_the_one_report() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var exits = 0;
        view.ProcessExited += (_, _) => Interlocked.Increment(ref exits);

        var connection = new ReadFailureConnection();
        view.AttachConnection(connection);
        connection.Fail(new IOException("Input/output error"));
        await WaitUntil(() => BufferText(view).Contains("Error reading from process"), "the error was printed");

        connection.RaiseProcessExited(5);
        await WaitUntil(() => Volatile.Read(ref exits) == 1, "the event was reported");
        await Task.Delay(600);

        Assert.That(Volatile.Read(ref exits), Is.EqualTo(1));
        await AssertNoLongerWatched(connection);

        window.Close();
    });

    private static async Task AssertNoLongerWatched(ReadFailureConnection connection)
    {
        // One poll may already be between its check and its look.
        await Task.Delay(400);
        var calls = connection.WaitCalls;
        await Task.Delay(800);
        Assert.That(connection.WaitCalls, Is.EqualTo(calls), "still polling a connection this view is done with");
    }

    /// <summary>
    /// A detached connection is handed back alive and undisposed, so the interlock (which the detach
    /// resets) and a throwing WaitForExit would never stop the watch. With no ceiling either, it would
    /// poll for as long as the child lived.
    /// </summary>
    [AvaloniaTest]
    public Task A_detach_ends_the_watch_and_the_exit_is_not_reported_here() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var exits = 0;
        view.ProcessExited += (_, _) => Interlocked.Increment(ref exits);

        var connection = new ReadFailureConnection();
        view.AttachConnection(connection);
        connection.Fail(new IOException("Input/output error"));
        await WaitUntil(() => connection.WaitCalls > 1, "the watch is polling");

        Assert.That(view.DetachConnection(), Is.SameAs(connection));
        await AssertNoLongerWatched(connection);

        connection.ChildExits(9);
        await Task.Delay(400);

        Assert.That(Volatile.Read(ref exits), Is.EqualTo(0), "this view gave the process away");

        window.Close();
    });

    /// <summary>The same, for a relaunch: the old process must not speak for its successor.</summary>
    [AvaloniaTest]
    public Task A_relaunch_ends_the_watch_and_the_old_exit_is_not_reported() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var exits = 0;
        view.ProcessExited += (_, _) => Interlocked.Increment(ref exits);

        var stale = new ReadFailureConnection();
        view.AttachConnection(stale);
        stale.Fail(new IOException("Input/output error"));
        await WaitUntil(() => stale.WaitCalls > 1, "the watch is polling");

        view.AttachConnection(new RecordingConnection());
        await AssertNoLongerWatched(stale);

        stale.ChildExits(9);
        await Task.Delay(400);

        Assert.Multiple(() =>
        {
            Assert.That(Volatile.Read(ref exits), Is.EqualTo(0));
            Assert.That(view.IsLive, Is.True, "the successor is running");
        });

        window.Close();
    });

    /// <summary>The next view to attach a handed-back connection is the one that hears the exit.</summary>
    [AvaloniaTest]
    public Task After_a_detach_the_next_owner_hears_the_exit() => RunAsync(async () =>
    {
        var first = new TerminalView { Process = "" };
        var firstWindow = Show(first);
        var second = new TerminalView { Process = "" };
        var secondWindow = Show(second);

        var firstExits = 0;
        first.ProcessExited += (_, _) => Interlocked.Increment(ref firstExits);
        var heard = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        second.ProcessExited += (_, e) => heard.TrySetResult(e.ExitCode);

        var connection = new ReadFailureConnection();
        first.AttachConnection(connection);
        connection.Fail(new IOException("Input/output error"));
        await WaitUntil(() => connection.WaitCalls > 1, "the first view's watch is polling");

        first.DetachConnection();
        second.AttachConnection(connection);
        connection.ChildExits(4);

        var done = await Task.WhenAny(heard.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.That(done, Is.SameAs(heard.Task));
        Assert.Multiple(() =>
        {
            Assert.That(heard.Task.Result, Is.EqualTo(4));
            Assert.That(Volatile.Read(ref firstExits), Is.EqualTo(0));
        });

        firstWindow.Close();
        secondWindow.Close();
    });

    /// <summary>
    /// The watch can outlast the view's interest in the process by hours, so it must not hold the emulator
    /// and the scrollback for that long once the view has let the connection go.
    /// </summary>
    [AvaloniaTest]
    public void A_detached_view_is_not_kept_alive_by_the_watch()
    {
        var connection = new ReadFailureConnection();
        var view = WatchThenDetach(connection);

        for (var i = 0; i < 5 && view.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.That(view.IsAlive, Is.False, "the watch is keeping the detached view alive");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference WatchThenDetach(ReadFailureConnection connection)
    {
        // Never shown, so no window can be what is holding it. That also means no emulator to write the
        // error line to, so the line is taken as handled.
        var view = new TerminalView { Process = "" };
        view.WritingMessage += (_, e) => e.Handled = true;

        view.AttachConnection(connection);
        connection.Fail(new IOException("Input/output error"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (connection.WaitCalls < 2 && DateTime.UtcNow < deadline)
        {
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(connection.WaitCalls, Is.GreaterThan(1), "sanity: the watch is polling, which is the case under test");

        view.DetachConnection();
        return new WeakReference(view);
    }

    /// <summary>
    /// EOF does say the child is dead, so there the ceiling stands: a child that never reaps still ends in a
    /// report, with the code unknown rather than the 0 that would read as success.
    /// </summary>
    [AvaloniaTest]
    public Task At_EOF_the_ceiling_reports_an_exit_with_an_unknown_code() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "", ExitReapCeilingMs = 200 };
        var window = Show(view);

        var exited = new TaskCompletionSource<ProcessExitedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.ProcessExited += (_, e) => exited.TrySetResult(e);

        view.AttachConnection(new ExitedButNotYetReaped(realExitCode: 3, everReaps: false));
        var session = view.SessionId;

        var done = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.That(done, Is.SameAs(exited.Task), "a host told nothing cannot leave the state it entered");

        Assert.Multiple(() =>
        {
            Assert.That(exited.Task.Result.ExitCodeKnown, Is.False);
            Assert.That(exited.Task.Result.SessionId, Is.EqualTo(session));
            Assert.That(view.IsLive, Is.False);
        });

        window.Close();
    });

    /// <summary>
    /// The contract, stated without a race: when the read loop sees EOF it must report what the
    /// process ACTUALLY returned, which means not reading the exit code until the child has been
    /// reaped. Before the fix this reported 0 for a process that returned 3, every time.
    /// </summary>
    [AvaloniaTest]
    public Task An_exit_seen_only_as_EOF_still_reports_the_real_code() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        Pump(window);

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

        var connection = new ExitedButNotYetReaped(realExitCode: 3);
        view.AttachConnection(connection);

        var done = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.That(done, Is.SameAs(exited.Task), "EOF alone has to be enough to report an exit");
        Assert.That(exited.Task.Result, Is.EqualTo(3), "reading ExitCode before the child is reaped reports 0 for a process that failed");
        Assert.That(connection.WasWaitedOn, Is.True, "the reap is what makes the code readable");

        window.Close();
    });

    /// <summary>
    /// A child that will not reap inside the grace period leaves no trustworthy exit code — and
    /// the one that would be read is 0, the single wrong answer that reads as SUCCESS. Rather than
    /// invent an outcome, the EOF path leaves the exit interlock unclaimed, so the real event can
    /// still report if it ever arrives.
    ///
    /// <para>This is the pathological branch; the ordinary one reaps immediately. It is covered
    /// because the alternative — claiming on a failed reap — silently reasserts the very bug this
    /// change exists to fix, and does so in the case nobody would think to try by hand.</para>
    ///
    /// <para>The deferral here is PERMANENT, which is what this test asserts: while the child has not
    /// been reaped, the read loop stays silent and leaves the exit to the authoritative event.</para>
    ///
    /// <para>That is deliberate for this change and not the end state. A child that never reaps then
    /// produces no <c>ProcessExited</c> at all, so a host is never told the process ended and cannot
    /// leave the state it entered when it started — observed for real downstream, with a terminal pane
    /// stuck showing a finished shell as running. Bounding the wait needs a way to say "it ended, the
    /// code is unknown", which is API this branch does not add; it belongs in its own change.</para>
    /// </summary>
    [AvaloniaTest]
    public Task A_child_that_will_not_reap_defers_to_the_real_event() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        Pump(window);

        var reported = new List<int>();
        view.ProcessExited += (_, e) => reported.Add(e.ExitCode);

        var connection = new ExitedButNotYetReaped(realExitCode: 3, everReaps: false);
        view.AttachConnection(connection);

        // EOF has been seen and the reap refused. Nothing may be reported off the back of that.
        await WaitUntil(() => connection.WasWaitedOn, "the EOF path tried to reap");
        await Task.Delay(200);
        Assert.That(reported, Is.Empty, "0 would be invented, and 0 is the answer that reads as success");

        // …and the interlock is still free, which is what leaves the authoritative event able to
        // speak. IsLive is exactly that flag (_ptyConnection != null && _processExitHandled == 0),
        // so it is the observable form of "nothing has claimed the exit yet" — asserted through the
        // public surface rather than by synthesising a PtyExitedEventArgs, whose constructor the
        // PTY library does not expose.
        Assert.That(view.IsLive, Is.True, "a failed reap must not claim the exit and lock the real event out");

        window.Close();
    });

    /// <summary>
    /// A child that misses the read loop's grace period but reaps a moment later must still be
    /// reported, with its real code.
    ///
    /// <para>This is the guard for the wedge. The EOF path used to give up when the grace period
    /// expired: the interlock stayed unclaimed, and if the PTY layer's own event never fired either
    /// — which this fake models, and which the layer genuinely does — then NO ProcessExited was
    /// raised at all. The trade recorded at the time was "no wrong exit code beats a wrong one",
    /// but the cost was not the number, it was the notification. Avalloy's TerminalWell stayed in
    /// TerminalPhase.Live forever and every test waiting for it to settle timed out at 20s. It
    /// surfaced as a load-sensitive CI flake, because a dead child only misses a 1000ms reap when
    /// the box is heavily contended.</para>
    ///
    /// <para>Modelled rather than raced, for the same reason as the test above: racing a real shell
    /// reproduces this only under load, and a guard that needs luck is not a guard.</para>
    /// </summary>
    [AvaloniaTest]
    public Task A_child_that_reaps_late_is_still_reported() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        Pump(window);

        var reported = new List<ProcessExitedEventArgs>();
        view.ProcessExited += (_, e) => reported.Add(e);

        // Misses the read loop's grace period, reaps on a later attempt — exactly a loaded box.
        var connection = new ExitedButNotYetReaped(realExitCode: 3, reapsOnCall: 3);
        view.AttachConnection(connection);

        await WaitUntil(() => reported.Count > 0,
            "the exit is reported even though the reap missed the read loop's grace period");

        Assert.That(reported, Has.Count.EqualTo(1), "one exit, reported once");
        Assert.That(reported[0].ExitCodeKnown, Is.True, "the child did reap, so the code is trustworthy");
        Assert.That(reported[0].ExitCode, Is.EqualTo(3),
            "a late reap still yields the REAL code — giving up was what lost it");

        window.Close();
    });

    // ── The relaunch race ───────────────────────────────────────────────────────────────────────


    /// <summary>
    /// <see cref="TerminalView.DetachConnection"/> gives the implicit detach a name: it hands the
    /// connection back, leaves the process running, and leaves the view with nothing attached.
    ///
    /// <para>The assertions are the same three the attached-connection guard makes, which is the point —
    /// the named operation and the side effect of cleanup must agree, or having both is worse than
    /// having one.</para>
    /// </summary>
    [AvaloniaTest]
    public Task DetachConnection_hands_the_connection_back_alive() => RunAsync(async () =>
    {
        var view = new TerminalView();
        var window = Show(view);
        Pump(window);

        var attached = new ParkedUntilReleased(realExitCode: 0);
        view.AttachConnection(attached);
        Assert.That(view.IsLive, Is.True, "the view was just handed a live connection");

        var returned = view.DetachConnection();

        Assert.That(returned, Is.SameAs(attached), "the caller gets back exactly what it handed over");
        Assert.That(attached.Disposed, Is.False,
            "detaching must not dispose — disposing a pty ends the child, which is the opposite of detaching");
        Assert.That(view.IsLive, Is.False, "the view is following nothing now");
        Assert.That(view.DetachConnection(), Is.Null, "nothing left to detach");

        attached.Release();
        await Task.Yield();
        window.Close();
    });

    /// <summary>
    /// A connection whose reader BLOCKS until it is released, then returns EOF — which is what a real one does
    /// when a relaunch disposes it out from under a parked read. On Unix the reader wraps a synchronous
    /// FileStream, so cancellation does not reliably interrupt it: the read returns, and whichever loop was
    /// sitting in it wakes up holding a connection that may no longer be the live one.
    /// </summary>

    /// <summary>
    /// An attached connection must survive the view: not killed, and NOT DISPOSED.
    ///
    /// <para>Disposing is not a neutral detach — it ends the child. Measured on both platforms, with no
    /// <c>Kill()</c> anywhere: the process is gone within 300ms on Windows (<c>PseudoConsoleConnection</c>)
    /// and on Unix, where closing the master fd sends <c>SIGHUP</c> to the foreground process group. So a
    /// host that closes a pane or re-parents a view would lose the process it owns — the exact thing
    /// <see cref="TerminalView.AttachConnection"/> exists to make safe.</para>
    ///
    /// <para>This assertion is why the fake records disposal at all. A fake whose <c>Dispose</c> is a no-op
    /// satisfies the contract no matter what the view does, which is how the earlier revision of this branch
    /// passed its tests while disposing every attached connection.</para>
    /// </summary>
    [Test]
    public void An_attached_connection_is_neither_killed_nor_disposed()
    {
        var view = new TerminalView();
        var attached = new ParkedUntilReleased(realExitCode: 0);

        view.AttachConnection(attached);
        Assert.That(view.IsLive, Is.True, "the view was just handed a live connection");

        // Replacing it is the detach path a pane close or re-parent takes.
        view.AttachConnection(new ParkedUntilReleased(realExitCode: 0));

        Assert.That(attached.Disposed, Is.False,
            "the view disposed a connection it does not own; disposing ends the child, so a host would lose "
            + "the process behind a pane it merely closed");

        attached.Release();
    }

    private sealed class ParkedUntilReleased : IPtyConnection
    {
        private readonly ManualResetEventSlim _release = new(false);

        public ParkedUntilReleased(int realExitCode) => ExitCode = realExitCode;

        /// <summary>Let the parked read return EOF, as a disposed stream would.</summary>
        public void Release() => _release.Set();

        public int ExitCode { get; }

        public bool WaitForExit(int milliseconds) => true;   // already dead by the time anyone asks

        public Stream ReaderStream => field ??= new BlockingEofStream(_release);
        public Stream WriterStream { get; } = new MemoryStream();

        public int Pid => -1;
        public void Kill() { }
        public void Resize(int columns, int rows) { }
        /// <summary>Whether the view disposed this connection. It must not, for an attached one.</summary>
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            _release.Set();
        }

        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }

        private sealed class BlockingEofStream(ManualResetEventSlim release) : Stream
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                release.Wait();
                return 0;   // EOF, exactly as a closed pty master reports
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }

    /// <summary>
    /// A read loop whose connection was replaced while it was parked must NOT report an exit — not for itself,
    /// and above all not against its successor.
    ///
    /// <para>The window is narrow but entirely reachable, and it is the one Copilot flagged on the upstream PR.
    /// The loop's ownership test is its <c>while</c> condition, evaluated BEFORE the blocking read. Attaching a
    /// new connection swaps <c>_ptyConnection</c> and arms a fresh interlock; when the old stream then reports
    /// EOF the stale loop walks into the exit path, and with a bare <c>Interlocked.Exchange</c> its claim
    /// SUCCEEDS — because the flag it finds was reset for the new process. The visible result is a
    /// freshly-started terminal that immediately prints the previous process's exit and reports itself dead.</para>
    /// </summary>
    [AvaloniaTest]
    public Task A_Stale_Read_Loop_Cannot_Report_An_Exit_Against_Its_Successor() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var reported = new List<int>();
        view.ProcessExited += (_, e) => reported.Add(e.ExitCode);

        // First connection: its reader is parked, so its loop is sitting in the read.
        var first = new ParkedUntilReleased(realExitCode: 3);
        view.AttachConnection(first);
        await Task.Delay(150);

        // The relaunch. This arms a fresh interlock for `second`.
        var second = new ParkedUntilReleased(realExitCode: 0);
        view.AttachConnection(second);
        await Task.Delay(50);

        // Now let the FIRST connection's parked read return EOF. Its loop wakes holding a connection the view
        // no longer owns. This line is what frees it: an attached connection is not disposed on replacement,
        // so nothing else has set the gate. (It was not always load-bearing — while the view still disposed
        // attached connections, the replacement above freed the read and this call only looked like it did.)
        first.Release();
        await Task.Delay(400);

        Assert.That(reported, Is.Empty, "the stale loop's connection is not the live one, so it has no exit to report — and reporting one "
            + "would both print the wrong process's code and mark the NEW connection as already exited");
        Assert.That(view.IsLive, Is.True, "the terminal was just handed a live connection; a stale loop must not be able to kill it");

        second.Release();
        Pump(window);
    });
}
