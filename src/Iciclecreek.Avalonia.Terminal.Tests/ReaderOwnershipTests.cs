using System.Text;
using Porta.Pty;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// A read loop must speak only for the connection it was started on, and only while the view still
/// owns it.
/// </summary>
/// <remarks>
/// <para>The loop checked that in its while condition, which is one ask too early. The read between
/// two passes of that condition is SYNCHRONOUS and blocks for as long as the process stays quiet --
/// at an idle prompt, indefinitely -- so the check says nothing about who owns the bytes it
/// eventually returns. A detach or a relaunch lands in that window because that window is nearly
/// the whole life of the loop.</para>
/// <para>Every test here drives the seam through a connection whose output the test controls, so
/// "a chunk arrives after the handover" is an event that can be staged rather than raced for.</para>
/// </remarks>
[TestFixture]
public class ReaderOwnershipTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A connection whose reader returns exactly what the test feeds it, when the test says so.
    /// </summary>
    /// <remarks>
    /// RecordingConnection's reader blocks for ever, which is right for tests about input and
    /// useless here: what is being tested is what happens to a chunk that arrives at an awkward
    /// moment, so the test has to choose the moment.
    /// </remarks>
    private sealed class ScriptedConnection : IPtyConnection
    {
        private readonly BlockingStream _reader = new();
        private readonly MemoryStream _written = new();

        public ScriptedConnection() => WriterStream = new RecorderStream(_written);

        /// <summary>Make the parked read return with this text.</summary>
        public void Emit(string text) => _reader.Deliver(Encoding.UTF8.GetBytes(text));

        /// <summary>Everything the view has sent to this process, decoded as UTF-8.</summary>
        public string Written { get { lock (_written) return Encoding.UTF8.GetString(_written.ToArray()); } }

        public Stream ReaderStream => _reader;
        public Stream WriterStream { get; }
        public int ExitCode => 0;
        public bool WaitForExit(int ms) => false;
        public int Pid => -1;
        public void Kill() { }
        public void Resize(int c, int r) { }
        public void Dispose() { }
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }

        private sealed class BlockingStream : Stream
        {
            private readonly SemaphoreSlim _ready = new(0);
            private readonly Queue<byte[]> _chunks = new();

            public void Deliver(byte[] chunk)
            {
                lock (_chunks) _chunks.Enqueue(chunk);
                _ready.Release();
            }

            public override int Read(byte[] b, int o, int c)
            {
                _ready.Wait();
                byte[] chunk;
                lock (_chunks) chunk = _chunks.Dequeue();
                var n = Math.Min(c, chunk.Length);
                Array.Copy(chunk, 0, b, o, n);
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }

        /// <summary>Hold every write until <see cref="ReleaseWrites"/>, so a send can be parked mid-flight.</summary>
        public void BlockWrites() => ((RecorderStream)WriterStream).Block();

        public void ReleaseWrites() => ((RecorderStream)WriterStream).Release();

        /// <summary>True while a write is parked inside the stream, holding the view's send semaphore.</summary>
        public bool WriteInProgress => ((RecorderStream)WriterStream).InProgress;

        private sealed class RecorderStream : Stream
        {
            private readonly MemoryStream _sink;
            private readonly ManualResetEventSlim _gate = new(true);
            private volatile bool _inProgress;

            public RecorderStream(MemoryStream sink) => _sink = sink;

            public void Block() => _gate.Reset();
            public void Release() => _gate.Set();
            public bool InProgress => _inProgress;

            // Overridden rather than left to the base class, so the wait happens where the view is
            // awaiting rather than on a pool thread of the base class's choosing.
            //
            // Genuinely asynchronous, and that is not a detail. Blocking here instead returned no
            // Task at all: SendToPtyAsync takes its semaphore uncontended, so it runs straight
            // through into this method, and a blocking wait froze the caller -- the test hung
            // holding the very semaphore it was trying to make a second send queue behind.
            public override async Task WriteAsync(byte[] b, int o, int c, CancellationToken ct)
            {
                _inProgress = true;
                try
                {
                    await Task.Run(() => _gate.Wait(ct), ct).ConfigureAwait(false);
                    Write(b, o, c);
                }
                finally { _inProgress = false; }
            }

            public override void Write(byte[] b, int o, int c) { lock (_sink) _sink.Write(b, o, c); }
            public override void Flush() { }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
        }
    }

    private static (TerminalView view, ScriptedConnection pty, Window window) LiveView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();

        var pty = new ScriptedConnection();
        view.AttachConnection(pty);
        return (view, pty, window);
    }

    /// <summary>Waits for <paramref name="condition"/> without asserting it, so the caller can.</summary>
    private static void Await(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < Patience && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    [AvaloniaTest]
    public void Output_arriving_after_a_detach_is_not_written_into_this_view()
    {
        // The chunk belongs to whoever owns the process now. Painting it here loses it to them AND
        // shows this view a process it has handed away -- and before the check after the read, that
        // is exactly what happened, because the ownership test had been made before the read blocked.
        var (view, pty, window) = LiveView();
        try
        {
            pty.Emit("before the handover\r\n");
            Await(() => Screen(view).Contains("before the handover"));
            Assert.That(Screen(view), Does.Contain("before the handover"),
                "sanity: the loop is running and delivering, or the rest of this proves nothing");

            view.DetachConnection();

            pty.Emit("AFTER THE HANDOVER\r\n");
            Thread.Sleep(300);
            Dispatcher.UIThread.RunJobs();

            Assert.That(Screen(view), Does.Not.Contain("AFTER THE HANDOVER"),
                "a chunk read after the detach belongs to the connection's new owner");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_sniffer_is_not_handed_output_from_a_connection_the_view_gave_up()
    {
        // The same defect one layer out, and the one the code was most confident about: the comment
        // on this path asserted the while condition had already made it impossible.
        var (view, pty, window) = LiveView();
        try
        {
            // ON THE READ TASK, which is the path the finding is about and the only one that was
            // unguarded. The default path posts to the dispatcher behind a token check that a detach
            // already invalidates, so a test left on the default measures a guard that was never
            // missing and passes against the broken code.
            view.OutputReceivedOnReadTask = true;

            var seen = new StringBuilder();
            view.OutputReceived += (_, e) => { lock (seen) seen.Append(e.Output); };

            pty.Emit("first\r\n");
            Await(() => Seen(seen).Contains("first"));
            Assert.That(Seen(seen), Does.Contain("first"), "sanity: the sniffer is wired up");

            view.DetachConnection();

            pty.Emit("STALE\r\n");
            Thread.Sleep(300);
            Dispatcher.UIThread.RunJobs();

            Assert.That(Seen(seen), Does.Not.Contain("STALE"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Input_queued_across_a_detach_is_not_typed_into_someone_elses_process()
    {
        // SendToPtyAsync captures the connection and then awaits a semaphore. A keystroke waiting
        // behind a slow write can sit there across a detach, and the captured connection is then one
        // the view has handed back -- so the input goes into a process that is not this view's.
        //
        // Staged by taking the semaphore's place: the send is made while the view still owns the
        // connection, and the detach happens before it can run.
        var (view, pty, window) = LiveView();
        try
        {
            // Staged rather than raced for. The first send is made to block inside the write, which
            // leaves it holding the send semaphore; the second then queues behind it. Detaching while
            // the second is parked there is the exact situation the capture cannot survive: it took
            // its connection reference before the wait, and by the time it runs that connection has
            // been handed back to its owner.
            //
            // Detaching BEFORE the send instead would prove nothing -- _ptyConnection is null by then
            // and the existing null check at the top of SendToPtyAsync catches it.
            pty.BlockWrites();

            var first = view.SendInputAsync("first");
            Await(() => pty.WriteInProgress);
            Assert.That(pty.WriteInProgress, Is.True, "sanity: the first send is holding the semaphore");

            var second = view.SendInputAsync("TYPED AFTER THE HANDOVER");
            Thread.Sleep(100);

            view.DetachConnection();

            pty.ReleaseWrites();

            // Asserted, not ignored. WaitAll reports a timeout by returning false, and swallowing
            // that would let a send that never completes read as "nothing was written" -- which is
            // exactly what this test asserts, so a deadlock would have passed it. It would also
            // leave both tasks running past the end of the test, in a fixture that shares one
            // application with the whole assembly.
            Assert.That(Task.WaitAll(new[] { first, second }, TimeSpan.FromSeconds(5)), Is.True,
                "a send that never finished would make the assertion below vacuously true");

            Thread.Sleep(100);

            Assert.That(pty.Written, Does.Not.Contain("TYPED AFTER THE HANDOVER"),
                "input aimed at a process the view no longer owns belongs to nothing");
        }
        finally { window.Close(); }
    }

    private static string Screen(TerminalView view)
    {
        var sb = new StringBuilder();
        for (var y = 0; y < view.Terminal.Rows; y++)
            sb.AppendLine(view.Terminal.Buffer.GetLine(view.Terminal.Buffer.YBase + y)?.TranslateToString(true) ?? "");
        return sb.ToString();
    }

    private static string Seen(StringBuilder seen) { lock (seen) return seen.ToString(); }
}
