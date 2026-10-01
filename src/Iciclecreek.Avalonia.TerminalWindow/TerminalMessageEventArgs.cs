using System;

namespace Iciclecreek.Terminal
{
    /// <summary>Which of the view's own lines is about to be written.</summary>
    public enum TerminalMessageKind
    {
        /// <summary>Reading the process's output failed: "Error reading from process: ...".</summary>
        ReadError,

        /// <summary>The process ended: "Process exited with code: N", or "Process exited" when the code is unreadable.</summary>
        ProcessExited,
    }

    /// <summary>
    /// EventArgs for <see cref="TerminalView.WritingMessage"/>: a line the view is about to write into the
    /// terminal on its own behalf, which the host may reword or suppress.
    /// </summary>
    /// <remarks>
    /// <para>Everything else in the buffer came from the process. These lines are the view speaking, and a
    /// host often has a better place for what they say -- a status bar, a window title, its own log -- or a
    /// better judgement of whether it is worth saying at all. Before this event the only way to keep them out
    /// of the buffer was to clear it afterwards, racing the line it meant to remove.</para>
    /// <para>Leaving the event alone keeps the default exactly: <see cref="Text"/> starts as the line the view
    /// has always written, and it is written unless a handler sets <see cref="Handled"/>.</para>
    /// </remarks>
    public sealed class TerminalMessageEventArgs : EventArgs
    {
        private string _text;

        private TerminalMessageEventArgs(TerminalMessageKind kind, string defaultText, Exception? exception, int? exitCode, long sessionId)
        {
            Kind = kind;
            DefaultText = defaultText;
            _text = defaultText;
            Exception = exception;
            ExitCode = exitCode;
            SessionId = sessionId;
        }

        public TerminalMessageKind Kind { get; }

        /// <summary>What went wrong, for <see cref="TerminalMessageKind.ReadError"/>; null otherwise.</summary>
        public Exception? Exception { get; }

        /// <summary>
        /// The code the process returned, for <see cref="TerminalMessageKind.ProcessExited"/>. Null when the
        /// process is known to have ended but its status could not be read -- the same case as
        /// <see cref="ProcessExitedEventArgs.ExitCodeKnown"/> false -- and always null for a read error.
        /// </summary>
        public int? ExitCode { get; }

        /// <summary>The pty session the line is about. See <see cref="ProcessExitedEventArgs.SessionId"/>.</summary>
        public long SessionId { get; }

        /// <summary>The line the view writes when nobody intervenes, newlines included.</summary>
        public string DefaultText { get; }

        /// <summary>
        /// What will be written, starting as <see cref="DefaultText"/>. Passed to the emulator as-is, so it
        /// carries its own newlines: the defaults begin and end with one, which is what keeps the line off
        /// whatever the process left on its last row.
        /// </summary>
        /// <remarks>Null is taken as empty rather than thrown on; suppressing is what <see cref="Handled"/> is for.</remarks>
        public string Text
        {
            get => _text;
            set => _text = value ?? string.Empty;
        }

        /// <summary>True to write nothing at all.</summary>
        public bool Handled { get; set; }

        /// <summary>The line for a failed read of the process's output.</summary>
        public static TerminalMessageEventArgs ReadError(Exception exception, long sessionId = 0) =>
            new(TerminalMessageKind.ReadError, $"\nError reading from process: {exception.Message}\n", exception, null, sessionId);

        /// <summary>The line for a process that ended. Pass null when its exit code could not be read.</summary>
        public static TerminalMessageEventArgs ProcessExited(int? exitCode, long sessionId = 0) =>
            new(TerminalMessageKind.ProcessExited,
                exitCode is { } code ? $"\nProcess exited with code: {code}\n" : "\nProcess exited\n",
                null, exitCode, sessionId);
    }
}
