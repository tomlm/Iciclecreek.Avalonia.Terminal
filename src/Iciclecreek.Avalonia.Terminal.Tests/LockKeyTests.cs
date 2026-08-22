using System.Text;
using Porta.Pty;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// CapsLock, NumLock and ScrollLock are modifier keys pressed on their own — they produce no character —
/// but <c>IsModifierKey</c> does not list them, so the terminal treats pressing one as typing.
/// </summary>
[TestFixture]
public class LockKeyTests
{
    private sealed class IdleConnection : IPtyConnection
    {
        // Never returns: the view only needs a live connection, not output.
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

        public Stream ReaderStream { get; } = new Blocking();
        public Stream WriterStream { get; } = new MemoryStream();
        public int ExitCode => 0;
        public bool WaitForExit(int ms) => false;
        public int Pid => -1;
        public void Kill() { }
        public void Resize(int c, int r) { }
        public void Dispose() { }
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
    }

    private static (TerminalView view, Window window) LiveFocusedView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        view.AttachConnection(new IdleConnection());
        view.Focus();
        Assert.That(view.IsFocused, Is.True, "sanity: OnKeyDown returns early without focus");
        return (view, window);
    }

    private static void PressKey(TerminalView view, Key key)
        => view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

    /// <summary>
    /// A lock key must not clear a selection. It is the same case the existing guard was written for —
    /// "a bare modifier press doesn't clear an active selection before the rest of a copy shortcut is
    /// typed" — and CapsLock is exactly as much a bare modifier press as Shift is.
    /// </summary>
    [TestCase(Key.CapsLock)]
    [TestCase(Key.NumLock)]
    [TestCase(Key.Scroll)]
    [AvaloniaTest]
    public void A_lock_key_does_not_clear_the_selection(Key key)
    {
        var (view, window) = LiveFocusedView();

        view.Terminal.Selection.StartSelection(0, 0, XTerm.Selection.SelectionMode.Normal);
        view.Terminal.Selection.UpdateSelection(4, 0);
        view.Terminal.Selection.EndSelection();
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: there is a selection to lose");

        PressKey(view, key);

        Assert.That(view.Terminal.Selection.HasSelection, Is.True,
            $"{key} produces no character — pressing it is not typing");

        window.Close();
    }

    /// <summary>Shift already behaves correctly; pinned so the lock keys are held to the same bar.</summary>
    [AvaloniaTest]
    public void A_shift_press_does_not_clear_the_selection()
    {
        var (view, window) = LiveFocusedView();

        view.Terminal.Selection.StartSelection(0, 0, XTerm.Selection.SelectionMode.Normal);
        view.Terminal.Selection.UpdateSelection(4, 0);
        view.Terminal.Selection.EndSelection();

        PressKey(view, Key.LeftShift);

        Assert.That(view.Terminal.Selection.HasSelection, Is.True);
        window.Close();
    }
}
