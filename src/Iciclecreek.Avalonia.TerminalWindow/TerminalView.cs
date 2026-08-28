using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
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

    public class TerminalView : Control, ICustomHitTest
    {
        /// <summary>
        /// Avalonia hit-tests what a control actually DREW, not the rectangle it occupies — the same
        /// rule that makes a <c>Grid</c> with no Background invisible to the pointer. <see cref="Render"/> paints
        /// glyph runs and per-cell background fills, and the fills are skipped for cells carrying no background of
        /// their own, so a terminal is hit-testable only over the pixels that happen to have text on them. The
        /// pointer landed on whatever sat BEHIND the view everywhere else: wheel events over blank space, over the
        /// gap right of a short line, or below the last line never reached the terminal at all, which reads as a
        /// terminal that only sometimes agrees to scroll. The whole rect is an input surface — click-to-focus,
        /// selection drags and the wheel all depend on it.
        /// </summary>
        public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

        private XT.Terminal _terminal;
        private FormattedText _measureText;
        private string? _currentDirectory;
        private double _charWidth;
        private double _charHeight;
        private int _bufferSize = 1000;
        private bool _isAlternateBuffer;

        // URL hover state.
        // The pattern is deliberately permissive about trailing characters — `,` `;` `)` and friends are
        // legal inside a url but usually sentence punctuation at the end — so TrimUrlEnd() decides where
        // the url really stops.
        private static readonly Regex UrlRegex = new(@"https?://[^\s<>""'`]+", RegexOptions.Compiled);
        private static readonly Cursor HandCursor = new Cursor(StandardCursorType.Hand);
        private HoveredUrl? _hoveredLink;
        private Cursor? _savedCursor;
        private bool _cursorOverridden;
        private (int Line, int Col)? _lastHoverProbe;
        private string? _pendingUrlClick;

        // Process management
        private IPtyConnection? _ptyConnection;

        /// <summary>
        /// True while an application has declared an atomic update — DEC private mode 2026.
        /// </summary>
        /// <remarks>
        /// A full-screen program redraws in many writes. Painting between them shows a frame half old
        /// and half new, which is the tearing you see when a TUI repaints under load. While this is
        /// set the view stops asking for frames, so the last complete one stays on screen, and the end
        /// of the update asks for exactly one.
        /// </remarks>
        private bool _atomicUpdate;

        private IDisposable? _atomicUpdateTimeout;

        /// <summary>
        /// How long a hold may last before the view paints anyway.
        /// </summary>
        /// <remarks>
        /// Not optional. An application that begins an update and then crashes, or is stopped at a
        /// breakpoint, would otherwise freeze the display for as long as it stays that way — the one
        /// failure mode of this feature, and worse than the tearing it prevents. A tear is a bad
        /// frame; a permanently frozen terminal looks like the application hung.
        /// </remarks>
        private static readonly TimeSpan AtomicUpdateTimeout = TimeSpan.FromMilliseconds(150);
        private CancellationTokenSource? _processCts;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private int _processExitHandled;    // 0=false, 1=true — claimed via TryClaimExit

        /// <summary>
        /// Guards the PAIR (<see cref="_ptyConnection"/>, <see cref="_processExitHandled"/>). They have to move
        /// together, or a read loop belonging to a DEAD connection can report an exit against a LIVE one.
        ///
        /// <para>The loop's ownership test is its <c>while</c> condition, evaluated BEFORE the read. A relaunch
        /// can replace the connection while that read is pending — and Porta.Pty's Unix reader wraps a
        /// synchronous FileStream, so cancellation does not reliably interrupt it. When the old stream is closed
        /// the read completes, and the stale loop walks into the exit path holding a connection nobody owns any
        /// more. Because the relaunch also reset the flag for the new process, its claim SUCCEEDS: the freshly
        /// started terminal immediately prints the previous process's exit code.</para>
        /// </summary>
        private readonly object _exitGate = new object();

        /// <summary>Ceiling on waiting for an already-exited child to be reaped so its real exit
        /// code is readable. See the EOF branch of <see cref="ReadPtyOutputAsync"/>.</summary>
        private const int ExitReapGraceMs = 1000;

        /// <summary>How long the BACKGROUND wait keeps trying after the read loop's grace period expires.
        /// The child is dead by definition, so this is a ceiling on patience, not an expected cost.</summary>
        private const int ExitReapCeilingMs = 30_000;

        /// <summary>Poll slice for that wait. Short enough to report promptly, long enough not to spin.</summary>
        private const int ExitReapPollMs = 100;
        private readonly object _terminalLock = new object(); // Serialises all _terminal.Write/WriteLine calls

        // Cursor blinking
        private DispatcherTimer _cursorBlinkTimer;
        private bool _cursorBlinkOn = true;

        // Selection state - tracks whether terminal is handling selection vs forwarding mouse to app
        private bool _isSelecting = false;
        // When non-null, a single left-click has been pressed but the selection hasn't started yet.
        // Selection start is deferred until pointer movement so that a plain click doesn't show a caret.
        private (int Col, int Row)? _pendingSelectionStart = null;

        // Wheel accumulator. A notched mouse delivers Delta.Y = ±1 per detent, but a trackpad (and any
        // precision mouse) delivers a stream of FRACTIONS — on macOS a slow two-finger drag is dozens of
        // ~0.05 events. Truncating each event to an int on its own rounds every one of those to zero
        // lines, so carry the remainder across events instead.
        private double _wheelResidual;      // local scrollback path
        private double _wheelResidualApp;   // mouse-reporting path (alt-buffer apps: less, vim, htop)

        // True while the view sits at the tail, which is the only state in which new output should drag
        // the viewport along. Sampled from the buffer before each write — see AutoScrollToBottomProperty.
        private bool _followBottom = true;

        // The buffer OnBufferTrimmed is subscribed to, held so the unsubscribe can name the same instance
        // the subscribe used. Terminal.Buffer returns the ACTIVE buffer and swaps to the alternate one while
        // a full-screen app runs, so `_terminal.Buffer.Trimmed -= ...` at an arbitrary moment can detach the
        // handler from a buffer that never had it and leave the real one subscribed.
        private TerminalBuffer? _scrollbackBuffer;

        // AutoScrollToBottom mirrored for the reader thread. The write path runs OFF the UI thread (the
        // Dispatcher.UIThread.Post beside it is the giveaway), so reading the StyledProperty there would be
        // a cross-thread GetValue. Kept in step by OnPropertyChanged.
        private volatile bool _autoScroll = true;

        // Keyboard selection. Both are CARET BOUNDARY ordinals — `row * Cols + col` counting the gaps
        // between cells, not the cells themselves — so Shift+Right from a fresh cursor selects exactly one
        // cell instead of two. Null anchor = no keyboard selection in flight.
        private int? _kbSelAnchor;

        // True while the selection covers the WHOLE input, from a select-all rather than from a gesture the
        // user steered. The caret is then hidden: with everything selected there is no one place it belongs,
        // and every editor that can select all hides it rather than parking it at an arbitrary end.
        private bool _kbSelWholeInput;

        // Where the shell's editable input begins, as an absolute row and a column on it. A keyboard
        // selection stops here rather than running back over the prompt.
        //
        // Derived rather than known: nothing tells a terminal where a prompt ends unless the shell emits
        // semantic markers, which most shells do not by default. What is reliable is the moment the user
        // FIRST types on a row the shell has just moved to — wherever the cursor is then is the end of
        // whatever the shell drew, which is the prompt.
        //
        // Sampled at that keystroke rather than after the write, because a prompt does not arrive whole: on
        // a real bash the newline and the prompt text land in separate reads, so the cursor is still at
        // column 0 when the row changes. Measured — it recorded (row 4, col 0) instead of (row 4, col 10).
        private int _inputStartRow = -1;
        private int _inputStartCol;
        private int _lastOutputRow = -1;

        // True once the shell has told us where its input begins, via OSC 133. A shell that reports it is
        // authoritative, so the guesswork below is switched off for good rather than left to fight it.
        private bool _semanticPrompt;
        // Armed only by shell OUTPUT moving to a new row. Starting armed would let the first interaction
        // record the input start wherever the cursor happens to be — which, if the user has already typed,
        // is the end of their input rather than the start of it, pinning the selection to a stop.
        private bool _inputStartPending;

        internal (int Row, int Col) InputStart => (_inputStartRow, _inputStartCol);

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

        // Set where a selection is retired by a keystroke that will type, and consumed by whichever path
        // then sends that character — within the same handler invocation, so it never spans two keystrokes.
        private string _pendingReplaceKeys = string.Empty;
        private int _kbSelFocus;

        // IME (Input Method Editor) support
        private TerminalInputMethodClient? _inputMethodClient;

        // Unique identifier for this terminal instance (for debugging)
        private readonly Guid _instanceId = Guid.NewGuid();

        // When true, OnDetachedFromLogicalTree skips CleanupProcess so the PTY
        // survives a visual-tree re-parent (e.g. floating window pop-out/dock-back).
        private bool _suppressCleanupOnDetach;

        // Background is null for a run that keeps the terminal's own default background — nothing is
        // painted for it, so a host that layers the view over its own themed surface still shows through.
        //
        // Image is non-null for a run of cells showing pieces of a Sixel picture, in which case Text is null and
        // CellCount is how many tiles the run covers. Both kinds live in the same cached list because the cache
        // is replayed verbatim: a picture that was not in it would simply not be drawn on any frame the row was
        // served from cache, which is most of them.
        // Internal rather than private so the runs a frame decided on can be asserted directly. The headless
        // platform's recording DrawingContext throws NotImplementedException from DrawImage, so a rendered frame
        // cannot be inspected for pictures the way it can for text and fills — the cached run list is the last
        // point at which what will be drawn is still observable.
        internal sealed record CachedTextRun(
            FormattedText? Text,
            int StartX,
            int CellCount,
            IBrush? Background,
            XT.Graphics.LinePlacement? Placement = null,
            XT.Graphics.TerminalImage? Image = null)
        {
            /// <summary>Whether this run draws a picture rather than text.</summary>
            public bool IsImage => Placement is not null && Image is not null;
        }

        // One bitmap per image, built on first sight and reused for the life of the picture.
        //
        // There is no dirty-rect culling — Render walks every visible row on every frame — so a picture on screen
        // is re-blitted up to thirty times a second and must never be re-uploaded to do it. Keyed weakly on the
        // image so the bitmap dies when the emulator drops its last cell: no eviction list, and nothing to keep
        // in step with a buffer that scrolls.
        //
        // Wrapped rather than stored bare so a failed upload can be remembered as well as a successful one --
        // otherwise a picture the platform cannot take would be retried on every frame it is on screen.
        private sealed class CachedBitmap
        {
            public Bitmap? Bitmap;

            /// <summary>
            /// Which frame of the picture this bitmap holds.
            /// </summary>
            /// <remarks>
            /// The cache is keyed on the image, which for an animation is not enough on its own --
            /// the pixels move while the key stays the same. The emulator changes this number
            /// whenever they do. A still picture leaves it at zero forever.
            /// </remarks>
            public int FrameSerial;
        }

        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<XT.Graphics.TerminalImage, CachedBitmap> _imageBitmaps = new();

        // Set once if the PLATFORM cannot draw bitmaps at all. Consolonia runs this same control over a
        // text-cell backend where DrawImage means nothing; the terminal should still render its text there
        // rather than throwing out of Render on every frame.
        //
        // Only the exceptions that say so set this — see IndicatesNoRasterBackend. A picture that fails for
        // its own reasons is remembered against that picture instead, because turning every image off for
        // the life of the control on the strength of one bad bitmap would hide the thing that caused it.
        private bool _imageRenderingUnavailable;

        // The colours the frame currently being drawn resolves against. Taken once at the top of Render, so
        // every cell in a frame agrees even if a program repaints the palette midway through.
        //
        // Not nullable: it is seeded alongside the emulator in OnInitialized and replaced at the top of every
        // frame, so no drawing code can reach it unset. Declaring it nullable only pushed the question onto
        // the several call sites that pass it to a non-null parameter, none of which could answer it either.
        private XT.Common.ColorSnapshot _palette;

        public static readonly DirectProperty<TerminalView, bool> IsAlternateBufferProperty =
            AvaloniaProperty.RegisterDirect<TerminalView, bool>(
                nameof(IsAlternateBuffer),
                o => o.IsAlternateBuffer);

        public static readonly DirectProperty<TerminalView, int> BufferSizeProperty =
            AvaloniaProperty.RegisterDirect<TerminalView, int>(
                nameof(BufferSize),
                o => o._bufferSize,
                (o, v) => o._bufferSize = v);

        public static readonly DirectProperty<TerminalView, int> ViewportYProperty =
            AvaloniaProperty.RegisterDirect<TerminalView, int>(
                nameof(ViewportY),
                o => o.ViewportY,
                (o, v) => o.ViewportY = v);

        public static readonly DirectProperty<TerminalView, int> MaxScrollbackProperty =
            AvaloniaProperty.RegisterDirect<TerminalView, int>(
                nameof(MaxScrollback),
                o => o.MaxScrollback);

        public static readonly DirectProperty<TerminalView, int> ViewportLinesProperty =
            AvaloniaProperty.RegisterDirect<TerminalView, int>(
                nameof(ViewportLines),
                o => o.ViewportLines);

        public static readonly DirectProperty<TerminalView, string?> CurrentDirectoryProperty =
            AvaloniaProperty.RegisterDirect<TerminalView, string?>(
                nameof(CurrentDirectory),
                o => o.CurrentDirectory);

        /// <summary>
        /// The font a terminal falls back to when nothing else is specified: a monospace stack, tried in
        /// order, ending at the platform's generic monospace family.
        /// </summary>
        /// <remarks>
        /// <see cref="FontFamily.Default"/> is the system UI font, which is proportional — and a terminal
        /// rendered in a proportional font is not merely ugly, it is wrong. The cell grid is derived from a
        /// single measured advance width, so glyphs that do not share that width drift out of their columns
        /// and box drawing, alignment and cursor positioning all come apart. A terminal control has to be
        /// usable without the consumer knowing to style it.
        /// </remarks>
        /// <remarks>
        /// The emoji families are at the END and never in front. The cell grid comes from the first family
        /// that exists on the machine, these are proportional, and one of them in that position breaks the
        /// grid rather than fixing the glyphs.
        ///
        /// They are named rather than left to the platform because the fallback picks badly for a joined
        /// sequence. With no emoji family in the chain, a cluster the monospace families cannot shape falls
        /// to whatever monochrome symbol font the system offers — and that font has the COMPONENTS without a
        /// ligature for the sequence, so a couple or a family is drawn as its separate parts, tinted by the
        /// terminal's foreground. That tint is the giveaway: a colour emoji carries its own colours, so
        /// anything wearing the foreground is not one.
        /// </remarks>
        public static readonly FontFamily DefaultFontFamily = new FontFamily(
            "Cascadia Mono,Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,Liberation Mono,Courier New," +
            "Segoe UI Emoji,Apple Color Emoji,Noto Color Emoji,monospace");

        public static readonly StyledProperty<FontFamily> FontFamilyProperty =
            AvaloniaProperty.Register<TerminalView, FontFamily>(
                nameof(FontFamily),
                defaultValue: DefaultFontFamily);

        public static readonly StyledProperty<double> FontSizeProperty =
            AvaloniaProperty.Register<TerminalView, double>(
                nameof(FontSize),
                defaultValue: 12);

        public static readonly StyledProperty<FontStyle> FontStyleProperty =
            AvaloniaProperty.Register<TerminalView, FontStyle>(
                nameof(FontStyle),
                defaultValue: FontStyle.Normal);

        public static readonly StyledProperty<FontWeight> FontWeightProperty =
            AvaloniaProperty.Register<TerminalView, FontWeight>(
                nameof(FontWeight),
                defaultValue: FontWeight.Normal);

        public static readonly StyledProperty<TextDecorationLocation?> TextDecorationsProperty =
            AvaloniaProperty.Register<TerminalView, TextDecorationLocation?>(
                nameof(TextDecorations),
                defaultValue: null);

        public static readonly StyledProperty<IBrush> ForegroundProperty =
            AvaloniaProperty.Register<TerminalView, IBrush>(
                nameof(Foreground),
                defaultValue: Brushes.White);

        public static readonly StyledProperty<IBrush> BackgroundProperty =
            AvaloniaProperty.Register<TerminalView, IBrush>(
                nameof(Background),
                defaultValue: Brushes.Black);

        public static readonly StyledProperty<IBrush> SelectionBrushProperty =
            AvaloniaProperty.Register<TerminalView, IBrush>(
                nameof(SelectionBrush),
                defaultValue: new SolidColorBrush(Color.FromArgb(128, 0, 120, 215)));

        public static readonly StyledProperty<string> ProcessProperty =
            AvaloniaProperty.Register<TerminalView, string>(
                nameof(Process),
                defaultValue: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash");

        public static readonly StyledProperty<IList<string>> ArgsProperty =
            AvaloniaProperty.Register<TerminalView, IList<string>>(
                nameof(Args),
                defaultValue: Array.Empty<string>());

        public static readonly StyledProperty<string?> StartingDirectoryProperty =
            AvaloniaProperty.Register<TerminalView, string?>(
                nameof(StartingDirectory),
                defaultValue: Environment.CurrentDirectory);

        public static readonly StyledProperty<Color> CursorColorProperty =
            AvaloniaProperty.Register<TerminalView, Color>(
                nameof(CursorColor),
                defaultValue: Colors.White);

        public static readonly StyledProperty<XT.Common.CursorStyle> CursorStyleProperty =
            AvaloniaProperty.Register<TerminalView, XT.Common.CursorStyle>(
                nameof(CursorStyle),
                defaultValue: XT.Common.CursorStyle.Bar);

        public static readonly StyledProperty<bool> CursorBlinkProperty =
            AvaloniaProperty.Register<TerminalView, bool>(
                nameof(CursorBlink),
                defaultValue: true);

        public static readonly StyledProperty<int> CursorBlinkRateProperty =
            AvaloniaProperty.Register<TerminalView, int>(
                nameof(CursorBlinkRate),
                defaultValue: 530);

        /// <summary>
        /// When <see langword="false"/> (default), a plain single left-click does not
        /// immediately show a selection highlight. The selection only starts once the
        /// pointer moves, so casual clicks produce no visible caret artifact.
        /// Set to <see langword="true"/> to restore the original behaviour where a
        /// single-cell highlight appears on every click.
        /// Double- and triple-click (word / line selection) are unaffected by this setting.
        /// </summary>
        public static readonly StyledProperty<bool> ShowCaretOnClickProperty =
            AvaloniaProperty.Register<TerminalView, bool>(
                nameof(ShowCaretOnClick),
                defaultValue: false);

        public bool ShowCaretOnClick
        {
            get => GetValue(ShowCaretOnClickProperty);
            set => SetValue(ShowCaretOnClickProperty, value);
        }

        /// <summary>
        /// Which convention the terminal follows for Ctrl+A, Ctrl+C, Ctrl+V and Ctrl+X. Defaults to
        /// <see cref="ShortcutMode.Terminal"/>, which changes nothing.
        /// </summary>
        /// <remarks>
        /// See <see cref="ShortcutMode"/> for what each mode does and why the choice exists at all.
        /// </remarks>
        public static readonly StyledProperty<ShortcutMode> ShortcutModeProperty =
            AvaloniaProperty.Register<TerminalView, ShortcutMode>(
                nameof(ShortcutMode),
                defaultValue: ShortcutMode.Terminal);

        /// <inheritdoc cref="ShortcutModeProperty"/>
        public ShortcutMode ShortcutMode
        {
            get => GetValue(ShortcutModeProperty);
            set => SetValue(ShortcutModeProperty, value);
        }

        /// <summary>
        /// Hold the cursor back even when a process IS attached. Between spawning a shell and
        /// its first byte of output the buffer is still empty, so the cursor paints at (0,0) — which is
        /// wrong wherever the host layers something over the view during that window: an overlay drawing
        /// its own caret would leave the shell's stranded in the corner beneath it. Clear it once the shell
        /// has painted — <see cref="ShellReady"/> is the signal.
        /// </summary>
        public static readonly StyledProperty<bool> SuppressCursorProperty =
            AvaloniaProperty.Register<TerminalView, bool>(
                nameof(SuppressCursor),
                defaultValue: false);

        public bool SuppressCursor
        {
            get => GetValue(SuppressCursorProperty);
            set => SetValue(SuppressCursorProperty, value);
        }

        /// <summary>
        /// When <see langword="true"/>, <see cref="OutputReceived"/> is raised directly on the background
        /// read task instead of being marshalled to the UI thread. Default is <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// <para>Opt in when latency and ordering matter more than convenience — matching a dev server's
        /// "listening on :port" line to know when to open a browser, say. The dispatcher hop coalesces
        /// chunks and delivers them a frame or more late, which is fine for logging and not fine for that.</para>
        /// <para>The cost is that a handler then runs on the read task and MUST NOT touch UI without
        /// marshalling itself, and must not block — the loop that raises it is the one pumping output, so a
        /// slow handler stalls the terminal. The default is the safe one for exactly that reason.</para>
        /// </remarks>
        public static readonly StyledProperty<bool> OutputReceivedOnReadTaskProperty =
            AvaloniaProperty.Register<TerminalView, bool>(
                nameof(OutputReceivedOnReadTask),
                defaultValue: false);

        /// <inheritdoc cref="OutputReceivedOnReadTaskProperty"/>
        public bool OutputReceivedOnReadTask
        {
            get => GetValue(OutputReceivedOnReadTaskProperty);
            set => SetValue(OutputReceivedOnReadTaskProperty, value);
        }

        // Styled properties are UI-thread-affine, so the read task reads this mirror rather than the
        // property. Kept in step by OnPropertyChanged.
        private volatile bool _outputOnReadTask;

        /// <summary>
        /// When <see langword="true"/> (default), new output drags the viewport along so the terminal keeps
        /// showing the tail. Scrolling back pauses that until the view returns to the bottom; typing resumes
        /// it. Set to <see langword="false"/> and the terminal never scrolls itself.
        /// </summary>
        /// <remarks>
        /// Follow state is SAMPLED from the buffer immediately before each write rather than tracked as a
        /// flag. A flag has to be cleared by every path that can move the viewport, and missing one — the
        /// scrollbar, a programmatic <see cref="ViewportY"/> set, a resize — is invisible until somebody
        /// scrolls that exact way. Sampling covers all of them by construction.
        /// </remarks>
        public static readonly StyledProperty<bool> AutoScrollToBottomProperty =
            AvaloniaProperty.Register<TerminalView, bool>(
                nameof(AutoScrollToBottom),
                defaultValue: true);

        /// <inheritdoc cref="AutoScrollToBottomProperty"/>
        public bool AutoScrollToBottom
        {
            get => GetValue(AutoScrollToBottomProperty);
            set => SetValue(AutoScrollToBottomProperty, value);
        }

        /// <summary>
        /// OSC 133 — shell integration. Only the marker for "the prompt ends here" is acted on.
        /// </summary>
        /// <remarks>
        /// <para><c>B</c> is emitted by the shell immediately after it has drawn the prompt, so the cursor
        /// is standing exactly where the user's input will begin. That is the answer
        /// <see cref="NoteInputStart"/> spends effort inferring, and it is exact. Measured: after
        /// <c>OSC 133;B</c> following a 12-character prompt, the cursor is at column 12.</para>
        /// <para><c>I</c> is accepted alongside it, which some shells emit for the same point.</para>
        /// <para>Once a shell has reported this, the heuristic is disabled rather than left to compete: a
        /// shell that speaks OSC 133 knows better than any inference drawn from cursor movement.</para>
        /// <para>Runs on the read task, inside the terminal lock, so it does nothing but record.</para>
        /// </remarks>
        private void OnTerminalOscReceived(object? sender, XT.Events.TerminalEvents.OscReceivedEventArgs e)
        {
            if (e.Code != 133 || string.IsNullOrEmpty(e.Data))
                return;

            if (e.Data[0] is 'B' or 'I')
            {
                _inputStartRow = _terminal.Buffer.YBase + _terminal.Buffer.Y;
                _inputStartCol = _terminal.Buffer.X;
                _inputStartPending = false;
                _semanticPrompt = true;
            }
        }

        /// <summary>
        /// The scrollback ring dropped <paramref name="count"/> lines off the top, so every absolute index
        /// below shifted by that much. A following view is about to be scrolled to the bottom anyway; a view
        /// parked up in the scrollback is moved down by the same amount, so the content the user is reading
        /// stays under their eye instead of sliding upward as output arrives.
        /// </summary>
        private void OnBufferTrimmed(int count)
        {
            if (_followBottom || count <= 0)
                return;

            var y = _terminal.Buffer.ViewportY;
            if (y > 0)
                _terminal.Buffer.ViewportY = Math.Max(0, y - count);
        }

        /// <summary>
        /// True while the view is following the tail — the state in which new output drags the viewport
        /// along. Read it to drive a "jump to bottom" affordance's visibility.
        /// </summary>
        public bool IsFollowingTail => _followBottom;

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
        /// Write one of the view's OWN lines — an exit notice, a read error — under the same follow rules the
        /// read loop applies to process output, and invalidate.
        /// </summary>
        /// <remarks>
        /// <para>These lines used to scroll to the bottom whenever <see cref="AutoScrollToBottom"/> was on,
        /// which is not what that property promises: scrolling back pauses the follow until the view returns
        /// to the tail, and a process exiting is no reason to yank a user who is reading scrollback down to
        /// the end. It is also the most likely moment for it to happen, since a process exiting is exactly
        /// when somebody is scrolled up looking at what it printed.</para>
        /// <para>Sampled BEFORE the write for the same reason the read loop samples there: afterwards
        /// <c>YBase</c> has advanced, so a view that genuinely was at the tail reads as not-following.</para>
        /// </remarks>
        private void WriteOwnLine(string text)
        {
            lock (_terminalLock)
            {
                var oldY = _terminal.Buffer.ViewportY;

                _followBottom = _isAlternateBuffer || (_autoScroll && _terminal.Buffer.IsAtBottom);

                _terminal.WriteLine(text);

                // Alternate-buffer apps position their own cursor and are left alone, as in the read loop.
                if (!_isAlternateBuffer)
                {
                    if (_followBottom)
                    {
                        _terminal.Buffer.ScrollToBottom();
                    }
                    else if (!_autoScroll && _terminal.Buffer.ViewportY != oldY)
                    {
                        // With auto-scroll off the emulator still advances ViewportY itself as YBase grows,
                        // so the position has to be held rather than merely not scrolled — see the read
                        // loop, where the same hunk exists for the same reason.
                        _terminal.Buffer.ViewportY = Math.Min(oldY, MaxScrollback);
                    }
                }
            }

            RequestPaint();
        }

        /// <summary>
        /// When <see langword="false"/> (default), each entry in <see cref="Args"/> reaches the process as a
        /// distinct argument, quoted as necessary so it arrives exactly as written. Set to
        /// <see langword="true"/> to hand the process one command line built by joining the entries with
        /// spaces, and let it apply its own parsing rules.
        /// </summary>
        /// <remarks>
        /// <para>Named after the <c>PtyOptions</c> member it sets, because that is genuinely what it does: the
        /// command line is taken verbatim. It is not merely that quoting is skipped — the argument vector is
        /// collapsed into one string and rebuilt by somebody else's parser, which can change how many
        /// arguments there are.</para>
        /// <para>The default is faithful and is what nearly every caller wants. But it is also unavoidable
        /// without this, and some programs parse their command line by rules of their own — so a caller
        /// reproducing an exact command line, or driving a tool with non-standard argument conventions, needs
        /// a way out. Requested in #17.</para>
        /// <para><b>This setting only has an effect on Windows.</b> Windows processes receive a single command
        /// line string, so something has to decide how the arguments are joined into it. Unix passes an argument
        /// vector to <c>exec</c> directly — there is no string to build, nothing to quote, and nothing this
        /// setting could change. Measured on both, with the argument list <c>hello world</c>, <c>a"b</c>,
        /// <c>plain</c>: Windows yields those three unchanged when false and <c>hello</c>, <c>world</c>,
        /// <c>ab plain</c> when true, while Unix yields the three unchanged either way.</para>
        /// </remarks>
        public static readonly StyledProperty<bool> VerbatimCommandLineProperty =
            AvaloniaProperty.Register<TerminalView, bool>(
                nameof(VerbatimCommandLine),
                defaultValue: false);

        /// <inheritdoc cref="VerbatimCommandLineProperty"/>
        public bool VerbatimCommandLine
        {
            get => GetValue(VerbatimCommandLineProperty);
            set => SetValue(VerbatimCommandLineProperty, value);
        }

        /// <summary>
        /// Extra environment variables for the launched process. Null (default) launches it with the host's
        /// environment unchanged.
        /// </summary>
        /// <remarks>
        /// <para>These are MERGED into the environment the child would otherwise inherit, not substituted for
        /// it — measured on both platforms: setting one variable took the child's environment from 88 entries
        /// to 89 on Windows and 31 to 32 on Linux, with the inherited ones, including <c>PATH</c>, still
        /// present. So a caller can add or override a single variable without having to reconstruct an entire
        /// environment, which would be the easy way to launch a shell that cannot find anything.</para>
        /// <para>Named <c>EnvironmentVariables</c> rather than <c>Environment</c>, which is what the PTY layer
        /// calls it, for one concrete reason: a property called <c>Environment</c> shadows
        /// <see cref="System.Environment"/> for every subclass, so anyone deriving from this control and
        /// writing <c>Environment.GetEnvironmentVariable(...)</c> would get a compile error rather than the
        /// framework. <c>ProcessStartInfo.EnvironmentVariables</c> is the established .NET name for exactly
        /// this concept.</para>
        /// <para><c>TERM</c> and <c>COLORTERM</c> are supplied automatically (as <c>xterm-256color</c> and
        /// <c>truecolor</c>) when this dictionary does not carry them, because nothing else does — the PTY
        /// layer sets neither, and on Windows there is none in the environment to inherit. Put either in
        /// here to override it.</para>
        /// </remarks>
        /// <summary>
        /// The <c>TERM</c> given to a launched process when the caller supplies none.
        /// </summary>
        /// <remarks>
        /// What this terminal actually behaves like. Overridden by putting <c>TERM</c> in
        /// <see cref="EnvironmentVariables"/>.
        /// </remarks>
        public const string DefaultTermType = "xterm-256color";

        /// <summary>
        /// The <c>COLORTERM</c> given to a launched process when the caller supplies none.
        /// </summary>
        /// <remarks>
        /// <para>Not a contradiction of <see cref="DefaultTermType"/>. The two answer different questions:
        /// <c>TERM</c> names a terminfo entry, and <c>xterm-256color</c> describes the 256-entry indexed
        /// palette that terminfo can express; <c>COLORTERM</c> advertises DIRECT 24-bit colour, which
        /// terminfo has no standard way to state. Every modern terminal sets both -- Windows Terminal,
        /// kitty, alacritty and iTerm2 among them.</para>
        /// <para>Without it a program reads the terminfo entry, concludes 256 colours, and quantises its
        /// output to the palette. This terminal takes full RGB, so that would be throwing away colour it
        /// could have shown.</para>
        /// </remarks>
        public const string DefaultColorTerm = "truecolor";

        public static readonly StyledProperty<IDictionary<string, string>?> EnvironmentVariablesProperty =
            AvaloniaProperty.Register<TerminalView, IDictionary<string, string>?>(
                nameof(EnvironmentVariables),
                defaultValue: null);

        /// <inheritdoc cref="EnvironmentVariablesProperty"/>
        public IDictionary<string, string>? EnvironmentVariables
        {
            get => GetValue(EnvironmentVariablesProperty);
            set => SetValue(EnvironmentVariablesProperty, value);
        }

        public static readonly StyledProperty<XTerm.Options.TerminalOptions?> OptionsProperty =
            AvaloniaProperty.Register<TerminalControl, XTerm.Options.TerminalOptions?>(
                nameof(Options),
                defaultValue: null);

        #region Terminal Attached Events

        public static readonly RoutedEvent<TitleChangedEventArgs> TitleChangedEvent =
            RoutedEvent.Register<TerminalView, TitleChangedEventArgs>(
                nameof(TitleChanged),
                RoutingStrategies.Bubble);

        public static void AddTitleChangedHandler(Interactive target, EventHandler<TitleChangedEventArgs> handler) =>
            target.AddHandler(TitleChangedEvent, handler);

        public static void RemoveTitleChangedHandler(Interactive target, EventHandler<TitleChangedEventArgs> handler) =>
            target.RemoveHandler(TitleChangedEvent, handler);

        public static readonly RoutedEvent<WindowMovedEventArgs> WindowMovedEvent =
            RoutedEvent.Register<TerminalView, WindowMovedEventArgs>(
                nameof(WindowMoved),
                RoutingStrategies.Bubble);

        public static void AddWindowMovedHandler(Interactive target, EventHandler<WindowMovedEventArgs> handler) =>
            target.AddHandler(WindowMovedEvent, handler);

        public static void RemoveWindowMovedHandler(Interactive target, EventHandler<WindowMovedEventArgs> handler) =>
            target.RemoveHandler(WindowMovedEvent, handler);

        public static readonly RoutedEvent<WindowResizedEventArgs> WindowResizedEvent =
            RoutedEvent.Register<TerminalView, WindowResizedEventArgs>(
                nameof(WindowResized),
                RoutingStrategies.Bubble);

        public static void AddWindowResizedHandler(Interactive target, EventHandler<WindowResizedEventArgs> handler) =>
            target.AddHandler(WindowResizedEvent, handler);

        public static void RemoveWindowResizedHandler(Interactive target, EventHandler<WindowResizedEventArgs> handler) =>
            target.RemoveHandler(WindowResizedEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowMinimizedEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowMinimized),
                RoutingStrategies.Bubble);

        public static void AddWindowMinimizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowMinimizedEvent, handler);

        public static void RemoveWindowMinimizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowMinimizedEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowMaximizedEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowMaximized),
                RoutingStrategies.Bubble);

        public static void AddWindowMaximizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowMaximizedEvent, handler);

        public static void RemoveWindowMaximizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowMaximizedEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowRestoredEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowRestored),
                RoutingStrategies.Bubble);

        public static void AddWindowRestoredHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowRestoredEvent, handler);

        public static void RemoveWindowRestoredHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowRestoredEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowRaisedEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowRaised),
                RoutingStrategies.Bubble);

        public static void AddWindowRaisedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowRaisedEvent, handler);

        public static void RemoveWindowRaisedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowRaisedEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowLoweredEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowLowered),
                RoutingStrategies.Bubble);

        public static void AddWindowLoweredHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowLoweredEvent, handler);

        public static void RemoveWindowLoweredHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowLoweredEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> WindowFullscreenedEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(WindowFullscreened),
                RoutingStrategies.Bubble);

        public static void AddWindowFullscreenedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(WindowFullscreenedEvent, handler);

        public static void RemoveWindowFullscreenedHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(WindowFullscreenedEvent, handler);

        public static readonly RoutedEvent<RoutedEventArgs> BellRangEvent =
            RoutedEvent.Register<TerminalView, RoutedEventArgs>(
                nameof(BellRang),
                RoutingStrategies.Bubble);

        public static void AddBellRangHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.AddHandler(BellRangEvent, handler);

        public static void RemoveBellRangHandler(Interactive target, EventHandler<RoutedEventArgs> handler) =>
            target.RemoveHandler(BellRangEvent, handler);

        public static readonly RoutedEvent<WindowInfoRequestedEventArgs> WindowInfoRequestedEvent =
            RoutedEvent.Register<TerminalView, WindowInfoRequestedEventArgs>(
                nameof(WindowInfoRequested),
                RoutingStrategies.Bubble);

        public static void AddWindowInfoRequestedHandler(Interactive target, EventHandler<WindowInfoRequestedEventArgs> handler) =>
            target.AddHandler(WindowInfoRequestedEvent, handler);

        public static void RemoveWindowInfoRequestedHandler(Interactive target, EventHandler<WindowInfoRequestedEventArgs> handler) =>
            target.RemoveHandler(WindowInfoRequestedEvent, handler);

        #endregion

        /// <summary>
        /// Event raised once when the shell produces its first output (e.g. the prompt),
        /// indicating it is ready to accept input.
        /// </summary>
        public event EventHandler? ShellReady;

        /// <summary>
        /// Event raised when the PTY process exits.
        /// </summary>
        public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

        /// <summary>
        /// Event raised for each chunk of output the terminal receives from the PTY process.
        /// </summary>
        /// <remarks>
        /// <para>Raised on the UI THREAD by default, so a handler may touch UI directly. The cost is that
        /// chunks arrive marshalled and slightly late; a consumer that needs the tightest possible latency —
        /// matching a dev server's "listening on :port" line, say — can set
        /// <see cref="OutputReceivedOnReadTask"/> and take delivery on the read task instead.</para>
        /// <para>The payload is UTF-8 decoded TEXT, not raw bytes, and it is the text as it came off the
        /// pty: escape sequences included, chunked arbitrarily rather than by line.</para>
        /// <para>A throwing handler is caught and swallowed. An unhandled exception on the dispatcher would
        /// take the application down, and sniffing output is exactly the place arbitrary host code runs. The
        /// catch wraps the whole invocation, so a handler that throws also suppresses handlers subscribed
        /// AFTER it for that chunk — isolating each would cost a <c>GetInvocationList()</c> allocation per
        /// chunk, which is the per-chunk overhead this event is otherwise careful to avoid.</para>
        /// </remarks>
        public event EventHandler<OutputReceivedEventArgs>? OutputReceived;

        /// <summary>
        /// Event raised when a URL in the terminal is Ctrl+Clicked.
        /// </summary>
        public event EventHandler<UrlClickedEventArgs>? UrlClicked;

        /// <summary>
        /// Event raised when the terminal title changes.
        /// </summary>
        public event EventHandler<TitleChangedEventArgs>? TitleChanged;

        /// <summary>
        /// Event raised when a window move command is received from the terminal.
        /// </summary>
        public event EventHandler<WindowMovedEventArgs>? WindowMoved;

        /// <summary>
        /// Event raised when a window resize command is received from the terminal.
        /// </summary>
        public event EventHandler<WindowResizedEventArgs>? WindowResized;

        /// <summary>
        /// Event raised when a window minimize command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowMinimized;

        /// <summary>
        /// Event raised when a window maximize command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowMaximized;

        /// <summary>
        /// Event raised when a window restore command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowRestored;

        /// <summary>
        /// Event raised when a window raise command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowRaised;

        /// <summary>
        /// Event raised when a window lower command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowLowered;

        /// <summary>
        /// Event raised when a window fullscreen command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowFullscreened;

        /// <summary>
        /// Event raised when the terminal bell is activated.
        /// </summary>
        public event EventHandler? BellRang;

        /// <summary>
        /// Event raised when window information is requested by the terminal.
        /// The handler should set the response properties on the event args.
        /// </summary>
        public event EventHandler<WindowInfoRequestedEventArgs>? WindowInfoRequested;


        static TerminalView()
        {
            AffectsRender<TerminalView>(
                FontFamilyProperty,
                FontSizeProperty,
                FontStyleProperty,
                FontWeightProperty,
                TextDecorationsProperty,
                ForegroundProperty,
                BackgroundProperty,
                SelectionBrushProperty,
                BufferSizeProperty,
                ViewportYProperty,
                CursorColorProperty,
                CursorStyleProperty,
                CursorBlinkProperty,
                SuppressCursorProperty);   // toggling it must repaint immediately

            AffectsMeasure<TerminalView>(
                FontFamilyProperty,
                FontSizeProperty,
                FontStyleProperty,
                FontWeightProperty,
                BufferSizeProperty);

            FocusableProperty.OverrideDefaultValue<TerminalView>(true);
        }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        public TerminalView()
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        {
            Focusable = true;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            TextInputMethodClientRequested += OnTextInputMethodClientRequested;
        }

        protected override void OnInitialized()
        {
            // Sync terminal options with styled properties
            var options = Options ?? new XT.Options.TerminalOptions();

            options.CursorStyle = CursorStyle;
            options.CursorBlink = CursorBlink;
            options.CursorBlinkRate = CursorBlinkRate;

            // Same reason BufferSize is carried across below: OnPropertyChanged returns early until the
            // emulator exists, so a value set in an object initialiser or an early template binding never
            // reaches the mirror. Seeding here is what makes `new TerminalView { OutputReceivedOnReadTask
            // = true }` actually take effect.
            _outputOnReadTask = OutputReceivedOnReadTask;

            // BufferSize may already have been set — by a template binding, or by a host that configured the
            // view before it was initialised. The setter cannot reach the emulator that early because it does
            // not exist yet, so the value is carried across here instead of being silently lost.
            options.Scrollback = _bufferSize;

            // On Linux, the PTY doesn't convert LF to CRLF (ONLCR is disabled for raw mode),
            // so we need XTerm to handle LF as implicit CRLF
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                options.ConvertEol = true;
            }

            // Foreground and Background ARE the terminal's default colour pair, so they are seeded into the
            // theme BEFORE the emulator is built rather than assigned afterwards. That is what makes them
            // the values SGR 39/49 resolve to, what OSC 10/11 report, and — the part an assignment after
            // construction would miss — what OSC 110/111 RESET to. Reset to a colour the host never chose
            // is how a program "restoring the defaults" ends up with white on black.
            SeedThemeFromBrushes(options.Theme);

            _terminal = new XT.Terminal(options);

            // Seeded here so it is never unset. Render replaces it every frame; this is only what the very
            // first one starts from, and what anything drawing before that frame would otherwise trip over.
            _palette = _terminal.Colors.Take();

            // A program can move the palette out from under the renderer with OSC 4 or OSC 10/11/12. The
            // cached runs hold resolved brushes, so they have to go with it.
            _terminal.Colors.ColorChanged += OnTerminalColorChanged;

            // The normal buffer's ring evicts its oldest lines once the scrollback fills, and every absolute
            // index shifts down with it. A view parked in the scrollback has to move with the eviction or the
            // content slides upward under the user while output keeps arriving.
            //
            // Subscribed HERE and not in OnAttachedToLogicalTree with the others, because the buffer object
            // outlives detach/re-attach and this is the one point that runs exactly once. The instance is
            // remembered rather than re-read later: it is BALANCED on detach and re-armed on re-attach
            // against this same object, so a re-parent can neither double the handler — which would move a
            // parked viewport by a multiple of the evicted count — nor leave one behind. Leaving one behind
            // is not merely untidy: Terminal is public, so a host holding the emulator keeps the whole view
            // alive through the subscription and goes on calling back into a control that is off the tree.
            _scrollbackBuffer = _terminal.Buffer;
            _scrollbackBuffer.Trimmed += OnBufferTrimmed;

            // Shell integration. A shell that emits OSC 133 says exactly where its prompt ends, which is the
            // one thing the input-start heuristic can only infer. Subscribed here for the same reason as
            // Trimmed above: this point runs exactly once, and the emulator outlives a detach/re-attach.
            _terminal.OscReceived += OnTerminalOscReceived;

            _terminal.DataReceived += OnTerminalDataReceived;
            _terminal.BufferChanged += OnTerminalBufferChanged;
            _terminal.CursorStyleChanged += OnTerminalCursorStyleChanged;
            // window events
            _terminal.TitleChanged += OnTerminalTitleChanged;
            _terminal.WindowMoved += OnTerminalWindowMoved;
            _terminal.WindowResized += OnTerminalWindowResized;
            _terminal.WindowMinimized += OnTerminalWindowMinimized;
            _terminal.WindowMaximized += OnTerminalWindowMaximized;
            _terminal.WindowRestored += OnTerminalWindowRestored;
            _terminal.WindowRaised += OnTerminalWindowRaised;
            _terminal.WindowLowered += OnTerminalWindowLowered;
            _terminal.WindowFullscreened += OnTerminalWindowFullscreened;
            _terminal.BellRang += OnTerminalBellRang;
            _terminal.WindowInfoRequested += OnTerminalWindowInfoRequested;
            _terminal.DirectoryChanged += OnTerminalDirectoryChanged;
            // end window events

            // Setup cursor blink timer
            _cursorBlinkTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(CursorBlinkRate)
            };
            _cursorBlinkTimer.Tick += OnCursorBlinkTick;

            // The animation clock. The emulator owns no timer -- it is driven entirely by Write --
            // so somebody has to tell it how much time has gone by, and that is a job for the side
            // with a render loop. It only runs while something is actually animating; see
            // SyncAnimationClock.
            _animationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(AnimationTickMilliseconds)
            };
            _animationTimer.Tick += OnAnimationTick;

            // Initialize IME client
            _inputMethodClient = new TerminalInputMethodClient(this);
        }

        /// <summary>
        /// How often the animation clock ticks, in milliseconds.
        /// </summary>
        /// <remarks>
        /// Finer than any plausible frame gap, because the emulator advances by ELAPSED time rather
        /// than one frame per tick -- so this bounds the jitter of a frame change, not the speed the
        /// animation runs at. A slow tick makes a 40ms animation stutter; it does not make it slow.
        /// </remarks>
        private const int AnimationTickMilliseconds = 16;

        private DispatcherTimer _animationTimer;

        /// <summary>
        /// Elapsed time since the last animation tick.
        /// </summary>
        /// <remarks>
        /// A stopwatch rather than two readings of the wall clock, because the wall clock is not
        /// monotonic: an NTP correction stepping it backwards would hand the emulator a negative
        /// interval to advance by. Nothing here needs to know what time it is, only how much of it
        /// has gone by, which is the question a stopwatch answers.
        /// </remarks>
        private readonly Stopwatch _animationClock = new();

        /// <summary>
        /// Starts or stops the animation clock to match whether anything is animating.
        /// </summary>
        /// <remarks>
        /// Called after output is processed, which is the only moment an animation can start or
        /// stop. A terminal showing nothing but text keeps no timer running at all.
        /// </remarks>
        private void SyncAnimationClock()
        {
            var wanted = _terminal.HasRunningAnimations();

            if (wanted == _animationTimer.IsEnabled)
                return;

            if (wanted)
            {
                // Reset the clock rather than counting the idle time: an animation started after a
                // quiet minute should begin at its first frame, not a minute into itself.
                _animationClock.Restart();
                _animationTimer.Start();
            }
            else
            {
                _animationTimer.Stop();
            }
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            var elapsed = _animationClock.Elapsed;
            _animationClock.Restart();

            // Through RequestPaint, so an atomic update holds an animation frame too. A picture
            // advancing mid-update would present the half-written screen underneath it, which is the
            // exact tearing this exists to stop -- and it arrives on a timer, so it is the one paint
            // path that can fire while an application is between BSU and ESU without being asked to.
            if (_terminal.AdvanceAnimations(elapsed))
                RequestPaint();

            // An animation that ran out of loops stops on its own, so the clock has to notice.
            SyncAnimationClock();
        }

        private void OnTerminalDirectoryChanged(object? sender, TerminalEvents.DirectoryChangeEventArgs e)
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                var oldValue = _currentDirectory;
                _currentDirectory = e.Directory;
                RaisePropertyChanged(CurrentDirectoryProperty, oldValue, _currentDirectory);
            });
        }

        /// <summary>
        /// Gets a value indicating whether the terminal is currently using the alternate screen buffer.
        /// </summary>
        public bool IsAlternateBuffer => _isAlternateBuffer;

        /// <summary>
        /// Gets or sets the terminal scrollback buffer size in lines.
        /// </summary>
        public int BufferSize
        {
            get => _bufferSize;
            set
            {
                // _terminal does not exist until OnInitialized, and a value can legitimately arrive before
                // then — a template binding is applied while the view is still initialising. Store it either
                // way; BuildTerminal reads _bufferSize when it constructs the emulator.
                if (_terminal != null)
                    _terminal.Options.Scrollback = value;

                SetAndRaise(BufferSizeProperty, ref _bufferSize, value);
                RequestPaint();
            }
        }

        /// <summary>
        /// The absolute line index of the top of the viewport in the buffer.
        /// 0 = top of buffer, higher values = scrolled forward towards current output.
        /// </summary>
        public int ViewportY
        {
            get => _terminal.Buffer.ViewportY;
            set
            {
                var oldValue = _terminal.Buffer.ViewportY;
                _terminal.Buffer.ViewportY = value;

                if (oldValue != _terminal.Buffer.ViewportY)
                {
                    RaisePropertyChanged(ViewportYProperty, oldValue, _terminal.Buffer.ViewportY);
                    RequestPaint();
                }
            }
        }

        /// <summary>
        /// Maximum scroll position (total buffer lines - viewport lines).
        /// This is the maximum value ViewportY can be.
        /// </summary>
        public int MaxScrollback
        {
            get
            {
                // Simple: total lines in buffer minus how many we can see
                var totalLines = _terminal.Buffer.Length;
                var viewportLines = _terminal.Rows;
                var max = Math.Max(0, totalLines - viewportLines);
                return max;
            }
        }

        public int ViewportLines => _terminal.Rows;

        public XTerm.Terminal Terminal => _terminal;

        public void WaitForExit(int ms) => _ptyConnection?.WaitForExit(ms);

        public void Kill() => _ptyConnection?.Kill();

        /// <summary>
        /// Wipe the screen AND the scrollback back to an empty buffer, via the parser's own
        /// erase sequences. Call it when a session returns to dormant, so the sleeping view is genuinely
        /// blank behind whatever stand-in the host draws, instead of showing the dead output of the process
        /// that just exited underneath it.
        /// No-op before <see cref="OnInitialized"/> has run — a pooled view that was never attached has no
        /// buffer to wipe, and a host posting this at Background priority can land the job on a view that
        /// has since been detached (or was never realised at all).
        /// </summary>
        public void ClearScreen()
        {
            if (_terminal == null) return;

            lock (_terminalLock)
            {
                _terminal.Write("\u001b[H\u001b[2J\u001b[3J");   // home · erase screen · erase scrollback
                _terminal.Buffer.ScrollToBottom();
            }
            RequestPaint();
        }

        /// <summary>
        /// The text of the row the cursor sits on, trailing blanks trimmed. Read it as a session goes
        /// dormant so the sleeping view can show the shell's REAL last prompt instead of a synthesized one.
        /// </summary>
        public string CurrentLineText
        {
            get
            {
                if (_terminal == null)
                    return string.Empty;

                lock (_terminalLock)
                {
                    var buffer = _terminal.Buffer;
                    var line = buffer.GetLine(buffer.YBase + buffer.Y);
                    if (line == null) return string.Empty;

                    var sb = new StringBuilder(line.Length);
                    for (int x = 0; x < line.Length; x++)
                        sb.Append(string.IsNullOrEmpty(line[x].Content) ? " " : line[x].Content);
                    return sb.ToString().TrimEnd();
                }
            }
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
        /// Copy the selection and then remove it, when it is removable. Returns false when there was
        /// nothing to copy.
        /// </summary>
        /// <remarks>
        /// <para>Cut is copy plus exactly the deletion that typing over a selection performs, so the same
        /// limit applies: only a KEYBOARD selection can be removed, because a mouse selection may sit
        /// anywhere on screen — including the scrollback — with no fixed relationship to the shell's
        /// cursor.</para>
        /// <para>Where it cannot remove, it does NOTHING and returns false — the clipboard is not touched
        /// and the selection is left standing. Copying instead would be worse than failing: the selection
        /// would clear, the clipboard would fill, and the source would still be there, which reads as a
        /// completed cut right until the user goes looking for what they moved.</para>
        /// </remarks>
        public async Task<bool> CutAsync()
        {
            if (!_terminal.Selection.HasSelection)
                return false;

            // Asked BEFORE anything is done, and answered without doing it.
            //
            // A cut that quietly turns into a copy is worse than a cut that does not happen: the selection
            // clears, the clipboard fills, and the source is still there — which reads as a completed cut
            // right until the user goes looking for what they moved. So a selection that cannot be removed
            // is left entirely alone, clipboard included, and false says so.
            //
            // The question has to be asked without consuming the answer: TakeKeyboardSelectionDeletion
            // CLEARS the selection as it takes it, so calling it first leaves CopyAsync nothing to copy.
            if (!CanRemoveSelection)
                return false;

            if (!await CopyAsync().ConfigureAwait(false))
                return false;

            var deletion = TakeKeyboardSelectionDeletion();
            if (deletion.Length == 0)
                return false;

            await SendToPtyAsync(deletion).ConfigureAwait(false);
            return true;
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

        /// <summary>
        /// Gets the exit code of the launched PTY process after it has terminated.
        /// </summary>
        public int ExitCode => _ptyConnection?.ExitCode ?? -1;

        /// <summary>
        /// Gets the operating system process identifier of the launched PTY process.
        /// </summary>
        /// <summary>
        /// Ask for a frame, unless an application is mid-update.
        /// </summary>
        private void RequestPaint()
        {
            if (_atomicUpdate)
                return;

            TerminalRenderThrottle.RequestInvalidate(this);
        }

        private void OnSynchronizedOutputChanged(object? sender, XT.Events.TerminalEvents.SynchronizedOutputEventArgs e)
        {
            if (e.Active)
                BeginAtomicUpdate();
            else
                EndAtomicUpdate();
        }

        private void BeginAtomicUpdate()
        {
            _atomicUpdate = true;

            _atomicUpdateTimeout?.Dispose();
            _atomicUpdateTimeout = DispatcherTimer.RunOnce(
                () =>
                {
                    // The application never finished. Paint what there is rather than stay frozen.
                    if (_atomicUpdate)
                        EndAtomicUpdate();
                },
                AtomicUpdateTimeout);
        }

        private void EndAtomicUpdate()
        {
            _atomicUpdateTimeout?.Dispose();
            _atomicUpdateTimeout = null;

            if (!_atomicUpdate)
                return;

            _atomicUpdate = false;

            // One frame for the whole update, which is the point.
            RequestPaint();
        }

        public int Pid => _ptyConnection!.Pid;

        /// <summary>
        /// Gets or sets the font family used to render terminal text.
        /// </summary>
        public FontFamily FontFamily
        {
            get => GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        /// <summary>
        /// Gets or sets the font size used to render terminal text.
        /// </summary>
        public double FontSize
        {
            get => GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        /// <summary>
        /// Gets or sets the font style used to render terminal text.
        /// </summary>
        public FontStyle FontStyle
        {
            get => GetValue(FontStyleProperty);
            set => SetValue(FontStyleProperty, value);
        }

        /// <summary>
        /// Gets or sets the font weight used to render terminal text.
        /// </summary>
        public FontWeight FontWeight
        {
            get => GetValue(FontWeightProperty);
            set => SetValue(FontWeightProperty, value);
        }

        /// <summary>
        /// Gets or sets the text decoration locations applied to terminal text.
        /// </summary>
        public TextDecorationLocation? TextDecorations
        {
            get => GetValue(TextDecorationsProperty);
            set => SetValue(TextDecorationsProperty, value);
        }

        /// <summary>
        /// Gets or sets the default foreground brush used for terminal text.
        /// </summary>
        public IBrush Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        /// <summary>
        /// Gets or sets the terminal background brush.
        /// </summary>
        public IBrush Background
        {
            get => GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        /// <summary>
        /// Gets or sets the brush used to render selected terminal text.
        /// </summary>
        public IBrush SelectionBrush
        {
            get => GetValue(SelectionBrushProperty);
            set => SetValue(SelectionBrushProperty, value);
        }

        /// <summary>
        /// Gets or sets the executable or shell to launch in the terminal.
        /// </summary>
        public string Process
        {
            get => GetValue(ProcessProperty);
            set => SetValue(ProcessProperty, value);
        }

        /// <summary>
        /// Gets or sets the command-line arguments passed to <see cref="Process"/> when launching.
        /// </summary>
        public IList<string> Args
        {
            get => GetValue(ArgsProperty);
            set => SetValue(ArgsProperty, value);
        }

        /// <summary>
        /// Gets or sets the initial working directory used when the PTY process is started.
        /// </summary>
        public string? StartingDirectory
        {
            get => GetValue(StartingDirectoryProperty);
            set => SetValue(StartingDirectoryProperty, value);
        }

        /// <summary>
        /// Gets the current working directory reported by the running terminal session.
        /// </summary>
        public string? CurrentDirectory => _currentDirectory;

        /// <summary>
        /// Gets or sets the cursor color used when rendering the terminal caret.
        /// </summary>
        public Color CursorColor
        {
            get => GetValue(CursorColorProperty);
            set => SetValue(CursorColorProperty, value);
        }

        /// <summary>
        /// Gets or sets the cursor style used by the terminal.
        /// </summary>
        public XT.Common.CursorStyle CursorStyle
        {
            get => GetValue(CursorStyleProperty);
            set => SetValue(CursorStyleProperty, value);
        }

        /// <summary>
        /// Gets or sets a value indicating whether the terminal cursor should blink.
        /// </summary>
        public bool CursorBlink
        {
            get => GetValue(CursorBlinkProperty);
            set => SetValue(CursorBlinkProperty, value);
        }

        /// <summary>
        /// Gets or sets the cursor blink rate in milliseconds.
        /// </summary>
        public int CursorBlinkRate
        {
            get => GetValue(CursorBlinkRateProperty);
            set => SetValue(CursorBlinkRateProperty, value);
        }

        /// <summary>
        /// Gets or sets the terminal emulation options used to configure the inner <see cref="XTerm.Terminal"/>.
        /// </summary>
        public XTerm.Options.TerminalOptions? Options
        {
            get => GetValue(OptionsProperty);
            set => SetValue(OptionsProperty, value);
        }

        /// <summary>
        /// Seed the emulator's DEFAULT colour pair from <see cref="Foreground"/> and <see cref="Background"/>.
        /// </summary>
        /// <remarks>
        /// Written into the theme before construction, so these become the values the emulator resets to and
        /// not merely its current pair. Only a solid brush carries one colour to seed with; a gradient has no
        /// single answer, so the emulator keeps its own default rather than being handed an arbitrary stop.
        /// </remarks>
        private void SeedThemeFromBrushes(XT.Options.ThemeOptions theme)
        {
            if (Foreground is ISolidColorBrush fg)
                theme.Foreground = ToHex(fg.Color);

            if (Background is ISolidColorBrush bg)
                theme.Background = ToHex(bg.Color);

            static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        /// <summary>
        /// Move the emulator's live colour pair after it has been built, for a host that re-themes.
        /// </summary>
        private void SyncPaletteToBrushes()
        {
            if (_terminal == null)
                return;

            if (Foreground is ISolidColorBrush fg)
                _terminal.Colors.SetForeground(ToRgb(fg.Color));

            if (Background is ISolidColorBrush bg)
                _terminal.Colors.SetBackground(ToRgb(bg.Color));

            static int ToRgb(Color c) => (c.R << 16) | (c.G << 8) | c.B;
        }

        /// <summary>
        /// A program moved the palette. Cached runs hold brushes resolved from the old colours, so they are
        /// dropped rather than replayed.
        /// </summary>
        private void OnTerminalColorChanged(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_terminal == null)
                    return;

                for (int y = 0; y < _terminal.Buffer.Length; y++)
                {
                    var line = _terminal.Buffer.GetLine(y);
                    if (line != null)
                        line.Cache = null;
                }

                InvalidateVisual();
            });
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            // BEFORE the _terminal null-check below, deliberately. That guard returns early while the view
            // is still initialising, which is exactly when an object initialiser or a template binding sets
            // this — mirroring after it would silently drop the value and leave the reader following the
            // default forever.
            if (change.Property == AutoScrollToBottomProperty)
                _autoScroll = change.GetNewValue<bool>();

            base.OnPropertyChanged(change);

            // _terminal and _cursorBlinkTimer are built in OnInitialized, and a cursor property can arrive
            // before that: the control template applies its bindings while the view is still initialising.
            // Nothing set these that early until TerminalControl began forwarding them, at which point this
            // threw a NullReferenceException from inside Avalonia's property machinery. Skipping here loses
            // nothing, because OnInitialized reads the current values when it builds the emulator.
            if (_terminal == null || _cursorBlinkTimer == null)
                return;

            if (change.Property == ForegroundProperty || change.Property == BackgroundProperty)
            {
                // Re-themed after the emulator was built, so the palette that answers OSC 10/11 has to move
                // with it. AffectsRender already covers the repaint; this is the emulator's own copy.
                SyncPaletteToBrushes();
            }
            else if (change.Property == CursorStyleProperty)
            {
                _terminal.Options.CursorStyle = (XT.Common.CursorStyle)change.NewValue!;
            }
            else if (change.Property == CursorBlinkProperty)
            {
                var blink = (bool)change.NewValue!;
                _terminal.Options.CursorBlink = blink;

                if (blink && IsFocused)
                {
                    _cursorBlinkTimer.Start();
                }
                else
                {
                    _cursorBlinkTimer.Stop();
                    _cursorBlinkOn = true;  // Reset to visible when blinking stops
                }
            }
            else if (change.Property == OutputReceivedOnReadTaskProperty)
            {
                _outputOnReadTask = (bool)change.NewValue!;
            }
            else if (change.Property == CursorBlinkRateProperty)
            {
                var rate = (int)change.NewValue!;
                _terminal.Options.CursorBlinkRate = rate;
                _cursorBlinkTimer.Interval = TimeSpan.FromMilliseconds(rate > 0 ? rate : 530);
            }
        }

        private async void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (_ptyConnection == null && !string.IsNullOrEmpty(Process))
            {
                await LaunchProcess();
            }

            // Start cursor blinking if enabled
            if (CursorBlink)
            {
                _cursorBlinkTimer.Start();
            }

            // And the animation clock, which OnUnloaded stopped. Output is the only other thing
            // that starts it, so without this a view detached and re-attached -- a tab switched
            // away and back -- comes back with its animation frozen until something writes, which
            // at an idle prompt is never.
            SyncAnimationClock();
        }

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            _cursorBlinkTimer.Stop();

            // A view off the tree has nothing to repaint, and a timer left running would hold it
            // alive through the dispatcher and go on advancing frames nobody can see.
            _animationTimer.Stop();

            _isSelecting = false;
            _pendingSelectionStart = null;
        }

        /// <summary>
        /// Call before removing this view from one visual tree and adding it to another.
        /// Prevents <see cref="OnDetachedFromLogicalTree"/> from killing the PTY process.
        /// Must be paired with <see cref="EndReparent"/> once re-attached.
        /// </summary>
        public void BeginReparent() => _suppressCleanupOnDetach = true;

        /// <summary>
        /// Call after the view has been re-attached to a new visual tree to restore
        /// normal cleanup behaviour and ensure render handlers are wired up.
        /// </summary>
        public void EndReparent() => _suppressCleanupOnDetach = false;

        protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromLogicalTree(e);

            // Mirror of the guard in OnAttachedToLogicalTree, which already notes that _terminal is null
            // during initial attachment because OnInitialized has not fired yet. Attachment is NOTIFIED in
            // that window, so a handler that re-parents the view on attach detaches it while the emulator
            // still does not exist — and every unsubscribe below then throws.
            //
            // CleanupProcess still runs: a view can have been handed a connection through AttachConnection
            // without ever having been initialised, and that connection still has to be let go.
            if (_terminal == null)
            {
                if (!_suppressCleanupOnDetach)
                    CleanupProcess();
                return;
            }

            // Against the remembered instance, not _terminal.Buffer: detaching while a full-screen app has
            // the alternate buffer active would otherwise unsubscribe from the wrong object.
            if (_scrollbackBuffer != null)
                _scrollbackBuffer.Trimmed -= OnBufferTrimmed;

            _terminal.DataReceived -= OnTerminalDataReceived;
            _terminal.BufferChanged -= OnTerminalBufferChanged;
            _terminal.CursorStyleChanged -= OnTerminalCursorStyleChanged;
            _terminal.TitleChanged -= OnTerminalTitleChanged;
            _terminal.SynchronizedOutputChanged -= OnSynchronizedOutputChanged;
            _terminal.WindowMoved -= OnTerminalWindowMoved;
            _terminal.WindowResized -= OnTerminalWindowResized;
            _terminal.WindowMinimized -= OnTerminalWindowMinimized;
            _terminal.WindowMaximized -= OnTerminalWindowMaximized;
            _terminal.WindowRestored -= OnTerminalWindowRestored;
            _terminal.WindowRaised -= OnTerminalWindowRaised;
            _terminal.WindowLowered -= OnTerminalWindowLowered;
            _terminal.WindowFullscreened -= OnTerminalWindowFullscreened;
            _terminal.BellRang -= OnTerminalBellRang;
            _terminal.DirectoryChanged -= OnTerminalDirectoryChanged;
            _terminal.WindowInfoRequested -= OnTerminalWindowInfoRequested;

            // A view detached mid-update must not keep the gate closed or the timer alive. The
            // timeout would self-heal in 150 ms, but the timer holds the view for that window, and
            // a view re-attached inside it would start out refusing to paint for no reason.
            _atomicUpdateTimeout?.Dispose();
            _atomicUpdateTimeout = null;
            _atomicUpdate = false;

            if (!_suppressCleanupOnDetach)
                CleanupProcess();
        }

        protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
        {
            base.OnAttachedToLogicalTree(e);

            // _terminal is null during initial attachment (OnInitialized hasn't fired yet).
            // Only re-subscribe when re-parenting after a prior detach.
            if (_terminal == null) return;

            // Re-subscribe terminal events that were unsubscribed on detach.
            // Use -= before += to avoid double-subscription.
            if (_scrollbackBuffer != null)
            {
                _scrollbackBuffer.Trimmed -= OnBufferTrimmed;
                _scrollbackBuffer.Trimmed += OnBufferTrimmed;
            }

            _terminal.DataReceived -= OnTerminalDataReceived;
            _terminal.BufferChanged -= OnTerminalBufferChanged;
            _terminal.CursorStyleChanged -= OnTerminalCursorStyleChanged;
            _terminal.TitleChanged -= OnTerminalTitleChanged;
            _terminal.SynchronizedOutputChanged -= OnSynchronizedOutputChanged;
            _terminal.WindowMoved -= OnTerminalWindowMoved;
            _terminal.WindowResized -= OnTerminalWindowResized;
            _terminal.WindowMinimized -= OnTerminalWindowMinimized;
            _terminal.WindowMaximized -= OnTerminalWindowMaximized;
            _terminal.WindowRestored -= OnTerminalWindowRestored;
            _terminal.WindowRaised -= OnTerminalWindowRaised;
            _terminal.WindowLowered -= OnTerminalWindowLowered;
            _terminal.WindowFullscreened -= OnTerminalWindowFullscreened;
            _terminal.BellRang -= OnTerminalBellRang;
            _terminal.DirectoryChanged -= OnTerminalDirectoryChanged;
            _terminal.WindowInfoRequested -= OnTerminalWindowInfoRequested;

            _terminal.DataReceived += OnTerminalDataReceived;
            _terminal.BufferChanged += OnTerminalBufferChanged;
            _terminal.CursorStyleChanged += OnTerminalCursorStyleChanged;
            _terminal.TitleChanged += OnTerminalTitleChanged;
            _terminal.SynchronizedOutputChanged += OnSynchronizedOutputChanged;
            _terminal.WindowMoved += OnTerminalWindowMoved;
            _terminal.WindowResized += OnTerminalWindowResized;
            _terminal.WindowMinimized += OnTerminalWindowMinimized;
            _terminal.WindowMaximized += OnTerminalWindowMaximized;
            _terminal.WindowRestored += OnTerminalWindowRestored;
            _terminal.WindowRaised += OnTerminalWindowRaised;
            _terminal.WindowLowered += OnTerminalWindowLowered;
            _terminal.WindowFullscreened += OnTerminalWindowFullscreened;
            _terminal.BellRang += OnTerminalBellRang;
            _terminal.DirectoryChanged += OnTerminalDirectoryChanged;
            _terminal.WindowInfoRequested += OnTerminalWindowInfoRequested;
        }

        private void OnCursorBlinkTick(object? sender, EventArgs e)
        {
            if (CursorBlink && IsFocused)
            {
                _cursorBlinkOn = !_cursorBlinkOn;
                for (int y = 0; y < _terminal.Rows; y++)
                {
                    var line = _terminal.Buffer.GetLine(y);
                    if (line != null && line.Any(cell => cell.Attributes.IsBlink()))
                    {
                        line.Cache = null;
                    }
                }

                RequestPaint();
            }
        }

        // macOS uses the Command (⌘ / Meta) key for clipboard shortcuts, following native
        // platform conventions (Terminal.app, iTerm2, etc.). Windows and Linux terminals use
        // Ctrl+Shift+C / Ctrl+Shift+V instead, because plain Ctrl+C is reserved for SIGINT.
        private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

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

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            // Only process input if this terminal has focus
            if (!IsFocused)
            {
                base.OnKeyDown(e);
                return;
            }

            // Capture the connection reference locally
            var ptyConnection = _ptyConnection;
            if (ptyConnection == null)
            {
                Debug.WriteLine($"[TerminalView] No PTY connection");
                base.OnKeyDown(e);
                return;
            }

            // When the process has exited, stop eating keyboard input so that Avalonia's
            // normal focus navigation (Tab/Shift+Tab etc.) works again.  We still handle
            // the copy shortcut so the user can copy terminal output after a run.
            // ShortcutMode.None hands the whole keyboard to the program: no copy, no paste, and Ctrl+C
            // reaching it as plain SIGINT because nothing intercepts it first.
            bool shortcuts = ShortcutMode != ShortcutMode.None;

            if (_processExitHandled != 0)
            {

                bool isCopy = shortcuts && e.Key == Key.C &&
                              (e.KeyModifiers == KeyModifiers.Control ||
                               e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) ||
                               (IsMacOS && e.KeyModifiers == KeyModifiers.Meta));
                if (isCopy && _terminal.Selection.HasSelection)
                {
                    e.Handled = true;
                    // Copy leaves the selection in place, the way every other application does:
                    // copying is not a destructive act, and a selection you can no longer see is one
                    // you cannot copy again, extend, or replace.
                    await CopyAsync();
                    RequestPaint();
                }
                else
                {
                    base.OnKeyDown(e);
                }
                return;
            }

            try
            {
                // macOS clipboard shortcuts use the Command (Meta) key. These don't collide
                // with terminal control codes (SIGINT is Ctrl+C, not Cmd+C), so we can handle
                // them directly here. On Windows/Linux this block is skipped and the
                // Ctrl / Ctrl+Shift shortcuts below are used instead.
                if (shortcuts && IsMacOS && e.KeyModifiers == KeyModifiers.Meta)
                {
                    // Cmd+C - copy the selection (no-op when nothing is selected, matching macOS)
                    if (e.Key == Key.C)
                    {
                        e.Handled = true;
                        if (_terminal.Selection.HasSelection)
                        {
                            // Copy leaves the selection in place, the way every other application does:
                            // copying is not a destructive act, and a selection you can no longer see is one
                            // you cannot copy again, extend, or replace.
                            await CopyAsync();
                            RequestPaint();
                        }
                        return;
                    }

                    // Cmd+V - paste from the clipboard
                    if (e.Key == Key.V)
                    {
                        e.Handled = true;
                        await PasteAsync();
                        return;
                    }
                }

                // The macOS clipboard gestures are Cmd-based, and every one of them is unbound in a
                // terminal — so they are unconditional, like the Cmd+C and Cmd+V above. There is nothing to
                // take and so nothing to opt into.
                if (shortcuts && IsMacOS && e.KeyModifiers == KeyModifiers.Meta)
                {
                    if (e.Key == Key.X)
                    {
                        // Only claimed when it actually cuts. A selection it cannot remove — one made with
                        // the mouse, or sitting up in the scrollback — is left alone rather than quietly
                        // copied, so nothing looks like it moved when it did not.
                        if (await CutAsync().ConfigureAwait(false))
                        {
                            e.Handled = true;
                            return;
                        }
                    }

                    if (e.Key == Key.A)
                    {
                        e.Handled = true;
                        await SelectInputAsync().ConfigureAwait(false);
                        return;
                    }
                }

                // The desktop map. Three things switch it off, each for its own reason.
                //
                // ShortcutMode, because these keys are contested and the host has to say which it wants.
                //
                // The ALTERNATE SCREEN, because a full-screen application owns its own keys: vim's Ctrl+V is
                // blockwise-visual, not paste. While one is running the terminal stands aside and behaves as
                // Terminal mode, so Ctrl+Shift+C still copies text out of it.
                //
                // macOS, because there the desktop clipboard lives on Cmd — handled above, in either mode —
                // while Ctrl+A and Ctrl+E are the system-wide emacs line bindings every macOS text field
                // honours. Leaving them to the program IS the desktop behaviour on that platform.
                if (ShortcutMode == ShortcutMode.Desktop && !IsMacOS && !_terminal.IsAlternateBufferActive)
                {
                    if (e.KeyModifiers == KeyModifiers.Control)
                    {
                        switch (e.Key)
                        {
                            case Key.A:
                                e.Handled = true;
                                await SelectInputAsync().ConfigureAwait(false);
                                return;

                            case Key.V:
                                e.Handled = true;
                                await PasteAsync().ConfigureAwait(false);
                                return;

                            case Key.X:
                                // Only when there is something to cut, and only when it can actually be
                                // removed. Otherwise the chord falls through to the program — where it is
                                // readline's prefix, and worth more than a cut that silently became a copy.
                                if (await CutAsync().ConfigureAwait(false))
                                {
                                    e.Handled = true;
                                    return;
                                }
                                break;
                        }
                    }

                    // Shift carries what the unshifted chord used to send: the literal control character.
                    if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key is Key.V or Key.X)
                    {
                        var literal = _terminal.GenerateCharInput(
                            e.Key == Key.V ? 'v' : 'x', XT.Input.KeyModifiers.Control);
                        if (!string.IsNullOrEmpty(literal))
                        {
                            e.Handled = true;
                            await SendToPtyAsync(literal).ConfigureAwait(false);
                            return;
                        }
                    }
                }

                // Handle Ctrl+C - copy if there's a selection, otherwise send SIGINT
                if (shortcuts && e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
                {
                    if (_terminal.Selection.HasSelection)
                    {
                        e.Handled = true;
                        // Copy leaves the selection in place, the way every other application does:
                        // copying is not a destructive act, and a selection you can no longer see is one
                        // you cannot copy again, extend, or replace.
                        await CopyAsync();
                        RequestPaint();
                        return;
                    }
                    // No selection - fall through to send Ctrl+C (SIGINT) to the process
                }

                // Handle Ctrl+Shift+C for copy (always copies, doesn't send SIGINT)
                if (shortcuts && e.Key == Key.C && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
                {
                    if (_terminal.Selection.HasSelection)
                    {
                        e.Handled = true;
                        // Copy leaves the selection in place, the way every other application does:
                        // copying is not a destructive act, and a selection you can no longer see is one
                        // you cannot copy again, extend, or replace.
                        await CopyAsync();
                        RequestPaint();
                        return;
                    }
                }

                // Typing means "put me back at the prompt" — every terminal jumps to the tail on input,
                // and without it a user who scrolled up types blind. Past the bare modifiers for the same
                // reason the selection-clear below skips them: pressing Ctrl on its own is not typing.
                if (!IsModifierKey(e.Key))
                    FollowTail();

                // Shift + navigation extends a selection in the buffer rather than sending the modified
                // cursor sequence (ESC[1;2C and friends), which no interactive shell binds — zsh just
                // echoes the ";2C" tail into the command line. Must come BEFORE the blanket clear below,
                // since this is the one keystroke family that GROWS a selection instead of dropping it.
                if (TryExtendKeyboardSelection(e))
                {
                    e.Handled = true;
                    return;
                }

                // Clear selection for any other keystroke - but ignore bare modifier
                // presses. Pressing ⌘/Ctrl/Shift on its own fires a KeyDown before the
                // shortcut's letter arrives; clearing here would lose the selection
                // before Cmd+C / Ctrl+Shift+C could copy it.
                if (!IsModifierKey(e.Key))
                {
                    // A keystroke that TYPES replaces the selection; Backspace and Delete remove it — both
                    // of them, as in any text field, where either key means "get rid of what is selected"
                    // rather than "act on one character". Anything else just drops the selection.
                    bool unmodified = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0;
                    bool willType = unmodified && TryGetPrintableChar(e, out _);
                    bool willErase = unmodified && e.Key is Key.Back or Key.Delete;

                    if (willType || willErase)
                        NoteInputStart();

                    _pendingReplaceKeys = willType || willErase ? TakeKeyboardSelectionDeletion() : string.Empty;
                    if (_pendingReplaceKeys.Length == 0)
                    {
                        // The anchor is released whether or not a selection is currently drawn. A gesture can
                        // leave the anchor set having selected NOTHING — Shift+End at the end of a line, say —
                        // and gating this on HasSelection then leaves the caret pinned to that boundary while
                        // typed characters append somewhere else.
                        _kbSelAnchor = null;
                        _kbSelWholeInput = false;

                        if (_terminal.Selection.HasSelection)
                        {
                            _terminal.Selection.ClearSelection();
                            RequestPaint();
                        }
                    }
                }

                // Handle Ctrl+Shift+V for paste (standard terminal shortcut)
                // Ctrl+V is NOT intercepted - it gets passed to the application
                // (some apps use Ctrl+V for literal character input mode)
                if (shortcuts && e.Key == Key.V && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
                {
                    e.Handled = true;
                    await PasteAsync();
                    return;
                }

                // Every other Meta chord belongs to the APPLICATION, not the shell. The macOS block above
                // claims Cmd+C and Cmd+V; anything else fell straight through to the character path below
                // and was typed into the process, so a host binding Cmd+K quietly sent the shell a "k".
                // Left unhandled so it bubbles to the app's key bindings.
                // Same reason as the selection alias above: a Mac keyboard has no Home/End, so Cmd+arrow
                // is how a Mac user asks for line-start and line-end. Sends exactly what Home and End
                // send, so it is an alias rather than a second code path — and it has to come BEFORE the
                // Meta passthrough below, which would otherwise swallow it.
                if (IsMacOS && e.KeyModifiers == KeyModifiers.Meta && e.Key is Key.Left or Key.Right)
                {
                    e.Handled = true;
                    await SendToPtyAsync(e.Key == Key.Left ? "\u001b[H" : "\u001b[F").ConfigureAwait(false);
                    return;
                }

                if ((e.KeyModifiers & KeyModifiers.Meta) != 0)
                    return;

                // Alt/Ctrl + Left/Right — "move by word". What the emulator generates for these is a
                // modified-cursor sequence (ESC[1;3D, ESC[1;5D) that no default shell keymap binds, so zsh
                // echoes the ";3D" tail straight into the command line. ESC-b / ESC-f — backward-word and
                // forward-word — is what zsh, bash's readline, fish and PSReadLine's default emacs mode all
                // bind out of the box, so that is what these chords send.
                //
                // Left alone in the alternate buffer, where a full-screen app reads the real sequence itself.
                //
                // And left alone when the process is reading WIN32 INPUT RECORDS. cmd.exe turns that mode on
                // as it starts (CSI ?9001h), and both it and PSReadLine already move by word on a real
                // Ctrl+Left — while neither binds ESC-b. Translating here replaced a chord they understand
                // with one they ignore, so on Windows the key did nothing at all. Falling through hands them
                // the actual key event a few lines below.
                if (e.Key is Key.Left or Key.Right
                    && e.KeyModifiers is KeyModifiers.Alt or KeyModifiers.Control
                    && !_terminal.Win32InputMode
                    && !_terminal.IsAlternateBufferActive)
                {
                    e.Handled = true;
                    await SendToPtyAsync(e.Key == Key.Left ? "\u001bb" : "\u001bf").ConfigureAwait(false);
                    return;
                }

                var modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);
                var hasAlt = (modifiers & XT.Input.KeyModifiers.Alt) != 0;

                // Windows ConPTY limitation: There is no VT sequence for plain ESCAPE key.
                // When ENABLE_VIRTUAL_TERMINAL_INPUT is enabled (by cmd.exe), the only way
                // to send ESCAPE is via Win32 INPUT_RECORD format. Always use Win32 for ESC on Windows.
                bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                bool isEscapeKey = e.Key == Key.Escape;
                bool useWin32Format = _terminal.Win32InputMode || (isWindows && isEscapeKey);

                if (useWin32Format)
                {
                    var sequence = GenerateWin32InputSequence(e, isKeyDown: true);
                    if (!string.IsNullOrEmpty(sequence))
                    {
                        e.Handled = true;

                        await SendToPtyAsync(sequence).ConfigureAwait(false);
                        return;
                    }
                    // If we couldn't generate a Win32 sequence, fall through to normal handling
                    // This can happen for keys that don't have a virtual key mapping
                }

                // Convert Avalonia key to XTerm key
                var xtermKey = ConvertAvaloniaKeyToXTermKey(e.Key);

                // Special keys (arrows, function keys, Tab, etc.) - always handle in KeyDown
                if (xtermKey != null)
                {
                    // Backspace or Delete over a selection removes the SELECTION, not one more character
                    // beyond it — so the keystroke's own sequence is replaced rather than added to.
                    var erase = _pendingReplaceKeys;
                    _pendingReplaceKeys = string.Empty;
                    if (erase.Length > 0 && e.Key is Key.Back or Key.Delete)
                    {
                        e.Handled = true;
                        await SendToPtyAsync(erase).ConfigureAwait(false);
                        return;
                    }

                    var sequence = _terminal.GenerateKeyInput(xtermKey.Value, modifiers);
                    if (!string.IsNullOrEmpty(sequence))
                    {
                        e.Handled = true;
                        await SendToPtyAsync(sequence).ConfigureAwait(false);
                    }
                    return;
                }

                // Ctrl/Alt + character combinations (these don't generate TextInput events)
                if ((modifiers & (XT.Input.KeyModifiers.Control | XT.Input.KeyModifiers.Alt)) != 0)
                {
                    if (TryGetPrintableChar(e, out var keyChar))
                    {
                        var sequence = _terminal.GenerateCharInput(keyChar, modifiers);
                        if (!string.IsNullOrEmpty(sequence))
                        {
                            e.Handled = true;
                            await SendToPtyAsync(sequence).ConfigureAwait(false);
                        }
                    }
                    return;
                }

                // Try to get a printable character - first from KeySymbol, then from key mapping
                // This is critical for Consolonia where KeySymbol may be empty
                if (TryGetPrintableChar(e, out var printableChar))
                {
                    e.Handled = true;
                    // One write: the deletion and the character replacing it, in that order.
                    var replaced = _pendingReplaceKeys;
                    _pendingReplaceKeys = string.Empty;
                    await SendToPtyAsync(replaced + printableChar).ConfigureAwait(false);
                    return;
                }

                // If we couldn't handle it, let TextInput try (for desktop Avalonia)
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_instanceId}] Error handling key input: {ex.Message}");
            }
        }

        /// <summary>
        /// The keystrokes that remove what a keyboard selection covers, so that typing over a selection
        /// REPLACES it the way it does in a text field. Empty when there is nothing to replace. Clears the
        /// selection as it takes it.
        /// </summary>
        /// <remarks>
        /// <para>The view cannot edit the line: the shell owns it. So the selection is turned into the
        /// keystrokes a user would have pressed to remove it. The shell's cursor never moved from the
        /// anchor, so a backwards selection is that many Backspaces; a forwards one walks the cursor to the
        /// far end with the right arrow first and deletes backwards from there.</para>
        /// <para>Backspace for both directions rather than Delete for the forward case, because
        /// forward-delete is not reliably bound: zsh started with no rc file does not know ESC[3~ — it
        /// swallows the ESC[3 and TYPES the tilde. Arrows and Backspace are bound everywhere.</para>
        /// <para>Returned rather than sent, so the caller can write the deletion and the new character as
        /// ONE write. Sending them separately loses the race against the next keystroke: the handler owning
        /// the deletion awaits it while the handlers behind it queue their characters first, and "there"
        /// typed over a selection arrives as "heret". Measured, not theorised.</para>
        /// <para>Only a KEYBOARD selection qualifies. A mouse selection can sit anywhere on screen,
        /// including the scrollback, with no fixed relationship to the shell's cursor, so typing over one
        /// clears it without deleting. The alternate buffer is excluded: a full-screen app owns its own
        /// editing.</para>
        /// </remarks>
        /// <summary>
        /// Whether the current selection is one this view can remove — the same conditions
        /// <see cref="TakeKeyboardSelectionDeletion"/> applies, asked without consuming them.
        /// </summary>
        private bool CanRemoveSelection
            => _kbSelAnchor is not null
               && _terminal.Selection.HasSelection
               && !_terminal.IsAlternateBufferActive
               && _kbSelAnchor.Value != _kbSelFocus;

        private string TakeKeyboardSelectionDeletion()
        {
            if (_kbSelAnchor is null || !_terminal.Selection.HasSelection)
                return string.Empty;
            if (_terminal.IsAlternateBufferActive)
                return string.Empty;

            int anchor = _kbSelAnchor.Value;
            int count = Math.Abs(anchor - _kbSelFocus);
            if (count == 0)
                return string.Empty;

            var backspace = _terminal.GenerateKeyInput(XT.Input.Key.Backspace, XT.Input.KeyModifiers.None);
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
                var right = _terminal.GenerateKeyInput(XT.Input.Key.RightArrow, XT.Input.KeyModifiers.None);
                if (string.IsNullOrEmpty(right))
                    return string.Empty;
                keys = string.Concat(Enumerable.Repeat(right, count))
                     + string.Concat(Enumerable.Repeat(backspace, count));
            }

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

        /// <summary>
        /// The highest boundary a keyboard selection may reach: just past the last written cell at or after
        /// the input start.
        /// </summary>
        /// <remarks>
        /// A terminal grid is padded to full width with blanks, so without this Shift+Right walks off the
        /// end of the input and selects the empty rest of the screen a cell at a time. There is nothing
        /// there to select, and nothing the replace could do with it.
        ///
        /// Scanned backwards from the end so a wrapped input — which spans rows — is bounded by its real
        /// end rather than by the row the caret happens to be on. Wide glyphs count their placeholder, for
        /// the same reason <see cref="LineEndBoundary"/> does.
        ///
        /// <para>KNOWN LIMIT: a trailing space the user typed is not counted, so a selection stops just
        /// before it. There is no way to do better here — the emulator fills unwritten cells with spaces,
        /// and a typed space is identical to one of those in every respect. Measured: both carry
        /// <c>Content == " "</c>, <c>Width == 1</c> and <c>CodePoint == 32</c>. Distinguishing them needs
        /// the buffer to record that a cell was written, which is a change in XTerm.NET rather than
        /// here.</para>
        /// </remarks>
        private int InputEndBoundary(int cols, int lastBoundary)
        {
            int floor = InputStartBoundary(cols, lastBoundary);

            for (int b = lastBoundary - 1; b >= floor; b--)
            {
                int row = b / cols;
                int col = b % cols;
                var line = _terminal.Buffer.GetLine(_terminal.Buffer.ViewportY + row);
                if (line == null || col >= line.Length)
                    continue;

                var cell = line[col];
                if (!string.IsNullOrWhiteSpace(cell.Content))
                    return Math.Min(b + Math.Max(1, cell.Width), lastBoundary);
            }

            return floor;
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
                bool useWin32Format = _terminal.Win32InputMode || (isWindows && isEscapeKey);

                if (useWin32Format)
                {
                    var sequence = GenerateWin32InputSequence(e, isKeyDown: false);
                    if (!string.IsNullOrEmpty(sequence))
                    {
                        await SendToPtyAsync(sequence).ConfigureAwait(false);
                        e.Handled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_instanceId}] Error handling key up: {ex.Message}");
            }
        }

        protected override async void OnTextInput(TextInputEventArgs e)
        {
            // Only process input if this terminal has focus
            if (!IsFocused)
            {
                Debug.WriteLine($"[TerminalView] OnTextInput: Not focused, passing to base");
                base.OnTextInput(e);
                return;
            }

            // Capture the connection reference locally
            var ptyConnection = _ptyConnection;
            if (ptyConnection == null || string.IsNullOrEmpty(e.Text) || _processExitHandled != 0)
            {
                Debug.WriteLine($"[TerminalView] OnTextInput: No PTY or empty text");
                base.OnTextInput(e);
                return;
            }

            // In Win32 Input Mode, text input is handled via KeyDown/KeyUp events
            if (_terminal.Win32InputMode)
            {
                Debug.WriteLine($"[TerminalView] OnTextInput: Win32 input mode, skipping");
                return;
            }

            // Typing over a selection replaces it; failing that, the selection is simply dropped. The
            // anchor goes either way — see OnKeyDown.
            NoteInputStart();

            var replaceKeys = _pendingReplaceKeys.Length > 0 ? _pendingReplaceKeys : TakeKeyboardSelectionDeletion();
            _pendingReplaceKeys = string.Empty;
            if (replaceKeys.Length == 0)
            {
                _kbSelAnchor = null;
                _kbSelWholeInput = false;
                if (_terminal.Selection.HasSelection)
                {
                    _terminal.Selection.ClearSelection();
                    RequestPaint();
                }
            }

            FollowTail();   // typing returns the view to the prompt

            try
            {
                Debug.WriteLine($"[TerminalView] OnTextInput: Sending '{e.Text}' to PTY");
                await SendToPtyAsync(replaceKeys + e.Text).ConfigureAwait(false);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_instanceId}] Error handling text input: {ex.Message}");
            }
        }

        protected override async void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            // Request focus when clicked
            Focus();

            try
            {
                var point = e.GetPosition(this);
                var col = (int)(point.X / _charWidth);
                var row = (int)(point.Y / _charHeight);

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
                        _pendingUrlClick = pressed.Url;
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
                        e.Handled = true;
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
                        _pendingSelectionStart = (col, row);
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
                    var releaseCol = (int)(releasePoint.X / _charWidth);
                    var releaseRow = (int)(releasePoint.Y / _charHeight);
                    var released = FindUrlAtColumn(_terminal.Buffer.ViewportY + releaseRow, releaseCol);

                    // Only fire if the pointer is still on the same url it was pressed on.
                    if (released != null && released.Url == pendingUrl)
                        UrlClicked?.Invoke(this, new UrlClickedEventArgs(pendingUrl));

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
                var col = (int)(point.X / _charWidth);
                var row = (int)(point.Y / _charHeight);

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
                var col = (int)(point.X / _charWidth);
                var row = (int)(point.Y / _charHeight);

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
                        _terminal.Selection.StartSelection(_pendingSelectionStart.Value.Col, _pendingSelectionStart.Value.Row, XT.Selection.SelectionMode.Normal);
                        _pendingSelectionStart = null;
                    }
                    _terminal.Selection.UpdateSelection(col, viewportRow);
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

                var sequence = _terminal.GenerateMouseEvent(button, col, row, eventType, modifiers);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling mouse move: {ex.Message}");
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            ClearHoveredUrl();
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

            if (_ptyConnection != null && _terminal.MouseTrackingMode != XT.Input.MouseTrackingMode.None)
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
                var col = (int)(point.X / _charWidth);
                var row = (int)(point.Y / _charHeight);
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

        private void OnTerminalBufferChanged(object? sender, XT.Events.TerminalEvents.BufferChangedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var oldValue = _isAlternateBuffer;
                _isAlternateBuffer = e.Buffer == XT.Common.BufferType.Alternate;

                if (oldValue != _isAlternateBuffer)
                {
                    RaisePropertyChanged(IsAlternateBufferProperty, oldValue, _isAlternateBuffer);
                }

                RaisePropertyChanged(MaxScrollbackProperty, default(int), MaxScrollback);
                RaisePropertyChanged(ViewportLinesProperty, default(int), ViewportLines);
                RaisePropertyChanged(ViewportYProperty, default(int), ViewportY);
                RequestPaint();
            });
        }

        private void OnTerminalCursorStyleChanged(object? sender, XT.Events.TerminalEvents.CursorStyleChangedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!Equals(CursorStyle, e.Style))
                {
                    SetValue(CursorStyleProperty, e.Style);
                }

                if (!Equals(CursorBlink, e.Blink))
                {
                    SetValue(CursorBlinkProperty, e.Blink);
                }

                RequestPaint();
            });
        }

        private void OnTerminalTitleChanged(object? sender, XT.Events.TerminalEvents.TitleChangeEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {

                var args = new TitleChangedEventArgs(e.Title)
                {
                    RoutedEvent = TitleChangedEvent
                };

                RaiseEvent(args);
                TitleChanged?.Invoke(this, args);
            });
        }

        private void OnTerminalWindowMoved(object? sender, XT.Events.TerminalEvents.WindowMovedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new WindowMovedEventArgs(e.X, e.Y)
                {
                    RoutedEvent = WindowMovedEvent
                };

                RaiseEvent(args);
                WindowMoved?.Invoke(this, args);
            });
        }

        private void OnTerminalWindowResized(object? sender, XT.Events.TerminalEvents.WindowResizedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {

                var args = new WindowResizedEventArgs(e.Width, e.Height)
                {
                    RoutedEvent = WindowResizedEvent
                };

                RaiseEvent(args);
                WindowResized?.Invoke(this, args);
            });
        }

        private void OnTerminalWindowMinimized(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowMinimizedEvent);
                RaiseEvent(args);
                WindowMinimized?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalWindowMaximized(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowMaximizedEvent);
                RaiseEvent(args);
                WindowMaximized?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalWindowRestored(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowRestoredEvent);
                RaiseEvent(args);
                WindowRestored?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalWindowRaised(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowRaisedEvent);
                RaiseEvent(args);
                WindowRaised?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalWindowLowered(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowLoweredEvent);
                RaiseEvent(args);
                WindowLowered?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalWindowFullscreened(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(WindowFullscreenedEvent);
                RaiseEvent(args);
                WindowFullscreened?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnTerminalBellRang(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var args = new RoutedEventArgs(BellRangEvent);
                RaiseEvent(args);
                BellRang?.Invoke(this, EventArgs.Empty);
            });
        }

        /// <summary>
        /// Answers a program asking about the window — its size in cells or pixels, its position, its title.
        /// </summary>
        /// <remarks>
        /// <para>INVOKE, not Post, and the difference is the whole behaviour. The emulator raises this
        /// synchronously and reads <c>e.Handled</c> the moment the handler returns, to decide whether it has
        /// an answer to send. Post returns immediately, so Handled was still false and the reply was never
        /// sent — the answer was written correctly, on the UI thread, after the only reader of it had moved
        /// on. Every window query went unanswered and the program asking waited out its timeout.</para>
        /// <para>Blocking the reader thread here is safe because nothing on the UI thread waits on it: output
        /// is delivered by posting, and input is written asynchronously. Invoke also runs inline when this
        /// is already the UI thread, so a host driving the terminal directly does not deadlock on itself.</para>
        /// </remarks>
        private void OnTerminalWindowInfoRequested(object? sender, XT.Events.TerminalEvents.WindowInfoRequestedEventArgs e)
        {
            Dispatcher.UIThread.Invoke(() =>
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
            });
        }

        private async void OnTerminalDataReceived(object? sender, XT.Events.TerminalEvents.DataEventArgs e)
        {
            // Terminal wants to send data (typically in response to device status queries, etc.)
            await SendToPtyAsync(e.Data).ConfigureAwait(false);
        }

        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Everything the terminal sends TO the process: keystrokes, replies to its queries, focus and mouse
        /// reports.
        /// </summary>
        /// <remarks>
        /// Diagnostic. A program that behaves differently because of something the TERMINAL said is
        /// otherwise impossible to explain from the outside — the process's own output shows the decision
        /// but never the cause, and focus and mouse reports do not pass through the emulator's
        /// <c>DataReceived</c> at all, so watching that misses them entirely.
        /// </remarks>
        public event EventHandler<string>? InputSent;

        private async Task SendToPtyAsync(string data, CancellationToken ct = default)
        {
            InputSent?.Invoke(this, data);

            // Capture the connection reference locally to avoid any potential race conditions
            var ptyConnection = _ptyConnection;
            if (ptyConnection == null || string.IsNullOrEmpty(data))
                return;

            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var bytes = Utf8NoBom.GetBytes(data);
                await ptyConnection.WriterStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                await ptyConnection.WriterStream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_instanceId}] Error writing to PTY: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
            }
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
        /// A url currently under the pointer, resolved to the buffer cells it occupies.
        /// A url that wrapped across the right edge covers more than one segment.
        /// </summary>
        private sealed class HoveredUrl
        {
            public HoveredUrl(string url, List<(int Line, int StartCol, int EndCol)> segments)
            {
                Url = url;
                Segments = segments;
            }

            public string Url { get; }

            /// <summary>Inclusive cell ranges, one per buffer line the url spans.</summary>
            public List<(int Line, int StartCol, int EndCol)> Segments { get; }

            public bool Contains(int line, int col)
            {
                foreach (var s in Segments)
                {
                    if (s.Line == line && col >= s.StartCol && col <= s.EndCol)
                        return true;
                }
                return false;
            }

            public bool SameAs(HoveredUrl? other)
                => other != null &&
                   Url == other.Url &&
                   other.Segments.Count == Segments.Count &&
                   other.Segments[0] == Segments[0];
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

        private static int CountChar(string text, char c)
        {
            int count = 0;
            foreach (var ch in text)
            {
                if (ch == c)
                    count++;
            }
            return count;
        }

        private HoveredUrl? FindUrlAtColumn(int bufferLine, int col)
        {
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
        /// Drive this view from a PTY the CALLER owns, instead of one the view spawns.
        /// </summary>
        /// <remarks>
        /// <para>The view already knows how to render a PTY and report its exit; what it cannot currently do is
        /// take one it did not create. A host that keeps connections alive across UI changes — a pane that is
        /// closed and reopened, a session moved between tabs, a process that must outlive the control showing
        /// it — has to own the <see cref="IPtyConnection"/> itself, and today there is no way to hand it over.</para>
        /// <para>Ownership follows the caller. An attached connection is neither killed NOR disposed when the
        /// view is cleaned up — it is unsubscribed and its reader stopped, which detaches this view without
        /// stopping the process behind it. (Disposing would stop it: closing the pty ends the child on every
        /// platform.) A connection the view spawned through <see cref="LaunchProcess()"/> is killed and disposed
        /// as before. <see cref="DetachConnection"/> does the same thing on demand.</para>
        /// <para>It also makes the exit paths testable. A test can hand the view a connection whose child has
        /// exited but not yet been reaped — the window the EOF/reap handling exists for — and assert what gets
        /// reported, instead of racing a real shell and hoping to land in it.</para>
        /// </remarks>
        public void AttachConnection(IPtyConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            CleanupProcess();
            _externalConnection = true;
            _processCts = new CancellationTokenSource();

            // Same ordering as the spawn path: publish the connection, SUBSCRIBE, then start the reader. An
            // attached connection may already have a live process behind it, so an exit can arrive immediately
            // — subscribing after the reader starts is a window in which it is missed entirely.
            InstallConnection(connection);
            connection.ProcessExited += OnPtyProcessExited;
            // A thread of its own, not the pool — see ReadPtyOutputAsync for why the read is blocking.
            //
            // No readiness wait here, unlike the spawn path, and that is deliberate. AttachConnection is
            // synchronous and called from the UI thread; blocking it for up to five seconds would freeze the
            // app to protect against losing a few bytes. Subscribing above already removes the part that
            // matters — an exit can no longer be missed — and for an attached connection the caller already
            // owned it, so output from before the attach was never ours to catch.
            _ = Task.Factory.StartNew(
                () => ReadPtyOutputAsync(connection, _processCts.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Stop following the current connection and hand it back, without stopping the process behind it.
        /// </summary>
        /// <returns>The connection that was detached, or <c>null</c> if none was attached.</returns>
        /// <remarks>
        /// <para>Detaching already happens implicitly — closing the view, or attaching a replacement, does it —
        /// but only as a side effect of cleanup, where it is easy to get wrong. It was wrong here until
        /// recently: cleanup disposed the connection and a comment called that the detach, when disposing is
        /// what ends the child. Giving the operation a name is what makes that mistake visible next time.</para>
        /// <para>Ownership passes to the caller for whatever it returns, including a connection this view
        /// spawned itself — detaching one of those hands over a process the view would otherwise have killed,
        /// so the caller must dispose it when done. The view is left with nothing attached and
        /// <see cref="IsLive"/> false.</para>
        /// </remarks>
        public IPtyConnection? DetachConnection()
        {
            IPtyConnection? connection;
            lock (_exitGate)
            {
                connection = _ptyConnection;
            }

            if (connection is null)
            {
                return null;
            }

            // Marked external BEFORE cleanup, which is what makes cleanup let it live: the same flag the
            // attach path sets, meaning exactly the same thing — this process is somebody else's now.
            _externalConnection = true;
            CleanupProcess();
            return connection;
        }

        /// <summary>True while the connection belongs to an outside owner — see <see cref="AttachConnection"/>.</summary>
        private bool _externalConnection;

        /// <summary>
        /// True while a PTY is attached and its process has not been reported as exited. A view that has never
        /// launched, or whose process has ended, is false.
        /// </summary>
        /// <remarks>
        /// A host that shows a terminal only once there is something to show needs to ask this — the alternative
        /// is tracking it in parallel from <see cref="ProcessExited"/> and guessing at the starting state.
        /// </remarks>
        public bool IsLive
        {
            get
            {
                // Under the gate, because the two halves only mean anything together. InstallConnection
                // publishes the connection and resets the interlock as one step; reading them outside can
                // catch the new connection paired with the old flag for a moment after an attach, and report
                // a freshly attached PTY as not live.
                lock (_exitGate)
                {
                    return _ptyConnection != null && Volatile.Read(ref _processExitHandled) == 0;
                }
            }
        }

        /// <summary>
        /// Launch the terminal process with the current Process, Args, and StartingDirectory properties. If the process is already running, it will be
        /// terminated and replaced with a new instance using the updated properties. 
        /// </summary>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public async Task LaunchProcess()
        {
            CleanupProcess();
            _externalConnection = false;   // this view owns what it spawns

            try
            {
                _processCts = new CancellationTokenSource();
                // NOT reset here: the interlock is armed by InstallConnection, together with the connection
                // itself. Arming it early opens a window in which the OUTGOING connection is still the live one
                // AND the flag is clear, which is the same defect with the operands swapped.

                // Determine the process to launch based on OS if not explicitly set
                string processToLaunch = Process;
                if (string.IsNullOrEmpty(processToLaunch))
                {
                    processToLaunch = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash";
                }

                // Held in a local as well as raised, because the PTY needs a directory that is definitely not
                // null and the field is only non-null by way of what SetAndRaise just did to it.
                var workingDirectory = StartingDirectory ?? Environment.CurrentDirectory;
                SetAndRaise(CurrentDirectoryProperty, ref _currentDirectory, workingDirectory);

                var options = new PtyOptions
                {
                    Name = processToLaunch,
                    Cols = _terminal.Cols,
                    Rows = _terminal.Rows,
                    Cwd = workingDirectory,
                    App = processToLaunch,
                    VerbatimCommandLine = VerbatimCommandLine
                };

                // Merged by the PTY layer into the environment the child would otherwise inherit, so a caller
                // adding one variable does not have to rebuild the rest.
                //
                // TERM is set here because nothing else sets it. The PTY layer does not, and on Windows the
                // environment has none to inherit, so the child was being launched with TERM absent entirely
                // -- which every curses-based program then has to guess around. `ucs-detect` reported this
                // terminal as "vtwin10", which is not something the terminal said: it is blessed's Windows
                // fallback for "no TERM, assume a Win10 console", and it costs the program every capability
                // it would otherwise have used.
                //
                // xterm-256color and not xterm-kitty. TERM is a claim about the WHOLE terminal, and
                // xterm-kitty asserts the keyboard protocol, notifications, text sizing and clipboard as well
                // as the graphics. The keyboard protocol matters most: it changes how applications SEND input,
                // so claiming it without answering risks breaking key handling to win a format negotiation
                // that already falls back correctly.
                var environment = EnvironmentVariables is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(EnvironmentVariables);

                if (!environment.ContainsKey("TERM"))
                    environment["TERM"] = DefaultTermType;

                // COLORTERM alongside it, because TERM cannot carry this. A terminfo entry describes an
                // indexed palette, so xterm-256color says "256 colours" and a program quantises to them --
                // this terminal takes full RGB, and that would be discarding colour it could have shown.
                // It is the first thing consulted by everything that looks, ahead of terminfo entirely.
                if (!environment.ContainsKey("COLORTERM"))
                    environment["COLORTERM"] = DefaultColorTerm;

                options.Environment = environment;


                // Add arguments if provided
                if (Args != null && Args.Count > 0)
                {
                    options.CommandLine = Args.ToArray();
                }

                var spawned = await PtyProvider.SpawnAsync(options, _processCts.Token);
                InstallConnection(spawned);

                // Subscribe to process exit event for reliable exit detection
                spawned.ProcessExited += OnPtyProcessExited;

                // Start reading from the PTY connection, and do not continue until the loop is actually
                // reading. The loop is handed THIS connection so a relaunch cannot redirect it onto the next
                // one — see ReadPtyOutputAsync.
                //
                // The process is already running the moment SpawnAsync returns, so every instant before the
                // first read is a window in which it can write, finish, and have its output discarded. A
                // shell that exits immediately loses EVERYTHING; one that lives loses its opening prompt and
                // banner, which presents as a pane that opened blank.
                //
                // Measured downstream over the same Porta.Pty layer: starting 24 short-lived shells at once
                // lost 23 of 24 outputs entirely while reporting a clean exit 0. It never reproduced on an
                // idle developer machine and was near-total on a contended CI box.
                var readerUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Factory.StartNew(
                    () => ReadPtyOutputAsync(spawned, _processCts.Token, readerUp),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);

                // Bounded and never fatal: if the reader cannot start, the terminal behaves exactly as it
                // used to rather than hanging the caller that opened it.
                await Task.WhenAny(readerUp.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _terminal.WriteLine($"Error launching process: {ex.Message}\n");
                });
            }
        }

        /// <summary>
        /// Launch the terminal process with the specified parameters, updating the Process, Args, and StartingDirectory properties. 
        /// If the process is already running, it will be terminated and replaced with a new instance using the updated properties.
        /// </summary>
        /// <param name="startingDirectory"></param>
        /// <param name="process"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public virtual async Task LaunchProcess(string? startingDirectory, string process, params string[] args)
        {
            StartingDirectory = startingDirectory;
            Process = process;
            Args = args ?? Array.Empty<string>();
            await LaunchProcess();
        }

        /// <param name="connection">
        /// The connection this loop reads, passed in rather than re-read from the field on every
        /// iteration. A read is an await, and a relaunch during one swaps the field — so a loop that
        /// consults _ptyConnection afterwards can find itself operating on the NEXT process: waiting
        /// on it, reading its exit code, and claiming the exit interlock LaunchProcess had just
        /// reset for it, which swallows that process's own exit.
        /// </param>
        private async Task ReadPtyOutputAsync(
            IPtyConnection connection, CancellationToken cancellationToken, TaskCompletionSource? up = null)
        {
            // Raised BEFORE the first read, and deliberately not after one. Signalling after a read would
            // make readiness depend on the process PRODUCING output, so a shell that prints nothing on
            // startup would never signal and every launch would pay the full five-second wait. The guarantee
            // wanted is only that the loop is running and the next thing it does is read.
            //
            // The window this leaves — between the signal and the read — is closed by the caller subscribing
            // to ProcessExited before starting the loop, so an exit landing in it is still seen.
            up?.TrySetResult();

            try
            {
                var buffer = new byte[0x40000];

                // Local rather than a field: this method runs once per launch, so the flag is
                // per-process by construction and a chunk still in flight from a previous process
                // cannot consume the current one's signal.
                var shellReadyPosted = false;

                while (!cancellationToken.IsCancellationRequested && ReferenceEquals(_ptyConnection, connection))
                {
                    // SYNCHRONOUS, on the thread StartNew handed this loop. `await ReadAsync` undid the
                    // LongRunning hint entirely: LongRunning owns a dedicated thread only up to the first
                    // await that YIELDS, and every continuation after that is scheduled on the THREAD POOL.
                    // Worse, the stream underneath is a FileStream opened isAsync: false on Windows, whose
                    // ReadAsync performs no overlapped I/O — it parks a POOL thread in a blocking read for
                    // the whole life of the process, because ConPTY does not signal EOF while the
                    // pseudoconsole is open.
                    //
                    // Measured downstream over the same layer, 24 concurrent short-lived processes on a
                    // 4-vCPU box: time-to-first-output was 137 ms with a dedicated thread and 7546 ms
                    // pooled, and under load the pooled form lost output entirely rather than merely
                    // delaying it. A blocking read on a thread we own cannot be starved, and costs one
                    // thread per terminal — which the pooled form was already costing, minus the scheduling.
                    //
                    // Cancellation is by teardown rather than by token: disposing the connection closes the
                    // stream and the blocking read throws, which the catch below handles.
                    var bytesRead = connection.ReaderStream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        // Process has exited — fallback in case OnPtyProcessExited didn't fire first.
                        //
                        // EOF on the master side means the child closed its end, which can beat the
                        // child actually being REAPED — and until it is, ExitCode is still its
                        // default 0. Reading it straight away reports a clean exit for a process
                        // that failed, whenever this path wins the race against OnPtyProcessExited.
                        //
                        // Reaping happens BEFORE the interlock is claimed, which does two things:
                        // it makes the exit code readable, and it gives OnPtyProcessExited — which
                        // carries the code authoritatively — its chance to win the race instead of
                        // being locked out by a claim staked before we knew anything. The child is
                        // gone by definition, so this returns almost immediately; the timeout is a
                        // ceiling for a pathological reap, not an expected cost.
                        var reaped = false;
                        try { reaped = connection.WaitForExit(ExitReapGraceMs); }
                        catch { /* never let reaping be the reason output stops */ }

                        // A child that will not reap inside the grace period leaves no trustworthy
                        // code, and the one we would otherwise read is 0 — the single wrong answer
                        // that reads as SUCCESS. So it is still not reported here.
                        //
                        // It is NOT abandoned either. Leaving the interlock unclaimed means no
                        // ProcessExited is raised AT ALL if the pty layer's own event never fires
                        // — and a host that is never told the process ended cannot leave the state
                        // it entered when the process started. Trading "no wrong exit code" for
                        // "no notification" loses more than it saves; the notification is the part
                        // a host cannot reconstruct.
                        //
                        // The child is dead by definition, so the reap WILL land — the grace period
                        // is only a ceiling on how long this READ LOOP waits for it. Hand the wait
                        // off, so the loop ends now and the host still hears about it.
                        if (!reaped)
                        {
                            ReapInBackground(connection);
                        }

                        // TryClaimExit rather than a bare interlock: this loop may have been waiting on a read
                        // while a relaunch replaced the connection, in which case the exit it is holding belongs
                        // to a process this view has already moved on from.
                        if (reaped && TryClaimExit(connection))
                        {
                            var exitCode = connection.ExitCode;

                            WriteOwnLine($"\nProcess exited with code: {exitCode}\n");

                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                ProcessExited?.Invoke(this, new ProcessExitedEventArgs(exitCode));
                            });
                        }
                        break;
                    }

                    var output = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // Guarded so an unsubscribed terminal pays nothing: without it every chunk allocates a
                    // closure and queues a dispatcher callback for no subscriber. The ?.Invoke inside still
                    // covers the race where the last handler unsubscribes between here and delivery.
                    if (OutputReceived != null)
                    {
                        if (_outputOnReadTask)
                        {
                            // Straight through, on this thread. No staleness guard is needed here that the
                            // loop does not already provide: the ReferenceEquals check in the while condition
                            // means a loop reading for a replaced connection has already stopped, so there is
                            // no queued callback that could outlive its process.
                            //
                            // The catch matters MORE on this path than on the dispatcher one, and for a
                            // different reason: an escaping exception here propagates into ReadPtyOutputAsync
                            // and ends the read loop, leaving a live process with a frozen view and nothing
                            // reported.
                            try { OutputReceived?.Invoke(this, new OutputReceivedEventArgs(output)); }
                            catch { /* a sniffer must never kill the read loop */ }
                        }
                        else
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                // Same guard ShellReady uses: the callback can still be queued when a relaunch
                                // swaps the process out underneath it, and without this a consumer sees the old
                                // process's bytes attributed to the new one.
                                if (_processCts?.Token != cancellationToken)
                                    return;

                                try { OutputReceived?.Invoke(this, new OutputReceivedEventArgs(output)); }
                                catch { /* a sniffer must never take the app down */ }
                            });
                        }
                    }

                    // Snapshot before write so we can detect buffer growth (MaxScrollback
                    // increases when _terminal.Write adds lines; ScrollToBottom only moves
                    // ViewportY and does not affect buffer length).
                    var oldMax = MaxScrollback;
                    var oldY = _terminal.Buffer.ViewportY;

                    // Sampled BEFORE the write. Ordering is the subtle part: once _terminal.Write has run
                    // YBase has already advanced, so a view that genuinely WAS at the tail reads as
                    // not-following and the terminal stops following its own output.
                    //
                    // Alternate-buffer apps (vim, htop) position their own cursor and the scroll below is
                    // skipped for them regardless, so they count as following.
                    _followBottom = _isAlternateBuffer || (_autoScroll && _terminal.Buffer.IsAtBottom);

                    lock (_terminalLock)
                    {
                        _terminal.Write(output);

                        // See _inputStartRow. A change of row means the shell drew something new, so the
                        // recorded input start is stale — but where the prompt ENDS is not known until the
                        // user types, since the prompt may still be arriving.
                        int cursorRow = _terminal.Buffer.YBase + _terminal.Buffer.Y;
                        if (cursorRow != _lastOutputRow)
                        {
                            _lastOutputRow = cursorRow;
                            _inputStartPending = !_semanticPrompt;
                        }
                    }

                    // Signal on the first chunk only. Posting per chunk would keep queueing UI-thread
                    // callbacks for the life of the process, which is pure overhead once the shell is
                    // long since ready and adds up under high-throughput output.
                    if (!shellReadyPosted)
                    {
                        shellReadyPosted = true;
                        Dispatcher.UIThread.Post(() =>
                        {
                            // The callback can still be queued when a relaunch swaps the process out
                            // underneath it; the token identifies which process it belongs to.
                            if (_processCts?.Token != cancellationToken)
                                return;

                            ShellReady?.Invoke(this, EventArgs.Empty);
                        });
                    }

                    // Auto-scroll to bottom when new content arrives, but only in normal buffer.
                    // Alternate buffer (used by full-screen apps like vim, htop, asciiquarium)
                    // handles its own cursor positioning and shouldn't be scrolled.
                    if (!_isAlternateBuffer)
                    {
                        if (_followBottom)
                        {
                            _terminal.Buffer.ScrollToBottom();
                        }
                        else if (!_autoScroll && _terminal.Buffer.ViewportY != oldY)
                        {
                            // Gating ScrollToBottom is NOT enough to mean "never auto-scrolls", which is what
                            // this property advertises. The emulator advances ViewportY itself as YBase grows
                            // whenever the view is sitting at the bottom, so with the scroll merely skipped a
                            // terminal with auto-scroll off still tracked the tail exactly — measured at
                            // ViewportY == MaxScrollback after every chunk, indistinguishable from on.
                            //
                            // ScrollToBottom only ever mattered for a view that had been scrolled AWAY, which
                            // is why skipping it looks sufficient and is not. Holding the position here is
                            // what actually hands the viewport to the host.
                            _terminal.Buffer.ViewportY = Math.Min(oldY, MaxScrollback);
                        }

                        // Read and notified either way: a view parked in the scrollback still needs its
                        // scrollbar to learn that the buffer grew underneath it.
                        var newY = _terminal.Buffer.ViewportY;
                        var newMax = MaxScrollback;

                        if (oldMax != newMax || oldY != newY)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (oldMax != newMax)
                                    RaisePropertyChanged(MaxScrollbackProperty, oldMax, newMax);
                                if (oldY != newY)
                                    RaisePropertyChanged(ViewportYProperty, oldY, newY);
                            });
                        }
                    }

                    // Notify IME of cursor position change after terminal processes data
                    Dispatcher.UIThread.Post(() => _inputMethodClient?.NotifyCursorRectangleChanged());

                    // Output is the only thing that can start or stop an animation, and the clock is
                    // a dispatcher timer, so the decision has to be made on the UI thread. The check
                    // behind it is a walk of a list that is empty for a terminal showing text.
                    Dispatcher.UIThread.Post(SyncAnimationClock);

                    RequestPaint();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                // If the process has already exited the stream closing is expected — swallow silently.
                if (_processExitHandled != 0)
                    return;

                WriteOwnLine($"\nError reading from process: {ex.Message}\n");
            }
        }

        /// <summary>
        /// Make <paramref name="connection"/> the live one and arm the exit interlock for it, atomically.
        /// Null clears both — the teardown case.
        /// </summary>
        private void InstallConnection(IPtyConnection? connection)
        {
            lock (_exitGate)
            {
                _ptyConnection = connection;
                Interlocked.Exchange(ref _processExitHandled, 0);
            }
        }

        /// <summary>
        /// Claim the right to report the exit OF THIS CONNECTION. False when somebody already reported it, and
        /// false when the connection is no longer the live one — a stale loop must not speak for its successor.
        /// </summary>
        private bool TryClaimExit(IPtyConnection connection)
        {
            lock (_exitGate)
            {
                if (!ReferenceEquals(_ptyConnection, connection)) return false;
                return Interlocked.Exchange(ref _processExitHandled, 1) == 0;
            }
        }

        /// <summary>
        /// Keep waiting for a child that did not reap inside <see cref="ExitReapGraceMs"/>, off the
        /// read loop, and report the exit when it finally lands.
        /// </summary>
        /// <remarks>
        /// <para>The read loop must not block on this — it is the thing that would otherwise be
        /// pumping output — but the exit still has to be reported, or the host is left believing a
        /// dead process is running.</para>
        /// <para>Claims the same interlock, so if <see cref="OnPtyProcessExited"/> gets there first
        /// with the authoritative code, this stays silent. If the ceiling expires the exit IS still
        /// reported, with <see cref="ProcessExitedEventArgs.ExitCodeKnown"/> false — "ended, outcome
        /// unreadable" is honest, whereas 0 would read as success and silence reads as running.</para>
        /// <para>The connection may be disposed underneath this at any point (a relaunch, a close).
        /// That is not an error worth surfacing: it means the exit is moot.</para>
        /// </remarks>
        private void ReapInBackground(IPtyConnection connection)
        {
            _ = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(ExitReapCeilingMs);
                var reaped = false;

                while (DateTime.UtcNow < deadline)
                {
                    if (Volatile.Read(ref _processExitHandled) != 0) return;   // someone else reported it
                    try
                    {
                        if (connection.WaitForExit(ExitReapPollMs)) { reaped = true; break; }
                    }
                    catch
                    {
                        return;   // disposed / gone — nothing left to report about
                    }
                    await Task.Yield();
                }

                if (!TryClaimExit(connection)) return;

                int? code = null;
                if (reaped)
                {
                    try { code = connection.ExitCode; } catch { /* fall through as unknown */ }
                }

                WriteOwnLine(code is { } c
                    ? $"\nProcess exited with code: {c}\n"
                    : "\nProcess exited\n");

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ProcessExited?.Invoke(this, code is { } c
                        ? new ProcessExitedEventArgs(c)
                        : ProcessExitedEventArgs.UnknownCode());
                });
            });
        }

        private void OnPtyProcessExited(object? sender, PtyExitedEventArgs e)
        {
            // Interlocked ensures only one of (event, EOF path, exception path) prints the message.
            // Claims for the connection that raised it, so a late event from a replaced connection cannot
            // speak for its successor either. A null sender predates this and is treated as the live one.
            if (sender is IPtyConnection origin ? !TryClaimExit(origin)
                                                : Interlocked.Exchange(ref _processExitHandled, 1) != 0)
                return;

            WriteOwnLine($"\nProcess exited with code: {e.ExitCode}\n");

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Raise event on UI thread so subscribers can safely update UI
                var args = new ProcessExitedEventArgs(e.ExitCode);
                ProcessExited?.Invoke(this, args);
            });
        }

        private void CleanupProcess()
        {
            _processCts?.Cancel();
            ReleaseImageBitmaps();

            if (_ptyConnection != null)
            {
                try
                {
                    // Unsubscribe from event before cleanup
                    _ptyConnection.ProcessExited -= OnPtyProcessExited;

                    // An ATTACHED connection belongs to its owner: neither killed NOR disposed. Closing or
                    // re-parenting a view must not stop the process behind it, and Dispose does stop it —
                    // disposing without any Kill() leaves the child dead within 300ms on both Windows
                    // (PseudoConsoleConnection) and Unix, where closing the master fd sends SIGHUP to the
                    // foreground process group. An earlier revision of this code disposed unconditionally and
                    // described it as the detach; it was the opposite.
                    //
                    // Detaching needs nothing from Dispose. The unsubscribe above drops this view's event, and
                    // the cancelled _processCts plus the read loop's ReferenceEquals check stop the reader.
                    // Disposing an object the view does not own would be wrong even if the process survived it.
                    if (!_externalConnection)
                    {
                        _ptyConnection.Kill();
                        _ptyConnection.Dispose();
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
                finally
                {
                    // Cleared with the flag, under the gate: a loop still unwinding must not find a null
                    // connection paired with a clear flag and conclude it owns the exit.
                    InstallConnection(null);
                }
            }

            _processCts?.Dispose();
            _processCts = null;
        }

        /// <summary>
        /// One cell's width at the current font — text is drawn at <c>col * CharWidth</c>. A host overlay
        /// can size its stand-in caret from this so waking the session shifts nothing.
        /// </summary>
        public double CharWidth
        {
            get { if (_charWidth <= 0) UpdateTextMetrics(); return _charWidth; }
        }

        /// <summary>
        /// One row's height at the current font — row N's text top-left is <c>(0, N * CharHeight)</c>,
        /// so a stand-in prompt lands on the live first row by placing it at the view's own origin.
        /// </summary>
        public double CharHeight
        {
            get { if (_charHeight <= 0) UpdateTextMetrics(); return _charHeight; }
        }

        private void UpdateTextMetrics()
        {
            var typeface = new Typeface(FontFamily, FontStyle, FontWeight);
            _measureText = new FormattedText(
                "W",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                Brushes.Black);

            _charWidth = _measureText.Width;
            _charHeight = _measureText.Height;

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

            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            _terminal.Options.CellWidthPixels = Math.Max(1, (int)Math.Round(_charWidth * scale));
            _terminal.Options.CellHeightPixels = Math.Max(1, (int)Math.Round(_charHeight * scale));
        }

        /// <summary>
        /// Force a correct full re-render. Upstream's first paint is focus-gated (the blink/redraw loop only runs
        /// when focused) and frame-throttled, so a freshly-launched or just-shown terminal can stay blank until
        /// clicked. This re-applies font metrics, re-grids to the current size, drops the per-line render caches,
        /// and invalidates immediately (bypassing <see cref="TerminalRenderThrottle"/>). Safe to call any time
        /// (no-op-ish before the terminal is initialised).
        /// </summary>
        public void Refresh()
        {
            if (_terminal == null)
                return;

            UpdateTextMetrics();

            // Drop cached text runs so each line rebuilds at the current metrics/size.
            for (int y = 0; y < _terminal.Buffer.Length; y++)
            {
                var line = _terminal.Buffer.GetLine(y);
                if (line != null)
                    line.Cache = null;
            }

            // Re-run layout (ArrangeOverride re-grids the terminal + PTY for the current size), then paint now.
            InvalidateMeasure();
            InvalidateArrange();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            UpdateTextMetrics();

            return availableSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // Calculate how many columns fit in the allocated width
            if (_charWidth > 0)
            {
                int newCols = Math.Max(1, (int)(finalSize.Width / _charWidth));
                int newRows = Math.Max(1, (int)(finalSize.Height / _charHeight));

                // Only resize if dimensions have changed
                if (newCols != _terminal.Cols || newRows != _terminal.Rows)
                {
                    _terminal.Resize(newCols, newRows);

                    // Also resize the PTY connection if it exists
                    _ptyConnection?.Resize(newCols, newRows);

                    RaisePropertyChanged(ViewportLinesProperty, default(int), ViewportLines);
                }
            }

            return finalSize;
        }


        public override void Render(DrawingContext context)
        {
            // The terminal's own background, painted once for the whole surface.
            //
            // Nothing else paints it. TerminalView is a plain Control, so Avalonia has no Background of its
            // own to draw, and the control template is a bare Grid with no Border in it. The only thing that
            // ever filled the surface was the per-cell fill — which no longer runs for a cell using the
            // default background, so Background became a property that was read and then thrown away, and
            // setting it did nothing at all.
            //
            // Painting it here rather than per cell keeps what that change was after: a host layering the
            // terminal over its own surface sets a Background with alpha and still sees through.
            //
            // ONE snapshot for the whole frame. Every colour on screen resolves against it, so the frame is
            // painted from a set of colours that belong to each other even if a program changes the palette
            // while it is being drawn.
            _palette = _terminal.Colors.Take();

            var surface = GetValue(BackgroundProperty);
            if (surface is ISolidColorBrush opaque && opaque.Color.A == 255)
            {
                // The terminal's default background is the emulator's, not this brush — they agree until a
                // program moves it with OSC 11, and then the program is the one that should win. A brush
                // carrying alpha is left alone: that is a host asking to be seen through, which no RGB
                // palette entry can express.
                surface = new SolidColorBrush(BufferCellExtensions.FromRgb(_palette.Background));
            }

            if (surface is not null)
                context.FillRectangle(surface, new Rect(Bounds.Size));

            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            //Debug.WriteLine("======");
            //Debug.WriteLine(_terminal.Buffer.PrintViewport());

            // Use the terminal buffer's ViewportY to determine what to render
            int viewportY = _terminal.Buffer.ViewportY;
            int viewportLines = _terminal.Rows;
            int startLine = viewportY;
            int endLine = Math.Min(_terminal.Buffer.Length, startLine + viewportLines);
            try
            {

                for (int y = startLine; y < endLine; y++)
                {
                    // The buffer can SHRINK underneath a render. CSI 3 J — what cmd.exe's `cls` sends —
                    // discards the entire scrollback, and it arrives on the PTY thread, so the bounds
                    // captured above can point past the end by the time we reach this line.
                    //
                    // This could not happen before: the buffer only ever grew, or dropped a single line at
                    // a time once it hit capacity, so a stale index stayed valid. A wholesale discard can
                    // remove hundreds at once. Without this check GetLine throws IndexOutOfRangeException,
                    // the catch below swallows it, and the REST OF THE FRAME is lost — plus anyone running
                    // under a debugger gets a first-chance break every time they clear the screen.
                    //
                    // Breaking out costs at most one dropped frame: the write that trimmed the buffer
                    // requests another render, and that one sees consistent bounds.
                    if (y >= _terminal.Buffer.Length)
                        break;

                    var line = _terminal.Buffer.GetLine(y);
                    if (line == null)
                        continue;

                    int screenY = y - startLine;

                    // Calculate Y positions for this screen row
                    var startYPos = Snap(screenY * _charHeight, scale);
                    var endYPos = Snap((screenY + 1) * _charHeight, scale);
                    var rowHeight = Math.Max(0, endYPos - startYPos);

                    // Check for double-width/double-height line attributes
                    var lineAttr = line.LineAttribute;
                    if (lineAttr == LineAttribute.DoubleWidth ||
                             lineAttr == LineAttribute.DoubleHeightTop ||
                             lineAttr == LineAttribute.DoubleHeightBottom)
                    {
                        RenderDoubleWidthLine(context, line, screenY, startYPos, rowHeight, lineAttr, scale);
                    }
                    else
                    {
                        RenderNormalLine(context, line, screenY, startYPos, rowHeight, scale);
                    }
                }

                // Render URL underline when hovering
                RenderHoveredUrl(context, viewportY, scale);

                // Render selection overlay
                RenderSelection(context, viewportY, scale);

                RenderCursor(context, viewportY, scale);

                // Render IME preedit (composition) text overlay
                RenderPreeditText(context, viewportY, scale);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TerminalView] Render error: {ex.Message}");
            }
        }

        /// <summary>
        /// Renders a normal (single-width, single-height) line.
        /// </summary>
        private void RenderNormalLine(DrawingContext context, BufferLine line, int screenY, double startYPos, double rowHeight, double scale)
        {
            // Try to use cached text runs for this line (but not when ReverseVideo mode is active as it affects all cells)
            var textRuns = !_terminal.ReverseVideo ? line.Cache as List<CachedTextRun> : null;
            if (textRuns != null)
            {
                foreach (var run in textRuns)
                {
                    // Recalculate position based on current screen row
                    var startX = Snap(run.StartX * _charWidth, scale);
                    var endX = Snap((run.StartX + run.CellCount) * _charWidth, scale);
                    var rect = new Rect(startX, startYPos, Math.Max(0, endX - startX), rowHeight);
                    var position = new Point(startX, startYPos);

                    // The background goes down first either way: a Sixel drawn with background select 1 leaves
                    // its unset pixels transparent, and the cell's own background is meant to show through them.
                    if (run.Background is not null)
                        context.FillRectangle(run.Background, rect);

                    if (run.IsImage)
                        DrawImageRun(context, run, startYPos, rowHeight, scale);
                    else if (run.Text is not null)
                        context.DrawText(run.Text, position);
                }
                return;
            }

            // Build and cache text runs for this line
            textRuns = new List<CachedTextRun>();

            // The line's own runs, back to front. Every picture on the line is already one run per
            // line, so there is nothing to collect and nothing to coalesce -- the emulator's storage
            // is the draw list, and each run is a single blit.
            //
            // Runs are drawn in the order they are added, so appending the ones behind the text,
            // then the text, then the ones in front is what makes the layers composite: a
            // translucent picture blends over whatever was drawn under it.
            var placements = OrderedPlacements(line);
            var nextPlacement = 0;
            var painted = new List<XT.Graphics.LinePlacement>();

            for (; nextPlacement < placements.Count && placements[nextPlacement].ZIndex < 0; nextPlacement++)
            {
                AppendImageRun(context, line, placements[nextPlacement], startYPos, rowHeight, scale,
                               textRuns, painted);
                painted.Add(placements[nextPlacement]);
            }

            for (int x = 0; x < _terminal.Cols;)
            {
                if (x >= line.Length)
                    break;
                var cell = line[x];
                string text = String.Empty;
                int cellCount = 0;
                int runStartX = 0;

                // Nothing is drawn where a Sixel covers, because a Sixel REPLACED what was there.
                if (CoveredBySixel(line, x))
                {
                    x++;
                    continue;
                }

                // Skip width-0 cells. There are TWO kinds, and only one of them is a placeholder.
                //
                // The placeholder behind a wide glyph carries no content, and skipping it is what stops the
                // glyph being drawn twice. The other kind is a combining character that had nothing in front
                // of it to combine with — a line beginning with U+0301, a stray variation selector, a keycap
                // with no digit — which the emulator stores in a cell of its own after
                // TryAppendToPreviousCell finds no base. That one DOES carry content, and skipping it is
                // also right: a combining mark with nothing to combine with has nothing to draw.
                //
                // This used to assert the content was empty, on the assumption that a placeholder was the
                // only way to reach here. It is not, so the assert fired on ordinary output — printing a
                // lone combining acute is enough — and cost a debugging session before anyone questioned
                // the premise rather than the buffer.
                if (cell.Width == 0)
                {
                    x++;
                    continue;
                }
                else if (cell.Width == 1)
                {
                    // Collect consecutive cells with same attributes
                    var textBuilder = new StringBuilder();
                    cellCount = 0;  // Total cell positions consumed (including wide char placeholders)
                    runStartX = x;
                    while (x < line.Length && x < _terminal.Cols)
                    {
                        var currentCell = line[x];

                        // Stop if we hit a different attribute or a placeholder cell mid-run.
                        //
                        // A KITTY picture is no reason to stop: it is an overlay, the cell under it
                        // still carries whatever was printed there, and the z-index decides which of
                        // them a viewer sees. A SIXEL is not -- see CoveredBySixel.
                        if (currentCell.Width != 1 || currentCell.Attributes != cell.Attributes
                            || CoveredBySixel(line, x))
                            break;
                        textBuilder.Append(currentCell.Content);
                        cellCount += currentCell.Width;

                        // Skip the placeholder cell that follows a wide character
                        x += currentCell.Width;
                    }
                    text = textBuilder.ToString();
                }
                else if (cell.Width == 2)
                {
                    text = cell.Content;
                    cellCount = cell.Width;
                    runStartX = x;
                    x += cell.Width;  // Move past wide character and its placeholder
                }

                // A ZWJ sequence spans several cells with the joiner tacked onto all but the last, so the run
                // collected above can be only the first component of one glyph. Pull in the rest before
                // shaping — otherwise HarfBuzz never sees the cluster and a family emoji draws as separate
                // people. Applies to both branches: ❤️‍🔥 starts in a width-1 cell and continues into a wide one.
                text = GraphemeRuns.AbsorbJoinedCells(line, _terminal.Cols, cell, text, ref x, ref cellCount);

                var startX = Snap(runStartX * _charWidth, scale);
                var endX = Snap((runStartX + cellCount) * _charWidth, scale);
                var rect = new Rect(startX, startYPos, Math.Max(0, endX - startX), rowHeight);
                var background = cell.GetBackgroundBrush(_palette, this.Background);
                var foreground = cell.GetForegroundBrush(_palette, this.Foreground);
                // Apply cell-level inverse attribute
                // Whether this run ends up drawn with the colours swapped. Once they are, the fill is no
                // longer optional: the "background" being painted is the text colour.
                bool swapped = false;
                if (cell.Attributes.IsInverse())
                    (foreground, background, swapped) = (background, foreground, !swapped);
                // Apply terminal-wide reverse video mode (DECSCNM)
                if (_terminal.ReverseVideo)
                    (foreground, background, swapped) = (background, foreground, !swapped);
                if (cell.Attributes.IsBlink() && this._cursorBlinkOn)
                    (foreground, background, swapped) = (background, foreground, !swapped);

                var typeface = new Typeface(FontFamily, cell.GetFontStyle(), cell.GetFontWeight());
                var formattedText = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground);
                var td = cell.GetTextDecorations();
                if (td != null)
                    formattedText.SetTextDecorations(td);

                var position = new Point(startX, startYPos);
                // A cell that carries no background of its own and was not swapped paints nothing, leaving
                // whatever the view is layered over to show through.
                var fill = swapped || cell.GetBackgroundColor(_palette).HasValue ? background : null;
                // Cache only content-dependent data, not screen position
                textRuns.Add(new CachedTextRun(formattedText, runStartX, cellCount, fill));

                if (fill is not null)
                    context.FillRectangle(fill, rect);
                context.DrawText(formattedText, position);
            }

            // And the pictures that cover the text, still back to front, now that it is down.
            for (; nextPlacement < placements.Count; nextPlacement++)
            {
                AppendImageRun(context, line, placements[nextPlacement], startYPos, rowHeight, scale,
                               textRuns, painted);
                painted.Add(placements[nextPlacement]);
            }

            // Cache the text runs (but not when ReverseVideo mode is active)
            if (!_terminal.ReverseVideo)
                line.Cache = textRuns;
        }

        /// <summary>
        /// A line's picture runs, ordered back to front.
        /// </summary>
        /// <remarks>
        /// <para>Z-index first, then age, age being the order the emulator added the runs to the
        /// line. <c>OrderBy</c> is stable, so sorting on z alone leaves equal depths in the order
        /// they arrived — which is Kitty's rule that the placement made later is drawn on top.</para>
        /// <para>Nothing is collected or coalesced: a picture is already one run per line, so the
        /// emulator's storage IS the draw list and each run is a single blit. The list is copied
        /// only because it has to be sorted, and only on a line that has a picture on it.</para>
        /// </remarks>
        private static List<XT.Graphics.LinePlacement> OrderedPlacements(BufferLine line)
        {
            if (!line.HasImages)
                return EmptyPlacements;

            var placements = line.Placements;
            if (placements.Count == 1)
                return new List<XT.Graphics.LinePlacement>(placements);

            return placements.OrderBy(p => p.ZIndex).ToList();
        }

        private static readonly List<XT.Graphics.LinePlacement> EmptyPlacements = new();

        /// <summary>
        /// The image a run shows, found among the ones its line holds.
        /// </summary>
        /// <remarks>
        /// A run names its picture by id rather than holding it, because the line owns the pixels and
        /// its death is what releases them. A line holds one or two images, so this is a scan of a
        /// list that is almost always length one.
        /// </remarks>
        private static XT.Graphics.TerminalImage? ImageFor(BufferLine line, XT.Graphics.LinePlacement placement)
        {
            foreach (var image in line.Images)
            {
                if (image.Id == placement.ImageId)
                    return image;
            }

            return null;
        }

        /// <summary>
        /// Draws one picture run and adds it to the row's cache.
        /// </summary>
        /// <remarks>
        /// One blit per run, which is one per row of a picture. There is no coalescing to do and no
        /// tile arithmetic left: the run already carries the source rectangle and the columns it
        /// covers, so the whole strip goes down in a single call.
        /// </remarks>
        private void AppendImageRun(DrawingContext context, BufferLine line,
                                    XT.Graphics.LinePlacement placement,
                                    double startYPos, double rowHeight, double scale,
                                    List<CachedTextRun> textRuns,
                                    List<XT.Graphics.LinePlacement> alreadyPainted)
        {
            var image = ImageFor(line, placement);
            if (image is null)
                return;

            // Cols is the picture's NATURAL width and is deliberately not clipped by the emulator, so
            // the clipping happens here: a narrow window shows less of the picture and a wider one
            // shows more, without anything having been destroyed in between.
            var start = Math.Max(0, placement.Column);
            var end = Math.Min(placement.EndColumn, Math.Min(line.Length, _terminal.Cols));
            var cellCount = end - start;
            if (cellCount <= 0)
                return;

            // The cell's own background goes under the picture, which is what a Sixel drawn with
            // background select 1 needs: its unset pixels are transparent and the cell colour is
            // meant to show through them.
            //
            // Only where nothing has painted it already. Runs are drawn back to front, so a nearer
            // picture repainting the background would erase the one behind it rather than blend over
            // it -- which is the whole of what overlapping placements are for.
            var first = line[start];
            var background = first.GetBackgroundBrush(_palette, this.Background);
            var fill = first.GetBackgroundColor(_palette).HasValue
                       && !OverlapsAny(alreadyPainted, start, end)
                       ? background
                       : null;

            var run = new CachedTextRun(null, start, cellCount, fill, placement, image);
            textRuns.Add(run);

            if (fill is not null)
            {
                var fillStart = Snap(start * _charWidth, scale);
                var fillEnd = Snap(end * _charWidth, scale);
                context.FillRectangle(fill, new Rect(fillStart, startYPos,
                                                     Math.Max(0, fillEnd - fillStart), rowHeight));
            }

            DrawImageRun(context, run, startYPos, rowHeight, scale);
        }

        /// <summary>
        /// Blits one strip of a picture into the cells it belongs to.
        /// </summary>
        /// <remarks>
        /// The destination is derived from the cell grid rather than from the image's own pixel size, so a
        /// picture stays locked to the text it was placed among even after a font or DPI change has moved the
        /// grid out from under it. Tiles on the right and bottom edges cover only part of a cell, so the
        /// destination is scaled by how much of one the source actually holds -- stretching a half-tile over a
        /// whole cell is the difference between a picture and a smeared one.
        /// </remarks>
        private void DrawImageRun(DrawingContext context, CachedTextRun run,
                                  double startYPos, double rowHeight, double scale)
        {
            if (_imageRenderingUnavailable)
                return;

            if (!TryPlanImageBlit(run, startYPos, rowHeight, _charWidth, _charHeight, scale,
                                  out var source, out var destination))
                return;

            var bitmap = GetOrCreateBitmap(run.Image!);
            if (bitmap is null)
                return;

            try
            {
                context.DrawImage(bitmap, source, destination);
            }
            catch (Exception ex) when (IndicatesNoRasterBackend(ex))
            {
                // The backend cannot draw a bitmap at all -- Consolonia runs this same control over text
                // cells, and the headless platform's recording context is the same. That will not change
                // on the next frame, so stop trying rather than throwing out of Render thirty times a
                // second, and let the text carry on drawing.
                _imageRenderingUnavailable = true;
                Debug.WriteLine($"[TerminalView] image rendering unavailable: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Anything else is about THIS picture rather than the platform: a bitmap that will not
                // blit, a frame that ran out of memory. Remember the failure against the image so only
                // that one is skipped. Latching here instead would let a single bad picture turn every
                // picture off for the life of the control, and hide whatever caused it.
                if (_imageBitmaps.TryGetValue(run.Image!, out var cached))
                {
                    try { cached.Bitmap?.Dispose(); } catch { /* already gone; nothing to salvage */ }
                    cached.Bitmap = null;
                }

                Debug.WriteLine($"[TerminalView] could not draw image {run.Image!.Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether an exception from <c>DrawImage</c> means the platform has no raster surface at all,
        /// as opposed to something being wrong with one picture.
        /// </summary>
        /// <remarks>
        /// The distinction decides whether images are abandoned for the life of the control or only for
        /// the image that failed, so it is kept as a named predicate rather than an inline type test --
        /// it is a policy, and it is worth being able to assert on it directly.
        /// </remarks>
        internal static bool IndicatesNoRasterBackend(Exception exception)
            => exception is NotImplementedException or PlatformNotSupportedException or NotSupportedException;

        /// <summary>
        /// Whether any run drawn earlier on this line covers part of this one's span.
        /// </summary>
        /// <remarks>
        /// <para>What decides whether a run paints the cell background under itself. Runs go down
        /// back to front, so a nearer picture repainting the background would erase the one behind
        /// it rather than blend over it — which is the whole of what overlapping placements buy.
        /// </para>
        /// <para>The whole span rather than the columns actually uncovered, because the run's fill
        /// is what its CACHED form replays: a rectangle over the run. A partial overlap therefore
        /// costs the upper run's spare columns their background, which errs toward leaving a picture
        /// alone rather than painting over one.</para>
        /// </remarks>
        /// <summary>
        /// Whether a Sixel covers this column, and so has replaced whatever text was under it.
        /// </summary>
        /// <remarks>
        /// <para>The one place the two protocols have to be told apart. A Kitty placement is an
        /// OVERLAY: the cell keeps its character, both are drawn, and the z-index decides which one
        /// is seen. A Sixel is CONTENT: it replaced what was there, which is why the emulator splits
        /// a Sixel run when something prints over it and leaves a Kitty run alone.</para>
        /// <para>The emulator does not clear the cells a Sixel covers -- placing one only adds a run
        /// -- so they keep whatever was on screen beforehand. Drawing them puts that text under the
        /// picture: invisible beneath an opaque one, and showing through a Sixel drawn with
        /// background select 1, whose unset pixels are transparent so that the cell's own colour
        /// comes through. The cell's colour, not the previous screen's text.</para>
        /// </remarks>
        private static bool CoveredBySixel(BufferLine line, int column)
        {
            if (!line.HasImages)
                return false;

            foreach (var placement in line.Placements)
            {
                if (placement.Kind == XT.Graphics.PlacementKind.Sixel && placement.Covers(column))
                    return true;
            }

            return false;
        }

        private static bool OverlapsAny(List<XT.Graphics.LinePlacement> earlier, int start, int end)
        {
            foreach (var placement in earlier)
            {
                if (placement.Column < end && placement.EndColumn > start)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Works out which pixels of a picture a run shows and where on screen they go.
        /// </summary>
        /// <remarks>
        /// <para>Separated from the drawing so the arithmetic can be asserted directly. It is the part with
        /// something to get wrong, and it cannot be observed through a rendered frame: the headless platform's
        /// recording context throws from DrawImage.</para>
        /// <para>The destination comes off the cell grid rather than the picture's own pixel size, so an
        /// image stays locked to the text it was placed among after a font or DPI change has moved the
        /// grid.</para>
        /// <para>A run's <c>Cols</c> is its natural width and the caller has already clipped the columns to
        /// what the line can show, so the SOURCE has to be narrowed by the same proportion — otherwise a
        /// narrow window would squeeze the whole picture into fewer cells instead of showing less of it.</para>
        /// </remarks>
        internal static bool TryPlanImageBlit(CachedTextRun run, double startYPos, double rowHeight,
                                              double charWidth, double charHeight, double scale,
                                              out Rect source, out Rect destination)
        {
            source = default;
            destination = default;

            if (run.Placement is not { } placement || run.Image is null)
                return false;
            if (run.CellCount <= 0 || charWidth <= 0 || charHeight <= 0)
                return false;
            if (placement.Cols <= 0 || placement.SrcWidth <= 0 || placement.SrcHeight <= 0)
                return false;

            // How much of the run's natural width is actually being drawn, and the slice of the
            // source that corresponds to it.
            var shown = Math.Min(run.CellCount, placement.Cols);
            var sourceWidth = (double)placement.SrcWidth * shown / placement.Cols;
            if (sourceWidth <= 0)
                return false;

            // The offsets shift the picture inside its first cell without enlarging the box, so what
            // overflows the last cell is clipped. They are in image pixels, and the cell they shift
            // within is a screen cell, so they cross over as a fraction of one.
            var cell = run.Image.CellWidth > 0 ? run.Image.CellWidth : 1;
            var cellHigh = run.Image.CellHeight > 0 ? run.Image.CellHeight : 1;
            var offsetX = placement.OffsetX / (double)cell * charWidth;
            var offsetY = placement.OffsetY / (double)cellHigh * charHeight;

            var startX = Snap(run.StartX * charWidth + offsetX, scale);
            var endX = Snap((run.StartX + shown) * charWidth + offsetX, scale);
            var topY = offsetY > 0 ? Snap(startYPos + offsetY, scale) : startYPos;
            var endY = Snap(startYPos + offsetY + rowHeight, scale);

            destination = new Rect(startX, topY, Math.Max(0, endX - startX), Math.Max(0, endY - topY));
            if (destination.Width <= 0 || destination.Height <= 0)
            {
                destination = default;
                return false;
            }

            source = new Rect(placement.SrcX, placement.SrcY, sourceWidth, placement.SrcHeight);
            return true;
        }

        /// <summary>
        /// Gets the bitmap for a picture, uploading its pixels the first time it is seen.
        /// </summary>
        /// <remarks>
        /// Internal rather than private so the cache rule can be asserted directly. It cannot be
        /// observed through a rendered frame -- the headless platform's recording context throws
        /// from DrawImage -- and "re-uploads when the frame changes, and only then" is exactly the
        /// kind of rule that silently stops holding.
        /// </remarks>
        internal Bitmap? GetOrCreateBitmap(XT.Graphics.TerminalImage image)
        {
            // A cached null is a remembered failure — worth keeping, so a picture that cannot be uploaded is not
            // retried thirty times a second.
            if (_imageBitmaps.TryGetValue(image, out var existing))
            {
                // An animated picture changes under a cache keyed on the image. The emulator bumps a
                // serial whenever the visible pixels move, so comparing that is enough to spot a
                // stale upload without comparing the pixels themselves. A still picture never moves,
                // and its serial stays zero, so this costs it an integer comparison per frame.
                if (existing.FrameSerial == image.FrameSerial)
                    return existing.Bitmap;

                try { existing.Bitmap?.Dispose(); } catch { /* already gone; nothing to salvage */ }

                existing.Bitmap = TryCreateBitmap(image);
                existing.FrameSerial = image.FrameSerial;
                return existing.Bitmap;
            }

            var bitmap = TryCreateBitmap(image);
            _imageBitmaps.Add(image, new CachedBitmap { Bitmap = bitmap, FrameSerial = image.FrameSerial });
            return bitmap;
        }

        /// <summary>Uploads a picture's current pixels, or remembers that it cannot be done.</summary>
        private static Bitmap? TryCreateBitmap(XT.Graphics.TerminalImage image)
        {
            try
            {
                return CreateBitmap(image);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TerminalView] could not upload image {image.Id}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Disposes every cached bitmap and empties the cache.
        /// </summary>
        /// <remarks>
        /// The weak table drops its entries by itself once the emulator lets go of the pictures, but that only
        /// makes the bitmaps collectable -- it does not free what they hold on the GPU until a finaliser runs.
        /// A terminal being torn down knows it is finished with all of them, and a program animating with Sixel
        /// produces one per frame, so it is worth saying so rather than waiting.
        /// </remarks>
        private void ReleaseImageBitmaps()
        {
            foreach (var entry in _imageBitmaps)
            {
                if (entry.Value is not { } cached)
                    continue;

                cached.Bitmap?.Dispose();
                cached.Bitmap = null;
            }

            _imageBitmaps.Clear();
        }

        /// <summary>
        /// Uploads a decoded picture's pixels into a bitmap.
        /// </summary>
        /// <remarks>
        /// Separated from the caching so the upload itself can be asserted: the byte order and the stride
        /// handling are the two things here that fail silently, as a picture with its colours swapped or its
        /// rows sheared rather than as an error.
        /// </remarks>
        internal static WriteableBitmap CreateBitmap(XT.Graphics.TerminalImage image)
        {
            var writeable = new WriteableBitmap(
                new PixelSize(image.PixelWidth, image.PixelHeight),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            using (var locked = writeable.Lock())
            {
                CopyPixels(image, locked.Address, locked.RowBytes);
            }

            return writeable;
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

        private void RenderHoveredUrl(DrawingContext context, int viewportY, double scale)
        {
            var link = _hoveredLink;
            if (link == null) return;

            Pen? pen = null;
            foreach (var segment in link.Segments)
            {
                int screenRow = segment.Line - viewportY;
                if (screenRow < 0 || screenRow >= _terminal.Rows) continue;

                var startX = Snap(segment.StartCol * _charWidth, scale);
                var endX = Snap((segment.EndCol + 1) * _charWidth, scale);
                var y = Snap((screenRow + 1) * _charHeight - 1, scale);

                pen ??= new Pen(Foreground, 1);
                context.DrawLine(pen, new Point(startX, y), new Point(endX, y));
            }
        }

        /// <summary>
        /// Renders a double-width or double-height line using transforms and clipping.
        /// </summary>
        private void RenderDoubleWidthLine(DrawingContext context, BufferLine line, int screenY, double startYPos, double rowHeight, LineAttribute lineAttr, double scale)
        {
            // Don't cache double-width lines (transform makes caching complex)
            line.Cache = null;

            // Calculate the clip rect for this row
            var clipRect = new Rect(0, startYPos, _terminal.Cols * _charWidth, rowHeight);

            // For double-height lines, we need to clip to show only top or bottom half
            double scaleX = 2.0;
            double scaleY = lineAttr.IsDoubleHeight() ? 2.0 : 1.0;

            // Calculate transform origin and translation
            // We scale from origin (0, startYPos) and then may need to shift for bottom half
            double translateY = 0;
            if (lineAttr == LineAttribute.DoubleHeightBottom)
            {
                // For bottom half, we render at 2x scale but shift up by one row height
                // so the bottom half of the scaled text is visible
                translateY = -rowHeight;
            }

            using (context.PushClip(clipRect))
            {
                // Create transform: scale 2x horizontally (and 2x vertically for double-height)
                // The transform origin is at (0, startYPos)
                var scaleTransform = Matrix.CreateScale(scaleX, scaleY);
                var translateToOrigin = Matrix.CreateTranslation(0, -startYPos);
                var translateBack = Matrix.CreateTranslation(0, startYPos + translateY);
                var combinedTransform = translateToOrigin * scaleTransform * translateBack;

                using (context.PushTransform(combinedTransform))
                {
                    // Render the line content at normal size - the transform will scale it
                    // Only render the first half of the columns since they'll be doubled
                    int effectiveCols = _terminal.Cols / 2;

                    for (int x = 0; x < effectiveCols && x < line.Length;)
                    {
                        var cell = line[x];
                        string text = String.Empty;
                        int cellCount = 0;
                        int runStartX = 0;

                        // Skip placeholder cells (width 0) that follow wide characters
                        if (cell.Width == 0)
                        {
                            x++;
                            continue;
                        }
                        else if (cell.Width == 1)
                        {
                            // Collect consecutive cells with same attributes
                            var textBuilder = new StringBuilder();
                            cellCount = 0;
                            runStartX = x;
                            while (x < line.Length && x < effectiveCols)
                            {
                                var currentCell = line[x];
                                if (currentCell.Width != 1 || currentCell.Attributes != cell.Attributes)
                                    break;
                                textBuilder.Append(currentCell.Content);
                                cellCount += currentCell.Width;
                                x += currentCell.Width;
                            }
                            text = textBuilder.ToString();
                        }
                        else if (cell.Width == 2)
                        {
                            text = cell.Content;
                            cellCount = cell.Width;
                            runStartX = x;
                            x += cell.Width;
                        }

                        var startX = Snap(runStartX * _charWidth, scale);
                        var endX = Snap((runStartX + cellCount) * _charWidth, scale);
                        var rect = new Rect(startX, startYPos, Math.Max(0, endX - startX), rowHeight);
                        var background = cell.GetBackgroundBrush(_palette, this.Background);
                        var foreground = cell.GetForegroundBrush(_palette, this.Foreground);
                        // Apply cell-level inverse attribute
                        bool swapped = false;
                        if (cell.Attributes.IsInverse())
                            (foreground, background, swapped) = (background, foreground, !swapped);
                        // Apply terminal-wide reverse video mode (DECSCNM)
                        if (_terminal.ReverseVideo)
                            (foreground, background, swapped) = (background, foreground, !swapped);
                        if (cell.Attributes.IsBlink() && this._cursorBlinkOn)
                            (foreground, background, swapped) = (background, foreground, !swapped);

                        var typeface = new Typeface(FontFamily, cell.GetFontStyle(), cell.GetFontWeight());
                        var formattedText = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground);
                        var td = cell.GetTextDecorations();
                        if (td != null)
                            formattedText.SetTextDecorations(td);

                        var position = new Point(startX, startYPos);

                        if (swapped || cell.GetBackgroundColor(_palette).HasValue)
                            context.FillRectangle(background, rect);
                        context.DrawText(formattedText, position);
                    }
                }
            }
        }

        /// <summary>
        /// Renders the selection overlay.
        /// </summary>
        private void RenderSelection(DrawingContext context, int viewportY, double scale)
        {
            if (!_terminal.Selection.HasSelection)
                return;

            int viewportLines = _terminal.Rows;

            for (int screenY = 0; screenY < viewportLines; screenY++)
            {
                // Find cells that are selected in this row
                int? selectionStartX = null;
                int? selectionEndX = null;

                for (int x = 0; x < _terminal.Cols; x++)
                {
                    if (_terminal.Selection.IsCellSelected(x, screenY))
                    {
                        if (!selectionStartX.HasValue)
                            selectionStartX = x;
                        selectionEndX = x;
                    }
                    else if (selectionStartX.HasValue)
                    {
                        // End of a selection run - draw it
                        DrawSelectionRect(context, selectionStartX.Value, selectionEndX!.Value + 1, screenY, scale);
                        selectionStartX = null;
                        selectionEndX = null;
                    }
                }

                // Draw remaining selection at end of row
                if (selectionStartX.HasValue)
                {
                    DrawSelectionRect(context, selectionStartX.Value, selectionEndX!.Value + 1, screenY, scale);
                }
            }
        }

        private void DrawSelectionRect(DrawingContext context, int startX, int endX, int screenY, double scale)
        {
            var x1 = Snap(startX * _charWidth, scale);
            var x2 = Snap(endX * _charWidth, scale);
            var y1 = Snap(screenY * _charHeight, scale);
            var y2 = Snap((screenY + 1) * _charHeight, scale);

            var rect = new Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
            context.FillRectangle(SelectionBrush, rect);
        }

        /// <summary>
        /// Where the caret is drawn: its column, and its ABSOLUTE row (<c>YBase + Y</c> space, the same one
        /// the viewport check uses).
        /// </summary>
        /// <remarks>
        /// <para>Normally the shell's cursor. While a keyboard selection is in flight it follows the
        /// selection's moving EDGE instead, the way it does in every text field — extending a selection and
        /// leaving the caret behind reads as a stuck cursor.</para>
        /// <para>Only where the caret is DRAWN changes. The shell still owns the real cursor and is never
        /// told about this, because it must not be: the buffer position is where the shell will write next,
        /// and moving it to follow a selection would put the next output in the wrong place.</para>
        /// <para>Internal rather than private so this is directly assertable. It is otherwise only
        /// observable as pixels, and the test suite runs on the headless drawing backend.</para>
        /// </remarks>
        /// <summary>
        /// True while the caret should not be drawn at all, because there is no one place it belongs.
        /// </summary>
        /// <remarks>
        /// Select-all leaves the caret indeterminate: the whole input is selected, so neither end is more
        /// the cursor than the other. Drawing it at one of them reads as though only that end were live.
        /// Editors hide it; so does this. Steering an edge with Shift+arrow makes it meaningful again.
        /// </remarks>
        internal bool CaretHidden => _kbSelWholeInput && _terminal.Selection.HasSelection;

        internal (int Column, int AbsoluteRow) CaretPosition
        {
            get
            {
                // Both conditions: the anchor says a gesture is in flight, the selection says it still has
                // something to show. Belt and braces — every path that clears one now clears the other, but
                // a stale anchor pins the caret while the shell's cursor moves on, which is the exact
                // failure this branch has already hit twice.
                if (_kbSelAnchor is not null && _terminal.Selection.HasSelection)
                {
                    int cols = Math.Max(1, _terminal.Cols);
                    return (_kbSelFocus % cols, _terminal.Buffer.ViewportY + (_kbSelFocus / cols));
                }

                return (_terminal.Buffer.X, _terminal.Buffer.YBase + _terminal.Buffer.Y);
            }
        }

        private void RenderCursor(DrawingContext context, int viewportY, double scale)
        {
            // No process, no cursor. The checks below are all about what the BUFFER says, and a buffer says
            // the same thing whether or not anything is attached to it — so a view that has never launched,
            // or whose process has exited, paints a block cursor in its top-left corner with nothing behind
            // it. A cursor represents a shell waiting for input; when there is no shell there is nothing for
            // it to represent, and offering one to type at is a lie.
            if (!IsLive || SuppressCursor)
                return;

            // Nowhere meaningful to put it — see CaretHidden.
            if (CaretHidden)
                return;

            // Only show cursor if terminal wants it visible (controlled by escape sequences)
            if (!_terminal.CursorVisible)
                return;

            // Only show cursor if in "on" phase of blink cycle (or not blinking)
            if (!_cursorBlinkOn)
                return;

            var (cursorX, absoluteCursorY) = CaretPosition;

            // Check if cursor is visible in current viewport
            if (absoluteCursorY < viewportY || absoluteCursorY >= viewportY + _terminal.Rows)
                return;

            // Calculate screen position
            int screenY = absoluteCursorY - viewportY;
            double posX = Snap(cursorX * _charWidth, scale);
            double posY = Snap(screenY * _charHeight, scale);
            double nextX = Snap((cursorX + 1) * _charWidth, scale);
            double nextY = Snap((screenY + 1) * _charHeight, scale);
            double cellWidth = Math.Max(0, nextX - posX);
            double cellHeight = Math.Max(0, nextY - posY);

            var cursorBrush = new SolidColorBrush(CursorColor);

            // Render based on cursor style (use property which syncs with terminal)
            switch (CursorStyle)
            {
                case XT.Common.CursorStyle.Block:
                    // TODO Use ConsoleFontBrush
                    if (IsFocused)
                    {
                        // Filled block when focused
                        context.FillRectangle(cursorBrush, new Rect(posX, posY, cellWidth, cellHeight));

                        // Draw the character under cursor with inverted colors
                        var line = _terminal.Buffer.GetLine(absoluteCursorY);
                        if (line != null && cursorX < line.Length)
                        {
                            var cell = line[cursorX];
                            var charContent = cell.Content ?? " ";
                            var typeface = new Typeface(FontFamily, FontStyle, FontWeight);
                            var invertedBrush = cell.GetBackgroundBrush(_palette, this.Background);
                            var formattedText = new FormattedText(
                                charContent,
                                CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight,
                                typeface,
                                FontSize,
                                invertedBrush);
                            context.DrawText(formattedText, new Point(posX, posY));
                        }
                    }
                    else
                    {
                        // Outline block when not focused
                        var pen = new Pen(cursorBrush, 1);
                        context.DrawRectangle(pen, new Rect(posX, posY, cellWidth, cellHeight));
                    }
                    break;

                case XT.Common.CursorStyle.Underline:
                    {
                        // Draw underline cursor (2 pixels high at bottom of cell)
                        var underlineHeight = Math.Min(2.0, cellHeight);
                        context.FillRectangle(cursorBrush, new Rect(posX, posY + cellHeight - underlineHeight, cellWidth, underlineHeight));
                    }
                    break;

                case XT.Common.CursorStyle.Bar:
                    {
                        // Draw bar cursor (2 pixels wide at left of cell)
                        var barWidth = Math.Min(2.0, cellWidth);
                        context.FillRectangle(cursorBrush, new Rect(posX, posY, barWidth, cellHeight));
                    }
                    break;
            }
        }

        private static double Snap(double value, double scale)
        {
            return Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
        }

        /// <summary>
        /// Renders the IME preedit (composition) text overlay at the cursor position.
        /// This displays the uncommitted text that the IME is composing, with an underline
        /// to indicate it is not yet committed.
        /// </summary>
        private void RenderPreeditText(DrawingContext context, int viewportY, double scale)
        {
            var preeditText = _inputMethodClient?.PreeditText;
            if (string.IsNullOrEmpty(preeditText))
                return;

            int cursorX = _terminal.Buffer.X;
            int cursorY = _terminal.Buffer.Y;
            int absoluteCursorY = _terminal.Buffer.YBase + cursorY;

            // Only render if cursor is visible in current viewport
            if (absoluteCursorY < viewportY || absoluteCursorY >= viewportY + _terminal.Rows)
                return;

            int screenY = absoluteCursorY - viewportY;
            double posX = Snap(cursorX * _charWidth, scale);
            double posY = Snap(screenY * _charHeight, scale);
            double cellHeight = Snap((screenY + 1) * _charHeight, scale) - posY;

            var typeface = new Typeface(FontFamily, FontStyle, FontWeight);
            var foreground = GetValue(ForegroundProperty) ?? Brushes.White;
            var background = GetValue(BackgroundProperty) ?? Brushes.Black;

            var formattedText = new FormattedText(
                preeditText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                foreground);

            double textWidth = formattedText.Width;

            // Draw background behind preedit text to cover existing content
            context.FillRectangle(background, new Rect(posX, posY, textWidth, cellHeight));

            // Draw the preedit text
            context.DrawText(formattedText, new Point(posX, posY));

            // Draw underline to indicate uncommitted composition text
            double underlineY = posY + cellHeight - Math.Max(1.0, scale);
            var pen = new Pen(foreground, Math.Max(1.0, scale));
            context.DrawLine(pen, new Point(posX, underlineY), new Point(posX + textWidth, underlineY));
        }

        #region Win32 Input Mode Support

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
            if ((e.KeyModifiers & KeyModifiers.Control) != 0 && unicodeChar != 0)
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

            // Repeat count (always 1 for our purposes)
            var repeatCount = 1;

            // Format: ESC [ Vk ; Sc ; Uc ; Kd ; Cs ; Rc _
            return $"\u001b[{vk};{scanCode};{unicodeChar};{(isKeyDown ? 1 : 0)};{(int)controlKeyState};{repeatCount}_";
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

            // Mark enhanced keys (navigation keys, etc.)
            if (IsEnhancedKey(key))
                state |= Win32ControlKeyState.EnhancedKey;

            return state;
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

        #endregion

        #region IME Support

        /// <summary>
        /// Implements Avalonia's <see cref="TextInputMethodClient"/> for the terminal.
        /// This enables IME (Input Method Editor) support so that non-English characters
        /// can be composed correctly with the composition window positioned at the cursor.
        /// </summary>
        private sealed class TerminalInputMethodClient : TextInputMethodClient
        {
            private readonly TerminalView _view;
            private string? _preeditText;

            public TerminalInputMethodClient(TerminalView view)
            {
                _view = view;
            }

            /// <summary>
            /// Gets the preedit (composition) text currently being entered by the IME.
            /// </summary>
            public string? PreeditText => _preeditText;

            /// <summary>
            /// The visual that is rendering the text — this is the terminal view itself.
            /// </summary>
            public override Visual TextViewVisual => _view;

            /// <summary>
            /// Indicates the terminal supports displaying uncommitted preedit text.
            /// </summary>
            public override bool SupportsPreedit => true;

            /// <summary>
            /// Indicates the terminal can provide surrounding text for IME context.
            /// </summary>
            public override bool SupportsSurroundingText => true;

            /// <summary>
            /// Returns the text content of the current line up to the cursor,
            /// providing context for the IME.
            /// </summary>
            public override string SurroundingText
            {
                get
                {
                    try
                    {
                        var buffer = _view._terminal.Buffer;
                        int absoluteY = buffer.YBase + buffer.Y;
                        var line = buffer.GetLine(absoluteY);
                        if (line == null) return string.Empty;

                        var sb = new StringBuilder();
                        for (int x = 0; x < line.Length; x++)
                        {
                            var cell = line[x];
                            sb.Append(string.IsNullOrEmpty(cell.Content) ? " " : cell.Content);
                        }
                        return sb.ToString();
                    }
                    catch
                    {
                        return string.Empty;
                    }
                }
            }

            /// <summary>
            /// Gets the cursor rectangle relative to the terminal view,
            /// used to position the IME composition window at the cursor.
            /// </summary>
            public override Rect CursorRectangle
            {
                get
                {
                    try
                    {
                        var buffer = _view._terminal.Buffer;
                        int cursorX = buffer.X;
                        int absoluteCursorY = buffer.YBase + buffer.Y;
                        int viewportY = buffer.ViewportY;
                        int screenY = absoluteCursorY - viewportY;

                        double posX = cursorX * _view._charWidth;
                        double posY = screenY * _view._charHeight;

                        return new Rect(posX, posY, _view._charWidth, _view._charHeight);
                    }
                    catch
                    {
                        return default;
                    }
                }
            }

            /// <summary>
            /// Gets or sets the selection range within the surrounding text.
            /// For a terminal, this corresponds to the cursor column position.
            /// </summary>
            public override TextSelection Selection
            {
                get
                {
                    try
                    {
                        int cursorX = _view._terminal.Buffer.X;
                        return new TextSelection(cursorX, cursorX);
                    }
                    catch
                    {
                        return new TextSelection(0, 0);
                    }
                }
                set { /* Terminal selection is managed separately */ }
            }

            /// <summary>
            /// Called by the IME to display uncommitted composition text at the cursor position.
            /// </summary>
            public override void SetPreeditText(string? preeditText)
            {
                _preeditText = preeditText;
                _view.RequestInvalidate();
            }

            /// <summary>
            /// Called by the IME to display uncommitted composition text with an optional
            /// cursor offset within the preedit string.
            /// </summary>
            /// <param name="preeditText">The current composition text, or null/empty to clear it.</param>
            /// <param name="cursorPos">The cursor position within the preedit string.
            /// A terminal renders preedit as a simple underlined overlay so the within-composition
            /// cursor position is not used here.</param>
            public override void SetPreeditText(string? preeditText, int? cursorPos)
            {
                // cursorPos (position of IME cursor within the composition string) is intentionally
                // not used: the terminal renders preedit as a simple underlined text overlay and
                // does not support a separate cursor inside the composition window.
                _preeditText = preeditText;
                _view.RequestInvalidate();
            }

            /// <summary>
            /// Clears any active preedit text (e.g. when focus is lost).
            /// </summary>
            public void ClearPreeditText()
            {
                if (_preeditText != null)
                {
                    _preeditText = null;
                    _view.RequestInvalidate();
                }
            }

            /// <summary>
            /// Notifies the IME that the cursor rectangle has changed.
            /// Called when the terminal buffer updates and the cursor may have moved.
            /// </summary>
            internal void NotifyCursorRectangleChanged() => RaiseCursorRectangleChanged();

            /// <summary>
            /// Notifies the IME that the surrounding text has changed.
            /// </summary>
            internal void NotifySurroundingTextChanged() => RaiseSurroundingTextChanged();
        }

        #endregion

    }
}
