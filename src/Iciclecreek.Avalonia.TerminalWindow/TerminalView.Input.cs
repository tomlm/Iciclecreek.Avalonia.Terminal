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
        /// Record where the editable input starts, if the shell has moved to a new row since the last time.
        /// Called wherever the user is about to interact with the line.
        /// </summary>
        private void NoteInputStart()
        {
            if (_semanticPrompt || !_inputStartPending || _terminal == null)
                return;

            _inputStartRow = _terminal.Buffer.YBase + _terminal.Buffer.Y;
            _inputStartCol = _terminal.Buffer.X;
            _inputStartPending = false;
        }

        /// <summary>
        /// Sends text to the running process, exactly as if it had been typed at the keyboard.
        /// </summary>
        /// <param name="text">The text to send. Sent verbatim — see the remarks about Enter.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <remarks>
        /// <para>The text is sent as-is, so a command is not submitted until you send a carriage return:
        /// <c>SendInputAsync("ls -la\r")</c>. Use <c>\r</c> rather than <c>\n</c> — that is what a terminal
        /// sends when Enter is pressed, and what a shell's line editor is waiting for.</para>
        /// <para>Does nothing when no process is running, rather than throwing, which matches
        /// <see cref="Kill"/> and keeps a host from having to guard every call against a terminal whose
        /// process has already exited. Writes are serialised against the view's own keyboard input, so
        /// injected text cannot interleave with a keystroke mid-sequence.</para>
        /// <para>Safe to call from any thread: it touches no Avalonia properties.</para>
        /// <para>Requested in #19, where the workaround in the wild was reflection over this type's private
        /// members — which is its own argument for the method existing.</para>
        /// </remarks>
        public Task SendInputAsync(string text, CancellationToken cancellationToken = default)
            => SendToPtyAsync(text, cancellationToken);

        /// <summary>
        /// Pastes text from the clipboard into the terminal.
        /// </summary>
        public async Task PasteAsync()
        {
            if (_ptyConnection == null)
                return;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
                return;

            // Bracketed paste MIME: with mode 5522 set the paste is ANNOUNCED — the emulator
            // emits the notification triple and the application fetches the formats it wants
            // with the token, so nothing is typed into the stream and nothing is bracketed
            // (the spec forbids sending both for one paste). The formats are read here, up
            // front, because the redemption arrives later on the pty stream where nothing can
            // await the OS clipboard.
            if (_terminal.PasteNotificationMode)
            {
                var paste = await BuildPasteAsync(clipboard);
                if (paste is null)
                    return;

                // An announced paste REPLACES a selection too, exactly as the classic path below does:
                // the user pressed Ctrl+V over selected text and expects it gone. Taken and sent before
                // the announce triple, so the deletion reaches the shell ahead of whatever the
                // application inserts when it redeems the token. Taking it is also what clears
                // _terminal.Selection - skipping it left the replaced text still highlighted.
                var deletion = TakeKeyboardSelectionDeletion();
                if (deletion.Length > 0)
                    await SendToPtyAsync(deletion);

                _terminal.Paste(paste);
                return;
            }

            var text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                // Wrap paste in bracketed paste sequences if mode is enabled
                if (_terminal.BracketedPasteMode)
                {
                    text = $"\u001b[200~{text}\u001b[201~";
                }

                // Pasting over a selection REPLACES it, the way typing over one does. Taken before the
                // text and written with it in a single call, so the deletion cannot lose the race against
                // whatever arrives next — the same reason typing does it that way.
                await SendToPtyAsync(TakeKeyboardSelectionDeletion() + text);
            }
        }

        /// <summary>
        /// Reads every clipboard format the paste can offer, NOW, so the later token redemption
        /// can be served synchronously. Text is offered as text/plain; files as text/uri-list
        /// (one file URI per line, the drag-and-drop convention); platform formats that already
        /// look like MIME types ride along untranslated for applications that know them.
        /// </summary>
        private static async Task<XT.TerminalPaste?> BuildPasteAsync(IClipboard clipboard)
        {
            var mimes = new List<string>();
            var data = new Dictionary<string, byte[]>();

            // Platform formats whose identifier already IS a MIME type (the norm on X11 and
            // Wayland) ride along untranslated for applications that know them.
            try
            {
                foreach (var format in await clipboard.GetDataFormatsAsync())
                {
                    if (!format.Identifier.Contains('/'))
                        continue;
                    var bytesFormat = DataFormat.CreateBytesPlatformFormat(format.Identifier);
                    if (await clipboard.TryGetValueAsync(bytesFormat) is { } bytes)
                    {
                        mimes.Add(format.Identifier);
                        data[format.Identifier] = bytes;
                    }
                }
            }
            catch
            {
                // An uncooperative clipboard offers whatever the probes below still find.
            }

            var text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text) && !data.ContainsKey("text/plain"))
            {
                mimes.Add("text/plain");
                data["text/plain"] = System.Text.Encoding.UTF8.GetBytes(text);
            }

            try
            {
                if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files && !data.ContainsKey("text/uri-list"))
                {
                    var uriList = string.Join('\n', files.Select(f => f.Path.AbsoluteUri));
                    mimes.Add("text/uri-list");
                    data["text/uri-list"] = System.Text.Encoding.UTF8.GetBytes(uriList);
                }
            }
            catch
            {
                // No files, then.
            }

            return mimes.Count == 0
                ? null
                : new XT.TerminalPaste(mimes, mime => data.TryGetValue(mime, out var bytes) ? bytes : null);
        }

        /// <summary>
        /// Copies selected text to the clipboard.
        /// </summary>
        /// <returns>True if text was copied, false if no selection.</returns>
        public async Task<bool> CopyAsync()
        {
            if (!_terminal.Selection.HasSelection)
                return false;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
                return false;

            var text = _terminal.Selection.GetSelectionText();
            if (!string.IsNullOrEmpty(text))
            {
                // Normalize line endings for the current platform
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Ensure Windows gets \r\n line endings
                    text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                }

                await clipboard.SetTextAsync(text);
                return true;
            }

            return false;
        }

        // True when the key is a modifier pressed on its own (no associated character),
        // e.g. the ⌘/Ctrl/Shift/Alt keys. Used so a bare modifier press doesn't clear
        // an active selection before the rest of a copy shortcut is typed.
        //
        // The lock keys belong here by the same test this list already applies — they produce no character,
        // so pressing one is not typing — and they were simply missed. They matter more now than they did
        // when the list was written, because auto-scroll also resumes on anything not in it: with them
        // absent, tapping CapsLock while reading scrollback both drops the selection and jumps the view
        // back to the prompt.
        private static bool IsModifierKey(Key key) => key switch
        {
            Key.LeftShift or Key.RightShift or
            Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or
            Key.LWin or Key.RWin or
            Key.CapsLock or Key.NumLock or Key.Scroll => true,
            _ => false,
        };

        /// <summary>
        /// The keystrokes that would remove the current selection, WITHOUT clearing it.
        /// </summary>
        /// <remarks>
        /// Split from <see cref="TakeKeyboardSelectionDeletion"/> so the question can be asked
        /// without being answered. The taking version clears the selection as it goes, which is right
        /// for a caller about to send the result and useless for one deciding whether it can -- and
        /// deciding is now necessary, because a chord is claimed before the await that would find
        /// out. Empty means this cannot be done, for any of the reasons below.
        /// </remarks>
        private string BuildKeyboardSelectionDeletion()
        {
            if (_kbSelAnchor is null || !_terminal.Selection.HasSelection)
                return string.Empty;
            if (_terminal.IsAlternateBufferActive)
                return string.Empty;

            int anchor = _kbSelAnchor.Value;
            int count = Math.Abs(anchor - _kbSelFocus);
            if (count == 0)
                return string.Empty;

            var backspace = EncodeSyntheticKey(Key.Back, XT.Input.Key.Backspace, "Backspace");
            if (string.IsNullOrEmpty(backspace))
                return string.Empty;

            // Backspace for both directions, rather than Delete for a forward selection.
            //
            // Forward-delete is not reliably bound. zsh started with no rc file does not know ESC[3~: it
            // swallows the ESC[3 and TYPES the tilde, so "hello world" became "hello wor~ld" instead of
            // losing a character. Arrow keys and Backspace are bound everywhere, so a forward selection
            // walks the cursor to the far end first and deletes backwards from there — same result, no
            // dependency on a binding the shell may not have.
            string keys;
            if (_kbSelFocus < anchor)
            {
                keys = string.Concat(Enumerable.Repeat(backspace, count));
            }
            else
            {
                var right = EncodeSyntheticKey(Key.Right, XT.Input.Key.RightArrow, "ArrowRight");
                if (string.IsNullOrEmpty(right))
                    return string.Empty;
                keys = string.Concat(Enumerable.Repeat(right, count))
                     + string.Concat(Enumerable.Repeat(backspace, count));
            }

            return keys;
        }

        /// <summary>
        /// <see cref="BuildKeyboardSelectionDeletion"/>, and clears the selection when there is one
        /// to give.
        /// </summary>
        /// <remarks>
        /// The clearing is deliberately conditional on having produced something. A selection dropped
        /// for a deletion that could not be built is a selection the user watched vanish with nothing
        /// happening to the line.
        /// </remarks>
        private string TakeKeyboardSelectionDeletion()
        {
            var keys = BuildKeyboardSelectionDeletion();
            if (keys.Length == 0)
                return string.Empty;

            _kbSelAnchor = null;
            _kbSelWholeInput = false;
            _terminal.Selection.ClearSelection();
            RequestPaint();

            return keys;
        }

        /// <summary>
        /// Shift + arrows / Home / End extend a buffer selection from the cursor, the way a
        /// text field does, instead of sending the modified-cursor escape sequence to the shell.
        /// </summary>
        /// <remarks>
        /// <para>Anchor and focus are caret BOUNDARIES (<c>row * Cols + col</c> over the gaps between
        /// cells), so one Shift+Right covers one cell and collapsing back onto the anchor clears the
        /// selection — exactly the arithmetic an editor does. The selection API itself is inclusive-cell,
        /// so the pair is converted at the end.</para>
        ///
        /// <para>Three chords reach here. Shift alone moves by a cell; Ctrl+Shift and Alt+Shift move by a
        /// word — Alt as well as Ctrl because on macOS Ctrl+arrow belongs to Mission Control and never
        /// arrives. Cmd+Shift+Left/Right is aliased to Shift+Home/End there, since a Mac keyboard has no
        /// Home or End key to press.</para>
        ///
        /// <para>Left alone in the alternate buffer: full-screen apps (vim, less, a TUI agent) draw their
        /// own selection and several bind Shift+arrow, so there the sequence still belongs to the app.</para>
        /// </remarks>
        private bool TryExtendKeyboardSelection(KeyEventArgs e)
        {
            // Only the navigation keys are claimed, and the check comes BEFORE anything is recorded: the
            // re-anchor below used to run first, so an unrelated shifted keystroke left an anchor set with
            // no selection behind it.
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End))
                return false;

            NoteInputStart();

            // Shift alone moves by a cell; Ctrl+Shift and Alt+Shift move by a WORD, matching every text
            // field. Alt is the same gesture on macOS, where Ctrl+arrow belongs to the window manager.
            //
            // Ctrl+Shift used to match neither this gate nor the word-motion one below, so it fell through
            // to the blanket selection-clear and then sent the modified-cursor sequence to the shell — which
            // moved the cursor by a word and dropped the selection on the way. Reported as #63.
            // Word-wise movement is horizontal only. Without the key check, Ctrl/Alt+Shift with Up, Down,
            // Home or End would be swallowed here too, and those chords belong to the application.
            bool byWord = e.Key is Key.Left or Key.Right
                          && e.KeyModifiers is (KeyModifiers.Control | KeyModifiers.Shift)
                                            or (KeyModifiers.Alt | KeyModifiers.Shift);

            // macOS keyboards have no Home/End, so Cmd+arrow is the platform's line-start/line-end and
            // Cmd+Shift+arrow is its select-to-line-edge. Treated as an alias for Shift+Home / Shift+End
            // rather than a second mechanism.
            bool toLineEdge = IsMacOS && e.KeyModifiers == (KeyModifiers.Meta | KeyModifiers.Shift);

            if (e.KeyModifiers != KeyModifiers.Shift && !byWord && !toLineEdge)
                return false;
            if (_terminal.IsAlternateBufferActive)
                return false;

            int cols = Math.Max(1, _terminal.Cols);
            int lastBoundary = cols * Math.Max(1, _terminal.Rows);

            // A selection dropped by anything else (a click, a plain keystroke) re-anchors at the cursor.
            if (_kbSelAnchor is null || !_terminal.Selection.HasSelection)
            {
                int cursorRow = Math.Clamp(
                    _terminal.Buffer.YBase + _terminal.Buffer.Y - _terminal.Buffer.ViewportY,
                    0, Math.Max(0, _terminal.Rows - 1));
                int cursorOrd = Math.Clamp(cursorRow * cols + _terminal.Buffer.X, 0, lastBoundary);
                _kbSelAnchor = cursorOrd;
                _kbSelFocus = cursorOrd;
            }

            var key = e.Key;
            if (toLineEdge)
            {
                // Only the horizontal pair aliases; Cmd+Shift+Up/Down is not a gesture this claims.
                if (key == Key.Left) key = Key.Home;
                else if (key == Key.Right) key = Key.End;
                else return false;
            }

            int focus = _kbSelFocus;
            switch (key)
            {
                case Key.Left: focus = byWord ? WordBoundary(focus, -1, cols, lastBoundary) : focus - 1; break;
                case Key.Right: focus = byWord ? WordBoundary(focus, +1, cols, lastBoundary) : focus + 1; break;
                case Key.Up: focus -= cols; break;
                case Key.Down: focus += cols; break;
                case Key.Home: focus = InputStartBoundary(cols, lastBoundary) is var st && st / cols == focus / cols
                                          ? st : focus - (focus % cols); break;
                case Key.End: focus = LineEndBoundary(focus, cols); break;
                default: return false;
            }

            int floor = InputStartBoundary(cols, lastBoundary);
            int ceiling = Math.Max(floor, InputEndBoundary(cols, lastBoundary));
            _kbSelFocus = Math.Clamp(focus, floor, ceiling);
            _kbSelWholeInput = false;   // the user is steering an edge again, so the caret means something

            int anchor = _kbSelAnchor.Value;
            if (_kbSelFocus == anchor)
            {
                _terminal.Selection.ClearSelection();

                // Selection gone means the gesture is over, so the anchor goes with it — otherwise the
                // caret stays pinned to this boundary and the next thing the shell prints moves the real
                // cursor out from under a caret that no longer follows it.
                _kbSelAnchor = null;
                _kbSelWholeInput = false;
            }
            else
            {
                // Boundaries → the inclusive run of cells between them.
                int first = Math.Min(anchor, _kbSelFocus);
                int last = Math.Max(anchor, _kbSelFocus) - 1;
                _terminal.Selection.StartSelection(first % cols, first / cols, XT.Selection.SelectionMode.Normal);
                _terminal.Selection.UpdateSelection(last % cols, last / cols);
                _terminal.Selection.EndSelection();
            }

            RequestPaint();
            return true;
        }

        /// <summary>
        /// Select the shell's editable input — what the user has typed — rather than the whole screen.
        /// </summary>
        /// <remarks>
        /// <para>Select-all at a prompt means the command being edited, not the scrollback and not the
        /// screenful of blanks around it. The emulator's own SelectAll takes the buffer, which is almost
        /// never what was wanted.</para>
        /// <para>Recorded as a KEYBOARD selection so it can be replaced or cut like any other. That needs
        /// the anchor to be where the shell's cursor actually is, because the deletion is expressed as
        /// keystrokes from there — so the cursor is moved to the end of the input first, which is where a
        /// select-all leaves the caret in any editor anyway.</para>
        /// </remarks>
        private async Task<bool> SelectInputAsync()
        {
            // Sample the prompt edge if it has not been sampled yet — pressing this before typing anything
            // is a perfectly ordinary thing to do, and the cursor is sitting on the answer.
            NoteInputStart();

            int cols = Math.Max(1, _terminal.Cols);
            int lastBoundary = cols * Math.Max(1, _terminal.Rows);

            int start = InputStartBoundary(cols, lastBoundary);
            int end = InputEndBoundary(cols, lastBoundary);

            if (end <= start)
            {
                // Nothing typed yet, so there is no input to select.
                _terminal.Selection.ClearSelection();
                _kbSelAnchor = null;
                _kbSelWholeInput = false;
                RequestPaint();
                return false;
            }

            var toEnd = _terminal.GenerateKeyInput(XT.Input.Key.End, XT.Input.KeyModifiers.None);
            if (!string.IsNullOrEmpty(toEnd))
                await SendToPtyAsync(toEnd).ConfigureAwait(false);

            _kbSelAnchor = end;
            _kbSelFocus = start;
            _kbSelWholeInput = true;

            _terminal.Selection.StartSelection(start % cols, start / cols, XT.Selection.SelectionMode.Normal);
            _terminal.Selection.UpdateSelection((end - 1) % cols, (end - 1) / cols);
            _terminal.Selection.EndSelection();
            RequestPaint();
            return true;
        }

        /// <summary>
        /// The lowest boundary a keyboard selection may reach: the start of the shell's editable input when
        /// that is on screen, otherwise the top of the viewport.
        /// </summary>
        /// <remarks>
        /// Selecting back over the prompt is never what the user meant — the prompt is not theirs to edit,
        /// and readline will not delete it either, so a selection covering it could not be replaced.
        /// Stopping the selection where the input starts keeps the two agreeing.
        /// </remarks>
        private int InputStartBoundary(int cols, int lastBoundary)
        {
            if (_inputStartRow < 0)
                return 0;

            int row = _inputStartRow - _terminal.Buffer.ViewportY;
            if (row < 0 || row >= _terminal.Rows)
                return 0;

            return Math.Clamp(row * cols + _inputStartCol, 0, lastBoundary);
        }

        protected override async void OnKeyUp(KeyEventArgs e)
        {
            // Only process input if this terminal has focus
            if (!IsFocused)
            {
                base.OnKeyUp(e);
                return;
            }

            // Capture the connection reference locally
            var ptyConnection = _ptyConnection;
            if (ptyConnection == null || _processExitHandled != 0)
            {
                base.OnKeyUp(e);
                return;
            }

            try
            {
                // Windows ConPTY limitation: Always send ESCAPE key in Win32 format
                bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                bool isEscapeKey = e.Key == Key.Escape;
                // The Escape exception does NOT apply while Kitty is negotiated. It exists because
                // ConPTY has no VT sequence for a plain Escape, so Win32 records are the only way to
                // deliver one -- but an application that asked for CSI-u is not reading VT sequences
                // for Escape either, it is reading CSI 27 u, and the terminal can send that. Without
                // this, every Escape on Windows took the Win32 path and a negotiated application
                // never received the encoding it had asked for.
                //
                // Win32 input MODE keeps its precedence unconditionally: that is a different
                // transport rather than a competing encoding, and a process reading INPUT_RECORDs is
                // reading them for every key.
                bool useWin32Format = _terminal.Win32InputMode
                                      || (isWindows && isEscapeKey && !_terminal.KittyKeyboardActive);

                if (useWin32Format)
                {
                    var sequence = GenerateWin32InputSequence(e, isKeyDown: false);
                    if (!string.IsNullOrEmpty(sequence))
                    {
                        // Marked BEFORE the await, not after. This handler is async void, so it
                        // returns to the routing at the first await and the event goes on bubbling
                        // while still marked unhandled -- the flag would arrive after whoever was
                        // going to act on it already had. Nothing between here and the send can
                        // decide not to send, so there is nothing to be premature about.
                        e.Handled = true;
                        await SendToPtyAsync(sequence).ConfigureAwait(false);
                    }

                    return;
                }

                // Releases exist as events at all only under the Kitty protocol -- the legacy
                // encodings describe presses and repeats, which is why key-up reached nothing but
                // the Win32 path before this. Whether a release is reported is the negotiated flags'
                // decision, and the generator answers null when they say not to.
                await TrySendKittyKeyAsync(e, XT.Input.KittyKeyboardEventType.Release).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_instanceId}] Error handling key up: {ex.Message}");
            }
            finally
            {
                // KeyDown -> TextInput -> KeyUp is the ordinary text-key lifecycle. Once KeyUp has
                // arrived, a marker left by a key with no TextInput cannot belong to future text.
                _win32RecordSentForThisStroke = false;
            }
        }

        protected override async void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);

            Debug.WriteLine($"[TerminalView] OnGotFocus: Source={e.Source?.GetType().Name}");

            // Reset blink state to visible when focused
            _cursorBlinkOn = true;
            if (CursorBlink)
            {
                _cursorBlinkTimer.Start();
            }

            if (_ptyConnection != null && _terminal.SendFocusEvents)
            {
                var sequence = _terminal.GenerateFocusEvent(true);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence);
                }
            }

            RequestPaint();
        }

        protected override async void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);

            // A key held while focus moves away never sends its release here, and would otherwise
            // stay in the set for ever -- so the next real press of it would report as a repeat.
            _keysHeld.Clear();

            Debug.WriteLine($"[TerminalView] OnLostFocus");

            // Stop blinking when not focused, but keep cursor visible (hollow block)
            _cursorBlinkTimer.Stop();
            _cursorBlinkOn = true;

            // Clear any preedit text when focus is lost
            _inputMethodClient?.ClearPreeditText();

            if (_ptyConnection != null && _terminal.SendFocusEvents)
            {
                var sequence = _terminal.GenerateFocusEvent(false);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence);
                }
            }

            RequestPaint();
        }

        private void OnTextInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
        {
            e.Client = _inputMethodClient;
        }

        private XT.Input.Key? ConvertAvaloniaKeyToXTermKey(Key key)
        {
            return key switch
            {
                Key.Enter => XT.Input.Key.Enter,
                Key.Back => XT.Input.Key.Backspace,
                Key.Tab => XT.Input.Key.Tab,
                Key.Escape => XT.Input.Key.Escape,
                Key.Up => XT.Input.Key.UpArrow,
                Key.Down => XT.Input.Key.DownArrow,
                Key.Left => XT.Input.Key.LeftArrow,
                Key.Right => XT.Input.Key.RightArrow,
                Key.Home => XT.Input.Key.Home,
                Key.End => XT.Input.Key.End,
                Key.PageUp => XT.Input.Key.PageUp,
                Key.PageDown => XT.Input.Key.PageDown,
                Key.Insert => XT.Input.Key.Insert,
                Key.Delete => XT.Input.Key.Delete,
                Key.F1 => XT.Input.Key.F1,
                Key.F2 => XT.Input.Key.F2,
                Key.F3 => XT.Input.Key.F3,
                Key.F4 => XT.Input.Key.F4,
                Key.F5 => XT.Input.Key.F5,
                Key.F6 => XT.Input.Key.F6,
                Key.F7 => XT.Input.Key.F7,
                Key.F8 => XT.Input.Key.F8,
                Key.F9 => XT.Input.Key.F9,
                Key.F10 => XT.Input.Key.F10,
                Key.F11 => XT.Input.Key.F11,
                Key.F12 => XT.Input.Key.F12,
                _ => null
            };
        }

        private XT.Input.KeyModifiers ConvertAvaloniaModifiers(KeyModifiers modifiers)
        {
            var result = XT.Input.KeyModifiers.None;

            if (modifiers.HasFlag(KeyModifiers.Shift))
                result |= XT.Input.KeyModifiers.Shift;
            if (modifiers.HasFlag(KeyModifiers.Control))
                result |= XT.Input.KeyModifiers.Control;
            if (modifiers.HasFlag(KeyModifiers.Alt))
                result |= XT.Input.KeyModifiers.Alt;

            return result;
        }

        /// <summary>
        /// The key's name as the Kitty keyboard protocol expects it — browser <c>ev.key</c> naming.
        /// </summary>
        /// <remarks>
        /// Empty for a key the protocol has no name for, which the caller treats as "not ours".
        /// The layout's own symbol is the fallback, so a character key reports what the user
        /// actually typed rather than what the physical key is engraved with.
        /// </remarks>
        private static string KittyKeyName(KeyEventArgs e) => e.Key switch
        {
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Escape => "Escape",
            Key.Return or Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Back => "Backspace",
            Key.CapsLock => "CapsLock",
            Key.Scroll => "ScrollLock",
            Key.NumLock => "NumLock",
            Key.PrintScreen or Key.Snapshot => "PrintScreen",
            Key.Pause => "Pause",
            Key.Apps => "ContextMenu",

            // The four the protocol reports as keys in their own right. Named without a side here
            // -- which one it was travels in Code, because to this protocol left and right Shift
            // are different keys.
            Key.LeftShift or Key.RightShift => "Shift",
            Key.LeftCtrl or Key.RightCtrl => "Control",
            Key.LeftAlt or Key.RightAlt => "Alt",
            Key.LWin or Key.RWin => "Meta",

            >= Key.F1 and <= Key.F24 => "F" + (e.Key - Key.F1 + 1).ToString(CultureInfo.InvariantCulture),

            _ => e.KeySymbol is { Length: 1 } symbol ? symbol : string.Empty,
        };

        /// <summary>
        /// The PHYSICAL key, named as the protocol expects — browser <c>ev.code</c> naming.
        /// </summary>
        /// <remarks>
        /// Avalonia's <see cref="PhysicalKey"/> follows the same specification, with two spellings
        /// that differ: a letter is <c>A</c> where the protocol wants <c>KeyA</c>, and the numpad is
        /// <c>NumPad7</c> where the protocol wants <c>Numpad7</c>. Both matter — the protocol reads
        /// this to find the base-layout key under a shifted character, and to tell the numpad keys
        /// apart from the digits above the letters, whose key NAME is identically "7".
        /// </remarks>
        private static string KittyPhysicalCode(KeyEventArgs e)
        {
            var physical = e.PhysicalKey;
            if (physical == PhysicalKey.None)
                return string.Empty;

            var name = physical.ToString();

            if (name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z')
                return "Key" + name;

            if (name.StartsWith("NumPad", StringComparison.Ordinal))
                return "Numpad" + name.Substring("NumPad".Length);

            return name;
        }

        /// <summary>Builds the event the Kitty generator reads, or false when the key has no name it knows.</summary>
        private static bool TryBuildKittyKeyEvent(KeyEventArgs e, out XT.Options.KeyEvent ev)
        {
            var name = KittyKeyName(e);
            if (name.Length == 0)
            {
                ev = null!;
                return false;
            }

            ev = new XT.Options.KeyEvent
            {
                Key = name,
                Code = KittyPhysicalCode(e),
                CtrlKey = e.KeyModifiers.HasFlag(KeyModifiers.Control),
                AltKey = e.KeyModifiers.HasFlag(KeyModifiers.Alt),
                ShiftKey = e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                MetaKey = e.KeyModifiers.HasFlag(KeyModifiers.Meta),
            };
            return true;
        }

        /// <summary>
        /// Sends a key through the Kitty keyboard protocol, if an application has negotiated it.
        /// </summary>
        /// <returns>
        /// True when this key belonged to the protocol and has been dealt with — INCLUDING when the
        /// answer was to send nothing.
        /// </returns>
        /// <remarks>
        /// A null from the generator means this event sends nothing: a bare modifier press, or a
        /// release the negotiated flags say not to report. It does NOT mean fall through to the
        /// legacy generators, and treating it that way would put the original bug back for exactly
        /// those keys -- an application would receive a legacy encoding it had told the terminal it
        /// was no longer reading. So the event is consumed either way, which also stops a TextInput
        /// from arriving afterwards and sending the character down the legacy path behind our back.
        /// </remarks>
        /// <param name="pendingErase">
        /// Keystrokes that must go out AHEAD of this key — the deletion of a keyboard selection the
        /// caller has already consumed. Prepended rather than sent separately, so the deletion and
        /// the character that replaces it are one write: sent as two, the next keystroke races in
        /// between and "there" typed over a selection arrives as "heret". Ignored when this returns
        /// false, since the key was not claimed and the caller still owns the deletion.
        /// </param>
        private async Task<bool> TrySendKittyKeyAsync(KeyEventArgs e, XT.Input.KittyKeyboardEventType eventType,
                                                      string pendingErase = "")
        {
            if (!_terminal.KittyKeyboardActive)
            {
                // Held keys are only meaningful while the protocol is. An application popping its
                // flags -- which every full-screen one does on exit -- left the set populated, so the
                // next press of a key that happened to be down at that moment reported as a REPEAT of
                // a press the new application never saw.
                _keysHeld.Clear();
                return false;
            }

            if (!TryBuildKittyKeyEvent(e, out var ev))
                return false;

            // A key-down for a key already held is the OS repeating it. Add returns false when the
            // entry was already there, which is exactly that question.
            if (eventType == XT.Input.KittyKeyboardEventType.Press
                && !_keysHeld.Add((e.PhysicalKey, e.Key)))
            {
                eventType = XT.Input.KittyKeyboardEventType.Repeat;
            }
            else if (eventType == XT.Input.KittyKeyboardEventType.Release)
            {
                // Only for a key whose PRESS this path actually sent. Remove answers that: the entry
                // is put there by the press a few lines up, so its absence means the press never got
                // this far.
                //
                // Plenty of presses do not. Shift+arrow extends a selection and returns; Ctrl+Shift+C
                // copies; the whole macOS Meta family is claimed above. Every one of those still
                // produces a key-up, and this method used to encode a release for it -- so an
                // application that negotiated event types saw releases with no matching press, and a
                // key it had never been told was down going up.
                //
                // Consumed rather than passed on, which is the same answer the press got: the host
                // dealt with this keystroke, and both halves of it belong to the host.
                if (!_keysHeld.Remove((e.PhysicalKey, e.Key)))
                {
                    e.Handled = true;
                    return true;
                }
            }

            var sequence = _terminal.GenerateKittyKeyInput(ev, eventType);

            e.Handled = true;

            // Backspace and Delete over a selection remove the SELECTION, not one character beyond
            // it, so for those two the deletion replaces the key's own sequence instead of preceding
            // it -- the same rule the legacy path below applies.
            if (pendingErase.Length > 0 && e.Key is Key.Back or Key.Delete)
                sequence = pendingErase;
            else
                sequence = pendingErase + sequence;

            if (!string.IsNullOrEmpty(sequence))
                await SendToPtyAsync(sequence).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Whether an OSC 52 / Kitty 5522 selection target names the one clipboard a host has.
        /// </summary>
        /// <remarks>
        /// <c>c</c> is the clipboard, and <c>s</c> ("select") is what xterm defaults an empty Pc to
        /// (<c>s0</c>) — both mean the system clipboard here. The primary and secondary selections
        /// (<c>p</c>, <c>q</c>) and the cut buffers (<c>0</c>-<c>7</c>) are DECLINED rather than
        /// aliased onto it: Avalonia has no primary-selection API, and an X11-era program that writes
        /// primary on every selection change would otherwise replace the user's real clipboard every
        /// time they dragged over some text.
        /// </remarks>
        private static bool IsClipboardTarget(string target)
            => target.Contains('c') || target.Contains('s');

        /// <summary>
        /// OSC 52 / Kitty OSC 5522 write: the program put something on the clipboard. Only text
        /// is forwarded — the one thing every platform clipboard can hold — and empty text is
        /// the protocol's clear idiom, honoured with an actual clear.
        /// </summary>
        private void OnTerminalClipboardWriteRequested(object? sender, XT.Events.TerminalEvents.ClipboardWriteEventArgs e)
        {
            if (!IsClipboardTarget(e.Target))
                return;

            // The WHOLE transfer is searched for the text, not just Formats[0] (which is all
            // e.MimeType and e.Data are). A Kitty 5522 write carries its formats in transmission
            // order, so an image/png that led used to make this return and drop the text/plain
            // behind it. text/plain is preferred over any other text/* for the same reason e.Text
            // is not used here: e.Text answers with the FIRST text/*, so a transfer leading with an
            // empty text/html read as the clear idiom and wiped the clipboard the text/plain behind
            // it was about to fill.
            var chosen = -1;
            for (var i = 0; i < e.Formats.Count; i++)
            {
                if (e.Formats[i].MimeType == "text/plain") { chosen = i; break; }
                if (chosen < 0 && e.Formats[i].MimeType.StartsWith("text/", StringComparison.Ordinal))
                    chosen = i;
            }
            if (chosen < 0)
                return;   // nothing a platform clipboard can hold as text; the transfer is declined whole

            var text = Encoding.UTF8.GetString(e.Formats[chosen].Data);
            Dispatcher.UIThread.Post(() => _ = QueueClipboardWriteAsync(text));
        }

        /// <summary>
        /// Puts <paramref name="text"/> on the clipboard, after everything asked for before it.
        /// </summary>
        /// <remarks>
        /// The writes used to be started fire-and-forget, one per OSC 52, so two arriving close
        /// together raced -- and the clipboard ended up holding whichever finished last, which is not
        /// the same as whichever was asked for last. A program writing a value and then correcting it
        /// could leave the first value standing.
        ///
        /// Posts to the dispatcher run in order, so entering this gate in that order is enough to
        /// keep them in it. Waiting here rather than at the post keeps the ordering without blocking
        /// the UI thread.
        /// </remarks>
        private Task QueueClipboardWriteAsync(string text)
        {
            // Appended to the tail and the tail replaced, both on the UI thread, so the order this
            // runs in is the order the posts arrived in.
            //
            // ContinueWith rather than awaiting the predecessor, because a predecessor that FAULTED
            // must not take its successors with it: one clipboard write failing is not a reason to
            // stop making the next one.
            _clipboardWrites = _clipboardWrites.ContinueWith(
                _ => WriteToClipboardAsync(text),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();

            return _clipboardWrites;
        }

        private async Task WriteToClipboardAsync(string text)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
                return;

            try
            {
                if (text.Length == 0)
                    await clipboard.ClearAsync();
                else
                    await clipboard.SetTextAsync(text);
            }
            catch
            {
                // Awaited rather than discarded so a failure cannot escape as an unobserved task
                // exception. There is nobody to report it to: the pty stream cannot wait on the OS
                // clipboard, and the program that asked has no way to be told.
            }
        }

        /// <summary>
        /// OSC 52 / Kitty OSC 5522 read: the program asked for the clipboard. Avalonia's
        /// clipboard is asynchronous, which is exactly what the emulator's Defer/Respond pair
        /// exists for — the handler returns immediately and the answer is emitted when the
        /// await completes. Only reachable when the embedding application opted in via
        /// <c>Options.ClipboardReadEnabled</c>.
        /// </summary>
        /// <remarks>
        /// Defer() has to happen INLINE, on the reader thread: the emulator reads <c>e.Deferred</c>
        /// the moment this handler returns, so posting the whole body would read as a decline.
        /// Everything after it goes to the UI thread, which is both where Avalonia's clipboard is
        /// thread-affine and the single thread <c>Respond</c> asks to be called from — Kitty 5522
        /// closes its reply with a plain <c>--outstanding</c>, so two answers landing on two
        /// thread-pool continuations could interleave the decrement and leave the program waiting
        /// forever for a DONE that never comes.
        /// </remarks>
        private void OnTerminalClipboardReadRequested(object? sender, XT.Events.TerminalEvents.ClipboardReadEventArgs e)
        {
            if (!IsClipboardTarget(e.Target))
                return;   // declined: OSC 52 stays silent, 5522 counts the mime unavailable

            // Kitty's "list what you have" query. It is a MIME position holding a full stop rather
            // than a mime, and it was falling through the text/ test and being declined -- so a
            // program that politely asked what the clipboard could give it was told "nothing", and
            // then asked for nothing.
            //
            // Answered synchronously, since the answer does not depend on the clipboard: it is what
            // this host is able to supply, which is the same list either way.
            if (e.MimeType == ".")
            {
                e.Text = SupportedClipboardMime;
                return;
            }

            // ONLY text/plain, and the empty mime OSC 52 sends, which means the same thing.
            //
            // Any text/* used to be accepted and then answered with the plain text under the label
            // that was asked for. A program requesting text/html received the plain text and was
            // told it was HTML -- which is worse than a decline, because a decline is a fact it can
            // act on and this is a lie it cannot detect.
            if (e.MimeType.Length != 0 && e.MimeType != SupportedClipboardMime)
                return;

            e.Defer();
            _ = Dispatcher.UIThread.InvokeAsync(() => RespondFromClipboardAsync(e));
        }

        private async Task RespondFromClipboardAsync(XT.Events.TerminalEvents.ClipboardReadEventArgs e)
        {
            string? text;
            try
            {
                // Read here rather than on the reader thread: the visual-tree walk is UI state too.
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                text = clipboard is null ? null : await clipboard.TryGetTextAsync();
            }
            catch
            {
                text = null;   // a locked or unavailable clipboard declines rather than throws
            }

            // Always answered, even with null: a deferred request the host never completes leaves a
            // 5522 read hanging, which is what the args' contract warns about.
            //
            // Under _terminalLock, which is this host's answer to that contract's other line: "call
            // it from the thread the terminal is driven on". This is the UI thread and the terminal
            // is driven from the pty reader, so the lock is what makes the two exclusive. Responding
            // writes a reply into the emulator, and doing that while the reader is inside
            // Terminal.Write is a write into a parser mid-sequence.
            //
            // Taken here rather than around the await above: holding it across a clipboard read --
            // which on Windows is a round trip to another process -- would stall the reader for as
            // long as that took.
            lock (_terminalLock)
            {
                e.Respond(text);
            }
        }

        private bool TryGetPrintableChar(KeyEventArgs e, out char character)
        {
            // Prefer the symbol provided by Avalonia (already respects layout)
            if (!string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol.Length == 1 && !char.IsControl(e.KeySymbol[0]))
            {
                character = e.KeySymbol[0];
                return true;
            }

            // Fallback mapping for cases where KeySymbol is empty (e.g., Consolonia, or Alt+<char> on some platforms)
            var result = TryMapKeyToChar(e.Key, e.KeyModifiers, out character);
            return result;
        }

        private bool TryMapKeyToChar(Key key, KeyModifiers modifiers, out char character)
        {
            character = default;
            bool hasShift = modifiers.HasFlag(KeyModifiers.Shift);

            // Letters A-Z
            if (key >= Key.A && key <= Key.Z)
            {
                var offset = key - Key.A;
                character = (char)((hasShift ? 'A' : 'a') + offset);
                return true;
            }

            // Numbers 0-9 (with shift symbols for US keyboard)
            if (key >= Key.D0 && key <= Key.D9)
            {
                if (hasShift)
                {
                    // Shift + number = symbol (US keyboard layout)
                    character = key switch
                    {
                        Key.D1 => '!',
                        Key.D2 => '@',
                        Key.D3 => '#',
                        Key.D4 => '$',
                        Key.D5 => '%',
                        Key.D6 => '^',
                        Key.D7 => '&',
                        Key.D8 => '*',
                        Key.D9 => '(',
                        Key.D0 => ')',
                        _ => default
                    };
                }
                else
                {
                    var offset = key - Key.D0;
                    character = (char)('0' + offset);
                }
                return character != default;
            }

            // Numpad numbers
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                var offset = key - Key.NumPad0;
                character = (char)('0' + offset);
                return true;
            }

            // Common punctuation and OEM keys (US keyboard layout)
            character = key switch
            {
                Key.Space => ' ',
                Key.OemPeriod => hasShift ? '>' : '.',
                Key.OemComma => hasShift ? '<' : ',',
                Key.OemMinus => hasShift ? '_' : '-',
                Key.OemPlus => hasShift ? '+' : '=',
                Key.OemSemicolon => hasShift ? ':' : ';',
                Key.OemQuotes => hasShift ? '"' : '\'',
                Key.OemTilde => hasShift ? '~' : '`',
                Key.OemOpenBrackets => hasShift ? '{' : '[',
                Key.OemCloseBrackets => hasShift ? '}' : ']',
                Key.OemPipe => hasShift ? '|' : '\\',
                Key.OemBackslash => hasShift ? '|' : '\\',
                Key.OemQuestion => hasShift ? '?' : '/',
                Key.Multiply => '*',
                Key.Add => '+',
                Key.Subtract => '-',
                Key.Divide => '/',
                Key.Decimal => '.',
                _ => default
            };

            return character != default;
        }

        /// <summary>
        /// Copies a decoded picture's pixels into a locked framebuffer.
        /// </summary>
        /// <remarks>
        /// Both channel order and stride are handled here, and both fail silently rather than throwing --
        /// as a picture with its colours swapped, or one whose rows are sheared. Split out so they can be
        /// asserted directly: the headless platform's WriteableBitmap hands back a fresh buffer on every
        /// Lock, so nothing written through one can be read back from the bitmap itself.
        /// </remarks>
        internal static void CopyPixels(XT.Graphics.TerminalImage image, IntPtr destination, int destinationRowBytes)
        {
            // The decoder hands over a plain array in the layout the bitmap wants, so this is a copy rather
            // than a conversion.
            //
            // CurrentPixels rather than Pixels: for an animation those are the frame being shown, and for
            // a still picture they are the same array. Uploading Pixels instead would draw every animation
            // frozen on its first frame, which looks like the clock never firing rather than like the wrong
            // buffer being read.
            if (!MemoryMarshal.TryGetArray(image.CurrentPixels, out ArraySegment<byte> source))
                source = new ArraySegment<byte>(image.CurrentPixels.ToArray());

            var sourceStride = image.Stride;
            if (destinationRowBytes == sourceStride)
            {
                // Same stride, so the whole picture is one contiguous run.
                Marshal.Copy(source.Array!, source.Offset, destination, sourceStride * image.PixelHeight);
                return;
            }

            // A bitmap is free to pad its rows; copying the picture as one block would then shear it.
            for (int y = 0; y < image.PixelHeight; y++)
            {
                Marshal.Copy(source.Array!, source.Offset + (y * sourceStride),
                             IntPtr.Add(destination, y * destinationRowBytes), sourceStride);
            }
        }


        /// <summary>
        /// Generates a Win32 INPUT_RECORD format escape sequence.
        /// Format: ESC [ Vk ; Sc ; Uc ; Kd ; Cs ; Rc _
        /// </summary>
        private string GenerateWin32InputSequence(KeyEventArgs e, bool isKeyDown)
        {
            var vk = ConvertAvaloniaKeyToVirtualKey(e.Key);

            // If we can't get a virtual key code, we can't generate a Win32 sequence
            if (vk == 0)
            {
                Debug.WriteLine($"[TerminalView] Win32: No VK for Key={e.Key}");
                return string.Empty;
            }

            // Get scan code (we use 0 as we don't have direct access to hardware scan codes)
            var scanCode = 0;

            // Get unicode character - first try KeySymbol, then fall back to key mapping
            // Note: Special keys (arrows, Enter, etc.) have unicodeChar=0 which is correct
            int unicodeChar = 0;
            if (!string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol.Length >= 1)
            {
                unicodeChar = char.ConvertToUtf32(e.KeySymbol, 0);
            }
            else if (TryMapKeyToChar(e.Key, e.KeyModifiers, out var mappedChar))
            {
                // Fallback for Consolonia where KeySymbol is empty
                unicodeChar = mappedChar;
            }
            // Special case: Enter key should send CR (0x0D)
            else if (e.Key == Key.Enter)
            {
                unicodeChar = 0x0D;
            }
            // Special case: Tab key should send Tab (0x09)
            else if (e.Key == Key.Tab)
            {
                unicodeChar = 0x09;
            }
            // Special case: Backspace should send BS (0x08)
            else if (e.Key == Key.Back)
            {
                unicodeChar = 0x08;
            }
            // Special case: Escape should send ESC (0x1B)
            else if (e.Key == Key.Escape)
            {
                unicodeChar = 0x1B;
            }
            // Special case: Space
            else if (e.Key == Key.Space)
            {
                unicodeChar = 0x20;
            }

            // If Ctrl is pressed and this is a printable character, prefer the corresponding control code.
            // This improves compatibility for terminal apps that expect ^X (0x18), ^C (0x03), etc.
            // even when the underlying transport is Win32 INPUT_RECORD format.
            //
            // NOT for AltGr, which Windows spells as Ctrl+Alt and which composed this character on
            // purpose. Folding it to a control code threw away the only thing the keystroke produced:
            // under cmd.exe a German layout could not type @, and the process received NUL instead.
            if ((e.KeyModifiers & KeyModifiers.Control) != 0 && unicodeChar != 0
                && !IsAltGrComposed(e, out _))
            {
                // Ctrl+A..Z => 0x01..0x1A
                if (unicodeChar >= 'a' && unicodeChar <= 'z')
                    unicodeChar = unicodeChar - 'a' + 1;
                else if (unicodeChar >= 'A' && unicodeChar <= 'Z')
                    unicodeChar = unicodeChar - 'A' + 1;
                else
                {
                    // Common Ctrl+<punct> mappings
                    unicodeChar = unicodeChar switch
                    {
                        0x20 => 0x00, // Ctrl+Space => NUL
                        '@' => 0x00,  // Ctrl+@ => NUL
                        '[' => 0x1B,  // Ctrl+[ => ESC
                        '\\' => 0x1C, // Ctrl+\\ => FS
                        ']' => 0x1D,  // Ctrl+] => GS
                        '^' => 0x1E,  // Ctrl+^ => RS
                        '_' => 0x1F,  // Ctrl+_ => US
                        '?' => 0x7F,  // Ctrl+? => DEL
                        _ => unicodeChar
                    };
                }
            }

            // Build control key state flags
            var controlKeyState = GetWin32ControlKeyState(e.KeyModifiers, e.Key);

            // AltGr is RIGHT alt plus left control on the wire -- that is how Windows reports it, and
            // how a console application recognises it. Reporting left Alt described a chord the user
            // had not pressed, and PSReadLine binds Alt+key.
            if (IsAltGrComposed(e, out _))
            {
                controlKeyState &= ~Win32ControlKeyState.LeftAltPressed;
                controlKeyState |= Win32ControlKeyState.RightAltPressed;
            }

            return Win32Record(vk, scanCode, unicodeChar, isKeyDown, controlKeyState);
        }

        /// <summary>
        /// One Win32 INPUT_RECORD, in the wire form ConPTY reads.
        /// </summary>
        /// <remarks>
        /// Split out from <see cref="GenerateWin32InputSequence"/> so a SYNTHETIC key — one this view
        /// presses on the user's behalf, with no <see cref="KeyEventArgs"/> behind it — is encoded by
        /// the same formatting as a real one rather than a second copy of the format string that can
        /// drift from it. See <see cref="EncodeSyntheticKey"/> and <see cref="Win32TextRecords"/>.
        /// </remarks>
        private static string Win32Record(int vk, int scanCode, int unicodeChar, bool isKeyDown,
                                          Win32ControlKeyState controlKeyState)
        {
            // Repeat count (always 1 for our purposes)
            var repeatCount = 1;

            // Format: ESC [ Vk ; Sc ; Uc ; Kd ; Cs ; Rc _
            return $"\u001b[{vk};{scanCode};{unicodeChar};{(isKeyDown ? 1 : 0)};{(int)controlKeyState};{repeatCount}_";
        }

        /// <summary>
        /// Composed text as Win32 INPUT_RECORDs, one press and release per UTF-16 code unit.
        /// </summary>
        /// <remarks>
        /// Through VK_PACKET, which exists for exactly this: a character with no key behind it.
        /// Windows' own IME injection uses it, so a console application reading records already
        /// knows what to do with one -- take the unicode field and ignore the virtual key.
        /// </remarks>
        private static string Win32TextRecords(string text)
        {
            var sb = new StringBuilder();

            // KEY_EVENT_RECORD.UnicodeChar is a WCHAR, not a Unicode scalar. Non-BMP characters
            // therefore travel as two records containing the UTF-16 surrogate pair, just as they do
            // through the Windows console APIs.
            foreach (var codeUnit in text)
            {
                sb.Append(Win32Record(VirtualKeyPacket, 0, codeUnit, isKeyDown: true, Win32ControlKeyState.None));
                sb.Append(Win32Record(VirtualKeyPacket, 0, codeUnit, isKeyDown: false, Win32ControlKeyState.None));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Converts Avalonia KeyModifiers to Win32 control key state flags.
        /// </summary>
        private static Win32ControlKeyState GetWin32ControlKeyState(KeyModifiers modifiers, Key key)
        {
            var state = Win32ControlKeyState.None;

            if (modifiers.HasFlag(KeyModifiers.Shift))
                state |= Win32ControlKeyState.ShiftPressed;

            if (modifiers.HasFlag(KeyModifiers.Control))
                state |= Win32ControlKeyState.LeftCtrlPressed;

            if (modifiers.HasFlag(KeyModifiers.Alt))
                state |= Win32ControlKeyState.LeftAltPressed;

            // The key ITSELF says which side, when the key is a modifier. Avalonia's KeyModifiers
            // carries no handedness -- Alt is Alt whichever one is down -- so a right modifier was
            // reported as the left one, and a program watching for RIGHT_ALT (which is how Windows
            // spells AltGr) never saw it.
            if (key == Key.RightAlt)
            {
                state &= ~Win32ControlKeyState.LeftAltPressed;
                state |= Win32ControlKeyState.RightAltPressed;
            }

            if (key == Key.RightCtrl)
            {
                state &= ~Win32ControlKeyState.LeftCtrlPressed;
                state |= Win32ControlKeyState.RightCtrlPressed;
            }

            // Lock state, which was never reported at all. A console application asking whether Caps
            // Lock is on -- and readline-style line editors do -- got told it never is.
            foreach (var (locked, flag) in LockStates())
            {
                if (locked)
                    state |= flag;
            }

            // Mark enhanced keys (navigation keys, etc.)
            if (IsEnhancedKey(key))
                state |= Win32ControlKeyState.EnhancedKey;

            return state;
        }

        /// <summary>The lock keys that are currently on, as the flags that report them.</summary>
        /// <remarks>
        /// Read from the platform rather than tracked, because a lock can be toggled while this
        /// control does not have focus and a tracked copy would then be wrong until the next press.
        /// Windows-only: the P/Invoke does not exist elsewhere, and neither does Win32 input mode.
        /// </remarks>
        private static IEnumerable<(bool On, Win32ControlKeyState Flag)> LockStates()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                yield break;

            yield return (IsToggled(VirtualKeyCapital), Win32ControlKeyState.CapsLockOn);
            yield return (IsToggled(VirtualKeyNumLock), Win32ControlKeyState.NumLockOn);
            yield return (IsToggled(VirtualKeyScroll), Win32ControlKeyState.ScrollLockOn);
        }

        /// <summary>Whether a toggle key is currently ON, which is the LOW bit of its key state.</summary>
        private static bool IsToggled(int virtualKey)
        {
            try { return (GetKeyState(virtualKey) & 1) != 0; }
            catch (EntryPointNotFoundException) { return false; }
            catch (DllNotFoundException) { return false; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        /// <summary>
        /// Whether this event is AltGr producing a character, rather than a real Ctrl+Alt chord.
        /// </summary>
        /// <remarks>
        /// <para>Neither Windows nor X11 has a distinct AltGr modifier: both report it as Ctrl+Alt,
        /// so the two are the same event as far as anything here can see. What tells them apart is
        /// the CHARACTER. AltGr composes a different one -- AltGr+Q is @ on a German layout -- while
        /// a genuine Ctrl+Alt+Q still reports q, because no composition happened.</para>
        /// <para>So the test is: both modifiers held, no Meta, a single printable symbol, and that
        /// symbol is not just the key's own unshifted character. That declines Ctrl+Alt+Q and accepts
        /// AltGr+Q, which is exactly the distinction a user makes.</para>
        /// <para>It cannot be perfect -- a layout where AltGr maps a key to itself is
        /// indistinguishable, and so is Ctrl+Alt on a key with no AltGr mapping. Both of those end up
        /// treated as the chord, which is the safer way to be wrong: a missing symbol is a nuisance,
        /// a control code sent to a shell is not.</para>
        /// </remarks>
        private bool IsAltGrComposed(KeyEventArgs e, out char composed)
        {
            composed = '\0';

            const KeyModifiers ctrlAlt = KeyModifiers.Control | KeyModifiers.Alt;
            if ((e.KeyModifiers & ctrlAlt) != ctrlAlt)
                return false;

            // Meta is never part of AltGr on any platform that spells it this way.
            if ((e.KeyModifiers & KeyModifiers.Meta) != 0)
                return false;

            if (e.KeySymbol is not { Length: 1 } symbol)
                return false;

            var c = symbol[0];
            if (char.IsControl(c))
                return false;

            // The key's own character means nothing was composed, so this is the chord.
            if (TryMapKeyToChar(e.Key, KeyModifiers.None, out var plain)
                && char.ToLowerInvariant(plain) == char.ToLowerInvariant(c))
                return false;

            composed = c;
            return true;
        }

        /// <summary>
        /// Determines if a key is an "enhanced" key (extended keyboard keys).
        /// </summary>
        private static bool IsEnhancedKey(Key key)
        {
            return key switch
            {
                Key.Insert or Key.Delete or Key.Home or Key.End or
                Key.PageUp or Key.PageDown or Key.Up or Key.Down or
                Key.Left or Key.Right or Key.Divide or
                Key.NumLock or Key.RightCtrl or Key.RightAlt or
                Key.PrintScreen or Key.Pause => true,
                _ => false
            };
        }

        /// <summary>
        /// Converts Avalonia Key to Windows Virtual Key code.
        /// </summary>
        private static int ConvertAvaloniaKeyToVirtualKey(Key key)
        {
            return key switch
            {
                // Letters
                Key.A => 0x41,
                Key.B => 0x42,
                Key.C => 0x43,
                Key.D => 0x44,
                Key.E => 0x45,
                Key.F => 0x46,
                Key.G => 0x47,
                Key.H => 0x48,
                Key.I => 0x49,
                Key.J => 0x4A,
                Key.K => 0x4B,
                Key.L => 0x4C,
                Key.M => 0x4D,
                Key.N => 0x4E,
                Key.O => 0x4F,
                Key.P => 0x50,
                Key.Q => 0x51,
                Key.R => 0x52,
                Key.S => 0x53,
                Key.T => 0x54,
                Key.U => 0x55,
                Key.V => 0x56,
                Key.W => 0x57,
                Key.X => 0x58,
                Key.Y => 0x59,
                Key.Z => 0x5A,

                // Numbers
                Key.D0 => 0x30,
                Key.D1 => 0x31,
                Key.D2 => 0x32,
                Key.D3 => 0x33,
                Key.D4 => 0x34,
                Key.D5 => 0x35,
                Key.D6 => 0x36,
                Key.D7 => 0x37,
                Key.D8 => 0x38,
                Key.D9 => 0x39,

                // Function keys
                Key.F1 => 0x70,
                Key.F2 => 0x71,
                Key.F3 => 0x72,
                Key.F4 => 0x73,
                Key.F5 => 0x74,
                Key.F6 => 0x75,
                Key.F7 => 0x76,
                Key.F8 => 0x77,
                Key.F9 => 0x78,
                Key.F10 => 0x79,
                Key.F11 => 0x7A,
                Key.F12 => 0x7B,
                Key.F13 => 0x7C,
                Key.F14 => 0x7D,
                Key.F15 => 0x7E,
                Key.F16 => 0x7F,
                Key.F17 => 0x80,
                Key.F18 => 0x81,
                Key.F19 => 0x82,
                Key.F20 => 0x83,
                Key.F21 => 0x84,
                Key.F22 => 0x85,
                Key.F23 => 0x86,
                Key.F24 => 0x87,

                // Navigation keys
                Key.Left => 0x25,
                Key.Up => 0x26,
                Key.Right => 0x27,
                Key.Down => 0x28,
                Key.Home => 0x24,
                Key.End => 0x23,
                Key.PageUp => 0x21,
                Key.PageDown => 0x22,
                Key.Insert => 0x2D,
                Key.Delete => 0x2E,

                // Control keys
                Key.Back => 0x08,
                Key.Tab => 0x09,
                Key.Enter => 0x0D,
                Key.Escape => 0x1B,
                Key.Space => 0x20,
                Key.Pause => 0x13,
                Key.CapsLock => 0x14,
                Key.NumLock => 0x90,
                Key.Scroll => 0x91,
                Key.PrintScreen => 0x2C,

                // Modifier keys
                Key.LeftShift => 0x10,
                Key.RightShift => 0x10,
                Key.LeftCtrl => 0x11,
                Key.RightCtrl => 0x11,
                Key.LeftAlt => 0x12,
                Key.RightAlt => 0x12,
                Key.LWin => 0x5B,
                Key.RWin => 0x5C,

                // Numpad
                Key.NumPad0 => 0x60,
                Key.NumPad1 => 0x61,
                Key.NumPad2 => 0x62,
                Key.NumPad3 => 0x63,
                Key.NumPad4 => 0x64,
                Key.NumPad5 => 0x65,
                Key.NumPad6 => 0x66,
                Key.NumPad7 => 0x67,
                Key.NumPad8 => 0x68,
                Key.NumPad9 => 0x69,
                Key.Multiply => 0x6A,
                Key.Add => 0x6B,
                Key.Separator => 0x6C,
                Key.Subtract => 0x6D,
                Key.Decimal => 0x6E,
                Key.Divide => 0x6F,

                // OEM keys
                Key.OemSemicolon => 0xBA,
                Key.OemPlus => 0xBB,
                Key.OemComma => 0xBC,
                Key.OemMinus => 0xBD,
                Key.OemPeriod => 0xBE,
                Key.OemQuestion => 0xBF,
                Key.OemTilde => 0xC0,
                Key.OemOpenBrackets => 0xDB,
                Key.OemPipe => 0xDC,
                Key.OemCloseBrackets => 0xDD,
                Key.OemQuotes => 0xDE,
                Key.OemBackslash => 0xE2,

                _ => 0
            };
        }

    }
}
