using Avalonia.Interactivity;
using System;
using System.Threading;
using XT = global::XTerm;

namespace Iciclecreek.Terminal
{
    public partial class TerminalView
    {
        /// <summary>
        /// Raised on the UI thread when the shell moves between its prompt, its input line and a running
        /// command, as reported by its OSC 133 shell integration marks.
        /// </summary>
        /// <remarks>
        /// <para>Bubbles, so a handler on a <see cref="TerminalControl"/> or <see cref="TerminalWindow"/>
        /// sees it too. The transitions are the emulator's; see <see cref="XT.Common.ShellState"/>.</para>
        /// <para>The question it answers is the one a host asks before closing: "is a command running?"
        /// A shell without integration never raises it and stays <see cref="XT.Common.ShellState.Unknown"/>.</para>
        /// </remarks>
        public static readonly RoutedEvent<ShellStateChangedEventArgs> ShellStateChangedEvent =
            RoutedEvent.Register<TerminalView, ShellStateChangedEventArgs>(
                nameof(ShellStateChanged),
                RoutingStrategies.Bubble);

        /// <inheritdoc cref="ShellStateChangedEvent"/>
        public event EventHandler<ShellStateChangedEventArgs> ShellStateChanged
        {
            add => AddHandler(ShellStateChangedEvent, value);
            remove => RemoveHandler(ShellStateChangedEvent, value);
        }

        /// <summary>
        /// Where the shell is in its prompt/command cycle, as of the emulator's latest OSC 133 mark.
        /// </summary>
        /// <remarks>
        /// Read from the emulator rather than from a copy kept by <see cref="ShellStateChanged"/>, so it
        /// is current even while that event is still queued for the UI thread, and stays current across a
        /// detach, when this view is not listening. A handler wanting the state an event describes should
        /// read <see cref="ShellStateChangedEventArgs.NewState"/>.
        /// </remarks>
        public XT.Common.ShellState ShellState => _terminal?.ShellState ?? XT.Common.ShellState.Unknown;

        // Raised from Terminal.Write on the pty reader thread, so posted like every other host seam. The
        // session is read HERE, inside the write and so under _terminalLock, which is where the reader
        // checks that its chunk still belongs to the live session; a change still queued when another
        // session is installed described the old process, and is dropped rather than reported as the
        // new one's.
        private void OnTerminalShellStateChanged(object? sender, XT.Events.TerminalEvents.ShellStateChangedEventArgs e)
        {
            var session = Interlocked.Read(ref _sessionId);
            PostToHost(() =>
            {
                if (Interlocked.Read(ref _sessionId) == session)
                    RaiseEvent(new ShellStateChangedEventArgs(ShellStateChangedEvent, e.OldState, e.NewState, e.ExitCode));
            });
        }

        /// <summary>
        /// Forgets what the previous process's shell last said, once a new session is installed.
        /// </summary>
        /// <remarks>
        /// <para>The emulator outlives the process: a relaunch or an attach keeps its screen, and kept its
        /// shell state with it, so a new shell without integration was described forever by the old one's
        /// last mark -- idle from a B, or running from a C, which is exactly the answer a host closing
        /// the tab acts on. A visual detach is not a new session and keeps the state.</para>
        /// <para>Called after the session id is bumped and before the new reader (or a handover replay)
        /// can write, under _terminalLock so a write from the outgoing reader cannot interleave: one that
        /// gets the lock first is still the old session's and is reset here, one that gets it after
        /// fails the reader's session check and is never parsed.</para>
        /// </remarks>
        private void ForgetShellState()
        {
            if (_terminal is not { } terminal)
                return;

            lock (_terminalLock)
                terminal.ResetShellState();
        }
    }
}
