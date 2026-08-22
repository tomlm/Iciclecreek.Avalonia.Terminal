using System.Text;
using Porta.Pty;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// <see cref="TerminalView.OutputReceived"/> — the hook that lets a host sniff process output without
/// reading the terminal buffer back.
///
/// <para>Driven through <see cref="TerminalView.AttachConnection"/> with a scripted connection rather than
/// by running a real shell, so "the event fires with exactly this text" is an assertion rather than a hope
/// about how a shell happened to chunk its output.</para>
/// </summary>
[TestFixture]
public class OutputReceivedTests
{
    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static Window Show(Control content)
    {
        var window = new Window { Width = 520, Height = 900, Content = content };
        window.Show();
        window.UpdateLayout();
        global::Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return window;
    }

    private static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out after {timeoutMs}ms waiting until {because}");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A stream that hands back one scripted chunk per read, then EOF — so a test controls the chunk
    /// BOUNDARIES, which is the thing that actually matters for an event that fires per chunk. A
    /// MemoryStream would coalesce the whole script into a single read and prove nothing about that.
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly Queue<byte[]> _chunks;
        public ScriptedStream(IEnumerable<string> chunks)
            => _chunks = new Queue<byte[]>(chunks.Select(Encoding.UTF8.GetBytes));

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunks.Count == 0) return 0;   // EOF
            var chunk = _chunks.Dequeue();
            Array.Copy(chunk, 0, buffer, offset, chunk.Length);
            return chunk.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A connection that emits a scripted script of chunks and then exits cleanly.</summary>
    private sealed class ScriptedOutput : IPtyConnection
    {
        public ScriptedOutput(params string[] chunks) => ReaderStream = new ScriptedStream(chunks);

        public Stream ReaderStream { get; }
        public Stream WriterStream { get; } = new MemoryStream();
        public int ExitCode => 0;
        public bool WaitForExit(int milliseconds) => true;
        public int Pid => -1;
        public void Kill() { }
        public void Resize(int columns, int rows) { }
        public void Dispose() { }
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
    }

    // ── The contract ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The payload is the decoded text of one chunk, delivered per chunk rather than coalesced — which is
    /// what makes the event usable for matching on output as it arrives.
    /// </summary>
    [AvaloniaTest]
    public Task Each_chunk_reaches_a_subscriber_as_decoded_text() => Run(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var seen = new List<string>();
        view.OutputReceived += (_, e) => seen.Add(e.Output);

        view.AttachConnection(new ScriptedOutput("first", "second"));

        await WaitUntil(() => seen.Count >= 2, "both chunks were delivered");
        Assert.That(seen, Is.EqualTo(new[] { "first", "second" }), "chunks arrive whole, in order, decoded");

        window.Close();
    });

    /// <summary>
    /// UTF-8 decoded TEXT, not raw bytes — stated in the doc comment, so it is worth a guard. A multi-byte
    /// character arriving whole must come out as one character rather than as its bytes.
    /// </summary>
    [AvaloniaTest]
    public Task Output_is_decoded_as_utf8() => Run(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var seen = new List<string>();
        view.OutputReceived += (_, e) => seen.Add(e.Output);

        view.AttachConnection(new ScriptedOutput("héllo ▸ 世界"));

        await WaitUntil(() => seen.Count >= 1, "the chunk was delivered");
        Assert.That(seen[0], Is.EqualTo("héllo ▸ 世界"));

        window.Close();
    });

    /// <summary>
    /// Documented as UI-thread delivery, which is the whole reason a handler may touch UI directly. If that
    /// ever changes it is a breaking change for every subscriber, so it is pinned here.
    /// </summary>
    [AvaloniaTest]
    public Task Delivery_is_on_the_ui_thread() => Run(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        bool? onUiThread = null;
        view.OutputReceived += (_, _) => onUiThread ??= Dispatcher.UIThread.CheckAccess();

        view.AttachConnection(new ScriptedOutput("anything"));

        await WaitUntil(() => onUiThread is not null, "the event was raised");
        Assert.That(onUiThread, Is.True, "handlers are documented as safe to touch UI directly");

        window.Close();
    });

    /// <summary>
    /// A throwing subscriber must not be fatal. The invoke runs inside a dispatcher callback, so an escaping
    /// exception is an unhandled exception on the UI thread — which takes the APPLICATION down, not merely
    /// the reader. Handing output to arbitrary host code is exactly where that happens.
    ///
    /// <para>Asserted on the SECOND chunk: delivery continuing past a throw is the thing that proves nothing
    /// was torn down, and it is the assertion a single "it didn't crash" check would miss.</para>
    /// </summary>
    [AvaloniaTest]
    public Task A_throwing_subscriber_does_not_take_anything_down() => Run(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var delivered = new List<string>();
        view.OutputReceived += (_, e) =>
        {
            delivered.Add(e.Output);
            throw new InvalidOperationException("a badly behaved sniffer");
        };

        view.AttachConnection(new ScriptedOutput("one", "two"));

        await WaitUntil(() => delivered.Count >= 2, "delivery continued after the handler threw");
        Assert.That(delivered, Is.EqualTo(new[] { "one", "two" }), "throwing must not stop the read loop or the event");

        window.Close();
    });

    /// <summary>
    /// The limit of that guard, pinned so it is a known property rather than a surprise: the catch wraps the
    /// whole multicast invoke, so a handler that throws does suppress handlers registered AFTER it, for that
    /// chunk. Isolating each handler would mean a <c>GetInvocationList()</c> allocation on every chunk, which
    /// is the per-chunk cost this PR was asked to avoid elsewhere.
    /// </summary>
    [AvaloniaTest]
    public Task A_throwing_subscriber_suppresses_later_subscribers_for_that_chunk() => Run(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var first = new List<string>();
        var second = new List<string>();
        view.OutputReceived += (_, e) => { first.Add(e.Output); throw new InvalidOperationException("throws"); };
        view.OutputReceived += (_, e) => second.Add(e.Output);

        view.AttachConnection(new ScriptedOutput("one", "two"));

        await WaitUntil(() => first.Count >= 2, "the first handler saw both chunks");
        await Task.Delay(100);
        Assert.That(second, Is.Empty, "documented limit: the throw aborts the rest of the invocation list");

        window.Close();
    });

    /// <summary>
    /// Both wrappers must re-raise it. <see cref="SurfaceParityTests"/> already catches a MISSING member by
    /// reflection; this catches the subtler failure of a member that exists but was never wired to anything.
    /// </summary>
    [AvaloniaTest]
    public Task TerminalControl_forwards_the_event() => Run(async () =>
    {
        var control = new TerminalControl { Process = "" };
        var window = Show(control);

        var seen = new List<string>();
        control.OutputReceived += (_, e) => seen.Add(e.Output);

        control.AttachConnection(new ScriptedOutput("through the control"));

        await WaitUntil(() => seen.Count >= 1, "the control re-raised it");
        Assert.That(seen[0], Is.EqualTo("through the control"));

        window.Close();
    });

    // ── The read-task opt-in ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opted in, delivery happens on the read task rather than the dispatcher — which is the whole point:
    /// no coalescing and no frame of latency for a consumer matching on output as it arrives.
    /// </summary>
    [AvaloniaTest]
    public Task Opting_in_delivers_on_the_read_task() => Run(async () =>
    {
        var view = new TerminalView { Process = "", OutputReceivedOnReadTask = true };
        var window = Show(view);

        bool? onUiThread = null;
        view.OutputReceived += (_, _) => onUiThread ??= Dispatcher.UIThread.CheckAccess();

        view.AttachConnection(new ScriptedOutput("anything"));

        await WaitUntil(() => onUiThread is not null, "the event was raised");
        Assert.That(onUiThread, Is.False, "opting in means the handler runs on the reader, not the dispatcher");

        window.Close();
    });

    /// <summary>Default is off, so an existing subscriber keeps UI-thread delivery and stays safe.</summary>
    [AvaloniaTest]
    public void The_opt_in_defaults_to_off()
    {
        Assert.That(new TerminalView().OutputReceivedOnReadTask, Is.False);
    }

    /// <summary>
    /// The catch has to hold on this path too, and for a different reason than on the dispatcher path: here an
    /// escaping exception would propagate into the read loop and end it, freezing a view over a live process.
    /// </summary>
    [AvaloniaTest]
    public Task A_throwing_subscriber_does_not_kill_the_read_loop() => Run(async () =>
    {
        var view = new TerminalView { Process = "", OutputReceivedOnReadTask = true };
        var window = Show(view);

        var delivered = new List<string>();
        view.OutputReceived += (_, e) =>
        {
            delivered.Add(e.Output);
            throw new InvalidOperationException("a badly behaved sniffer");
        };

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

        view.AttachConnection(new ScriptedOutput("one", "two"));

        await WaitUntil(() => delivered.Count >= 2, "the loop kept reading past the throw");
        Assert.That(delivered, Is.EqualTo(new[] { "one", "two" }));

        // The loop reaching EOF and reporting the exit is what proves it was never torn down.
        var done = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.That(done, Is.SameAs(exited.Task), "a throwing sniffer must not stop the reader reaching EOF");

        window.Close();
    });

    /// <summary>
    /// The opt-in has to be a STYLED property on the wrapper, not a forwarder onto the inner view: a
    /// forwarder drops anything set before the template runs, which is the normal timing for XAML attributes
    /// and object initialisers — leaving a consumer on UI-thread delivery having asked for the read task.
    ///
    /// <para>Asserted on the DELIVERY THREAD rather than by reading the property back. Reading it back only
    /// proves the wrapper's own styled property holds the value, which it does whether or not anything
    /// carries it to the inner view — so that version passes with the template binding in Generic.axaml
    /// deleted, which is the single line this test exists to protect.</para>
    /// </summary>
    [AvaloniaTest]
    public Task TerminalControl_carries_the_opt_in_set_before_its_template_runs() => Run(async () =>
    {
        var control = new TerminalControl { Process = "", OutputReceivedOnReadTask = true };
        Assert.That(control.OutputReceivedOnReadTask, Is.True, "set before the template is applied");

        var window = Show(control);

        bool? onUiThread = null;
        control.OutputReceived += (_, _) => onUiThread ??= Dispatcher.UIThread.CheckAccess();

        control.AttachConnection(new ScriptedOutput("anything"));

        await WaitUntil(() => onUiThread is not null, "the event was raised");
        Assert.That(onUiThread, Is.False, "the opt-in reached the inner view, not just the wrapper");

        window.Close();
    });

    private static Task Run(Func<Task> body) => body();
}
