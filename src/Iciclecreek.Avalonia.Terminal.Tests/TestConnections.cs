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

    public RecordingConnection() => WriterStream = new Recorder(_written);

    /// <summary>Everything the terminal has sent to the process so far, decoded as UTF-8.</summary>
    public string Written { get { lock (_written) return Encoding.UTF8.GetString(_written.ToArray()); } }

    public Stream ReaderStream { get; } = new Blocking();
    public Stream WriterStream { get; }
    public int ExitCode => 0;
    public bool WaitForExit(int ms) => false;
    public int Pid => -1;
    public void Kill() { }
    public void Resize(int c, int r) { }
    public void Dispose() { }
    public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
}
