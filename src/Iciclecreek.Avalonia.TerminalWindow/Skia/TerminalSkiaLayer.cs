using System;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Iciclecreek.Terminal.Skia
{
    /// <summary>
    /// Draws a <see cref="TerminalSnapshot"/> straight onto the leased Skia canvas.
    /// </summary>
    /// <remarks>
    /// <para>The path this replaces issues a FillRectangle and a DrawText per styled run through
    /// DrawingContext, each recording a node in a scene graph that is replayed onto Skia afterwards.
    /// When every cell carries its own colour that is thousands of calls a frame, and measurement put
    /// the whole frame's cost there: text alone cost 26.4 ms against 0.18 ms for every fill in it.
    /// Drawing onto the canvas directly reached 79 fps where the scene-graph path managed 27.</para>
    ///
    /// <para>It also moves the work off the UI thread, which may matter more than the ratio — painting
    /// stops competing with input handling.</para>
    ///
    /// <para><b>What it gives up.</b> DrawingContext supplied font fallback, text shaping and
    /// decorations; here they are this file's problem. Positions are computed per cell from the grid
    /// rather than from a text layout, which is how cell alignment is kept exact — verified against
    /// the DrawingContext path in RendererCompare, where the two agree column for column.</para>
    ///
    /// <para><b>Fallback and shaping are both handled.</b> Put side by side, colours, attributes,
    /// CJK, kana, hangul and combining marks matched from the start; a ZWJ emoji did not, because
    /// SKCanvas.DrawText maps characters to glyphs one for one and drew the family sequence as its
    /// components. Cluster text now goes through HarfBuzz, which is what DrawingContext was doing on
    /// our behalf.</para>
    /// </remarks>
    internal sealed class TerminalSkiaLayer : ICustomDrawOperation
    {
        private readonly TerminalSnapshot _snapshot;
        private readonly SkiaFontCache _fonts;

        public Rect Bounds { get; }

        public TerminalSkiaLayer(TerminalSnapshot snapshot, SkiaFontCache fonts, Rect bounds)
        {
            _snapshot = snapshot;
            _fonts = fonts;
            Bounds = bounds;
        }

        public bool HitTest(Point p) => false;
        /// <summary>
        /// Releases the shaping buffer this operation built, if it built one. Avalonia constructs a
        /// custom operation per frame and disposes it when the frame retires, so a builder kept
        /// here without this leaked one native buffer per frame on any screen showing ligatures.
        /// (The font cache is NOT disposed here -- it belongs to the view and outlives every frame.)
        /// </summary>
        public void Dispose()
        {
            _blobs?.Dispose();
            _blobs = null;
        }
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
            {
                // Not the Skia backend, so this operation can draw nothing -- and the view has
                // already skipped the classic rows on the strength of it. Say so: the view reads
                // this, gives up on the direct path for good, and repaints classically. One frame
                // of an unpainted grid, once, instead of a permanently empty terminal.
                Volatile.Write(ref _unsupported, 1);
                return;
            }

            // The cache is owned by a view that may be disposing on the UI thread right now.
            // Entering says "a composite is using this"; a cache already being disposed says no
            // and this frame draws nothing, which is the right answer for a dead terminal.
            if (!_fonts.TryEnter())
                return;

            try
            {
                using var lease = feature.Lease();
                Draw(lease.SkCanvas);
            }
            finally
            {
                _fonts.Leave();
            }
        }

        /// <summary>
        /// Set by <see cref="Render"/> when the backend would not lease a Skia canvas. Read by
        /// TerminalView on the next frame, which then abandons the direct path permanently.
        /// </summary>
        public bool Unsupported => Volatile.Read(ref _unsupported) != 0;

        private int _unsupported;

        /// <summary>
        /// To the device pixel grid, exactly as the classic path's Snap does. Without it the cell
        /// grid and the overlays still drawn over it -- selection, search highlights, the cursor,
        /// all of which ARE snapped -- disagree by up to a pixel at fractional display scales, and
        /// the disagreement accumulates down the screen.
        /// </summary>
        private static float Snap(double value, double scale) =>
            (float)(Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale);

        private void Draw(SKCanvas canvas)
        {
            var snapshot = _snapshot;
            var scale = snapshot.RenderScale;
            var cw = (float)snapshot.CellWidth;
            var ch = (float)snapshot.CellHeight;

            using var paint = new SKPaint { IsAntialias = true };

            // The surface is NOT painted here. TerminalView.Render fills it immediately before
            // enqueuing this operation, and painting it again composited a translucent host
            // background twice -- 50% alpha reading as 75% -- besides covering a gradient or image
            // brush with a flat palette colour. The snapshot still carries the resolved colour
            // because inverse cells resolve their background against it.

            // Backgrounds first, as a whole pass.
            //
            // Runs of one colour are merged along the row, which the run builder could not do because
            // its runs were cut by FOREGROUND changes too. A screen with a coloured background and
            // varying text now costs one rectangle per span rather than one per run.
            for (var rowIndex = 0; rowIndex < snapshot.RowCount; rowIndex++)
            {
                var row = snapshot.Rows[rowIndex];
                if (row is null)
                    continue;

                var y = Snap(rowIndex * ch, scale);
                var col = 0;

                while (col < snapshot.Cols)
                {
                    var background = row.Cells[col].Background;
                    var start = col;

                    while (col < snapshot.Cols && row.Cells[col].Background == background)
                        col++;

                    if (background != 0)
                    {
                        // Both edges snapped, then subtracted: snapping the width instead would let
                        // adjacent spans round apart and show a hairline of surface between them.
                        var left = Snap(start * cw, scale);
                        var right = Snap(col * cw, scale);
                        var bottom = Snap((rowIndex + 1) * ch, scale);

                        paint.Color = new SKColor(background);
                        canvas.DrawRect(left, y, right - left, bottom - y, paint);
                    }
                }
            }

            // No image pass. A line holding a picture is declined by the builder and drawn by the
            // classic renderer, which already plans the blit from the placement's pixel offsets and
            // per-cell scale, clips a partial edge cell, re-uploads on an animation's frame serial,
            // and suppresses the cell background under a backdrop. Reproducing that here would be a
            // second implementation of the subtlest part of the renderer.

            // Then glyphs.
            var baseline = _fonts.Baseline(snapshot.FontFamily, snapshot.FontSize, SnapshotFlags.None);

            // Which characters this font draws differently beside a neighbour. Probed once per face
            // and null for most fonts, and when it is null nothing below costs anything.
            var ligatureAlphabet = snapshot.Ligatures
                ? _fonts.LigatureAlphabet(snapshot.FontFamily, SnapshotFlags.None)
                : null;

            for (var rowIndex = 0; rowIndex < snapshot.RowCount; rowIndex++)
            {
                var row = snapshot.Rows[rowIndex];
                if (row is null)
                    continue;

                var y = Snap(rowIndex * ch, scale) + baseline;

                for (var col = 0; col < snapshot.Cols; col++)
                {
                    ref var cell = ref row.Cells[col];

                    if (cell.Width == 0)
                        continue;   // the spacer that follows a wide glyph

                    // A stretch of punctuation the font may draw as one picture -- an arrow, an
                    // arrow with a tail, a triple equals. Drawn from a shaped run rather than a
                    // glyph at a time, because the substitution is CONTEXTUAL: the codepoint alone
                    // maps to the plain glyph, and only the shaper knows about its neighbour.
                    if (ligatureAlphabet is not null
                        && TryDrawLigatureRun(canvas, snapshot, row, col, cw, y, paint, ligatureAlphabet, out var after))
                    {
                        col = after - 1;   // the loop's own ++ takes it to `after`
                        continue;
                    }

                    // Nothing to draw for a blank cell, and blanks are most of a real screen. The
                    // background above has already been painted; only decorations can still make an
                    // empty cell visible.
                    var blank = cell.ClusterIndex < 0 && (cell.CodePoint == 0 || cell.CodePoint == ' ');
                    // Concealed text hides its decorations too: the classic path derives the
                    // underline brush from the post-conceal (transparent) foreground and sets the
                    // strikethrough on a FormattedText drawn in it, so SGR 8 takes the lot. An
                    // underline left visible under a concealed password shows where it is.
                    var decorated = (cell.Flags & SnapshotFlags.Conceal) == 0
                        && (cell.Flags & (SnapshotFlags.Underline | SnapshotFlags.Strikethrough | SnapshotFlags.Overline)) != 0;

                    if (blank && !decorated)
                        continue;

                    paint.Color = new SKColor(cell.Foreground);

                    // SGR 8. The cell keeps its background and its column -- the classic path draws
                    // its glyph in a transparent brush -- so only the glyph is skipped here.
                    if (!blank && (cell.Flags & SnapshotFlags.Conceal) != 0)
                        blank = true;

                    if (!blank)
                    {
                        var font = _fonts.For(snapshot.FontFamily, snapshot.FontSize, cell.Flags);
                        var x = Snap(col * cw, scale);

                        // SGR 2 at the same 40% the classic path uses.
                        paint.Color = (cell.Flags & SnapshotFlags.Dim) != 0
                            ? new SKColor(cell.Foreground).WithAlpha((byte)(new SKColor(cell.Foreground).Alpha * 0.4f))
                            : new SKColor(cell.Foreground);

                        if (cell.ClusterIndex >= 0)
                            // Shaped, not drawn character by character. SKCanvas.DrawText maps
                            // characters to glyphs one for one, which turns a ZWJ sequence into its
                            // components — a family emoji came out as several boxes and a stray
                            // child. DrawingContext shaped these for us; here HarfBuzz does.
                            _fonts.DrawShaped(canvas, row.ClusterTexts[cell.ClusterIndex],
                                              x, y, snapshot.FontFamily, snapshot.FontSize, cell.Flags, paint);
                        else
                            _fonts.DrawCodepoint(canvas, cell.CodePoint, x, y, font, paint);
                    }

                    if (decorated)
                    {
                        // The underline may have a colour of its own — SGR 58 — which is the point of
                        // the feature: a red squiggle under text that stays its normal colour.
                        var underlineColor = cell.UnderlineColorIndex != 0xFF
                            ? new SKColor(row.UnderlineColors[cell.UnderlineColorIndex])
                            : new SKColor(cell.Foreground);

                        DrawDecorations(canvas, paint, cell.Flags, (XTerm.Common.UnderlineStyle)cell.UnderlineStyle,
                                        underlineColor, Snap(col * cw, scale), Snap(rowIndex * ch, scale),
                                        cw * Math.Max(1, (int)cell.Width), ch);
                    }
                }
            }
        }

        /// <summary>
        /// Whether this face redraws a character when something sits beside it.
        /// </summary>
        /// <remarks>
        /// The set comes from the FONT rather than from a guess about which characters look like they
        /// ligate — see <c>SkiaFontCache.LigatureAlphabet</c>. A hardcoded list would be wrong for any
        /// font whose ligatures involve letters, and would be a table that rots besides.
        /// </remarks>
        private static bool CanLigate(bool[] alphabet, int codePoint) =>
            (uint)codePoint < (uint)alphabet.Length && alphabet[codePoint];

        /// <summary>
        /// Draws a shaped run of punctuation starting at <paramref name="col"/>, if there is one.
        /// </summary>
        /// <remarks>
        /// <para>Glyph <c>i</c> goes in cell <c>i</c>. That is the whole trick, and it works because
        /// these fonts implement ligatures as contextual ALTERNATES rather than as substitutions:
        /// two characters stay two glyphs, each one cell wide, and the pair happens to draw as an
        /// arrow. So the grid needs no defending — see <c>SkiaFontCache.HasLigatures</c>.</para>
        /// <para>One blob for the run rather than a call per cell, so this is also slightly less work
        /// than the path it replaces for the cells it takes.</para>
        /// </remarks>
        private bool TryDrawLigatureRun(SKCanvas canvas, TerminalSnapshot snapshot, SnapshotRow row,
                                        int col, float cw, float y, SKPaint paint,
                                        bool[] alphabet, out int after)
        {
            after = col;

            ref var first = ref row.Cells[col];
            if (first.ClusterIndex >= 0 || first.Width != 1 || !CanLigate(alphabet, first.CodePoint))
                return false;

            // Concealed text never ligates. The per-cell path is where Conceal -- SGR 8, and the
            // dark half of the blink phase -- hides the glyph and its decorations, and this run
            // draws its blob and decorations without ever consulting the flag; taking the run made
            // blinking ligatured text stay lit through the off phase. One check covers the whole
            // candidate: the loop below only extends across cells whose flags EQUAL first's.
            if ((first.Flags & SnapshotFlags.Conceal) != 0)
                return false;

            // Two adjacent characters at minimum, or there is no context to substitute in.
            var end = col + 1;
            while (end < snapshot.Cols)
            {
                ref var next = ref row.Cells[end];
                if (next.ClusterIndex >= 0 || next.Width != 1 || !CanLigate(alphabet, next.CodePoint)
                    || next.Flags != first.Flags || next.Foreground != first.Foreground)
                    break;
                end++;
            }

            if (end - col < 2)
                return false;

            var text = string.Create(end - col, (row, col), static (span, state) =>
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = (char)state.row.Cells[state.col + i].CodePoint;
            });

            var glyphs = _fonts.ShapeRun(text, snapshot.FontFamily, snapshot.FontSize, first.Flags);
            if (glyphs is null)
                return false;   // not one glyph per character; the per-cell path keeps the grid

            var font = _fonts.For(snapshot.FontFamily, snapshot.FontSize, first.Flags);
            paint.Color = new SKColor(first.Foreground);

            var builder = _blobs ??= new SKTextBlobBuilder();
            var blobRun = builder.AllocateHorizontalRun(font, glyphs.Length, 0);
            glyphs.CopyTo(blobRun.Glyphs);

            var positions = blobRun.Positions;
            for (var i = 0; i < glyphs.Length; i++)
                positions[i] = (col + i) * cw;

            using var blob = builder.Build();
            canvas.DrawText(blob, 0, y, paint);

            // The decorations still belong to their own cells: a run drawn as one picture can still
            // be half underlined, because the underline is an attribute of the cell and not of the
            // glyph sitting on it.
            for (var i = col; i < end; i++)
            {
                ref var cell = ref row.Cells[i];
                if ((cell.Flags & (SnapshotFlags.Underline | SnapshotFlags.Strikethrough)) == 0)
                    continue;

                var underlineColor = cell.UnderlineColorIndex != 0xFF
                    ? new SKColor(row.UnderlineColors[cell.UnderlineColorIndex])
                    : new SKColor(cell.Foreground);

                DrawDecorations(canvas, paint, cell.Flags, (XTerm.Common.UnderlineStyle)cell.UnderlineStyle,
                                underlineColor, i * cw, y - _fonts.Baseline(snapshot.FontFamily, snapshot.FontSize, SnapshotFlags.None),
                                cw, (float)snapshot.CellHeight);
            }

            after = end;
            return true;
        }

        /// <summary>Reused across runs and frames; Build resets it, and this path is single-threaded.</summary>
        private SKTextBlobBuilder? _blobs;

        /// <summary>
        /// Underline and strikethrough, which DrawingContext drew for us and this path must not lose.
        /// </summary>
        /// <remarks>
        /// Thickness is derived from the cell so it scales with the font rather than being a constant
        /// that disappears at small sizes and looks heavy at large ones. Every style is drawn from
        /// that one thickness for the same reason: a dotted underline whose dots do not grow with the
        /// text stops reading as dotted.
        /// </remarks>
        private static void DrawDecorations(SKCanvas canvas, SKPaint paint, SnapshotFlags flags,
                                            XTerm.Common.UnderlineStyle style, SKColor underlineColor,
                                            float x, float y, float cellWidth, float cellHeight)
        {
            var thickness = Math.Max(1f, cellHeight / 14f);
            var textColor = paint.Color;

            if ((flags & SnapshotFlags.Underline) != 0)
            {
                paint.Color = underlineColor;
                var baseY = y + cellHeight - thickness * 2;

                switch (style)
                {
                    case XTerm.Common.UnderlineStyle.Double:
                        // Two lines need somewhere to sit. Drawing the second below the first would
                        // fall out of the cell, so the pair straddles where the single one goes.
                        canvas.DrawRect(x, baseY - thickness, cellWidth, thickness, paint);
                        canvas.DrawRect(x, baseY + thickness, cellWidth, thickness, paint);
                        break;

                    case XTerm.Common.UnderlineStyle.Curly:
                        DrawCurly(canvas, paint, x, baseY, cellWidth, thickness);
                        break;

                    case XTerm.Common.UnderlineStyle.Dotted:
                        DrawDashes(canvas, paint, x, baseY, cellWidth, thickness, thickness, thickness);
                        break;

                    case XTerm.Common.UnderlineStyle.Dashed:
                        DrawDashes(canvas, paint, x, baseY, cellWidth, thickness, thickness * 3f, thickness * 2f);
                        break;

                    default:
                        canvas.DrawRect(x, baseY, cellWidth, thickness, paint);
                        break;
                }

                paint.Color = textColor;
            }

            // Strikethrough OR overline, never both -- GetTextDecorations returns one collection
            // and strikethrough wins when a cell carries both, so drawing the pair here put a bar
            // on a cell the other path gives one.
            if ((flags & SnapshotFlags.Strikethrough) != 0)
                canvas.DrawRect(x, y + cellHeight / 2f, cellWidth, thickness, paint);
            else if ((flags & SnapshotFlags.Overline) != 0)
                canvas.DrawRect(x, y, cellWidth, thickness, paint);
        }

        /// <summary>
        /// The squiggle an LSP puts under an error.
        /// </summary>
        /// <remarks>
        /// Drawn as a stroked sine-ish path rather than an image, and phase-locked to the cell's own
        /// x so that adjacent cells continue one wave instead of each restarting it — a squiggle that
        /// resets every cell looks like a row of ticks.
        /// </remarks>
        private static void DrawCurly(SKCanvas canvas, SKPaint paint, float x, float baseY,
                                      float cellWidth, float thickness)
        {
            var amplitude = thickness * 1.5f;
            var period = Math.Max(4f, cellWidth / 2f);

            var previousStyle = paint.Style;
            var previousWidth = paint.StrokeWidth;
            var previousAa = paint.IsAntialias;

            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = thickness;
            paint.IsAntialias = true;

            using var path = new SKPath();
            var centre = baseY + amplitude;
            var step = period / 8f;

            path.MoveTo(x, centre + amplitude * (float)Math.Sin(x / period * Math.PI * 2));
            for (var dx = step; dx <= cellWidth; dx += step)
                path.LineTo(x + dx, centre + amplitude * (float)Math.Sin((x + dx) / period * Math.PI * 2));

            canvas.DrawPath(path, paint);

            paint.Style = previousStyle;
            paint.StrokeWidth = previousWidth;
            paint.IsAntialias = previousAa;
        }

        /// <summary>
        /// Dotted and dashed, which differ only in how long the marks are.
        /// </summary>
        /// <remarks>
        /// Phase-locked to the cell's x, for the same reason the curl is: a dash pattern that starts
        /// over every cell would draw a mark at every cell boundary and read as a solid line.
        /// </remarks>
        private static void DrawDashes(SKCanvas canvas, SKPaint paint, float x, float baseY,
                                       float cellWidth, float thickness, float dash, float gap)
        {
            var period = dash + gap;
            var phase = x % period;

            for (var start = -phase; start < cellWidth; start += period)
            {
                var from = Math.Max(0f, start);
                var to = Math.Min(cellWidth, start + dash);

                if (to > from)
                    canvas.DrawRect(x + from, baseY, to - from, thickness, paint);
            }
        }
    }
}
