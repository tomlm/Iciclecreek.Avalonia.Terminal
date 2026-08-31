using System;
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
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
                return;   // not the Skia backend; the caller keeps the DrawingContext path for that

            using var lease = feature.Lease();
            Draw(lease.SkCanvas);
        }

        private void Draw(SKCanvas canvas)
        {
            var snapshot = _snapshot;
            var cw = (float)snapshot.CellWidth;
            var ch = (float)snapshot.CellHeight;

            using var paint = new SKPaint { IsAntialias = true };

            if (snapshot.Surface != 0)
            {
                paint.Color = new SKColor(snapshot.Surface);
                canvas.DrawRect(0, 0, snapshot.Cols * cw, snapshot.RowCount * ch, paint);
            }

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

                var y = rowIndex * ch;
                var col = 0;

                while (col < snapshot.Cols)
                {
                    var background = row.Cells[col].Background;
                    var start = col;

                    while (col < snapshot.Cols && row.Cells[col].Background == background)
                        col++;

                    if (background != 0)
                    {
                        paint.Color = new SKColor(background);
                        canvas.DrawRect(start * cw, y, (col - start) * cw, ch, paint);
                    }
                }
            }

            // Images, between backgrounds and glyphs — a picture sits under any text printed over it.
            //
            // One blit per run. There is no coalescing pass any more: a run already IS the coalesced
            // thing, and it carries its own source rectangle, so the arithmetic that used to rebuild
            // strips from adjacent tiles — and the edge-tile scaling correction it needed — is gone.
            // The run is clipped to the visible width here rather than in the buffer, which is what
            // lets a narrowed window show less of a picture without destroying any of it.
            for (var rowIndex = 0; rowIndex < snapshot.RowCount; rowIndex++)
            {
                var row = snapshot.Rows[rowIndex];
                if (row is null || row.RunCount == 0)
                    continue;

                var y = rowIndex * ch;

                for (var i = 0; i < row.RunCount; i++)
                {
                    ref var run = ref row.Runs[i];

                    var visibleCols = Math.Min(run.Cols, snapshot.Cols - run.Column);
                    if (visibleCols <= 0)
                        continue;

                    var image = row.Images[run.ImageIndex];
                    if (_fonts.Upload(image) is not { } uploaded)
                        continue;

                    // Clip the source to match, so narrowing shows less of the picture rather than
                    // squeezing all of it into fewer columns.
                    var srcWidth = run.Cols > 0
                        ? (int)Math.Round(run.SrcWidth * (visibleCols / (double)run.Cols))
                        : 0;

                    srcWidth = Math.Min(srcWidth, image.PixelWidth - run.SrcX);
                    var srcHeight = Math.Min(run.SrcHeight, image.PixelHeight - run.SrcY);

                    if (srcWidth <= 0 || srcHeight <= 0)
                        continue;

                    var src = new SKRect(run.SrcX, run.SrcY, run.SrcX + srcWidth, run.SrcY + srcHeight);
                    var dest = new SKRect(
                        run.Column * cw, y,
                        (run.Column + visibleCols) * cw, y + ch);

                    canvas.DrawImage(uploaded, src, dest);
                }
            }

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

                var y = rowIndex * ch + baseline;

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
                    var decorated = (cell.Flags & (SnapshotFlags.Underline | SnapshotFlags.Strikethrough)) != 0;

                    if (blank && !decorated)
                        continue;

                    paint.Color = new SKColor(cell.Foreground);

                    if (!blank)
                    {
                        var font = _fonts.For(snapshot.FontFamily, snapshot.FontSize, cell.Flags);
                        var x = col * cw;

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
                                        underlineColor, col * cw, rowIndex * ch, cw, ch);
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
            glyphs.CopyTo(blobRun.GetGlyphSpan());

            var positions = blobRun.GetPositionSpan();
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

            if ((flags & SnapshotFlags.Strikethrough) != 0)
                canvas.DrawRect(x, y + cellHeight / 2f, cellWidth, thickness, paint);
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
