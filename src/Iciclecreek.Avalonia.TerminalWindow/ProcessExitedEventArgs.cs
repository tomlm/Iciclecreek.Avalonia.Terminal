using System;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// EventArgs for the ProcessExited event.
    /// </summary>
    public class ProcessExitedEventArgs : EventArgs
    {
        public int ExitCode { get; }

        /// <summary>
        /// False when the process is known to have ended but its status could not be read — the child never
        /// reaped. <see cref="ExitCode"/> is 0 in that case and means NOTHING; treat it as "exited, outcome
        /// unknown" rather than as success.
        /// </summary>
        /// <remarks>
        /// Exists so that "we could not read the code" can be reported without inventing a 0, which is the one
        /// wrong answer that reads as SUCCESS. Additive: every existing construction sets it true, so a
        /// consumer that never checks it behaves exactly as before.
        /// </remarks>
        public bool ExitCodeKnown { get; }

        /// <summary>
        /// Identifies the pty session that exited. Compare with <c>TerminalView.SessionId</c> to tell
        /// whether this is the process the view is hosting now — the exit is raised on the UI thread, so
        /// a replacement can be installed between the death and the notification.
        /// </summary>
        /// <remarks>0 means "no session recorded".</remarks>
        public long SessionId { get; init; }

        public ProcessExitedEventArgs(int exitCode)
        {
            ExitCode = exitCode;
            ExitCodeKnown = true;
        }

        private ProcessExitedEventArgs()
        {
            ExitCode = 0;
            ExitCodeKnown = false;
        }

        /// <summary>The process ended but its status was unreadable. See <see cref="ExitCodeKnown"/>.</summary>
        public static ProcessExitedEventArgs UnknownCode() => new ProcessExitedEventArgs();

        /// <summary>
        /// The process ended but its status was unreadable, carrying the session it belonged to.
        /// See <see cref="ExitCodeKnown"/> and <see cref="SessionId"/>.
        /// </summary>
        public static ProcessExitedEventArgs UnknownCode(long sessionId) =>
            new ProcessExitedEventArgs { SessionId = sessionId };
    }
}
