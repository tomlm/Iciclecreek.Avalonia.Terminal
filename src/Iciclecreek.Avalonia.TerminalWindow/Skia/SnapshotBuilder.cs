using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Media;
using XTerm.Buffer;
using XTerm.Common;
using Iciclecreek.Avalonia.Terminal;   // BufferCellExtensions, GraphemeRuns

namespace Iciclecreek.Terminal.Skia
{
    /// <summary>
    /// Gathers the visible rows for a frame, on the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>Every read of the terminal buffer and of the palette happens here, where the rest of the
    /// control already reads them. The draw operation that consumes the result runs on the render
    /// thread and never touches either: a pty read loop writing to the buffer during a composite
    /// would otherwise be a race with no lock to take.</para>
    ///
    /// <para><b>Unchanged lines are not rebuilt.</b> The first version copied the whole viewport every
    /// frame, and measured SLOWER end to end than the renderer it replaced — the draw calls it saved
    /// did not pay for the copy, because on any real screen most lines are the same as last frame.
    /// A row is kept on its line through <c>BufferLine.Cache</c>, which every write path already
    /// clears, so a line that changed arrives with no row and one that did not costs a pointer.</para>
    /// </remarks>
    internal sealed class SnapshotBuilder
    {
        /// <summary>
        /// The most headers kept for reuse.
        ///
        /// Only the header is pooled — the rows it points at belong to their lines. A header comes
        /// back when the frame using it RETIRES, which Avalonia signals by disposing the operation
        /// (see TerminalSkiaLayer.Dispose), so the free list settles at the depth the compositor
        /// actually runs at rather than a number guessed here. This cap only bounds what a
        /// pathological run can retain; past it a header is not pooled, which costs an allocation
        /// and never a shared header.
        ///
        /// Headers rotated on a counter three deep before, which is the same bet made blind. A
        /// fourth frame assembled before the first retired overwrote a header the render thread was
        /// still reading — and by then the other half of that frame, the rows the classic path drew
        /// from <c>Deferred</c>, had already been recorded into the display list against the OLD
        /// contents. The two halves disagree, rows draw twice or not at all, the next unrelated
        /// repaint clears it, and it reproduces on nobody's machine.
        /// </summary>
        private const int MaxPooled = 8;

        private readonly Stack<TerminalSnapshot> _free = new();

        /// <summary>
        /// Stamped on every header and captured by the operation that will draw it. Only ever
        /// incremented here, on the UI thread; the render thread only reads.
        /// </summary>
        private long _frameId;

        public TerminalSnapshot Build(
            XTerm.Terminal terminal,
            ColorSnapshot palette,
            int startLine,
            int rows,
            int cols,
            double cellWidth,
            double cellHeight,
            double fontSize,
            string fontFamily,
            IBrush? defaultForeground,
            IBrush? defaultBackground,
            Action requestPaint,
            bool ligatures,
            bool reverseVideo,
            bool blinkOn,
            bool boldIsBright,
            MinimumContrast? minimumContrast)
        {
            TerminalSnapshot snapshot;
            lock (_free)
                snapshot = _free.Count > 0 ? _free.Pop() : new TerminalSnapshot();

            snapshot.FrameId = ++_frameId;

            snapshot.EnsureCapacity(rows, cols);
            snapshot.CellWidth = cellWidth;
            snapshot.CellHeight = cellHeight;
            snapshot.FontSize = fontSize;
            snapshot.FontFamily = fontFamily;
            snapshot.Ligatures = ligatures;
            snapshot.Surface = ToArgb(defaultBackground) ?? Argb(BufferCellExtensions.FromRgb(palette.Background));

            var fallbackForeground = ToArgb(defaultForeground) ?? Argb(BufferCellExtensions.FromRgb(palette.Foreground));

            for (var row = 0; row < rows; row++)
            {
                var absolute = startLine + row;
                var line = absolute >= 0 && absolute < terminal.Buffer.Length
                    ? terminal.Buffer.GetLine(absolute)
                    : null;

                if (line is null)
                {
                    snapshot.Rows[row] = null;
                    continue;
                }

                // Still good? Then it is the same pixels, wherever on screen the line has moved to.
                //
                // Except under DECSCNM, where the cache is bypassed in BOTH directions -- the same
                // rule the classic path follows (see the cache read in CollectLineRuns and the
                // refusal to store beside it). Reverse video is a whole-screen state that no line
                // write announces, so a row built before it was set would stay un-inverted for as
                // long as the line went untouched, and one built during it would stay inverted
                // after it was cleared.
                if (!reverseVideo && line.Cache is SnapshotRow cachedRow && cachedRow.Cols == cols)
                {
                    snapshot.Rows[row] = cachedRow;
                    continue;
                }

                // A NEW row, never a rewrite of the cached one: the render thread may be reading that
                // one right now, and a row mutated mid-composite draws as half of two frames.
                // A line the direct path cannot express goes to the classic renderer instead of
                // being drawn wrong: a doubled row (DECDWL/DECDHL) has a transform this snapshot has
                // no field for, and a line carrying OSC 66 sized runs is painted by the deferred
                // block pass. Null here means "not mine"; TerminalView draws exactly these rows.
                // Pictures go to the classic renderer too, and for more reasons than the two
                // above put together: it plans a blit from the placement's pixel offsets and its
                // per-cell scale, clips a partial edge cell instead of stretching it, re-uploads
                // when an animation bumps its frame serial, suppresses the cell background under a
                // backdrop placement, and skips the text a Sixel replaced. Reproducing all of that
                // here would be a second implementation of the hardest part of the renderer, to
                // speed up screens whose cost is a blit rather than glyphs.
                if (line.HasImages || line.HasSizedRuns || line.LineAttribute != LineAttribute.Normal)
                {
                    snapshot.Rows[row] = null;
                    snapshot.Deferred[row] = true;
                    continue;
                }

                // The same stamp-and-retry CollectLineRuns uses, including its retry count: the
                // line is read without a lock, and a writer that clears Cache mid-read must not
                // have its invalidation overwritten by the row this read produced -- that would
                // cache a mixture of the old line and the new one, permanently. Bounded, because
                // unbounded retries let a line being written continuously stall the frame.
                const int Attempts = 3;

                for (var attempt = 1; ; attempt++)
                {
                    var stamp = new object();
                    line.Cache = stamp;

                    var built = new SnapshotRow(cols);
                    Fill(built, line, palette, cols, fallbackForeground, snapshot.Surface,
                         reverseVideo, blinkOn, boldIsBright, minimumContrast);

                    if (!ReferenceEquals(line.Cache, stamp))
                    {
                        if (attempt < Attempts)
                            continue;

                        // Out of attempts: draw what this read produced, cache nothing, and ask for
                        // another frame ourselves rather than trusting the writer to do it.
                        line.Cache = null;
                        snapshot.Rows[row] = built;
                        requestPaint();
                        break;
                    }

                    // Not stored while the screen is inverted, for the reason above.
                    line.Cache = reverseVideo ? null : built;
                    snapshot.Rows[row] = built;
                    break;
                }
            }

            return snapshot;
        }

        /// <summary>
        /// Takes a header back, once the frame that used it has retired.
        /// </summary>
        /// <remarks>
        /// Called from TerminalSkiaLayer.Dispose, which Avalonia runs on the RENDER thread when the
        /// frame leaves the scene — the same signal the layer already relies on to free its shaping
        /// buffer. <see cref="Build"/> rents on the UI thread, which is what the lock is for.
        ///
        /// A header Avalonia never disposes is simply never reused: the next Build allocates a
        /// replacement and the free list stabilises one deeper. Losing a header costs an allocation;
        /// reusing one too early costs a torn frame, so the asymmetry is deliberate.
        /// </remarks>
        public void Return(TerminalSnapshot snapshot)
        {
            // The rows stay on their lines, where the cache keeps them. Dropping the pointers here
            // lets a retired frame's rows be collected once their lines have moved on, instead of
            // being held alive by a header sitting in the free list.
            if (snapshot.RowCount > 0)
                Array.Clear(snapshot.Rows, 0, Math.Min(snapshot.RowCount, snapshot.Rows.Length));

            snapshot.FrameId = 0;

            lock (_free)
            {
                if (_free.Count < MaxPooled)
                    _free.Push(snapshot);
            }
        }

        private static void Fill(SnapshotRow row, BufferLine line, ColorSnapshot palette,
                                 int cols, uint fallbackForeground, uint surface,
                                 bool reverseVideo, bool blinkOn, bool boldIsBright,
                                 MinimumContrast? minimumContrast)
        {
            var col = 0;
            while (col < cols)
            {
                ref var target = ref row.Cells[col];

                if (col >= line.Length)
                {
                    target = default;
                    target.Width = 1;
                    target.ClusterIndex = -1;
                    target.ImageIndex = -1;
                    target.UnderlineColorIndex = 0xFF;
                    target.Foreground = fallbackForeground;
                    col++;
                    continue;
                }

                var cell = line[col];

                var foreground = cell.GetForegroundColor(palette, boldIsBright) is { } fg ? Argb(fg) : fallbackForeground;
                var background = cell.GetBackgroundColor(palette) is { } bg ? Argb(bg) : 0u;

                // The same two swaps the classic path applies, in the same order: a cell's own
                // inverse, then DECSCNM for the whole screen. Each is its own toggle -- the two can
                // cancel -- so they are counted rather than short-circuited. Blink is NOT among
                // them: it hides the glyph rather than exchanging colours, below with conceal.
                var swapped = cell.Attributes.IsInverse();
                if (reverseVideo) swapped = !swapped;

                if (swapped)
                {
                    // Once swapped the background is no longer optional: what it paints is the text
                    // colour, so a cell with no background of its own takes the default one.
                    //
                    // The PALETTE's default, not the surface actually painted. Under DECSCNM the
                    // surface is itself inverted, so swapping against it inverts a second time and
                    // the glyph comes out the same colour as the fill behind it -- an inverted
                    // screen rendered as a blank sheet, every character invisible.
                    var behind = background == 0
                        ? Argb(BufferCellExtensions.FromRgb(palette.Background))
                        : background;
                    (foreground, background) = (behind, foreground);
                }
                else if (reverseVideo && background == 0)
                {
                    // The cancelled double swap: the cell inverted, the screen inverted, and the two
                    // came out even -- so this cell keeps the ORDINARY default background while the
                    // surface behind it is the inverted one. A zero here means "leave the surface
                    // alone", which would draw the glyph in the normal foreground on an inverted
                    // sheet. Naming the colour is what keeps the cell visible.
                    background = Argb(BufferCellExtensions.FromRgb(palette.Background));
                }

                // The readability floor, applied where the classic path applies it -- after the
                // swaps, against the colour actually behind the glyph, so an inverse cell is
                // measured against what it really sits on -- and skipped where the classic path
                // skips it: a fully transparent backdrop has no single colour to measure against,
                // and the box-drawing and powerline runs are exempt because their glyphs join into
                // shapes with their neighbours and a nudged shade breaks the join mid-line.
                // (A run over a picture cannot arise here: lines holding one are declined above.)
                if (minimumContrast is { Active: true })
                {
                    var behind = background == 0 ? surface : background;
                    if ((behind >> 24) == 0xFF && !MinimumContrast.IsExemptRun(cell.Content))
                        foreground = Argb(minimumContrast.Apply(FromArgb(foreground), FromArgb(behind)));
                }

                target.CodePoint = cell.CodePoint;
                target.Foreground = foreground;
                target.Background = background;
                target.Width = (byte)Math.Clamp(cell.Width, 0, 2);
                target.Flags = FlagsFor(cell);

                // The off half of the blink phase rides the conceal flag rather than one of its own:
                // "draw the cell but not its glyph or its decorations" is exactly what conceal
                // already means to the layer, and the two never need telling apart downstream.
                if (!blinkOn && cell.Attributes.IsBlink())
                    target.Flags |= SnapshotFlags.Conceal;

                // A cell whose text is more than its codepoint — combining marks, a ZWJ sequence.
                // Rare, so it costs a string only when it happens.
                var content = cell.Content;
                var span = Math.Max(1, (int)target.Width);

                // A joined emoji sequence lives across SEVERAL cells: the emulator tacks the joiner
                // onto every component but the last, which is right — the cursor really did advance
                // that many columns. But a shaper handed one component cannot form the ligature, so
                // the family drew as three separate people. Gather the continuation cells into one
                // cluster, and blank the cells they occupied so nothing draws over the ligature.
                if (GraphemeRuns.ContinuesIntoNextCell(content))
                {
                    var joined = new StringBuilder(content);
                    var probe = col + span;

                    while (joined.Length > 0
                           && joined[joined.Length - 1] == GraphemeRuns.ZeroWidthJoiner
                           && probe < cols && probe < line.Length)
                    {
                        var next = line[probe];

                        // A colour change mid-sequence breaks it, rather than silently recolouring
                        // the whole ligature to match its first component.
                        if (next.Attributes != cell.Attributes)
                            break;

                        joined.Append(next.Content);
                        probe += Math.Max(1, (int)next.Width);
                    }

                    content = joined.ToString();
                    span = probe - col;
                    target.Width = (byte)Math.Min(byte.MaxValue, span);
                }

                // Resolved here, on the UI thread, where the palette is safe to read — the same
                // reason the foreground and background are.
                target.UnderlineStyle = (byte)cell.Attributes.GetUnderlineStyle();
                target.UnderlineColorIndex = 0xFF;

                if (target.UnderlineStyle != 0 && cell.GetUnderlineColor(palette) is { } underlineColor)
                    target.UnderlineColorIndex = row.AddUnderlineColor(Argb(underlineColor));

                target.ClusterIndex = NeedsCluster(content, cell.CodePoint)
                    ? row.AddCluster(content)
                    : -1;

                // Pictures are not read per cell any more — they are runs on the line, gathered once
                // below. A cell under one is an ordinary space and needs nothing here.
                target.ImageIndex = -1;

                // Everything the glyph covers past its first column is a spacer.
                for (var filled = 1; filled < span && col + filled < cols; filled++)
                {
                    ref var spacer = ref row.Cells[col + filled];
                    spacer = default;
                    spacer.ClusterIndex = -1;
                    spacer.ImageIndex = -1;
                    spacer.UnderlineColorIndex = 0xFF;
                    spacer.Background = target.Background;
                }

                col += span;
            }

            // No picture runs are collected here. A line holding one is declined above and drawn by
            // the classic renderer; see the note there for why its geometry is not worth a second
            // implementation.
        }

        /// <summary>True when the cell's text is not simply its codepoint rendered.</summary>
        private static bool NeedsCluster(string content, int codePoint)
        {
            if (string.IsNullOrEmpty(content))
                return false;

            var expected = codePoint <= 0xFFFF ? 1 : 2;
            return content.Length != expected;
        }

        private static SnapshotFlags FlagsFor(BufferCell cell)
        {
            var flags = SnapshotFlags.None;
            if (cell.Attributes.IsBold()) flags |= SnapshotFlags.Bold;
            if (cell.Attributes.IsInvisible()) flags |= SnapshotFlags.Conceal;
            if (cell.Attributes.IsDim()) flags |= SnapshotFlags.Dim;
            if (cell.Attributes.IsOverline()) flags |= SnapshotFlags.Overline;
            if (cell.Attributes.IsItalic()) flags |= SnapshotFlags.Italic;
            if (cell.Attributes.IsUnderline()) flags |= SnapshotFlags.Underline;
            if (cell.Attributes.IsStrikethrough()) flags |= SnapshotFlags.Strikethrough;
            return flags;
        }

        /// <summary>The inverse of <see cref="Argb(Color)"/>, for handing a packed colour back to a
        /// helper that works in Avalonia colours -- the contrast floor being the one that does.</summary>
        private static Color FromArgb(uint argb) =>
            Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        private static uint Argb(Color color) =>
            ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

        private static uint? ToArgb(IBrush? brush) =>
            brush is ISolidColorBrush solid ? Argb(solid.Color) : null;
    }
}
