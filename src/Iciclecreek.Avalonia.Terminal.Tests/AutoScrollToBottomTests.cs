using System.Collections.Concurrent;
using System.Text;
using Porta.Pty;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// <see cref="TerminalView.AutoScrollToBottom"/> — follow the tail, pause when the user scrolls back,
/// resume when they return to it or type.
///
/// <para>Driven through <see cref="TerminalView.AttachConnection"/> with a connection the test pushes output
/// into, so each assertion is made at a known point rather than against whatever a real shell happened to
/// have written by then.</para>
/// </summary>
[TestFixture]
public class AutoScrollToBottomTests
{
    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static Window Show(Control content)
    {
        var window = new Window { Width = 800, Height = 600, Content = content };
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

    /// <summary>A stream the test feeds on demand; Read blocks until something is pushed or it is closed.</summary>
    private sealed class PushStream : Stream
    {
        private readonly BlockingCollection<byte[]> _queue = new();

        public void Push(string text) => _queue.Add(Encoding.UTF8.GetBytes(text));
        public void Done() => _queue.CompleteAdding();

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                var chunk = _queue.Take();          // blocks; throws when completed and drained
                Array.Copy(chunk, 0, buffer, offset, chunk.Length);
                return chunk.Length;
            }
            catch (InvalidOperationException) { return 0; }   // EOF
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

    private sealed class PushConnection : IPtyConnection
    {
        private readonly PushStream _stream = new();
        public Stream ReaderStream => _stream;
        public Stream WriterStream { get; } = new MemoryStream();

        public void Push(string text) => _stream.Push(text);
        public void Done() => _stream.Done();

        public int ExitCode => 0;
        public bool WaitForExit(int milliseconds) => true;
        public int Pid => -1;
        public void Kill() { }
        public void Resize(int columns, int rows) { }
        public void Dispose() { }
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
    }

    private static string Lines(int n, string tag = "line")
        => string.Concat(Enumerable.Range(0, n).Select(i => $"{tag} {i}\r\n"));

    /// <summary>Push output and wait for the emulator to have consumed it.</summary>
    private static async Task PushAndSettle(TerminalView view, PushConnection connection, string text)
    {
        var before = view.MaxScrollback;
        connection.Push(text);
        await WaitUntil(() => view.MaxScrollback > before, "the buffer grew");
        await Task.Delay(50);   // let the posted change notifications drain
    }

    // ── The contract ────────────────────────────────────────────────────────────────────────────

    /// <summary>Default on: output drags the viewport along, which is the pre-existing behaviour.</summary>
    [AvaloniaTest]
    public Task Following_by_default() => Run(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        var connection = new PushConnection();
        view.AttachConnection(connection);

        await PushAndSettle(view, connection, Lines(200));
        Assert.That(view.ViewportY, Is.EqualTo(view.MaxScrollback), "a following view sits at the tail");

        connection.Done();
        window.Close();
    });

    /// <summary>
    /// The point of the feature: scroll back and output stops yanking the viewport down. This is the case
    /// that made reading a scrollback impossible while anything was still printing.
    /// </summary>
    [AvaloniaTest]
    public Task Scrolling_back_pauses_the_follow() => Run(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        var connection = new PushConnection();
        view.AttachConnection(connection);

        await PushAndSettle(view, connection, Lines(200));

        view.ViewportY = view.MaxScrollback - 50;      // park up in the scrollback
        var parked = view.ViewportY;

        await PushAndSettle(view, connection, Lines(200, "more"));

        Assert.That(view.ViewportY, Is.EqualTo(parked), "output must not move a viewport the user parked");
        Assert.That(view.ViewportY, Is.LessThan(view.MaxScrollback), "and the buffer did grow underneath it");

        connection.Done();
        window.Close();
    });

    /// <summary>Scrolling back to the tail resumes following, with no explicit resume call needed.</summary>
    [AvaloniaTest]
    public Task Returning_to_the_tail_resumes_the_follow() => Run(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        var connection = new PushConnection();
        view.AttachConnection(connection);

        await PushAndSettle(view, connection, Lines(200));
        view.ViewportY = view.MaxScrollback - 50;
        await PushAndSettle(view, connection, Lines(50, "more"));

        view.ViewportY = view.MaxScrollback;           // back to the bottom
        await PushAndSettle(view, connection, Lines(50, "again"));

        Assert.That(view.ViewportY, Is.EqualTo(view.MaxScrollback), "returning to the tail resumes following");

        connection.Done();
        window.Close();
    });

    /// <summary>
    /// The last item of #25's own test plan, which failed as originally written: turning the property off has
    /// to actually stop the terminal scrolling itself.
    /// </summary>
    [AvaloniaTest]
    public Task Off_means_never_auto_scrolls() => Run(async () =>
    {
        var view = new TerminalView { Process = "", AutoScrollToBottom = false };
        var window = Show(view);
        var connection = new PushConnection();
        view.AttachConnection(connection);

        await PushAndSettle(view, connection, Lines(200));

        Assert.That(view.ViewportY, Is.Zero, "with auto-scroll off the viewport never moves on its own");
        Assert.That(view.MaxScrollback, Is.GreaterThan(0), "though the buffer still grew");

        connection.Done();
        window.Close();
    });

    /// <summary>
    /// Set in an object initialiser, which runs before the emulator exists. The volatile mirror the reader
    /// consults is updated ahead of the null-guard in OnPropertyChanged precisely so this is not dropped —
    /// without that, the test above passes for the wrong reason and this one fails.
    /// </summary>
    [AvaloniaTest]
    public Task Off_survives_being_set_before_initialisation() => Run(async () =>
    {
        var view = new TerminalView { Process = "", AutoScrollToBottom = false };
        var window = Show(view);
        Assert.That(view.AutoScrollToBottom, Is.False, "the property itself round-trips");

        var connection = new PushConnection();
        view.AttachConnection(connection);
        await PushAndSettle(view, connection, Lines(100));

        Assert.That(view.ViewportY, Is.Zero, "the reader saw the mirrored value, not the default");

        connection.Done();
        window.Close();
    });

    /// <summary>
    /// A scrollbar drag moves ViewportY directly rather than going through the wheel handler. The flag-based
    /// design missed this — sampling the buffer at write time covers it without enumerating the paths.
    /// </summary>
    [AvaloniaTest]
    public Task A_programmatic_viewport_move_pauses_the_follow() => Run(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        var connection = new PushConnection();
        view.AttachConnection(connection);

        await PushAndSettle(view, connection, Lines(200));

        // Exactly what TerminalControl.OnScrollBarScroll does — no wheel event involved.
        view.ViewportY = view.MaxScrollback - 30;
        var parked = view.ViewportY;

        await PushAndSettle(view, connection, Lines(100, "more"));
        Assert.That(view.ViewportY, Is.EqualTo(parked), "a scrollbar-driven move has to pause the follow too");

        connection.Done();
        window.Close();
    });

    /// <summary>
    /// On TerminalControl the property must be STYLED, not a CLR forwarder: a forwarder drops anything set
    /// before the template runs, which is the normal timing for XAML attributes and object initialisers.
    /// </summary>
    [AvaloniaTest]
    public void TerminalControl_keeps_a_value_set_before_its_template_runs()
    {
        var control = new TerminalControl { Process = "", AutoScrollToBottom = false };
        Assert.That(control.AutoScrollToBottom, Is.False, "set before the template is applied");

        var window = Show(control);
        Assert.That(control.AutoScrollToBottom, Is.False, "and still false once it has");

        window.Close();
    }

    /// <summary>
    /// Once the scrollback ring is FULL it drops its oldest lines, and every absolute index shifts down with
    /// them. A parked viewport keeps its ViewportY, so without compensation the content under the user's eye
    /// slides upward while output keeps arriving — they drift off what they were reading even though the
    /// follow is correctly paused.
    ///
    /// <para>A small BufferSize is what makes this testable: it reaches the eviction threshold in a hundred
    /// lines rather than a thousand. The push is sized to evict PAST the parked position but not past the
    /// parked CONTENT — once the ring drops the line the user is reading, it is genuinely gone and no
    /// compensation can help, which is a limit of the buffer rather than of this fix.</para>
    /// </summary>
    [AvaloniaTest]
    public Task A_parked_viewport_rides_out_scrollback_eviction() => Run(async () =>
    {
        var view = new TerminalView { Process = "", BufferSize = 120 };
        var window = Show(view);
        var connection = new PushConnection();
        view.AttachConnection(connection);

        await PushAndSettle(view, connection, Lines(100, "old"));

        view.ViewportY = view.MaxScrollback - 20;
        var parkedY = view.ViewportY;
        var parkedText = TopVisibleLine(view);
        Assert.That(parkedText, Does.StartWith("old "), "sanity: parked over the earlier output");

        // Enough to drive the ring past capacity and force eviction.
        await PushAndSettle(view, connection, Lines(90, "new"));

        Assert.That(view.ViewportY, Is.LessThan(parkedY),
            "sanity: the ring evicted, so the compensation had something to do");
        Assert.That(TopVisibleLine(view), Is.EqualTo(parkedText),
            "the line under the user must not slide away as the ring evicts beneath it");

        connection.Done();
        window.Close();
    });

    /// <summary>The text of the first row the viewport shows, trailing blanks trimmed.</summary>
    private static string TopVisibleLine(TerminalView view)
    {
        var line = view.Terminal.Buffer.GetLine(view.Terminal.Buffer.ViewportY);
        if (line == null) return string.Empty;
        var sb = new StringBuilder();
        for (int x = 0; x < line.Length; x++)
            sb.Append(string.IsNullOrEmpty(line[x].Content) ? " " : line[x].Content);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A terminal that can pause its follow needs a way to resume it on demand — the "jump to bottom"
    /// affordance a host shows once the user scrolls away. IsFollowingTail drives whether that affordance is
    /// visible; FollowTail() is what it calls.
    /// </summary>
    [AvaloniaTest]
    public Task FollowTail_returns_the_view_to_the_bottom() => Run(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        var connection = new PushConnection();
        view.AttachConnection(connection);

        await PushAndSettle(view, connection, Lines(200));
        Assert.That(view.IsFollowingTail, Is.True, "a fresh view follows");

        view.ViewportY = view.MaxScrollback - 50;
        await PushAndSettle(view, connection, Lines(50, "more"));
        Assert.That(view.IsFollowingTail, Is.False, "scrolling back stops the follow");

        view.FollowTail();
        Assert.That(view.ViewportY, Is.EqualTo(view.MaxScrollback), "and this puts it back");

        await PushAndSettle(view, connection, Lines(50, "again"));
        Assert.That(view.IsFollowingTail, Is.True, "following again, so new output drags the viewport");
        Assert.That(view.ViewportY, Is.EqualTo(view.MaxScrollback));

        connection.Done();
        window.Close();
    });

    /// <summary>
    /// The guards are the reason this is a method rather than "set ViewportY yourself": with auto-scroll off
    /// the host owns the viewport, and a jump-to-bottom button must not quietly take it back.
    /// </summary>
    [AvaloniaTest]
    public Task FollowTail_is_a_no_op_when_auto_scroll_is_off() => Run(async () =>
    {
        var view = new TerminalView { Process = "", AutoScrollToBottom = false };
        var window = Show(view);
        var connection = new PushConnection();
        view.AttachConnection(connection);

        await PushAndSettle(view, connection, Lines(200));
        var before = view.ViewportY;

        view.FollowTail();
        Assert.That(view.ViewportY, Is.EqualTo(before), "auto-scroll off means the host owns the viewport");

        connection.Done();
        window.Close();
    });

    private static Task Run(Func<Task> body) => body();
}
