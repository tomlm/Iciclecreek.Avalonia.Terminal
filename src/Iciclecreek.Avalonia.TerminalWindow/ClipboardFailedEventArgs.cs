using System;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// The clipboard operation a key press or mouse click asked for.
    /// </summary>
    public enum ClipboardOperation
    {
        /// <summary>Copying the selection.</summary>
        Copy,

        /// <summary>Copying the selection and removing it from the shell's input.</summary>
        Cut,

        /// <summary>Pasting into the terminal.</summary>
        Paste,
    }

    /// <summary>
    /// Provides data for the event raised when a copy, cut or paste the user asked for failed.
    /// </summary>
    public class ClipboardFailedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the operation that failed.
        /// </summary>
        public ClipboardOperation Operation { get; }

        /// <summary>
        /// Gets what it failed with. On Windows this is usually a COMException, because another process
        /// had the clipboard open. Its message is free to quote the text involved, so think before logging it.
        /// </summary>
        public Exception Exception { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClipboardFailedEventArgs"/> class.
        /// </summary>
        /// <param name="operation">The operation that failed.</param>
        /// <param name="exception">What it failed with.</param>
        public ClipboardFailedEventArgs(ClipboardOperation operation, Exception exception)
        {
            Operation = operation;
            Exception = exception;
        }
    }
}
