using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using Iciclecreek.Avalonia.Terminal;
using Porta.Pty;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XTerm.Buffer;
using XTerm.Events;
using XT = global::XTerm;

namespace Iciclecreek.Terminal
{

    public partial class TerminalView
    {

        public static readonly RoutedEvent<TitleChangedEventArgs> TitleChangedEvent =
            RoutedEvent.Register<TerminalView, TitleChangedEventArgs>(
                nameof(TitleChanged),
                RoutingStrategies.Bubble);

        public static void AddTitleChangedHandler(Interactive target, EventHandler<TitleChangedEventArgs> handler) =>
            target.AddHandler(TitleChangedEvent, handler);

        public static void RemoveTitleChangedHandler(Interactive target, EventHandler<TitleChangedEventArgs> handler) =>
            target.RemoveHandler(TitleChangedEvent, handler);

        public static readonly RoutedEvent<WindowMovedEventArgs> WindowMovedEvent =
            RoutedEvent.Register<TerminalView, WindowMovedEventArgs>(
                nameof(WindowMoved),
                RoutingStrategies.Bubble);

        public static void AddWindowMovedHandler(Interactive target, EventHandler<WindowMovedEventArgs> handler) =>
            target.AddHandler(WindowMovedEvent, handler);

        public static void RemoveWindowMovedHandler(Interactive target, EventHandler<WindowMovedEventArgs> handler) =>
            target.RemoveHandler(WindowMovedEvent, handler);

        public static readonly RoutedEvent<WindowResizedEventArgs> WindowResizedEvent =
            RoutedEvent.Register<TerminalView, WindowResizedEventArgs>(
                nameof(WindowResized),
                RoutingStrategies.Bubble);

        public static void AddWindowResizedHandler(Interactive target, EventHandler<WindowResizedEventArgs> handler) =>
            target.AddHandler(WindowResizedEvent, handler);

        public static void RemoveWindowResizedHandler(Interactive target, EventHandler<WindowResizedEventArgs> handler) =>
            target.RemoveHandler(WindowResizedEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowMinimizedEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowMinimized),
                RoutingStrategies.Bubble);

        public static void AddWindowMinimizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowMinimizedEvent, handler);

        public static void RemoveWindowMinimizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowMinimizedEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowMaximizedEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowMaximized),
                RoutingStrategies.Bubble);

        public static void AddWindowMaximizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowMaximizedEvent, handler);

        public static void RemoveWindowMaximizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowMaximizedEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowRestoredEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowRestored),
                RoutingStrategies.Bubble);

        public static void AddWindowRestoredHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowRestoredEvent, handler);

        public static void RemoveWindowRestoredHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowRestoredEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowRaisedEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowRaised),
                RoutingStrategies.Bubble);

        public static void AddWindowRaisedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowRaisedEvent, handler);

        public static void RemoveWindowRaisedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowRaisedEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowLoweredEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowLowered),
                RoutingStrategies.Bubble);

        public static void AddWindowLoweredHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowLoweredEvent, handler);

        public static void RemoveWindowLoweredHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowLoweredEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowFullscreenedEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowFullscreened),
                RoutingStrategies.Bubble);

        public static void AddWindowFullscreenedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowFullscreenedEvent, handler);

        public static void RemoveWindowFullscreenedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowFullscreenedEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> BellRangEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(BellRang),
                RoutingStrategies.Bubble);

        public static void AddBellRangHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(BellRangEvent, handler);

        public static void RemoveBellRangHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(BellRangEvent, handler);

        public static readonly RoutedEvent<WindowInfoRequestedEventArgs> WindowInfoRequestedEvent =
            RoutedEvent.Register<TerminalView, WindowInfoRequestedEventArgs>(
                nameof(WindowInfoRequested),
                RoutingStrategies.Bubble);

        public static void AddWindowInfoRequestedHandler(Interactive target, EventHandler<WindowInfoRequestedEventArgs> handler) =>
            target.AddHandler(WindowInfoRequestedEvent, handler);

        public static void RemoveWindowInfoRequestedHandler(Interactive target, EventHandler<WindowInfoRequestedEventArgs> handler) =>
            target.RemoveHandler(WindowInfoRequestedEvent, handler);

        /// <summary>
        /// A desktop notification requested by the running program (OSC 9 or Kitty OSC 99). The
        /// terminal cannot post OS notifications itself; the application decides how — or
        /// whether — to show it. Structured fields are populated for OSC 99; OSC 9 carries only
        /// <c>Text</c>.
        /// </summary>
        public static readonly RoutedEvent<TerminalNotificationEventArgs> NotificationRequestedEvent =
            RoutedEvent.Register<TerminalView, TerminalNotificationEventArgs>(
                nameof(NotificationRequested),
                RoutingStrategies.Bubble);

        public event EventHandler<TerminalNotificationEventArgs> NotificationRequested
        {
            add => AddHandler(NotificationRequestedEvent, value);
            remove => RemoveHandler(NotificationRequestedEvent, value);
        }

        /// <summary>
        /// The running program asked for the user's attention (iTerm2 OSC 1337 RequestAttention).
        /// <see cref="TerminalAttentionEventArgs.Action"/> is the request verbatim: "yes" (bounce
        /// until focused), "once", "fireworks", or "no" (cancel a pending request) — the
        /// application owns the dock/taskbar, so the application interprets it.
        /// </summary>
        public static readonly RoutedEvent<TerminalAttentionEventArgs> AttentionRequestedEvent =
            RoutedEvent.Register<TerminalView, TerminalAttentionEventArgs>(
                nameof(AttentionRequested),
                RoutingStrategies.Bubble);

        public event EventHandler<TerminalAttentionEventArgs> AttentionRequested
        {
            add => AddHandler(AttentionRequestedEvent, value);
            remove => RemoveHandler(AttentionRequestedEvent, value);
        }


        /// <summary>
        /// Event raised once when the shell produces its first output (e.g. the prompt),
        /// indicating it is ready to accept input.
        /// </summary>
        public event EventHandler? ShellReady;

        /// <summary>
        /// Event raised when the PTY process exits.
        /// </summary>
        public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

        /// <summary>
        /// Event raised for each chunk of output the terminal receives from the PTY process.
        /// </summary>
        /// <remarks>
        /// <para>Raised on the UI THREAD by default, so a handler may touch UI directly. The cost is that
        /// chunks arrive marshalled and slightly late; a consumer that needs the tightest possible latency —
        /// matching a dev server's "listening on :port" line, say — can set
        /// <see cref="OutputReceivedOnReadTask"/> and take delivery on the read task instead.</para>
        /// <para>The payload is UTF-8 decoded TEXT, not raw bytes, and it is the text as it came off the
        /// pty: escape sequences included, chunked arbitrarily rather than by line.</para>
        /// <para>A throwing handler is caught and swallowed. An unhandled exception on the dispatcher would
        /// take the application down, and sniffing output is exactly the place arbitrary host code runs. The
        /// catch wraps the whole invocation, so a handler that throws also suppresses handlers subscribed
        /// AFTER it for that chunk — isolating each would cost a <c>GetInvocationList()</c> allocation per
        /// chunk, which is the per-chunk overhead this event is otherwise careful to avoid.</para>
        /// </remarks>
        public event EventHandler<OutputReceivedEventArgs>? OutputReceived;

        /// <summary>
        /// Event raised when a URL in the terminal is Ctrl+Clicked.
        /// </summary>
        public event EventHandler<UrlClickedEventArgs>? UrlClicked;

        /// <summary>
        /// Event raised when the terminal title changes.
        /// </summary>
        public event EventHandler<TitleChangedEventArgs>? TitleChanged;

        /// <summary>
        /// Event raised when a window move command is received from the terminal.
        /// </summary>
        public event EventHandler<WindowMovedEventArgs>? WindowMoved;

        /// <summary>
        /// Event raised when a window resize command is received from the terminal.
        /// </summary>
        public event EventHandler<WindowResizedEventArgs>? WindowResized;

        /// <summary>
        /// Event raised when a window minimize command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowMinimized;

        /// <summary>
        /// Event raised when a window maximize command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowMaximized;

        /// <summary>
        /// Event raised when a window restore command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowRestored;

        /// <summary>
        /// Event raised when a window raise command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowRaised;

        /// <summary>
        /// Event raised when a window lower command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowLowered;

        /// <summary>
        /// Event raised when a window fullscreen command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowFullscreened;

        /// <summary>
        /// Event raised when the terminal bell is activated.
        /// </summary>
        public event EventHandler? BellRang;

        /// <summary>
        /// Event raised when window information is requested by the terminal.
        /// The handler should set the response properties on the event args.
        /// </summary>
        public event EventHandler<WindowInfoRequestedEventArgs>? WindowInfoRequested;


        private void OnTerminalTitleChanged(object? sender, XT.Events.TerminalEvents.TitleChangeEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {

                var args = new TitleChangedEventArgs(e.Title)
                {
                    RoutedEvent = TitleChangedEvent
                };

                RaiseEvent(args);
                TitleChanged?.Invoke(this, args);
            });
        }

        private void OnTerminalWindowMoved(object? sender, XT.Events.TerminalEvents.WindowMovedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new WindowMovedEventArgs(e.X, e.Y)
                {
                    RoutedEvent = WindowMovedEvent
                };

                RaiseEvent(args);
                WindowMoved?.Invoke(this, args);
            });
        }

        private void OnTerminalWindowResized(object? sender, XT.Events.TerminalEvents.WindowResizedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {

                var args = new WindowResizedEventArgs(e.Width, e.Height)
                {
                    RoutedEvent = WindowResizedEvent
                };

                RaiseEvent(args);
                WindowResized?.Invoke(this, args);
            });
        }

        private void OnTerminalWindowMinimized(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowMinimizedEvent);
                RaiseEvent(args);
                WindowMinimized?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalWindowMaximized(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowMaximizedEvent);
                RaiseEvent(args);
                WindowMaximized?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalWindowRestored(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowRestoredEvent);
                RaiseEvent(args);
                WindowRestored?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalWindowRaised(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowRaisedEvent);
                RaiseEvent(args);
                WindowRaised?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalWindowLowered(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowLoweredEvent);
                RaiseEvent(args);
                WindowLowered?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalWindowFullscreened(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowFullscreenedEvent);
                RaiseEvent(args);
                WindowFullscreened?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalBellRang(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(BellRangEvent);
                RaiseEvent(args);
                BellRang?.Invoke(this, EventArgs.Empty);
            });
        }

        /// <summary>
        /// Answers a program asking about the window — its size in cells or pixels, its position, or
        /// its title — from the UI thread, without letting the reader wait on it for ever.
        /// </summary>
        /// <remarks>
        /// <para>The answer genuinely needs the UI thread -- only it knows the window -- and the
        /// emulator reads <c>e.Handled</c> the moment this returns, to decide whether it has a reply
        /// to send. So it cannot be posted: Post returns immediately, Handled is still false, and the
        /// answer is written correctly, on the UI thread, after the only reader of it has moved on.
        /// Every window query then goes unanswered and the program asking waits out its timeout.</para>
        /// <para>But it cannot block for ever either. This is raised from <c>Terminal.Write</c>, which
        /// the read loop calls inside <c>lock (_terminalLock)</c> on the pty reader thread, and the UI
        /// thread takes that same lock in <see cref="ClearScreen"/>, <see cref="CurrentLineText"/> and
        /// <c>WriteOwnLine</c>. An unbounded wait is a DEADLOCK, not a stall: the application freezes
        /// with no exception to look for.</para>
        /// <para>So it is bounded. An idle UI thread answers in microseconds and the normal path is
        /// unchanged; a wedged one costs a pause instead of the session. Leaving <c>Handled</c> false
        /// on timeout is not a guess -- it is the answer the emulator already documents for a terminal
        /// that cannot say, and a wrong number would be worse than none.</para>
        /// </remarks>
        private void OnTerminalWindowInfoRequested(object? sender, XT.Events.TerminalEvents.WindowInfoRequestedEventArgs e)
        {
            // PIXEL GEOMETRY is answered here, inline, on whatever thread the query arrived on --
            // which is the PTY reader's. The emulator waits synchronously for these answers, so
            // routing them through the UI thread stalled the reader for up to WindowInfoPatience
            // whenever the UI was busy: a program that asks CSI 14/15/16 t mid-stream froze its own
            // output for a quarter of a second. The answers are the metrics PublishCellPixelSize
            // already published into Options on every layout pass -- plain int reads, safe from any
            // thread, and the SAME numbers the emulator tiles images by, which is the consistency
            // the placeholder path depends on. A host cannot meaningfully override the view's real
            // pixel metrics, so these no longer consult handlers; position, titles and state are
            // genuinely the host's to answer and keep the dispatched path below.
            switch (e.Request)
            {
                case XT.Common.WindowInfoRequest.CellSizePixels:
                    e.CellWidth = _terminal.Options.CellWidthPixels;
                    e.CellHeight = _terminal.Options.CellHeightPixels;
                    e.Handled = e.CellWidth > 0 && e.CellHeight > 0;
                    return;

                case XT.Common.WindowInfoRequest.SizePixels:
                case XT.Common.WindowInfoRequest.ScreenSizePixels:
                    e.WidthPixels = _terminal.Cols * _terminal.Options.CellWidthPixels;
                    e.HeightPixels = _terminal.Rows * _terminal.Options.CellHeightPixels;
                    e.Handled = e.WidthPixels > 0 && e.HeightPixels > 0;
                    return;
            }

            if (!Dispatcher.UIThread.CheckAccess())
            {
                // Whoever gets here first wins, and the loser does nothing.
                //
                // Timing out does not cancel the queued job -- the UI thread runs it whenever it
                // frees up, which may be long after the emulator gave up waiting and sent its
                // "cannot say". Writing the answer into e THEN is a write to state the emulator has
                // moved past, and on the next query it would find Handled already true from the
                // previous one. So the timeout claims the right to answer, and the late callback
                // finds it taken and returns.
                var claim = 0;

                var pending = Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Interlocked.Exchange(ref claim, 1) == 0)
                        AnswerWindowInfo(e);
                });

                if (!pending.GetTask().Wait(WindowInfoPatience) && Interlocked.Exchange(ref claim, 1) == 0)
                    Debug.WriteLine("[TerminalView] window-info query timed out; answering unhandled");

                return;
            }

            // Inline when this IS the UI thread, so a host driving the terminal directly neither
            // deadlocks on itself nor pays a dispatcher round trip.
            AnswerWindowInfo(e);
        }

        /// <summary>
        /// Everything the terminal sends TO the process: keystrokes, replies to its queries, focus and mouse
        /// reports.
        /// </summary>
        /// <remarks>
        /// Diagnostic. A program that behaves differently because of something the TERMINAL said is
        /// otherwise impossible to explain from the outside — the process's own output shows the decision
        /// but never the cause, and focus and mouse reports do not pass through the emulator's
        /// <c>DataReceived</c> at all, so watching that misses them entirely.
        /// </remarks>
        public event EventHandler<string>? InputSent;

        private void OnTerminalNotificationReceived(object? sender, XT.Events.TerminalEvents.NotificationEventArgs e) =>
            PostToHost(() => RaiseEvent(new TerminalNotificationEventArgs(NotificationRequestedEvent, e)));

        private void OnTerminalAttentionRequested(object? sender, XT.Events.TerminalEvents.AttentionRequestedEventArgs e) =>
            PostToHost(() => RaiseEvent(new TerminalAttentionEventArgs(AttentionRequestedEvent, e.Action)));

    }
}
