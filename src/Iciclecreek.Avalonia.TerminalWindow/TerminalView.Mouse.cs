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
        protected override async void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            // Request focus when clicked
            Focus();

            try
            {
                var point = e.GetPosition(this);
                var col = PointerColumn(point.X);
                var row = PointerRow(point.Y);

                // Any press hands selection back to the mouse — a later Shift+arrow re-anchors at the
                // cursor rather than extending whatever the pointer just drew. FIRST, so it still runs
                // when the Ctrl+Click path below returns early.
                _kbSelAnchor = null;
                _kbSelWholeInput = false;

                // Ctrl+Click on a URL. Resolved from the press position rather than the hover state,
                // which goes stale whenever the viewport moves without the pointer (wheel scroll, new
                // output), and armed here but raised on release the way other terminals do it.
                _pendingUrlClick = null;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                    e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    var pressed = FindUrlAtColumn(_terminal.Buffer.ViewportY + row, col);
                    if (pressed != null)
                    {
                        _pendingUrlClick = pressed;
                        e.Handled = true;
                        return;
                    }
                }

                // Check if we should handle selection (app doesn't want mouse, or Shift override)
                if (ShouldHandleSelection(e.KeyModifiers))
                {
                    var props = e.GetCurrentPoint(this).Properties;

                    // Right-click: copy if selection exists, otherwise paste
                    if (props.IsRightButtonPressed)
                    {
                        // Claimed BEFORE the await, not after it.
                        //
                        // This handler is async void. The first await returns to the caller, and the
                        // routed event finishes bubbling right then -- with Handled still false,
                        // because the line that sets it has not run yet. It runs afterwards, on an
                        // event nothing is listening to any more. So the press was consumed here AND
                        // delivered to everything upstream: a host with its own context menu on
                        // right-click got both.
                        //
                        // Unconditional because both branches below claim it: copy when there is a
                        // selection, paste when there is not. There is no path here that declines.
                        e.Handled = true;

                        if (_terminal.Selection.HasSelection)
                        {
                            await CopyAsync();
                            _terminal.Selection.ClearSelection();
                            RequestPaint();
                        }
                        else
                        {
                            await PasteAsync();
                        }
                        return;
                    }

                    // Left-click clears existing selection before starting new one
                    if (props.IsLeftButtonPressed && _terminal.Selection.HasSelection)
                    {
                        _terminal.Selection.ClearSelection();
                        RequestPaint();
                    }

                    // Determine selection mode based on click count
                    var clickCount = e.ClickCount;
                    var mode = clickCount switch
                    {
                        2 => XT.Selection.SelectionMode.Word,
                        3 => XT.Selection.SelectionMode.Line,
                        _ => XT.Selection.SelectionMode.Normal
                    };

                    if (mode == XT.Selection.SelectionMode.Normal && !ShowCaretOnClick)
                    {
                        // Defer single-click selection until the pointer actually moves;
                        // this avoids showing a single-cell caret on every click.
                        // The ABSOLUTE buffer row, not the viewport row. Output arriving between the
                        // press and the first movement scrolls the viewport, and a viewport row then
                        // names different content than it did when the user clicked -- so the
                        // selection began somewhere they had not pressed. Absolute rows do not move.
                        _pendingSelectionStart = (col, _terminal.Buffer.ViewportY + row);
                        _isSelecting = true;
                    }
                    else
                    {
                        // Word / line select, or ShowCaretOnClick=true — start immediately.
                        int viewportRow = row;
                        _terminal.Selection.StartSelection(col, viewportRow, mode);
                        _isSelecting = true;
                        _pendingSelectionStart = null;
                        RequestPaint();
                    }
                    e.Handled = true;
                    return;
                }

                // Forward mouse event to application
                if (_ptyConnection == null)
                    return;

                var button = ConvertPointerButton(e.GetCurrentPoint(this).Properties);
                var modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);

                var sequence = _terminal.GenerateMouseEvent(button, col, row, XT.Input.MouseEventType.Down, modifiers);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling mouse press: {ex.Message}");
            }
        }

        protected override async void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            try
            {
                // Complete a Ctrl+Click armed on press. Handling the release too keeps a mouse-reporting
                // application from seeing an "up" with no matching "down".
                var pendingUrl = _pendingUrlClick;
                _pendingUrlClick = null;
                if (pendingUrl != null)
                {
                    var releasePoint = e.GetPosition(this);
                    var releaseCol = PointerColumn(releasePoint.X);
                    var releaseRow = PointerRow(releasePoint.Y);
                    var released = FindUrlAtColumn(_terminal.Buffer.ViewportY + releaseRow, releaseCol);

                    // Only fire if the pointer is still on the same url it was pressed on.
                    if (released != null && released.Url == pendingUrl.Url)
                        UrlClicked?.Invoke(this,
                            new UrlClickedEventArgs(pendingUrl.Url, pendingUrl.FromSequence));

                    e.Handled = true;
                    return;
                }

                // If we were selecting, end selection
                if (_isSelecting)
                {
                    if (_pendingSelectionStart.HasValue)
                    {
                        // Pointer was never moved after the click — no visible selection was started,
                        // so just clear the pending state without leaving any caret.
                        _pendingSelectionStart = null;
                    }
                    else
                    {
                        _terminal.Selection.EndSelection();
                    }
                    _isSelecting = false;
                    e.Handled = true;
                    return;
                }

                // Forward mouse event to application
                if (_ptyConnection == null)
                    return;

                var point = e.GetPosition(this);
                var col = PointerColumn(point.X);
                var row = PointerRow(point.Y);

                var button = ConvertPointerButton(e.GetCurrentPoint(this).Properties, e.InitialPressMouseButton);
                var modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);

                var sequence = _terminal.GenerateMouseEvent(button, col, row, XT.Input.MouseEventType.Up, modifiers);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling mouse release: {ex.Message}");
            }
        }

        protected override async void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            try
            {
                var point = e.GetPosition(this);
                var col = PointerColumn(point.X);
                var row = PointerRow(point.Y);

                // If we're selecting, update the selection
                if (_isSelecting)
                {
                    // Dragging out a selection isn't hovering — drop the hand cursor and underline
                    // rather than leaving them stuck for the length of the drag.
                    ClearHoveredUrl();

                    int viewportRow = row;
                    if (_pendingSelectionStart.HasValue)
                    {
                        // First movement after a single click — now actually start the selection.
                        // Back into viewport space against the CURRENT scroll position, since that is
                        // what the selection API takes. A row stored absolute and converted here
                        // names the same content it named at the press, however far the view has
                        // scrolled since.
                        var anchorRow = _pendingSelectionStart.Value.Row - _terminal.Buffer.ViewportY;
                        _terminal.Selection.StartSelection(_pendingSelectionStart.Value.Col, anchorRow,
                                                           XT.Selection.SelectionMode.Normal);
                        _pendingSelectionStart = null;
                    }
                    _terminal.Selection.UpdateSelection(col, viewportRow);

                    // Dragging past an edge scrolls the view, so the selection can reach content that
                    // is not on screen -- which is what dragging past an edge means in every other
                    // text surface. Without it the drag pinned itself to the edge row and the user
                    // had to let go, scroll, and start again.
                    ScrollForDragAt(point.Y);

                    RequestPaint();
                    e.Handled = true;
                    return;
                }

                // URL hover detection
                int bufferLine = _terminal.Buffer.ViewportY + row;
                UpdateHoveredUrl(bufferLine, col);

                // Forward mouse event to application
                if (_ptyConnection == null)
                    return;

                var props = e.GetCurrentPoint(this).Properties;
                var modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);
                var button = ConvertPointerButton(props);
                var eventType = (props.IsLeftButtonPressed || props.IsMiddleButtonPressed || props.IsRightButtonPressed)
                    ? XT.Input.MouseEventType.Drag
                    : XT.Input.MouseEventType.Move;

                // Once per CELL crossed, not once per pointer event.
                //
                // The protocol reports positions in cells, so every event inside one cell produces
                // an identical sequence -- and a modern pointer fires them at several hundred hertz.
                // A program tracking the mouse was reading hundreds of copies of "still at 40,12" a
                // second, which is bandwidth through the pty, parse work at the far end, and for
                // anything that redraws on motion, a repaint per event.
                //
                // Remembered per button state as well as position: a drag and a hover at the same
                // cell are different reports, and collapsing them would swallow the button change.
                var here = (col, row, eventType, _terminal.MouseTrackingMode, button, modifiers);
                if (_lastReportedMotion == here)
                    return;

                var sequence = _terminal.GenerateMouseEvent(button, col, row, eventType, modifiers);
                if (!string.IsNullOrEmpty(sequence))
                {
                    // Remember only a report that actually went out. A move while tracking is off
                    // generates nothing; caching it would suppress the first real report if an
                    // application enables tracking before the pointer crosses into another cell.
                    _lastReportedMotion = here;
                    await SendToPtyAsync(sequence).ConfigureAwait(false);
                }
                else
                {
                    _lastReportedMotion = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling mouse move: {ex.Message}");
            }
        }

        /// <summary>
        /// Scrolls the viewport when a drag has gone past the top or bottom edge.
        /// </summary>
        /// <remarks>
        /// One row per move event, which is what makes it feel like a scroll rather than a jump: the
        /// pointer keeps producing events while it is held outside the control, so the view keeps
        /// creeping, and it stops the moment the pointer comes back inside.
        /// </remarks>
        private void ScrollForDragAt(double y)
        {
            if (_charHeight <= 0)
                return;

            int delta = y < 0 ? -1
                      : y > _terminal.Rows * _charHeight ? 1
                      : 0;

            if (delta == 0)
                return;

            var target = Math.Clamp(ViewportY + delta, 0, MaxScrollback);
            if (target != ViewportY)
                ViewportY = target;
        }

        protected override async void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            // Number of lines to scroll per wheel notch
            const int scrollLines = 3;

            // Delta.Y is positive when scrolling up (towards user), negative when scrolling down
            var delta = e.Delta.Y;
            if (delta == 0)
                return;

            // ShouldHandleSelection rather than the tracking mode alone, which is the same question
            // asked the same way as for a click: Shift is the host's override, and it means "this
            // gesture is mine, not the application's". It worked for Shift+click and not for
            // Shift+wheel, so the one way to scroll a terminal's own scrollback while a full-screen
            // application had the mouse did nothing.
            if (_ptyConnection != null && !ShouldHandleSelection(e.KeyModifiers))
            {
                // The app owns the wheel — report NOTCHES to it, and notches are what Delta.Y already
                // counts: one per detent. Accumulate so a trackpad's fractional stream turns into whole
                // notches instead of one report per micro-event (which flies a pager past the end) or none
                // at all.
                //
                // Deliberately NOT scaled by scrollLines. That multiplier is the local scrollback's "three
                // lines per detent" convention and has no meaning here: an app receives discrete wheel
                // reports and applies its own step. Scaling it sent three reports per detent, tripling the
                // scroll speed of every mouse-tracking application — vim, less, htop — against a baseline
                // of exactly one.
                var notches = TakeWheelSteps(ref _wheelResidualApp, delta);
                if (notches == 0)
                {
                    e.Handled = true;
                    return;
                }

                var point = e.GetPosition(this);
                var col = PointerColumn(point.X);
                var row = PointerRow(point.Y);
                var modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);

                var button = notches > 0 ? XT.Input.MouseButton.WheelUp : XT.Input.MouseButton.WheelDown;
                var eventType = notches > 0 ? XT.Input.MouseEventType.WheelUp : XT.Input.MouseEventType.WheelDown;

                var sequence = _terminal.GenerateMouseEvent(button, col, row, eventType, modifiers);
                if (!string.IsNullOrEmpty(sequence))
                {
                    // Mark handled BEFORE the await — after it the event has already finished bubbling.
                    e.Handled = true;
                    var repeated = string.Concat(Enumerable.Repeat(sequence, Math.Min(Math.Abs(notches), 12)));
                    await SendToPtyAsync(repeated).ConfigureAwait(false);
                    return;
                }
            }

            // Scroll up (negative delta to ViewportY) when wheel scrolls up (positive delta)
            // Scroll down (positive delta to ViewportY) when wheel scrolls down (negative delta)
            int linesToScroll = -TakeWheelSteps(ref _wheelResidual, delta * scrollLines);

            if (linesToScroll != 0)
            {
                // Calculate new viewport position
                int newViewportY = Math.Clamp(
                    ViewportY + linesToScroll,
                    0,
                    MaxScrollback);

                if (newViewportY != ViewportY)
                {
                    ViewportY = newViewportY;
                }
            }

            e.Handled = true;
        }

        /// <summary>
        /// Add <paramref name="step"/> to a running remainder and hand back the whole steps that have
        /// piled up, keeping the fraction for next time. A direction change drops the stale remainder so
        /// a reversal answers on the first event rather than paying off the old direction's debt first.
        /// </summary>
        private static int TakeWheelSteps(ref double residual, double step)
        {
            if (residual != 0 && Math.Sign(step) != Math.Sign(residual))
                residual = 0;

            residual += step;

            var whole = Math.Truncate(residual);
            residual -= whole;
            return (int)whole;
        }

        private XT.Input.MouseButton ConvertPointerButton(PointerPointProperties props, MouseButton? releasedButton = null)
        {
            if (props.IsLeftButtonPressed)
                return XT.Input.MouseButton.Left;
            if (props.IsMiddleButtonPressed)
                return XT.Input.MouseButton.Middle;
            if (props.IsRightButtonPressed)
                return XT.Input.MouseButton.Right;

            if (releasedButton.HasValue)
            {
                return releasedButton.Value switch
                {
                    MouseButton.Left => XT.Input.MouseButton.Left,
                    MouseButton.Middle => XT.Input.MouseButton.Middle,
                    MouseButton.Right => XT.Input.MouseButton.Right,
                    _ => XT.Input.MouseButton.None
                };
            }

            return XT.Input.MouseButton.None;
        }

        private void UpdateHoveredUrl(int bufferLine, int col)
        {
            // Pointer moves arrive far more often than they cross a cell boundary; scanning the line
            // again for the same cell would be pure waste.
            if (_lastHoverProbe is { } probe && probe.Line == bufferLine && probe.Col == col)
                return;
            _lastHoverProbe = (bufferLine, col);

            var found = FindUrlAtColumn(bufferLine, col);
            if (found == null)
            {
                ClearHoveredUrl();
                return;
            }

            if (found.SameAs(_hoveredLink))
                return;

            ClearHoveredUrl();
            _hoveredLink = found;

            if (!_cursorOverridden)
            {
                _savedCursor = Cursor;
                _cursorOverridden = true;
            }
            SetCurrentValue(CursorProperty, HandCursor);
            RequestPaint();
        }

        private void ClearHoveredUrl()
        {
            _lastHoverProbe = null;

            // The cursor override is undone even when no link is current: Cursor defaults to null, so a
            // saved-value-only restore would leave the hand cursor stuck for the life of the control.
            if (_cursorOverridden)
            {
                SetCurrentValue(CursorProperty, _savedCursor);
                _savedCursor = null;
                _cursorOverridden = false;
            }

            if (_hoveredLink == null)
                return;

            _hoveredLink = null;
            // The underline is an overlay drawn after the text runs, so the cached runs stay valid.
            RequestPaint();
        }

        /// <summary>
        /// Kitty OSC 22: the program chose a pointer shape. The link-hover hand keeps the last
        /// word while a link is under the pointer — its save/restore already treats the current
        /// cursor as "whatever the rest of the world wanted", so the program's shape goes into
        /// the saved slot during a hover and takes effect the moment the hover ends.
        /// </summary>
        private void OnTerminalPointerShapeChanged(object? sender, XT.Events.TerminalEvents.PointerShapeEventArgs e)
        {
            // LAST ONE WINS, so a program cycling shapes queues one job rather than one per change.
            // Unlike a notification, an intermediate pointer shape is not an event anybody missed:
            // it is a state, and only the final value was ever going to be visible.
            _pendingPointerShape = e.Shape;

            // And one JOB for however many arrive, not one each. Coalescing the value alone still
            // queued a callback per sequence, each of which then applied the same final shape -- so
            // a program cycling a spinner through OSC 22 grew the queue exactly as before, just to
            // do redundant work when it drained.
            lock (_pendingHostCallbacks)
            {
                if (_pointerShapeQueued)
                    return;

                _pointerShapeQueued = true;
            }

            PostToHost(() =>
            {
                lock (_pendingHostCallbacks)
                {
                    _pointerShapeQueued = false;
                }

                var shape = _pendingPointerShape;
                var cursor = MapPointerShape(shape);

                // A reset restores what the CONTROL had, not null. MapPointerShape(null) is null, and so
                // is any name Avalonia has no cursor for, so restoring the mapped value would have made
                // "the program stopped asking for a shape" mean "the embedder never set a cursor" — a
                // <c>TerminalView Cursor="IBeam"</c> lost its cursor to the first program that used OSC 22.
                if (cursor is null)
                {
                    if (!_shapeOverridden)
                        return;
                    cursor = _preShapeCursor;
                    _preShapeCursor = null;
                    _shapeOverridden = false;
                }
                else if (!_shapeOverridden)
                {
                    // During a hover the control's own cursor is the one the hover saved, not Cursor,
                    // which is currently the hand.
                    _preShapeCursor = _cursorOverridden ? _savedCursor : Cursor;
                    _shapeOverridden = true;
                }

                if (_cursorOverridden)
                {
                    _savedCursor = cursor;
                    return;
                }
                SetCurrentValue(CursorProperty, cursor);
            });
        }

        /// <summary>
        /// Kitty's CSS pointer names onto Avalonia's cursors. Null (protocol reset) and any name
        /// without an Avalonia counterpart both fall back to null — the control's default
        /// pointer — because a wrong cursor misleads where a default merely underwhelms.
        /// </summary>
        private static Cursor? MapPointerShape(string? shape) => shape switch
        {
            "default" => Cursor.Default,
            "text" or "vertical-text" => new Cursor(StandardCursorType.Ibeam),
            "pointer" => new Cursor(StandardCursorType.Hand),
            "help" => new Cursor(StandardCursorType.Help),
            "wait" or "progress" => new Cursor(StandardCursorType.Wait),
            "crosshair" or "cell" => new Cursor(StandardCursorType.Cross),
            "not-allowed" or "no-drop" => new Cursor(StandardCursorType.No),
            "grab" or "grabbing" or "move" or "all-scroll" => new Cursor(StandardCursorType.SizeAll),
            "n-resize" or "s-resize" or "ns-resize" or "row-resize" => new Cursor(StandardCursorType.SizeNorthSouth),
            "e-resize" or "w-resize" or "ew-resize" or "col-resize" => new Cursor(StandardCursorType.SizeWestEast),
            "ne-resize" or "sw-resize" or "nesw-resize" => new Cursor(StandardCursorType.TopRightCorner),
            "nw-resize" or "se-resize" or "nwse-resize" => new Cursor(StandardCursorType.TopLeftCorner),
            "e" or "ne" or "n" or "nw" or "w" or "sw" or "s" or "se" => new Cursor(StandardCursorType.Arrow),
            _ => null,
        };

        /// <summary>
        /// The column a pointer position falls in, with the gutter taken off first.
        /// </summary>
        /// <remarks>
        /// The other end of the translation the render pushes. Without this a click lands one or two
        /// columns to the right of where it looks, and only when a gutter is switched on -- the kind
        /// of thing that gets reported as "selection is off by a bit" long after the change that did
        /// it.
        /// </remarks>
        private int PointerColumn(double x)
            => _charWidth > 0
                // Clamped at BOTH ends. Zero because a pointer inside the gutter is over no column,
                // and a negative one would flow into selection and mouse reporting as an index --
                // and Cols - 1 because the control is wider than a whole number of cells, so the
                // strip of padding at the right edge resolves to a column that does not exist. A
                // click there reported a column past the last one to the application, and asked the
                // selection for a cell off the end of the line.
                ? Math.Clamp((int)((x - Math.Max(0, GutterWidth)) / _charWidth), 0, Math.Max(0, _terminal.Cols - 1))
                : 0;

        /// <summary>The row under a pointer at <paramref name="y"/>, clamped to the grid.</summary>
        /// <remarks>
        /// The counterpart to <see cref="PointerColumn"/>, and it did not exist -- every caller
        /// divided by the row height and used the result unchecked. A pointer above the control gives
        /// a NEGATIVE row and one below the last line gives a row past the end, and both reached the
        /// selection and the application as if they were positions on screen. Both happen in ordinary
        /// use: a drag that leaves the control at speed reports well outside it, and a capture keeps
        /// delivering those events.
        /// </remarks>
        private int PointerRow(double y)
            => _charHeight > 0
                ? Math.Clamp((int)(y / _charHeight), 0, Math.Max(0, _terminal.Rows - 1))
                : 0;

    }
}
