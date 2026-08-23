using System;
using System.Text;
using XTerm.Buffer;

namespace Iciclecreek.Avalonia.Terminal
{
    /// <summary>
    /// Keeps a ZWJ emoji sequence in ONE draw run so the shaper can ligate it.
    ///
    /// <para>The emulator stores a joined sequence across several cells, tacking the joiner onto the end of
    /// every component but the last. A family (👨‍👩‍👧) lands as:</para>
    /// <code>
    ///   [0] w2  U+1F468 U+200D    [1] w0  (placeholder)
    ///   [2] w2  U+1F469 U+200D    [3] w0  (placeholder)
    ///   [4] w2  U+1F467          [5] w0  (placeholder)
    /// </code>
    /// <para>That is the correct thing for the emulator to do — the cursor really did advance six columns, and
    /// the application laid out the rest of its screen on that basis. But the renderer used to draw each
    /// wide cell as its own <c>FormattedText</c>, so HarfBuzz never saw the sequence whole, the ligature
    /// could not form, and the reader got three separate people instead of one family.</para>
    ///
    /// <para>Nothing is lost in the buffer, so this is purely a run-building concern: gather the continuation
    /// cells into one string and hand the shaper the intact cluster. Skin-tone modifiers, variation selectors
    /// and combining marks need none of this — the emulator already merges those into a single cell.</para>
    ///
    /// <para>The column span deliberately keeps every cell consumed. Drawing the ligature into the wider box
    /// leaves some slack after the glyph, which is right: narrowing it would shift everything after it on the
    /// line out of step with what the application thinks it drew.</para>
    /// </summary>
    internal static class GraphemeRuns
    {
        /// <summary>U+200D, the joiner that says "this cluster continues in the next cell".</summary>
        public const char ZeroWidthJoiner = '\u200D';

        /// <summary>Whether the run collected so far ends in a joiner and therefore continues.</summary>
        public static bool ContinuesIntoNextCell(string? text) =>
            !string.IsNullOrEmpty(text) && text[text.Length - 1] == ZeroWidthJoiner;

        /// <summary>
        /// Extend <paramref name="text"/> with any cells that a trailing joiner pulls in, advancing
        /// <paramref name="x"/> and <paramref name="cellCount"/> over everything consumed.
        /// </summary>
        /// <param name="line">The buffer line being rendered.</param>
        /// <param name="cols">The terminal width, so the walk stops at the right edge.</param>
        /// <param name="first">The cell the run started at — continuation cells must share its attributes,
        /// so a colour change mid-sequence still breaks the run rather than silently recolouring it.</param>
        /// <param name="text">The run text collected so far.</param>
        /// <param name="x">Cursor into the line; advanced past every absorbed cell.</param>
        /// <param name="cellCount">Columns consumed by the run; grown by every absorbed cell.</param>
        /// <returns>The run text, extended if the cluster continued.</returns>
        public static string AbsorbJoinedCells(
            BufferLine line, int cols, BufferCell first, string text, ref int x, ref int cellCount)
        {
            if (!ContinuesIntoNextCell(text)) return text;

            var builder = new StringBuilder(text);
            while (builder.Length > 0 && builder[builder.Length - 1] == ZeroWidthJoiner
                   && x < line.Length && x < cols)
            {
                var next = line[x];

                // A width-0 cell is the placeholder trailing a wide character and carries no content of its
                // own; a differently-attributed cell is a different run however the codepoints read.
                if (next.Width < 1 || next.Attributes != first.Attributes) break;

                builder.Append(next.Content);
                cellCount += next.Width;
                x += next.Width;
            }

            return builder.ToString();
        }
    }
}
