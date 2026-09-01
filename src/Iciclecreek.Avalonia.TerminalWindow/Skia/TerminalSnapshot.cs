using System;

namespace Iciclecreek.Terminal.Skia
{
    /// <summary>
    /// One cell, flattened to what drawing needs and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately blittable. The renderer walks tens of thousands of these per frame, and a
    /// reference field would put a GC write barrier on every one — the same reason BufferCell in the
    /// emulator stopped carrying its text as a string. Multi-codepoint clusters are the exception and
    /// live in the row's own string array, indexed only when <see cref="ClusterIndex"/> says so.
    /// </remarks>
    internal struct SnapshotCell
    {
        public int CodePoint;

        /// <summary>0xAARRGGBB. Resolved on the UI thread, where the palette is safe to read.</summary>
        public uint Foreground;

        /// <summary>0xAARRGGBB, or 0 for "leave the surface alone".</summary>
        public uint Background;

        /// <summary>Cells this glyph occupies: 1, 2 for a wide glyph, or 0 for its trailing spacer.</summary>
        public byte Width;

        public SnapshotFlags Flags;

        /// <summary>How the underline is drawn — <see cref="XTerm.Common.UnderlineStyle"/> as a byte.</summary>
        /// <remarks>
        /// A byte, and the colour below is an index rather than a colour, so both land in the padding
        /// that already sat after <see cref="Width"/> and <see cref="Flags"/>. The snapshot cell does
        /// not grow, which matters because the render thread walks tens of thousands of them a frame.
        /// </remarks>
        public byte UnderlineStyle;

        /// <summary>Index into the row's underline colours, or 0xFF when the underline follows the text.</summary>
        public byte UnderlineColorIndex;

        /// <summary>Index into the row's cluster texts, or -1 when <see cref="CodePoint"/> says everything.</summary>
        public int ClusterIndex;

        /// <summary>Kept at -1. Pictures are runs on the row now, not a property of a cell.</summary>
        public int ImageIndex;
    }

    /// <summary>
    /// One picture run on a row, flattened to exactly what a blit needs.
    /// </summary>
    /// <remarks>
    /// A run is already the coalesced thing, so the layer no longer walks cells looking for adjacent
    /// tiles of the same image to merge — that whole pass is gone, along with the edge-tile scaling
    /// correction it needed.
    /// </remarks>
    internal readonly struct SnapshotImageRun
    {
        public readonly int ImageIndex;
        public readonly int Column;
        public readonly int Cols;
        public readonly int SrcX, SrcY, SrcWidth, SrcHeight;

        /// <summary>
        /// The placement's z-index, carried so the draw can order as the classic path does: negative
        /// z goes under the text, non-negative over it, and equal z keeps placement order.
        /// </summary>
        public readonly int ZIndex;

        public SnapshotImageRun(int imageIndex, int column, int cols, int srcX, int srcY, int srcWidth, int srcHeight, int zIndex = 0)
        {
            ImageIndex = imageIndex;
            Column = column;
            Cols = cols;
            ZIndex = zIndex;
            SrcX = srcX;
            SrcY = srcY;
            SrcWidth = srcWidth;
            SrcHeight = srcHeight;
        }
    }

    [Flags]
    internal enum SnapshotFlags : ushort
    {
        None = 0,
        Bold = 1 << 0,
        Italic = 1 << 1,
        Underline = 1 << 2,
        Strikethrough = 1 << 3,

        /// <summary>SGR 8. The cell keeps its background and its column; the glyph is not drawn.</summary>
        Conceal = 1 << 4,

        /// <summary>SGR 2, drawn as the classic path draws it: the foreground at 40% alpha.</summary>
        Dim = 1 << 5,

        /// <summary>SGR 53.</summary>
        Overline = 1 << 6,
    }

    /// <summary>
    /// One line's cells, owned by the <see cref="XTerm.Buffer.BufferLine"/> it came from.
    /// </summary>
    /// <remarks>
    /// <para><b>Treated as immutable once published.</b> A line whose content changes gets a NEW row
    /// rather than having this one overwritten, because the render thread may be reading it: a row
    /// mutated in place mid-composite draws as a line half from one frame and half from the next.
    /// Allocating on change is the cost of that safety, and change is the uncommon case — a screen
    /// that scrolls replaces a line or two per frame and leaves the rest alone.</para>
    ///
    /// <para>Kept on the line through <c>BufferLine.Cache</c>, which every write path already clears,
    /// so invalidation needs no bookkeeping here and a row follows its line as the line scrolls.</para>
    /// </remarks>
    internal sealed class SnapshotRow
    {
        public SnapshotCell[] Cells;
        public string[] ClusterTexts;
        public int ClusterCount;

        /// <summary>
        /// The images this row's cells show, resolved on the UI thread.
        ///
        /// Holding the reference here does two jobs at once: the render thread never touches the
        /// registry (whose weak entries it would race), and an image stays alive for as long as any
        /// frame in flight still points at it — the row is what the snapshot pool retains.
        /// </summary>
        public XTerm.Graphics.TerminalImage[] Images = Array.Empty<XTerm.Graphics.TerminalImage>();
        public int ImageCount;

        /// <summary>
        /// The underline colours this row uses, resolved on the UI thread where the palette is safe
        /// to read. A row has one or two; a cell refers to one by index.
        /// </summary>
        public uint[] UnderlineColors = Array.Empty<uint>();
        public int UnderlineColorCount;

        /// <summary>Index for a colour, adding it if this row has not used it yet.</summary>
        public byte AddUnderlineColor(uint color)
        {
            for (var i = 0; i < UnderlineColorCount; i++)
            {
                if (UnderlineColors[i] == color)
                    return (byte)i;
            }

            // 255 is the "no colour" marker, so that many distinct colours on ONE row is where this
            // stops counting. Falling back to the last is invisible in practice and keeps the index
            // a byte.
            if (UnderlineColorCount >= 255)
                return (byte)(UnderlineColorCount - 1);

            if (UnderlineColors.Length == UnderlineColorCount)
                Array.Resize(ref UnderlineColors, Math.Max(2, UnderlineColorCount * 2));

            UnderlineColors[UnderlineColorCount] = color;
            return (byte)UnderlineColorCount++;
        }

        /// <summary>The picture runs on this row, in the order they were placed.</summary>
        public SnapshotImageRun[] Runs = Array.Empty<SnapshotImageRun>();
        public int RunCount;

        public void AddRun(SnapshotImageRun run)
        {
            if (Runs.Length == RunCount)
                Array.Resize(ref Runs, Math.Max(2, RunCount * 2));

            Runs[RunCount++] = run;
        }

        /// <summary>Columns this row was built for; a resize makes it stale.</summary>
        public int Cols;

        public SnapshotRow(int cols)
        {
            Cells = new SnapshotCell[cols];
            ClusterTexts = Array.Empty<string>();
            Cols = cols;
        }

        public int AddImage(XTerm.Graphics.TerminalImage image)
        {
            for (var i = 0; i < ImageCount; i++)
            {
                if (ReferenceEquals(Images[i], image))
                    return i;
            }

            if (Images.Length == ImageCount)
                Array.Resize(ref Images, Math.Max(2, ImageCount * 2));

            Images[ImageCount] = image;
            return ImageCount++;
        }

        public int AddCluster(string text)
        {
            if (ClusterTexts.Length == ClusterCount)
                Array.Resize(ref ClusterTexts, Math.Max(4, ClusterCount * 2));

            ClusterTexts[ClusterCount] = text;
            return ClusterCount++;
        }
    }

    /// <summary>
    /// A frame's visible rows, gathered on the UI thread for the render thread to draw.
    /// </summary>
    /// <remarks>
    /// <para>This exists because of where the two halves run. A custom draw operation's Render happens
    /// during compositing, on the render thread, while the pty read loop is writing to the terminal
    /// buffer under its own lock. Reading the buffer from inside the operation would be a race with
    /// no lock to take — so the buffer is read on the UI thread, where the rest of the control already
    /// reads it, and the operation only ever sees this.</para>
    ///
    /// <para>It holds REFERENCES to per-line rows, not copies of their cells. Copying a whole viewport
    /// every frame was measurably worse end to end than the path it replaced: the draw calls it saved
    /// did not pay for the copy, because most lines had not changed. Referencing rows means an
    /// unchanged line costs one pointer.</para>
    /// </remarks>
    internal sealed class TerminalSnapshot
    {
        public SnapshotRow?[] Rows = Array.Empty<SnapshotRow?>();
        public int RowCount;
        public int Cols;

        public double CellWidth;
        public double CellHeight;
        public double FontSize;
        public string FontFamily = "monospace";

        /// <summary>Whether to draw the font's ligatures. See <c>TerminalView.Ligatures</c>.</summary>
        public bool Ligatures = true;

        /// <summary>
        /// The display's render scaling, so this path can snap to the device pixel grid the way the
        /// classic one does. Without it the grid and the overlays that still draw over it --
        /// selection, search highlight, cursor -- disagree by up to a pixel at fractional scales,
        /// and the disagreement grows down the screen.
        /// </summary>
        public double RenderScale = 1.0;

        /// <summary>The surface colour behind the grid, as resolved for the frame. Never painted
        /// here -- TerminalView.Render has already filled it -- but inverse cells resolve against
        /// it, so the value has to travel with the frame.</summary>
        public uint Surface;

        /// <summary>
        /// Rows this snapshot declined, which the classic renderer draws instead: a doubled row
        /// (DECDWL/DECDHL) whose transform has no field here, or a line carrying OSC 66 sized runs,
        /// which the deferred block pass owns. Indexed by screen row.
        /// </summary>
        public bool[] Deferred = new bool[0];

        /// <summary>Whether the classic path should draw <paramref name="screenRow"/> itself.</summary>
        public bool IsDeferred(int screenRow) =>
            screenRow >= 0 && screenRow < RowCount && screenRow < Deferred.Length && Deferred[screenRow];

        public void EnsureCapacity(int rows, int cols)
        {
            if (Rows.Length < rows)
                Rows = new SnapshotRow?[rows];

            if (Deferred.Length < rows)
                Deferred = new bool[rows];
            else
                Array.Clear(Deferred, 0, rows);

            RowCount = rows;
            Cols = cols;
        }
    }
}
