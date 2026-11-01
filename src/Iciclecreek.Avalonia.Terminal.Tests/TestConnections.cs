using System.Collections.Concurrent;
using System.Text;
using Porta.Pty;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// A live pty connection that never produces output and records everything written to it.
///
/// <para>This is the connection every input test wants: <see cref="TerminalView"/> only checks that a
/// connection exists before it will handle a keystroke, and the question under test is always "what reached
/// the process", never "what did the process say". Reading <see cref="Written"/> answers that against the
/// bytes themselves rather than against internal emulator state.</para>
/// </summary>
/// <remarks>
/// One shared type on purpose. Three test classes had grown their own copy of this same plumbing, which is
/// how a fix lands in one place and not the others.
/// </remarks>
internal sealed class RecordingConnection : IPtyConnection
{
    /// <summary>
    /// A reader that never returns. The view only needs a live connection, not output, and a stream that
    /// returned 0 would look like EOF and tear the reader loop down mid-test.
    /// </summary>
    private sealed class Blocking : Stream
    {
        private readonly ManualResetEventSlim _never = new(false);
        public override int Read(byte[] b, int o, int c) { _never.Wait(); return 0; }
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

    /// <summary>
    /// The writer half. Locked because the view writes from whatever thread its async key handler resumed
    /// on, while the test reads <see cref="Written"/> from the headless UI thread.
    /// </summary>
    private sealed class Recorder : Stream
    {
        private readonly MemoryStream _sink;
        public Recorder(MemoryStream sink) => _sink = sink;
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

    private readonly MemoryStream _written = new();

    /// <param name="reader">What the process "prints" -- a <see cref="PushStream"/> to drive the read loop; silence by default.</param>
    public RecordingConnection(Stream? reader = null)
    {
        WriterStream = new Recorder(_written);
        ReaderStream = reader ?? new Blocking();
    }

    /// <summary>Everything the terminal has sent to the process so far, decoded as UTF-8.</summary>
    public string Written { get { lock (_written) return Encoding.UTF8.GetString(_written.ToArray()); } }

    public Stream ReaderStream { get; }
    public Stream WriterStream { get; }
    public int ExitCode => 0;
    public bool WaitForExit(int ms) => false;
    public int Pid => -1;
    public void Kill() { }
    public void Resize(int c, int r) { }
    public void Dispose() { }
    public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
}

/// <summary>A stream the test feeds on demand; Read blocks until something is pushed or it is closed.</summary>
/// <remarks>
/// Honours <c>count</c> and carries the remainder of an oversized chunk into the next call. Copying a
/// whole chunk regardless would happen to work today only because the read loop's buffer is far larger
/// than anything a test pushes — the moment that stops being true it would overrun the caller's buffer
/// rather than fail an assertion, which makes every test in this file quietly dependent on a sizing
/// decision made somewhere else.
/// </remarks>
internal sealed class PushStream : Stream
{
    private readonly BlockingCollection<byte[]> _queue = new();
    private byte[]? _chunk;     // the chunk being handed out
    private int _consumed;      // how much of it has already gone

    public void Push(string text) => _queue.Add(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Pushes raw bytes, so a test can put a chunk boundary anywhere — including the middle of a
    /// multi-byte character, which is what a real pty read does whenever the read happens to end there.
    /// </summary>
    public void Push(byte[] bytes) => _queue.Add(bytes);
    public void Done() => _queue.CompleteAdding();

    private volatile Exception? _failure;

    /// <summary>
    /// Ends the stream with <paramref name="failure"/> thrown from the next read that finds nothing queued,
    /// instead of EOF -- what a Unix pty's blocking stream does when the child closes its end.
    /// </summary>
    public void Fail(Exception failure)
    {
        _failure = failure;
        _queue.CompleteAdding();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (count == 0) return 0;

        // Empty pushes are skipped rather than returned: a zero-length read is EOF to the caller, and a
        // test that pushed "" would end the read loop instead of doing nothing.
        while (_chunk == null || _consumed == _chunk.Length)
        {
            try { _chunk = _queue.Take(); }               // blocks; throws when completed and drained
            catch (InvalidOperationException) when (_failure is { } failure) { throw failure; }
            catch (InvalidOperationException) { return 0; }   // EOF
            _consumed = 0;
        }

        var n = Math.Min(count, _chunk.Length - _consumed);
        Array.Copy(_chunk, _consumed, buffer, offset, n);
        _consumed += n;
        return n;
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

internal sealed class PushConnection : IPtyConnection
{
    private readonly PushStream _stream = new();
    public Stream ReaderStream => _stream;
    public Stream WriterStream { get; } = new MemoryStream();

    public void Push(string text) => _stream.Push(text);
    public void Push(byte[] bytes) => _stream.Push(bytes);
    public void Done() => _stream.Done();
    public void Fail(Exception failure) => _stream.Fail(failure);

    public int ExitCode => 0;
    public bool WaitForExit(int milliseconds) => true;
    public int Pid => -1;
    public void Kill() { }
    public void Resize(int columns, int rows) { }
    public void Dispose() { }
    public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
}

/// <summary>
/// A blocking pty double whose reads are served in the order they were MADE, one chunk per read, only
/// when the test hands one out.
/// </summary>
/// <remarks>
/// <para>Built for the handover race. A blocking reader cannot be unparked, so after a detach the old
/// view's read and the new owner's read can both be waiting on one descriptor, and which of them a
/// real pty wakes is up to the kernel. Here it is up to the test: <see cref="Emit"/> always serves the
/// OLDEST waiting read, so "the stale reader wins" is staged rather than raced for.</para>
/// <para><see cref="WaitingReads"/> says how many reads are parked right now, which is how a test
/// tells that a loop has reached its read, and how a test sees two readers on one descriptor.</para>
/// </remarks>
internal sealed class TurnConnection : IPtyConnection
{
    /// <summary>One parked read: which thread made it, and whether it has returned its chunk yet.</summary>
    public sealed class Turn
    {
        internal readonly ManualResetEventSlim Served = new(false);
        internal byte[]? Chunk;
        private volatile bool _returned;

        public Thread Reader { get; } = Thread.CurrentThread;

        /// <summary>True once Read has handed the chunk back to its caller.</summary>
        public bool Returned { get => _returned; internal set => _returned = value; }
    }

    private sealed class TurnStream : Stream
    {
        private readonly LinkedList<Turn> _waiting = new();
        private bool _closed;

        public int WaitingReads { get { lock (_waiting) return _waiting.Count; } }

        /// <summary>Ends every parked read with EOF, and every later read at once.</summary>
        public void End()
        {
            lock (_waiting)
            {
                _closed = true;
                foreach (var turn in _waiting)
                {
                    turn.Chunk = Array.Empty<byte>();
                    turn.Served.Set();
                }
                _waiting.Clear();
            }
        }

        public Turn Serve(byte[] chunk, TimeSpan patience)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                lock (_waiting)
                {
                    if (_waiting.First is { } oldest)
                    {
                        _waiting.RemoveFirst();
                        oldest.Value.Chunk = chunk;
                        oldest.Value.Served.Set();
                        return oldest.Value;
                    }
                }

                if (clock.Elapsed > patience)
                    throw new TimeoutException("nothing was reading the connection");
                Thread.Sleep(5);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            var turn = new Turn();
            lock (_waiting)
            {
                if (_closed)
                    return 0;
                _waiting.AddLast(turn);
            }
            turn.Served.Wait();

            var n = Math.Min(count, turn.Chunk!.Length);
            Array.Copy(turn.Chunk, 0, buffer, offset, n);
            turn.Returned = true;
            return n;
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

    private readonly TurnStream _stream = new();

    public Stream ReaderStream => _stream;
    public Stream WriterStream { get; } = new MemoryStream();

    /// <summary>Hands <paramref name="text"/> to the oldest parked read, waiting up to five seconds for one.</summary>
    /// <returns>The read that took it, so the test can follow that reader's thread.</returns>
    public Turn Emit(string text) => _stream.Serve(Encoding.UTF8.GetBytes(text), TimeSpan.FromSeconds(5));

    /// <summary>How many reads are parked on the connection right now.</summary>
    public int WaitingReads => _stream.WaitingReads;

    public int ExitCode => 0;
    public bool WaitForExit(int milliseconds) => false;
    public int Pid => -1;
    public void Kill() { }
    public void Resize(int columns, int rows) { }

    /// <summary>
    /// Ends every parked read with EOF, as closing a real pty does. A test calls it in its finally,
    /// after closing its views: a blocking reader parked here otherwise holds a thread and its read
    /// buffer until the test host exits.
    /// </summary>
    public void Dispose() => _stream.End();

    public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
}

/// <summary>
/// A pty double whose reads honour their cancellation token the way Porta.Pty's async-IO streams do:
/// cancellation lands while WAITING and consumes nothing.
/// </summary>
/// <remarks>
/// The contract under test in HandoverTests, held by a double so the tests need no real pty. A
/// cancelled read leaves every pushed chunk exactly where it was — that is the property
/// <see cref="Porta.Pty.IPtyConnection.SupportsCancellableRead"/> certifies, and the double must
/// keep it or the tests would pass against semantics the real thing does not have.
/// </remarks>
internal sealed class CancellablePushConnection : IPtyConnection
{
    private sealed class CancellableStream : Stream
    {
        private readonly SemaphoreSlim _available = new(0);
        private readonly ConcurrentQueue<byte[]> _queue = new();
        private byte[]? _chunk;
        private int _consumed;

        public void Push(byte[] bytes) { _queue.Enqueue(bytes); _available.Release(); }

        /// <summary>How many chunks sit unconsumed — what a lossless detach must leave intact.</summary>
        public int PendingChunks => _queue.Count + (_chunk != null && _consumed < _chunk!.Length ? 1 : 0);

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            while (_chunk == null || _consumed == _chunk.Length)
            {
                // The wait is where cancellation lands; a token fired here has consumed nothing.
                await _available.WaitAsync(cancellationToken);
                _queue.TryDequeue(out _chunk);
                _consumed = 0;
            }

            var n = Math.Min(count, _chunk!.Length - _consumed);
            Array.Copy(_chunk, _consumed, buffer, offset, n);
            _consumed += n;
            return n;
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

    private readonly CancellableStream _stream = new();

    public Stream ReaderStream => _stream;
    public Stream WriterStream { get; } = new MemoryStream();
    public bool SupportsCancellableRead => true;

    public void Push(string text) => _stream.Push(Encoding.UTF8.GetBytes(text));

    /// <summary>Chunks pushed but not yet read — zero loss means a detach leaves this untouched.</summary>
    public int PendingChunks => _stream.PendingChunks;

    public int ExitCode => 0;
    public bool WaitForExit(int milliseconds) => true;
    public int Pid => -1;
    public void Kill() { }
    public void Resize(int columns, int rows) { }
    public void Dispose() { }
    public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
}
