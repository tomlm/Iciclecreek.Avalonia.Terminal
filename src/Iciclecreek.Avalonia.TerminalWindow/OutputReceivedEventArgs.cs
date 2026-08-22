using System;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// Provides data for the event raised when the terminal receives output from the PTY process.
    /// </summary>
    /// <remarks>
    /// Carries a dedicated args type rather than the raw string so the payload can grow without breaking
    /// subscribers — a byte count, a stdout/stderr distinction, or a <c>Handled</c> flag are all additive
    /// here and would each be a breaking change against <c>EventHandler&lt;string&gt;</c>.
    /// </remarks>
    public class OutputReceivedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the output chunk, as UTF-8 decoded text.
        /// </summary>
        /// <remarks>
        /// This is the same text handed to the terminal parser, before it is interpreted — so it still
        /// contains escape sequences, and it is a chunk as it came off the pty rather than a whole line.
        /// A consumer matching on content should expect a match to be split across two chunks.
        /// </remarks>
        public string Output { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="OutputReceivedEventArgs"/> class.
        /// </summary>
        /// <param name="output">The output chunk, as UTF-8 decoded text.</param>
        public OutputReceivedEventArgs(string output)
        {
            Output = output;
        }
    }
}
