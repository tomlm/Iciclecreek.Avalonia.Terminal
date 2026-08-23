using System.Text;
using Porta.Pty;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Shift + navigation extends a selection in the buffer instead of sending the modified-cursor sequence.
///
/// <para>No interactive shell binds ESC[1;2C, so what the emulator would otherwise send comes back as the
/// literal text ";2C" in the command line — the same failure as the word-motion keys, one modifier over.</para>
/// </summary>
[TestFixture]
public class ShiftSelectionTests
{
    private sealed class IdleConnection : IPtyConnection
    {
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
        public IdleConnection() => WriterStream = new Recorder(_written);
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

    private static (TerminalView view, IdleConnection pty, Window window) LiveView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        var pty = new IdleConnection();
        view.AttachConnection(pty);
        view.Focus();
        Assert.That(view.IsFocused, Is.True, "sanity: OnKeyDown returns early without focus");
        return (view, pty, window);
    }

    private static void Press(TerminalView v, Key k, KeyModifiers m = KeyModifiers.None)
        => v.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = m });

    /// <summary>
    /// One Shift+Right selects exactly ONE cell. That is the whole reason anchor and focus are caret
    /// boundaries rather than cell indices — counting cells makes the first press select two.
    /// </summary>
    [AvaloniaTest]
    public async Task Shift_right_selects_exactly_one_cell()
    {
        var (view, pty, window) = LiveView();

        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "a selection started");
        Assert.That(pty.Written, Is.Empty, "and nothing was sent to the shell");
        Assert.That(view.Terminal.Selection.GetSelectionText()?.Length ?? 0, Is.EqualTo(1),
            "exactly one cell, not two — the reason anchor and focus are caret boundaries");

        window.Close();
    }

    /// <summary>Collapsing back onto the anchor clears the selection, the way an editor does.</summary>
    [AvaloniaTest]
    public async Task Collapsing_back_onto_the_anchor_clears_it()
    {
        var (view, pty, window) = LiveView();

        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: something is selected");

        Press(view, Key.Left, KeyModifiers.Shift);
        await Task.Delay(40);

        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "back at the anchor means no selection");
        window.Close();
    }

    /// <summary>
    /// The point of the change: these keys must not reach the shell. Previously each sent a modified-cursor
    /// sequence that no default keymap binds, so zsh echoed its tail into the command line.
    /// </summary>
    [TestCase(Key.Left)]
    [TestCase(Key.Right)]
    [TestCase(Key.Up)]
    [TestCase(Key.Down)]
    [TestCase(Key.Home)]
    [TestCase(Key.End)]
    [AvaloniaTest]
    public async Task Shift_navigation_is_not_sent_to_the_shell(Key key)
    {
        var (view, pty, window) = LiveView();
        Press(view, key, KeyModifiers.Shift);
        await Task.Delay(60);
        Assert.That(pty.Written, Is.Empty, $"Shift+{key} extends a selection; it is not shell input");
        window.Close();
    }

    /// <summary>
    /// Left alone in the alternate buffer: full-screen apps draw their own selection and several bind
    /// Shift+arrow themselves, so there the sequence still belongs to the app.
    /// </summary>
    [AvaloniaTest]
    public async Task The_alternate_buffer_still_gets_the_sequence()
    {
        var (view, pty, window) = LiveView();

        view.Terminal.Write("\u001b[?1049h");     // switch to the alternate buffer
        await Task.Delay(60);
        Assert.That(view.Terminal.IsAlternateBufferActive, Is.True, "sanity: in the alternate buffer");

        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(pty.Written, Is.Not.Empty, "a full-screen app reads the real sequence itself");
        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "and no buffer selection was made");

        window.Close();
    }

    /// <summary>A plain keystroke drops the selection, and the next Shift+arrow re-anchors at the cursor.</summary>
    [AvaloniaTest]
    public async Task A_plain_keystroke_drops_the_selection()
    {
        var (view, pty, window) = LiveView();

        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity");

        Press(view, Key.A);
        await Task.Delay(40);

        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "typing clears it");
        window.Close();
    }
}
