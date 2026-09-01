using System;
using System.Text;

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
        private readonly ReadOnlyMemory<byte> _bytes;
        private string? _output;

        /// <summary>
        /// Gets the output chunk exactly as it came off the pty, undecoded.
        /// </summary>
        /// <remarks>
        /// The bytes are the authoritative form, and the reason this type carries them rather than only
        /// text: a pty read ends wherever it ends, so a chunk boundary routinely falls in the middle of a
        /// multi-byte character. Decoding each chunk on its own turns that character into replacement
        /// characters — the terminal itself used to have this bug and no longer does, because its parser
        /// carries the partial sequence into the next chunk. A consumer that cares about text across chunk
        /// boundaries needs to do the same, with a stateful <see cref="System.Text.Decoder"/> over these
        /// bytes rather than with <see cref="Output"/>.
        /// </remarks>
        public ReadOnlyMemory<byte> Bytes => _bytes;

        /// <summary>
        /// Gets the output chunk decoded as UTF-8 text.
        /// </summary>
        /// <remarks>
        /// <para>Decoded on first access and cached, so a subscriber that only wants the bytes never pays
        /// for a string it will not read.</para>
        /// <para>This is the same data handed to the terminal parser, before it is interpreted — so it
        /// still contains escape sequences, and it is a chunk as it came off the pty rather than a whole
        /// line. A consumer matching on content should expect a match to be split across two chunks.</para>
        /// <para>Decodes THIS chunk in isolation, so a multi-byte character split across the chunk
        /// boundary appears here as replacement characters. Use <see cref="Bytes"/> when that matters.</para>
        /// </remarks>
        public string Output => _output ??= Encoding.UTF8.GetString(_bytes.Span);

        /// <summary>
        /// Initializes a new instance of the <see cref="OutputReceivedEventArgs"/> class.
        /// </summary>
        /// <param name="bytes">The output chunk, as it came off the pty. Must not alias a reused buffer.</param>
        public OutputReceivedEventArgs(ReadOnlyMemory<byte> bytes)
        {
            _bytes = bytes;
        }

        /// <summary>
        /// Initializes a new instance from already-decoded text.
        /// </summary>
        /// <param name="output">The output chunk, as UTF-8 decoded text.</param>
        public OutputReceivedEventArgs(string output)
        {
            _output = output;
            _bytes = Encoding.UTF8.GetBytes(output);
        }
    }
}
