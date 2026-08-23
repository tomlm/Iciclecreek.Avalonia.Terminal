using System.Text;
using Porta.Pty;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The members a host needs when it owns a view's lifecycle rather than just showing one — pooling a view,
/// laying its own chrome over it, or reading back what the shell last printed.
/// </summary>
[TestFixture]
public class HostSurfaceTests
{
    private sealed class ScriptedStream : Stream
    {
        private readonly Queue<byte[]> _chunks;
        public ScriptedStream(IEnumerable<string> chunks)
            => _chunks = new Queue<byte[]>(chunks.Select(Encoding.UTF8.GetBytes));
        private readonly ManualResetEventSlim _idle = new(false);

        // Blocks rather than returning EOF once the script is spent. EOF would make the view write its
        // "Process exited" message, which moves the cursor off the row under test.
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunks.Count == 0) { _idle.Wait(); return 0; }
            var c = _chunks.Dequeue();
            Array.Copy(c, 0, buffer, offset, c.Length);
            return c.Length;
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

    private sealed class ScriptedOutput : IPtyConnection
    {
        public ScriptedOutput(params string[] chunks) => ReaderStream = new ScriptedStream(chunks);
        public Stream ReaderStream { get; }
        public Stream WriterStream { get; } = new MemoryStream();
        public int ExitCode => 0;
        public bool WaitForExit(int ms) => true;
        public int Pid => -1;
        public void Kill() { }
        public void Resize(int c, int r) { }
        public void Dispose() { }
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
    }

    private static Window Show(Control content)
    {
        var w = new Window { Width = 800, Height = 600, Content = content };
        w.Show();
        w.UpdateLayout();
        global::Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return w;
    }

    private static async Task WaitUntil(Func<bool> cond, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting until {because}");
            await Task.Delay(10);
        }
    }

    private static string BufferText(TerminalView v)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < v.Terminal.Buffer.Length; y++)
        {
            var line = v.Terminal.Buffer.GetLine(y);
            if (line == null) continue;
            for (int x = 0; x < line.Length; x++)
                sb.Append(string.IsNullOrEmpty(line[x].Content) ? " " : line[x].Content);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The screen AND the scrollback, not just the screen — the point is a view that is genuinely blank
    /// behind whatever a host draws over it, rather than one still showing a dead process's output.
    /// </summary>
    [AvaloniaTest]
    public async Task ClearScreen_wipes_the_scrollback_too()
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        view.AttachConnection(new ScriptedOutput(string.Concat(Enumerable.Range(0, 200).Select(i => $"line {i}\r\n"))));

        await WaitUntil(() => view.MaxScrollback > 0, "output filled the scrollback");
        Assert.That(BufferText(view), Does.Contain("line 5"), "sanity: there is something to clear");

        view.ClearScreen();

        Assert.That(BufferText(view).Trim(), Is.Empty, "screen and scrollback both");
        window.Close();
    }

    /// <summary>No-op before OnInitialized: a pooled view that was never attached has no buffer to wipe.</summary>
    [AvaloniaTest]
    public void ClearScreen_is_safe_before_the_view_is_realised()
    {
        Assert.DoesNotThrow(() => new TerminalView { Process = "" }.ClearScreen());
    }

    /// <summary>
    /// The cursor row, trailing blanks trimmed — so a host can show the shell's real last prompt instead of
    /// synthesising one.
    /// </summary>
    [AvaloniaTest]
    public async Task CurrentLineText_reads_the_cursor_row()
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        view.AttachConnection(new ScriptedOutput("first line\r\nuser@host $ "));

        await WaitUntil(() => BufferText(view).Contains("user@host"), "the prompt was written");
        Assert.That(view.CurrentLineText, Is.EqualTo("user@host $"), "trailing blanks are trimmed");
        window.Close();
    }

    /// <summary>
    /// Cell metrics, so a host overlay can size and place a stand-in caret. They compute metrics on demand
    /// rather than returning 0 before the first layout pass, which is when a host is most likely to ask.
    /// </summary>
    [AvaloniaTest]
    public void Cell_metrics_are_available_and_positive()
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        Assert.Multiple(() =>
        {
            Assert.That(view.CharWidth, Is.GreaterThan(0));
            Assert.That(view.CharHeight, Is.GreaterThan(0));
        });
        window.Close();
    }

    /// <summary>Asked before any layout has run, they still answer rather than reporting zero.</summary>
    [AvaloniaTest]
    public void Cell_metrics_answer_before_the_first_layout_pass()
    {
        var view = new TerminalView { Process = "" };
        Assert.Multiple(() =>
        {
            Assert.That(view.CharWidth, Is.GreaterThan(0));
            Assert.That(view.CharHeight, Is.GreaterThan(0));
        });
    }

    /// <summary>Safe to call at any time, including on a view that was never realised.</summary>
    [AvaloniaTest]
    public void Refresh_is_safe_before_the_view_is_realised()
    {
        Assert.DoesNotThrow(() => new TerminalView { Process = "" }.Refresh());
    }

    [AvaloniaTest]
    public void Refresh_is_safe_on_a_realised_view()
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        Assert.DoesNotThrow(() => view.Refresh());
        window.Close();
    }

    /// <summary>
    /// SuppressCursor is only observable as pixels, so what is pinned here is the part that is not: that it
    /// is a real styled property and survives being set before the template runs — the normal timing for a
    /// XAML attribute or an object initialiser, and where a plain CLR forwarder would silently drop it.
    /// </summary>
    [AvaloniaTest]
    public void SuppressCursor_survives_being_set_before_the_template_runs()
    {
        var control = new TerminalControl { Process = "", SuppressCursor = true };
        Assert.That(control.SuppressCursor, Is.True, "set before the template is applied");

        var window = Show(control);
        Assert.That(control.SuppressCursor, Is.True, "and still true once it has");
        window.Close();
    }

    [AvaloniaTest]
    public void SuppressCursor_defaults_to_off()
    {
        Assert.That(new TerminalView().SuppressCursor, Is.False);
    }
}
