using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The clipboard belongs to every process on the machine, and on Windows one holding it open makes
/// Avalonia's calls throw. A host has to hear about that: the public methods throw to their caller, and
/// the key and mouse handlers and the program's own requests, which have no caller, raise ClipboardFailed.
/// The handlers are async void, so an exception escaping one would end the application.
/// </summary>
[TestFixture]
public class ClipboardFailureTests
{
    private const string Esc = "\u001b";
    private const string Bel = "\u0007";

    /// <summary>What a platform's error message is free to quote, and the log must not repeat.</summary>
    private const string QuotedText = "S3CR3T-clipboard-text";

    /// <summary>Fails every call the way Windows does while another process has the clipboard.</summary>
    public class LockedClipboard : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new COMException($"OpenClipboard Failed: {QuotedText}", unchecked((int)0x800401D0));
    }

    private sealed class Capture : TraceListener
    {
        private readonly StringBuilder _text = new();
        public override void Write(string? message) { lock (_text) _text.Append(message); }
        public override void WriteLine(string? message) { lock (_text) _text.AppendLine(message); }
        public string Text { get { lock (_text) return _text.ToString(); } }
    }

    private static (TerminalView view, RecordingConnection pty, Window window) LiveView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();

        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        view.Focus();
        Assert.That(view.IsFocused, Is.True, "sanity: OnKeyDown returns early without focus");
        return (view, pty, window);
    }

    private static (TerminalView view, RecordingConnection pty, Window window, IDisposable unlock) LockedView()
    {
        var (view, pty, window) = LiveView();
        try
        {
            return (view, pty, window, Lock(view));
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    /// <summary>Makes the clipboard fail until the result is disposed.</summary>
    private static IDisposable Lock(TerminalView view)
    {
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
            throw;
        }

        return new Restore(() => implField.SetValue(clipboard, real));
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

    private static void MarkProcessExited(TerminalView view)
    {
        var exited = typeof(TerminalView).GetField("_processExitHandled",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(exited, Is.Not.Null, "_processExitHandled has been renamed; this test needs updating");
        exited!.SetValue(view, 1);
    }

    private static void Press(TerminalView view, Key key, KeyModifiers modifiers)
    {
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Thread.Sleep(60);
        Dispatcher.UIThread.RunJobs();
    }

    private static void RightClick(TerminalView view)
    {
        var pointer = new global::Avalonia.Input.Pointer(global::Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var props = new PointerPointProperties(RawInputModifiers.RightMouseButton,
                                               PointerUpdateKind.RightButtonPressed);
        view.RaiseEvent(new PointerPressedEventArgs(view, pointer, view, new global::Avalonia.Point(10, 10),
                                                    0, props, KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerPressedEvent,
        });
        Thread.Sleep(60);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Runs a gesture against a locked clipboard and proves the host heard about it and the handler
    /// survived: what matters about an async void handler is that nothing arrives at the dispatcher, which
    /// is what ends an application.
    /// </summary>
    private static void AssertGestureReports(ClipboardOperation expected, Action<TerminalView> arrange, Action<TerminalView> gesture)
    {
        var capture = new Capture();
        var unhandled = new List<Exception>();
        var reported = new List<ClipboardFailedEventArgs>();
        void Record(object? _, DispatcherUnhandledExceptionEventArgs e)
        {
            unhandled.Add(e.Exception);
            e.Handled = true;
        }

        var (view, _, window, unlock) = LockedView();
        view.ClipboardFailed += (_, e) => reported.Add(e);
        Trace.Listeners.Add(capture);
        Dispatcher.UIThread.UnhandledException += Record;
        try
        {
            arrange(view);
            gesture(view);

            Assert.That(unhandled, Is.Empty);
            Assert.That(reported, Has.Count.EqualTo(1));
            Assert.That(reported[0].Operation, Is.EqualTo(expected));
            Assert.That(reported[0].Exception, Is.TypeOf<COMException>());
            Assert.That(capture.Text, Does.Contain($"Clipboard {expected} failed: COMException"),
                "proves the capture works, so the next line cannot pass on an empty log");
            Assert.That(capture.Text, Does.Not.Contain(QuotedText), "the failure is logged without its message");
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= Record;
            Trace.Listeners.Remove(capture);
            unlock.Dispose();
            window.Close();
        }
    }

    // ------------------------------------------------------ a host calling the public methods

    [AvaloniaTest]
    public void CopyAsync_tells_the_caller_and_keeps_the_selection()
    {
        var (view, _, window, unlock) = LockedView();
        try
        {
            Select(view);

            Assert.Catch<COMException>(() => view.CopyAsync().GetAwaiter().GetResult());
            Assert.That(view.Terminal.Selection.HasSelection, Is.True);
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void CutAsync_tells_the_caller_and_deletes_nothing()
    {
        var (view, pty, window, unlock) = LockedView();
        try
        {
            view.Terminal.Write("hello");
            Thread.Sleep(60);
            Press(view, Key.Left, KeyModifiers.Shift);
            Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: a keyboard selection to cut");
            var before = pty.Written.Length;

            Assert.Catch<COMException>(() => view.CutAsync().GetAwaiter().GetResult());

            Assert.That(pty.Written.Substring(before), Is.Empty,
                "what could not be copied must not be deleted: it would be gone from both places");
            Assert.That(view.Terminal.Selection.HasSelection, Is.True);
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void PasteAsync_tells_the_caller_sends_nothing_and_keeps_the_selection()
    {
        var (view, pty, window, unlock) = LockedView();
        try
        {
            Select(view);
            var before = pty.Written.Length;

            Assert.Catch<COMException>(() => view.PasteAsync().GetAwaiter().GetResult());

            Assert.That(pty.Written.Substring(before), Is.Empty);
            Assert.That(view.Terminal.Selection.HasSelection, Is.True);
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void An_announced_PasteAsync_tells_the_caller_and_sends_nothing()
    {
        var (view, pty, window, unlock) = LockedView();
        try
        {
            view.Terminal.Write($"{Esc}[?5522h");
            Thread.Sleep(60);
            Assert.That(view.Terminal.PasteNotificationMode, Is.True, "sanity: the announced path is the one taken");
            var before = pty.Written.Length;

            Assert.Catch<COMException>(() => view.PasteAsync().GetAwaiter().GetResult());

            Assert.That(pty.Written.Substring(before), Is.Empty);
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void A_failure_thrown_to_a_caller_is_not_reported_a_second_time()
    {
        var (view, _, window, unlock) = LockedView();
        var reported = 0;
        view.ClipboardFailed += (_, _) => reported++;
        try
        {
            Select(view);

            Assert.Catch<COMException>(() => view.CopyAsync().GetAwaiter().GetResult());
            Assert.Catch<COMException>(() => view.PasteAsync().GetAwaiter().GetResult());

            Assert.That(reported, Is.Zero);
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    // ------------------------------------------------- the key and mouse handlers, with no caller

    [AvaloniaTest]
    public void Copying_after_the_process_exited_reports_a_locked_clipboard()
    {
        // The one keyboard copy outside OnKeyDown's try block.
        AssertGestureReports(ClipboardOperation.Copy, view =>
        {
            Select(view);
            MarkProcessExited(view);
        }, view => Press(view, Key.C, KeyModifiers.Control));
    }

    [AvaloniaTest]
    public void A_keyboard_copy_reports_a_locked_clipboard()
        => AssertGestureReports(ClipboardOperation.Copy, Select, view => Press(view, Key.C, KeyModifiers.Control));

    [AvaloniaTest]
    public void A_keyboard_paste_reports_a_locked_clipboard()
        => AssertGestureReports(ClipboardOperation.Paste, _ => { },
            view => Press(view, Key.V, KeyModifiers.Control | KeyModifiers.Shift));

    [AvaloniaTest]
    public void A_keyboard_cut_reports_a_locked_clipboard()
    {
        AssertGestureReports(ClipboardOperation.Cut, view =>
        {
            view.ShortcutMode = ShortcutMode.Desktop;
            view.Terminal.Write("hello");
            Thread.Sleep(60);
            Press(view, Key.Left, KeyModifiers.Shift);
            Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: a keyboard selection to cut");
        }, view => Press(view, Key.X, TerminalKeyGestures.CommandModifiers));
    }

    [AvaloniaTest]
    public void A_right_click_paste_reports_a_locked_clipboard()
        => AssertGestureReports(ClipboardOperation.Paste, _ => { }, RightClick);

    [AvaloniaTest]
    public void A_right_click_copy_reports_a_locked_clipboard_and_keeps_the_selection()
    {
        AssertGestureReports(ClipboardOperation.Copy, Select, view =>
        {
            RightClick(view);
            Assert.That(view.Terminal.Selection.HasSelection, Is.True,
                "nothing reached the clipboard, so the selection is the only copy left to retry from");
        });
    }

    [AvaloniaTest]
    public void A_ClipboardFailed_handler_that_throws_cannot_take_the_application_down()
    {
        var unhandled = new List<Exception>();
        void Record(object? _, DispatcherUnhandledExceptionEventArgs e)
        {
            unhandled.Add(e.Exception);
            e.Handled = true;
        }

        var (view, _, window, unlock)= LockedView();
        var called = false;
        view.ClipboardFailed += (_, _) =>
        {
            called = true;
            throw new InvalidOperationException("from the host");
        };
        Dispatcher.UIThread.UnhandledException += Record;
        try
        {
            // After exit, because that copy is the one with no outer try to catch this instead.
            Select(view);
            MarkProcessExited(view);
            Press(view, Key.C, KeyModifiers.Control);

            Assert.That(called, Is.True, "sanity: the handler ran");
            Assert.That(unhandled, Is.Empty);
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= Record;
            unlock.Dispose();
            window.Close();
        }
    }

    // ------------------------------------------------------ the program's own requests

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [AvaloniaTest]
    public void A_program_write_reports_a_locked_clipboard()
    {
        AssertProgramRequestReports(ClipboardOperation.ProgramWrite, _ => { },
            view => view.Terminal.Write($"{Esc}]52;c;{B64("from the program")}{Bel}"));
    }

    [AvaloniaTest]
    public void A_program_clearing_the_clipboard_reports_a_locked_clipboard()
    {
        AssertProgramRequestReports(ClipboardOperation.ProgramWrite, _ => { },
            view => view.Terminal.Write($"{Esc}]52;c;{Bel}"));
    }

    [AvaloniaTest]
    public void A_program_read_reports_a_locked_clipboard()
    {
        AssertProgramRequestReports(ClipboardOperation.ProgramRead,
            view => view.Terminal.Options.ClipboardReadEnabled = true,
            view => view.Terminal.Write($"{Esc}]52;c;?{Bel}"));
    }

    [AvaloniaTest]
    public void A_program_read_the_host_has_not_enabled_reports_nothing()
    {
        // A decision, not a failure: the clipboard was never touched.
        var (view, _, window, unlock) = LockedView();
        var reported = 0;
        view.ClipboardFailed += (_, _) => reported++;
        try
        {
            view.Terminal.Write($"{Esc}]52;c;?{Bel}");
            Thread.Sleep(60);
            Dispatcher.UIThread.RunJobs();

            Assert.That(reported, Is.Zero);
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void A_program_read_is_answered_before_the_host_is_told()
    {
        // 5522 has to answer every request, so a handler must not be able to keep the program waiting.
        var (view, _, window, unlock) = LockedView();
        var responses = new List<string>();
        var answeredByThen = -1;
        view.Terminal.Options.ClipboardReadEnabled = true;
        view.Terminal.DataReceived += (_, e) => responses.Add(e.Data);
        view.ClipboardFailed += (_, _) =>
        {
            answeredByThen = responses.Count;
            throw new InvalidOperationException("from the host");
        };
        try
        {
            view.Terminal.Write($"{Esc}]5522;type=read;{B64("text/plain")}{Bel}");
            Thread.Sleep(60);
            Dispatcher.UIThread.RunJobs();

            Assert.That(answeredByThen, Is.GreaterThan(0), "the reply must already be on its way when the handler runs");
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void A_failed_program_write_does_not_stop_the_next_one()
    {
        var (view, _, window, unlock) = LockedView();
        view.ClipboardFailed += (_, _) => throw new InvalidOperationException("from the host");
        try
        {
            view.Terminal.Write($"{Esc}]52;c;{B64("lost")}{Bel}");
            Thread.Sleep(60);
            Dispatcher.UIThread.RunJobs();
        }
        finally { unlock.Dispose(); }

        try
        {
            view.Terminal.Write($"{Esc}]52;c;{B64("next")}{Bel}");
            Thread.Sleep(60);
            Dispatcher.UIThread.RunJobs();

            Assert.That(TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult(), Is.EqualTo("next"));
        }
        finally { window.Close(); }
    }

    private static void AssertProgramRequestReports(ClipboardOperation expected, Action<TerminalView> arrange, Action<TerminalView> request)
    {
        var capture = new Capture();
        var reported = new List<ClipboardFailedEventArgs>();
        var (view, _, window, unlock) = LockedView();
        view.ClipboardFailed += (_, e) => reported.Add(e);
        Trace.Listeners.Add(capture);
        try
        {
            arrange(view);
            request(view);
            Thread.Sleep(60);
            Dispatcher.UIThread.RunJobs();

            Assert.That(reported, Has.Count.EqualTo(1));
            Assert.That(reported[0].Operation, Is.EqualTo(expected));
            Assert.That(reported[0].Exception, Is.TypeOf<COMException>());
            Assert.That(capture.Text, Does.Contain($"Clipboard {expected} failed: COMException"),
                "proves the capture works, so the next line cannot pass on an empty log");
            Assert.That(capture.Text, Does.Not.Contain(QuotedText), "the failure is logged without its message");
        }
        finally
        {
            Trace.Listeners.Remove(capture);
            unlock.Dispose();
            window.Close();
        }
    }

    // ---------------------------------------------------------------- the wrappers

    [AvaloniaTest]
    public void TerminalControl_forwards_ClipboardFailed()
    {
        var control = new TerminalControl { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = control };
        window.Show();
        window.UpdateLayout();
        var reported = new List<ClipboardFailedEventArgs>();
        control.ClipboardFailed += (_, e) => reported.Add(e);
        var view = control.GetVisualDescendants().OfType<TerminalView>().Single();
        view.AttachConnection(new RecordingConnection());
        var unlock = Lock(view);
        try
        {
            RightClick(view);

            Assert.That(reported, Has.Count.EqualTo(1));
            Assert.That(reported[0].Operation, Is.EqualTo(ClipboardOperation.Paste));
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void TerminalWindow_forwards_ClipboardFailed()
    {
        var window = new TerminalWindow { Process = "" }.Realise();
        var reported = new List<ClipboardFailedEventArgs>();
        window.ClipboardFailed += (_, e) => reported.Add(e);
        var view = window.GetVisualDescendants().OfType<TerminalView>().Single();
        view.AttachConnection(new RecordingConnection());
        var unlock = Lock(view);
        try
        {
            RightClick(view);

            Assert.That(reported, Has.Count.EqualTo(1));
            Assert.That(reported[0].Operation, Is.EqualTo(ClipboardOperation.Paste));
        }
        finally { unlock.Dispose(); window.Close(); }
    }

    [AvaloniaTest]
    public void A_right_click_copy_clears_the_selection_once_it_is_on_the_clipboard()
    {
        // The other side of the test above, so that one cannot pass by never clearing at all.
        var (view, _, window) = LiveView();
        try
        {
            Select(view);

            RightClick(view);

            Assert.That(view.Terminal.Selection.HasSelection, Is.False);
            Assert.That(TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult(),
                Does.StartWith("hello"));
        }
        finally { window.Close(); }
    }
}
