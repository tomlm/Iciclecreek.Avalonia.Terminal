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
    /// <para>NOTE the deferral is BOUNDED now, not permanent. Leaving it permanent meant a child
    /// that never reaped produced no ProcessExited at all, and a host that is never told the
    /// process ended cannot leave the state it entered when it started — Avalloy's TerminalWell sat
    /// in Live forever. The exit is reported once the ceiling expires, with ExitCodeKnown false.
    /// This test asserts the first 200ms, which is the part that must not change: the authoritative
    /// event still gets its window.</para>
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

    // ── The relaunch race ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A connection whose reader BLOCKS until it is released, then returns EOF — which is what a real one does
    /// when a relaunch disposes it out from under a parked read. On Unix the reader wraps a synchronous
    /// FileStream, so cancellation does not reliably interrupt it: the read returns, and whichever loop was
    /// sitting in it wakes up holding a connection that may no longer be the live one.
    /// </summary>
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
        public void Dispose() => _release.Set();
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
        // no longer owns.
        first.Release();
        await Task.Delay(400);

        Assert.That(reported, Is.Empty, "the stale loop's connection is not the live one, so it has no exit to report — and reporting one "
            + "would both print the wrong process's code and mark the NEW connection as already exited");
        Assert.That(view.IsLive, Is.True, "the terminal was just handed a live connection; a stale loop must not be able to kill it");

        second.Release();
        Pump(window);
    });
}
