using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using Iciclecreek.Avalonia.Terminal;
using Porta.Pty;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XTerm.Buffer;
using XTerm.Events;
using XT = global::XTerm;

namespace Iciclecreek.Terminal
{

    public partial class TerminalView
    {
        /// <summary>
        /// Return the view to the tail and resume following — what a host's "jump to bottom" affordance
        /// calls, and what typing does implicitly. A no-op in the alternate buffer, which has no scrollback
        /// of its own, and when <see cref="AutoScrollToBottom"/> is off.
        /// </summary>
        /// <remarks>
        /// <para>Public because a terminal that can pause its follow needs a way to resume it on demand, and
        /// the paired affordance — a button that appears once the user scrolls away — is the usual way that
        /// is surfaced. A host can get most of the way there with <c>ViewportY = MaxScrollback</c>; what this
        /// adds is the two guards, so the button does nothing rather than something surprising in the
        /// alternate buffer or with auto-scroll off.</para>
        /// <para>Deliberately NOT called from <c>SendToPtyAsync</c>. That writer is not limited to typed
        /// input — it also carries mouse-tracking reports, terminal query responses and focus notifications
        /// — so resuming there means merely moving the mouse over a mouse-reporting app snaps a user who is
        /// reading scrollback back to the bottom. Only the keyboard entry points call it.</para>
        /// </remarks>
        public void FollowTail()
        {
            if (_isAlternateBuffer || !_autoScroll)
                return;

            _followBottom = true;

            // Through the ViewportY PROPERTY, not Buffer.ScrollToBottom(), so the change notification is
            // raised and a host scrollbar does not keep showing a stale position until something unrelated
            // moves the viewport again.
            var max = MaxScrollback;
            if (ViewportY < max)
                ViewportY = max;
        }

        /// <summary>
        /// Searches the scrollback and highlights every match.
        /// </summary>
        /// <remarks>
        /// Cheap enough to call per keystroke -- measured at 3.7 ms over 10,000 lines, allocating
        /// nothing -- so a find box can search as the user types and leave debouncing for buffers
        /// large enough to need it.
        /// </remarks>
        /// <returns>How many matches, capped at <see cref="XT.Search.BufferSearch.MaxHits"/>.</returns>
        public int FindInBuffer(string needle, XT.Search.SearchOptions options = default)
        {
            _search ??= new XT.Search.BufferSearch(_terminal);
            var count = _search.Find(needle, options);
            _currentMatchId = -1;
            InvalidateVisual();
            return count;
        }

        /// <summary>Moves to the next match and scrolls it into view. Wraps at the end.</summary>
        public bool FindNext() => MoveSearch(next: true);

        /// <summary>Moves to the previous match and scrolls it into view. Wraps at the start.</summary>
        public bool FindPrevious() => MoveSearch(next: false);

        /// <summary>Forgets the search and removes the highlights.</summary>
        public void ClearSearch()
        {
            _search?.Clear();
            _currentMatchId = -1;
            InvalidateVisual();
        }

        private bool MoveSearch(bool next)
        {
            if (_search is null)
                return false;

            XT.Search.SearchHit hit;
            var moved = next ? _search.TryMoveNext(out hit) : _search.TryMovePrevious(out hit);
            if (!moved)
                return false;

            _currentMatchId = hit.MatchId;

            // Scroll only when the match is off screen, and then put it mid-viewport rather than on
            // the edge -- a match on the last row with nothing after it is a match with no context.
            var top = _terminal.Buffer.ViewportY;
            if (hit.BufferRow < top || hit.BufferRow >= top + _terminal.Rows)
            {
                // Clamped at BOTH ends: a match near the bottom of the buffer, centred naively,
                // asks for a viewport past MaxScrollback and leaves blank rows under the output.
                ViewportY = Math.Clamp(hit.BufferRow - _terminal.Rows / 2, 0, Math.Max(0, MaxScrollback));
            }

            InvalidateVisual();
            return true;
        }

        // ---- shell integration, for a host to drive ------------------------------------------
        //
        // Methods and data rather than gestures. Nothing here binds a key, opens a menu or decides
        // what a mark should look like: the terminal knows where the prompts are and what the
        // commands exited with, and the host decides what that is worth on screen. A keybinding
        // baked in here would be one the host cannot move.

        /// <summary>
        /// Scrolls to the nearest prompt above what is on screen.
        /// </summary>
        /// <returns>False when there is no earlier prompt, so a host can leave the gesture unhandled.</returns>
        public bool ScrollToPreviousPrompt()
        {
            if (!_terminal.TryFindPreviousPrompt(_terminal.Buffer.ViewportY, out var row))
                return false;

            ViewportY = row;
            return true;
        }

        /// <summary>Scrolls to the nearest prompt below what is on screen.</summary>
        /// <returns>False when there is no later prompt.</returns>
        public bool ScrollToNextPrompt()
        {
            if (!_terminal.TryFindNextPrompt(_terminal.Buffer.ViewportY, out var row))
                return false;

            ViewportY = row;
            return true;
        }

        /// <summary>
        /// Selects the output of the command that ran at <paramref name="bufferRow"/>.
        /// </summary>
        /// <remarks>
        /// <para>Output means what the command PRODUCED, not the line it was typed on: the range runs
        /// from the row after the command was executed to the row before the next prompt begins. So
        /// selecting the output of a build gives the build log without the command that started it or
        /// the prompt that followed.</para>
        /// <para>A command still running has no next prompt, and its output runs to the end of what
        /// has arrived so far — which is the useful answer rather than a refusal.</para>
        /// </remarks>
        /// <returns>False when that row is not part of a command, or the command produced nothing.</returns>
        public bool SelectCommandOutput(int bufferRow)
        {
            var lines = _terminal.Buffer.Lines;
            if (bufferRow < 0 || bufferRow >= lines.Length)
                return false;

            // Walk back to the command this row belongs to. A C mark is where output starts.
            var start = -1;
            for (var i = bufferRow; i >= 0; i--)
            {
                if (HasMark(lines[i], XT.Common.ShellIntegrationMark.CommandExecuted))
                {
                    start = i + 1;
                    break;
                }

                // A prompt above with no command between means this row is not output at all.
                if (i != bufferRow && HasMark(lines[i], XT.Common.ShellIntegrationMark.PromptStart))
                    return false;
            }

            if (start < 0)
                return false;

            var end = lines.Length - 1;
            for (var i = start; i < lines.Length; i++)
            {
                if (HasMark(lines[i], XT.Common.ShellIntegrationMark.PromptStart))
                {
                    end = i - 1;
                    break;
                }
            }

            if (end < start)
                return false;

            _terminal.Selection.StartSelection(0, start, XT.Selection.SelectionMode.Normal);
            _terminal.Selection.UpdateSelection(Math.Max(0, _terminal.Cols - 1), end);
            _terminal.Selection.EndSelection();
            InvalidateVisual();
            return true;
        }

        private static bool HasMark(BufferLine? line, XT.Common.ShellIntegrationMark kind)
        {
            if (line is null || !line.HasMarks)
                return false;

            foreach (var mark in line.Marks)
            {
                if (mark.Kind == kind)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The caret boundary just past the last non-blank cell on <paramref name="from"/>'s row.
        /// </summary>
        /// <remarks>
        /// End means "end of what is written", not "end of the grid". A terminal row is padded out to the
        /// full width with blanks, so jumping to the row edge selects a screenful of spaces after the
        /// prompt — the same surprise as walking a word chord into empty space.
        /// </remarks>
        private int LineEndBoundary(int from, int cols)
        {
            int row = from / cols;
            var line = _terminal.Buffer.GetLine(_terminal.Buffer.ViewportY + row);
            if (line == null)
                return from;

            // Past the WHOLE glyph, not just its first cell. A width-2 character is followed by a width-0
            // placeholder, so recording only the column the glyph starts in leaves the boundary — and the
            // selection edge with it — in the middle of one character.
            int lastContent = -1;
            for (int x = 0; x < Math.Min(line.Length, cols); x++)
            {
                var cell = line[x];
                if (!string.IsNullOrWhiteSpace(cell.Content))
                    lastContent = x + Math.Max(0, cell.Width - 1);
            }

            // Nothing on the row, or the caret is already past the content: stay put.
            int edge = row * cols + lastContent + 1;
            return edge > from ? edge : from;
        }

        /// <summary>
        /// The next word boundary from <paramref name="from"/> in <paramref name="direction"/>, as a caret
        /// boundary ordinal.
        /// </summary>
        /// <remarks>
        /// <para>Readline's rule, which is what a shell user already has in their fingers: skip any run of
        /// separators, then skip the run of word characters beyond it. Moving left looks at the cell BEFORE
        /// the caret and moving right at the cell after, because a caret sits between cells.</para>
        /// <para>Stays put when the scan finds no word that way. A terminal's grid is mostly empty cells,
        /// so without that a chord at the prompt selects the whole rest of the screen.</para>
        /// </remarks>
        private int WordBoundary(int from, int direction, int cols, int lastBoundary)
        {
            // What counts as a word is XTerm.NET's definition, not a second one invented here.
            // SelectionManager.IsWordChar is what double-click expansion uses, and a terminal that
            // disagreed with itself about where "foo-bar" ends depending on whether you reached for the
            // mouse or the keyboard would be worse than either answer on its own.
            static bool IsWordChar(string? content)
                => !string.IsNullOrEmpty(content)
                   && (char.IsLetterOrDigit(content[0]) || content[0] == '_');

            // A wide glyph — CJK, emoji — occupies two cells: the glyph, then a width-0 PLACEHOLDER whose
            // content is empty. Read as content that placeholder is not a word character, so a word scan
            // stops between the two halves of one character and the selection covers half a glyph. It is
            // part of the glyph before it, so it is never a separator.
            static bool IsSeparator((string? Content, int Width) cell)
                => cell.Width != 0 && !IsWordChar(cell.Content);

            (string? Content, int Width) CellAt(int boundary)
            {
                int row = boundary / cols;
                int col = boundary % cols;
                var line = _terminal.Buffer.GetLine(_terminal.Buffer.ViewportY + row);
                if (line == null || col < 0 || col >= line.Length) return (null, 1);
                return (line[col].Content, line[col].Width);
            }

            int i = Math.Clamp(from, 0, lastBoundary);
            bool foundWord = false;

            if (direction < 0)
            {
                while (i > 0 && IsSeparator(CellAt(i - 1))) i--;
                while (i > 0 && !IsSeparator(CellAt(i - 1))) { i--; foundWord = true; }
            }
            else
            {
                while (i < lastBoundary && IsSeparator(CellAt(i))) i++;
                while (i < lastBoundary && !IsSeparator(CellAt(i))) { i++; foundWord = true; }
            }

            // Nothing but blanks that way, so there is no word to move to. Stay put rather than running to
            // the edge of the grid: a terminal's buffer is mostly empty cells, so without this a chord at
            // the prompt selects the whole rest of the screen — which is what it did the first time.
            return foundWord ? i : from;
        }


        private void AnswerWindowInfo(XT.Events.TerminalEvents.WindowInfoRequestedEventArgs e)
        {
            {
                // Raise routed event so any parent can handle it without custom plumbing.
                var args = new WindowInfoRequestedEventArgs(e.Request)
                {
                    RoutedEvent = WindowInfoRequestedEvent
                };

                RaiseEvent(args);

                // Keep CLR event for back-compat.
                WindowInfoRequested?.Invoke(this, args);

                // Copy response data back to the terminal's event args
                if (args.Handled)
                {
                    e.Handled = true;
                    e.IsIconified = args.IsIconified;
                    e.X = args.X;
                    e.Y = args.Y;
                    e.WidthPixels = args.WidthPixels;
                    e.HeightPixels = args.HeightPixels;
                    e.CellWidth = args.CellWidth;
                    e.CellHeight = args.CellHeight;
                    e.Title = args.Title;
                }
            }
        }

        /// <summary>
        /// Determines if the terminal should handle text selection vs forwarding mouse to app.
        /// Selection is handled when: (1) app hasn't captured mouse, OR (2) Shift is held (override).
        /// </summary>
        private bool ShouldHandleSelection(KeyModifiers modifiers)
        {
            bool appWantsMouse = _terminal.MouseTrackingMode != XT.Input.MouseTrackingMode.None;
            bool shiftHeld = modifiers.HasFlag(KeyModifiers.Shift);

            // Handle selection if app doesn't want mouse, OR if Shift override is active
            return !appWantsMouse || shiftHeld;
        }

        /// <summary>
        /// Flattens the logical line containing <paramref name="bufferLine"/> — following wrapped
        /// continuations in both directions — into text, along with a map from each character back to
        /// the cell it came from. The map is what keeps hit-testing honest: a wide (CJK/emoji) character
        /// occupies two columns but contributes one entry, and a combining sequence contributes several
        /// characters that all belong to the same column, so string offsets are never column numbers.
        /// </summary>
        private (string Text, List<(int Line, int Col)> Map)? BuildLogicalLine(int bufferLine)
        {
            var buffer = _terminal.Buffer;
            if (bufferLine < 0 || bufferLine >= buffer.Length)
                return null;

            // A line flagged IsWrapped is a continuation of the one above it, so walk back to the real start.
            int start = bufferLine;
            while (start > 0 && buffer.GetLine(start)?.IsWrapped == true)
                start--;

            int end = bufferLine;
            while (end + 1 < buffer.Length && buffer.GetLine(end + 1)?.IsWrapped == true)
                end++;

            var cols = _terminal.Cols;
            var sb = new StringBuilder(cols * (end - start + 1));
            var map = new List<(int Line, int Col)>(cols * (end - start + 1));

            for (int lineIndex = start; lineIndex <= end; lineIndex++)
            {
                var line = buffer.GetLine(lineIndex);
                if (line == null)
                    continue;

                for (int x = 0; x < cols; x++)
                {
                    // Placeholder cells trailing a wide character carry no content of their own.
                    if (x >= line.Length)
                    {
                        sb.Append(' ');
                        map.Add((lineIndex, x));
                        continue;
                    }

                    var cell = line[x];
                    if (cell.Width == 0)
                        continue;

                    var content = cell.Content;
                    if (string.IsNullOrEmpty(content))
                    {
                        sb.Append(' ');
                        map.Add((lineIndex, x));
                        continue;
                    }

                    sb.Append(content);
                    for (int i = 0; i < content.Length; i++)
                        map.Add((lineIndex, x));
                }
            }

            return (sb.ToString(), map);
        }

        /// <summary>
        /// Trims trailing characters that are legal in a url but far more often sentence punctuation,
        /// e.g. the period in "see https://example.com." Closing brackets survive only when the url
        /// opened them itself, so "https://en.wikipedia.org/wiki/Foo_(bar)" stays intact while
        /// "(see https://example.com)" does not swallow the closing paren.
        /// </summary>
        private static string TrimUrlEnd(string url)
        {
            while (url.Length > 0)
            {
                var last = url[url.Length - 1];
                if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"')
                {
                    url = url.Substring(0, url.Length - 1);
                    continue;
                }

                char open = last switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
                if (open != '\0' && CountChar(url, open) < CountChar(url, last))
                {
                    url = url.Substring(0, url.Length - 1);
                    continue;
                }

                break;
            }

            return url;
        }

        /// <summary>
        /// The link under a cell: one the program declared with OSC 8, or failing that one the text
        /// happens to look like.
        /// </summary>
        /// <remarks>
        /// <para>The declared one wins, and it is not a tie-break between two ways of doing the same
        /// thing. A regular expression can only find a link whose DISPLAY TEXT is the URL, and the
        /// whole point of OSC 8 is the case where it is not — "click here", a filename, a commit
        /// subject. The two are complementary, and only the program knows which cells it meant.</para>
        /// <para>Everything downstream of here — the underline, the hand cursor, requiring press and
        /// release on the same link — takes a <see cref="HoveredUrl"/> and never asks where it came
        /// from, so OSC 8 gets all of it for the cost of this branch.</para>
        /// </remarks>
        internal HoveredUrl? FindUrlAtColumn(int bufferLine, int col)
        {
            var declared = FindHyperlinkAtColumn(bufferLine, col);
            if (declared is not null)
                return declared;

            var logical = BuildLogicalLine(bufferLine);
            if (logical == null)
                return null;

            var (text, map) = logical.Value;

            // Locate the character the pointer is over. Wide characters map two columns to one entry,
            // so accept the entry that starts at or just before the hovered column.
            int hitIndex = -1;
            for (int i = 0; i < map.Count; i++)
            {
                if (map[i].Line == bufferLine && map[i].Col == col)
                {
                    hitIndex = i;
                    break;
                }
            }

            if (hitIndex < 0)
                return null;

            foreach (Match m in UrlRegex.Matches(text))
            {
                var url = TrimUrlEnd(m.Value);
                if (url.Length == 0)
                    continue;

                int startIndex = m.Index;
                int endIndex = m.Index + url.Length - 1;      // inclusive, after trimming
                if (hitIndex < startIndex || hitIndex > endIndex)
                    continue;

                // Collapse the character range into one inclusive cell range per buffer line.
                var segments = new List<(int Line, int StartCol, int EndCol)>();
                for (int i = startIndex; i <= endIndex && i < map.Count; i++)
                {
                    var (line, cellCol) = map[i];
                    if (segments.Count > 0)
                    {
                        var lastSegment = segments[segments.Count - 1];
                        if (lastSegment.Line == line)
                        {
                            segments[segments.Count - 1] = (line, lastSegment.StartCol, Math.Max(lastSegment.EndCol, cellCol));
                            continue;
                        }
                    }
                    segments.Add((line, cellCol, cellCol));
                }

                return segments.Count > 0 ? new HoveredUrl(url, segments) : null;
            }

            return null;
        }

        /// <summary>
        /// The OSC 8 link covering a cell, if the program declared one there.
        /// </summary>
        /// <remarks>
        /// Simpler than the regular-expression path, because the emulator already stores the answer:
        /// the span is on the line, so there is no logical line to rebuild and no character indices
        /// to map back onto cells.
        ///
        /// <para>A link that wrapped is several spans carrying one <c>id=</c>, which is what that
        /// parameter is for. They are gathered so hovering either half underlines both — but only
        /// across CONTIGUOUS lines, so two unrelated uses of the same id elsewhere in the scrollback
        /// do not join up.</para>
        /// </remarks>
        private HoveredUrl? FindHyperlinkAtColumn(int bufferLine, int col)
        {
            var lines = _terminal.Buffer.Lines;
            if (bufferLine < 0 || bufferLine >= lines.Length)
                return null;

            if (lines[bufferLine] is not { } line || !line.HasLinks)
                return null;

            if (!line.TryGetLinkAt(col, out var link))
                return null;

            var segments = new List<(int Line, int StartCol, int EndCol)>
            {
                (bufferLine, link.Column, link.EndColumn - 1)
            };

            if (link.Id is not null)
            {
                for (var i = bufferLine - 1; i >= 0 && TryGetSpanWithId(lines[i], link.Id, out var above); i--)
                    segments.Insert(0, (i, above.Column, above.EndColumn - 1));

                for (var i = bufferLine + 1; i < lines.Length && TryGetSpanWithId(lines[i], link.Id, out var below); i++)
                    segments.Add((i, below.Column, below.EndColumn - 1));
            }

            return new HoveredUrl(link.Url, segments, fromSequence: true);
        }

        private static bool TryGetSpanWithId(BufferLine? line, string id, out XT.Buffer.LineHyperlink span)
        {
            if (line is not null && line.HasLinks)
            {
                foreach (var candidate in line.Links)
                {
                    if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
                    {
                        span = candidate;
                        return true;
                    }
                }
            }

            span = default;
            return false;
        }

        private void UpdateTextMetrics()
        {
            var typeface = new Typeface(FontFamily, FontStyle, FontWeight);

            // The WHOLE chain, not FontFamily.Name, which is only the first name in it. The Skia
            // layer takes a string and splits it back into candidates; handed one name it has one
            // candidate, and a chain exists precisely because the first name is usually absent --
            // the default chain opens with Cascadia Mono, which no stock Linux has. SKTypeface
            // .FromFamilyName does not fail on a name it cannot find, it substitutes, so the Skia
            // path drew every cell in the platform's PROPORTIONAL default while this method, going
            // through Avalonia's own resolution, measured the grid from a monospace face further
            // down the chain. Grid monospace, glyphs not: columns land correctly and the characters
            // inside them do not.
            //
            // Built HERE so it cannot drift from the metrics: the two have to describe one font, and
            // this is the one place the font is resolved.
            _fontFamilyChain = FontFamily is null ? "monospace" : string.Join(",", FontFamily.FamilyNames);
            _measureText = new FormattedText(
                "W",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                Brushes.Black);

            _charWidth = _measureText.Width;
            _charHeight = _measureText.Height;

            // Where the glyph path puts its baseline. FormattedText draws from a top-left origin and
            // works this out itself; a GlyphRun is positioned BY its baseline, so the difference has
            // to be known explicitly or every glyph sits a fraction of a line off.
            _baseline = _measureText.Baseline;

            // The typefaces a run can ask for, invalidated with the font. Resolving one goes through
            // the font manager, and doing that per run per rebuild was measurable next to the draw
            // it precedes.
            _glyphTypefaces.Clear();

            PublishCellPixelSize();
        }

        /// <summary>
        /// Tells the emulator how big a character cell is, in device pixels.
        /// </summary>
        /// <remarks>
        /// <para>XTerm.NET is headless and cannot measure a font, so this is the only way it can know how many
        /// columns a Sixel image of a given pixel width covers. It is also the answer given to a CSI 16 t query
        /// when nothing handles that, which is how an application decides what size picture to send. Both have to
        /// agree with what is actually drawn or images do not line up with the grid they were sized for.</para>
        /// <para><see cref="_charWidth"/> and <see cref="_charHeight"/> are layout units rather than device
        /// pixels, so the render scaling has to go in. That is also why this cannot be worked out once: moving a
        /// window to a display with different scaling changes the answer without changing the font.</para>
        /// </remarks>
        private void PublishCellPixelSize()
        {
            if (_terminal is null || _charWidth <= 0 || _charHeight <= 0)
                return;

            var scale = RenderScale;
            var cellWidth = Math.Max(1, (int)Math.Round(_charWidth * scale));
            var cellHeight = Math.Max(1, (int)Math.Round(_charHeight * scale));

            // The scale rides beside the pixel metrics it produced: iTerm2's ReportCellSize
            // speaks points, and the emulator divides by this to answer it. Set unconditionally,
            // because it is an answer to a query rather than a notification -- there is nobody to
            // over-notify by keeping it current.
            _terminal.Options.DisplayScale = scale;

            // Nothing moved, so nothing to say. This runs from UpdateTextMetrics, which
            // MeasureOverride calls on EVERY layout pass -- reporting unconditionally below would
            // send an application an in-band resize report every time the view was measured, which
            // is a flood rather than a notification.
            if (cellWidth == _terminal.Options.CellWidthPixels &&
                cellHeight == _terminal.Options.CellHeightPixels)
                return;

            _terminal.Options.CellWidthPixels = cellWidth;
            _terminal.Options.CellHeightPixels = cellHeight;

            // The metrics and the report are two halves of one event, and only the first half was
            // happening. Resize covers a grid that changed shape; this covers the case it cannot
            // see -- the pixel size of the text area changing while the grid does not. A DPI
            // switch from dragging the window to another monitor, or a font change that alters the
            // cell box, leaves the terminal 80x24 while every pixel dimension the application was
            // told about becomes wrong, and anything sizing images or Sixel output from the
            // reported geometry goes on drawing at the old scale.
            //
            // Unconditional by design: the emulator drops it unless an application asked for mode
            // 2048, which is what its own documentation says to rely on rather than guessing here.
            _terminal.NotifyTextAreaPixelsChanged();
        }

        /// <remarks>
        /// A terminal takes whatever it is given, so handing the constraint straight back is right
        /// for a container that offers a real one -- and wrong for the several that do not. A
        /// ScrollViewer measures its content with an INFINITE dimension to ask how big it wants to
        /// be, and so do StackPanel, an auto-sized Grid row and a WrapPanel. Returning that infinity
        /// as a desired size makes Avalonia throw out of the layout pass, which is a crash rather
        /// than a bad layout: the view could not be put inside any of them.
        ///
        /// Asked what it WANTS, it answers with the grid it is currently showing. Nothing else here
        /// knows a preferred size -- there is no content to measure, only a cell grid whose size is
        /// chosen by whatever space it was last given.
        /// </remarks>
        protected override Size MeasureOverride(Size availableSize)
        {
            UpdateTextMetrics();

            // 80x24 before the emulator exists, which is the only size a terminal has ever defaulted
            // to. MeasureOverride can run before OnInitialized has built it.
            var cols = _terminal?.Cols ?? 80;
            var rows = _terminal?.Rows ?? 24;

            var width = double.IsInfinity(availableSize.Width)
                ? cols * _charWidth + Math.Max(0, GutterWidth)
                : availableSize.Width;

            var height = double.IsInfinity(availableSize.Height)
                ? rows * _charHeight
                : availableSize.Height;

            return new Size(width, height);
        }

    }
}
