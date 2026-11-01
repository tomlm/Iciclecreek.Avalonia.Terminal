using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The clipboard belongs to every process on the machine, and on Windows one holding it open makes
/// Avalonia's calls throw. Copy and paste mostly run from async void key and mouse handlers, where an
/// escaping exception ends the application.
/// </summary>
[TestFixture]
public class ClipboardFailureTests
{
    private const string Esc = "\u001b";

    /// <summary>Fails every call the way Windows does while another process has the clipboard.</summary>
    public class LockedClipboard : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new COMException($"OpenClipboard Failed: {QuotedText}", unchecked((int)0x800401D0));
    }

    /// <summary>What a platform's error message is free to quote, and the log must not repeat.</summary>
    private const string QuotedText = "S3CR3T-clipboard-text";

    private sealed class Capture : TraceListener
    {
        private readonly StringBuilder _text = new();
        public override void Write(string? message) { lock (_text) _text.Append(message); }
        public override void WriteLine(string? message) { lock (_text) _text.AppendLine(message); }
        public string Text { get { lock (_text) return _text.ToString(); } }
    }

    private static (TerminalView view, RecordingConnection pty, Window window, IDisposable unlock) LockedView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();

        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        view.Focus();
        Assert.That(view.IsFocused, Is.True, "sanity: OnKeyDown returns early without focus");

        // Below Avalonia's own Clipboard, which is where the platform's failures come from.
        var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
        var implField = clipboard.GetType().GetField("_clipboardImpl", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(implField, Is.Not.Null, "Avalonia's Clipboard has changed; this test needs updating");
        var real = implField!.GetValue(clipboard);
        implField.SetValue(clipboard, DispatchProxy.Create(implField.FieldType, typeof(LockedClipboard)));

        // Put back on the way out if this fails too. The caller's finally has not started yet, and this
        // assembly shares one Application, so a throwing clipboard left behind breaks unrelated tests.
        try
        {
            Assert.Catch<COMException>(() => clipboard.TryGetTextAsync().GetAwaiter().GetResult(),
                "sanity: the clipboard has to be failing or this proves nothing");
        }
        catch
        {
            implField.SetValue(clipboard, real);
            window.Close();
            throw;
        }

        return (view, pty, window, new Restore(() => implField.SetValue(clipboard, real)));
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static void Select(TerminalView view)
    {
        view.Terminal.Write("hello");
        view.Terminal.Selection.StartSelection(0, 0, XTerm.Selection.SelectionMode.Normal);
        view.Terminal.Selection.UpdateSelection(4, 0);
        view.Terminal.Selection.EndSelection();
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: there is a selection");
    }

    private static void Press(TerminalView view, Key key, KeyModifiers modifiers)
    {
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Thread.Sleep(60);
    }

    private static PointerPressedEventArgs RightPress(TerminalView view)
    {
        var pointer = new global::Avalonia.Input.Pointer(global::Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var props = new PointerPointProperties(RawInputModifiers.RightMouseButton,
                                               PointerUpdateKind.RightButtonPressed);
        return new PointerPressedEventArgs(view, pointer, view, new global::Avalonia.Point(10, 10),
                                           0, props, KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerPressedEvent,
        };
    }

    [AvaloniaTest]
    public void A_right_click_copy_clears_the_selection_once_it_is_on_the_clipboard()
    {
        // The other side of the test below, so that one cannot pass by never clearing at all.
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        view.AttachConnection(new RecordingConnection());
        try
        {
            Select(view);

            view.RaiseEvent(RightPress(view));
            Thread.Sleep(60);
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Selection.HasSelection, Is.False);
            Assert.That(TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult(),
                Does.StartWith("hello"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_right_click_copy_the_clipboard_refuses_keeps_the_selection()
    {
        var (view, _, window, unlock) = LockedView();
        try
        {
            Select(view);

            view.RaiseEvent(RightPress(view));
            Thread.Sleep(60);
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Terminal.Selection.HasSelection, Is.True,
                "nothing reached the clipboard, so the selection is the only copy left to retry from");
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void A_clipboard_failure_is_logged_without_its_message()
    {
        var capture = new Capture();
        Trace.Listeners.Add(capture);
        var (view, _, window, unlock) = LockedView();
        try
        {
            Select(view);

            view.CopyAsync().GetAwaiter().GetResult();
            view.PasteAsync().GetAwaiter().GetResult();

            // Proves the capture works, so an empty log cannot pass for the wrong reason.
            Assert.That(capture.Text, Does.Contain("Clipboard write failed"));
            Assert.That(capture.Text, Does.Contain("Clipboard read failed"));
            Assert.That(capture.Text, Does.Not.Contain(QuotedText));
        }
        finally
        {
            Trace.Listeners.Remove(capture);
            unlock.Dispose();
            window.Close();
        }
    }

    [AvaloniaTest]
    public void A_copy_the_clipboard_refuses_reports_false_and_keeps_the_selection()
    {
        var (view, _, window, unlock) = LockedView();
        try
        {
            Select(view);

            Assert.That(view.CopyAsync().GetAwaiter().GetResult(), Is.False);
            Assert.That(view.Terminal.Selection.HasSelection, Is.True);
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void A_cut_the_clipboard_refuses_deletes_nothing()
    {
        var (view, pty, window, unlock) = LockedView();
        try
        {
            view.Terminal.Write("hello");
            Thread.Sleep(60);
            Press(view, Key.Left, KeyModifiers.Shift);
            Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: a keyboard selection to cut");
            var before = pty.Written.Length;

            Assert.That(view.CutAsync().GetAwaiter().GetResult(), Is.False);

            Assert.That(pty.Written.Substring(before), Is.Empty,
                "what could not be copied must not be deleted: it would be gone from both places");
            Assert.That(view.Terminal.Selection.HasSelection, Is.True);
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void A_paste_the_clipboard_refuses_sends_nothing_and_keeps_the_selection()
    {
        var (view, pty, window, unlock) = LockedView();
        try
        {
            Select(view);
            var before = pty.Written.Length;

            Assert.DoesNotThrow(() => view.PasteAsync().GetAwaiter().GetResult());

            Assert.That(pty.Written.Substring(before), Is.Empty);
            Assert.That(view.Terminal.Selection.HasSelection, Is.True);
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void An_announced_paste_the_clipboard_refuses_sends_nothing()
    {
        var (view, pty, window, unlock) = LockedView();
        try
        {
            view.Terminal.Write($"{Esc}[?5522h");
            Thread.Sleep(60);
            Assert.That(view.Terminal.PasteNotificationMode, Is.True, "sanity: the announced path is the one taken");
            var before = pty.Written.Length;

            Assert.DoesNotThrow(() => view.PasteAsync().GetAwaiter().GetResult());

            Assert.That(pty.Written.Substring(before), Is.Empty);
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void Copying_after_the_process_exited_survives_a_locked_clipboard()
    {
        // The one keyboard copy outside OnKeyDown's try block. It is async void, so an exception here
        // does not come back to the caller: it goes to the dispatcher, which is what ends an application.
        var (view, _, window, unlock) = LockedView();
        var unhandled = new List<Exception>();
        void Record(object? _, DispatcherUnhandledExceptionEventArgs e)
        {
            unhandled.Add(e.Exception);
            e.Handled = true;
        }

        Dispatcher.UIThread.UnhandledException += Record;
        try
        {
            Select(view);
            var exited = typeof(TerminalView).GetField("_processExitHandled",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(exited, Is.Not.Null, "_processExitHandled has been renamed; this test needs updating");
            exited!.SetValue(view, 1);

            Press(view, Key.C, KeyModifiers.Control);
            Dispatcher.UIThread.RunJobs();

            Assert.That(unhandled, Is.Empty);
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= Record;
            unlock.Dispose();
            window.Close();
        }
    }
}
