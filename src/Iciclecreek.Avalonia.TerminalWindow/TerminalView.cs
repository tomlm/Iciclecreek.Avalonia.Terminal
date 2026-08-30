using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
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

    public class TerminalView : Control, ICustomHitTest, IDisposable
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
        private HoveredUrl? _pendingUrlClick;

        // Pointer-shape (OSC 22) override state, kept apart from the hover pair above because the two
        // nest: a shape can arrive during a hover and a hover can start over a shape. This is what the
        // CONTROL's cursor was before the program's first shape took effect, so a reset can put it back
        // — SetCurrentValue overwrites the local value, so without the snapshot an embedder who wrote
        // <TerminalView Cursor="IBeam"/> lost it the first time a program reset the shape.
        private Cursor? _preShapeCursor;
        private bool _shapeOverridden;

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
            XT.Graphics.TerminalImage? Image = null,
            XT.Common.UnderlineStyle UnderlineStyle = XT.Common.UnderlineStyle.None,
            IBrush? UnderlineBrush = null)
        {
            /// <summary>Whether this run draws a picture rather than text.</summary>
            public bool IsImage => Placement is not null && Image is not null;

            /// <summary>
            /// The curly underline's geometry, built on first draw and replayed with the run.
            /// Relative to the run's own origin, so the same geometry is valid at any row --
            /// which is what lets it live in the cache while the line scrolls.
            /// </summary>
            public Geometry? UnderlineGeometry { get; set; }

            /// <summary>
            /// The underline's pen, immutable, with the dash pattern's phase-lock baked into its
            /// offset. Built once for the same reason as the geometry.
            /// </summary>
            public IPen? UnderlinePen { get; set; }
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

        /// <summary>
        /// The emulator's <c>DrawBoldTextInBrightColors</c>, snapshotted per frame beside the palette.
        /// </summary>
        /// <remarks>
        /// A RENDERER option that XTerm.NET carries and cannot act on -- it has no renderer -- so this
        /// host is the only place it can mean anything, and until now it meant nothing here either.
        /// True is both the emulator's default and xterm.js's.
        /// </remarks>
        private bool _boldIsBright = true;

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

        /// <summary>The emulator options this view reads. See the property for the identity rules.</summary>
        /// <remarks>
        /// Owned by TerminalView, which it was not: it was registered against TerminalControl, which
        /// registers an Options of its OWN under that same owner and name. Two different
        /// StyledProperty objects then claimed one entry in the registry, so a style or a setter
        /// aimed at TerminalControl.Options could resolve to whichever was reached first, and nothing
        /// aimed at TerminalView.Options was aimed at this view at all.
        /// </remarks>
        public static readonly StyledProperty<XTerm.Options.TerminalOptions?> OptionsProperty =
            AvaloniaProperty.Register<TerminalView, XTerm.Options.TerminalOptions?>(
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

        /// <summary>
        /// A desktop notification requested by the running program (OSC 9 or Kitty OSC 99). The
        /// terminal cannot post OS notifications itself; the application decides how — or
        /// whether — to show it. Structured fields are populated for OSC 99; OSC 9 carries only
        /// <c>Text</c>.
        /// </summary>
        public static readonly RoutedEvent<TerminalNotificationEventArgs> NotificationRequestedEvent =
            RoutedEvent.Register<TerminalView, TerminalNotificationEventArgs>(
                nameof(NotificationRequested),
                RoutingStrategies.Bubble);

        public event EventHandler<TerminalNotificationEventArgs> NotificationRequested
        {
            add => AddHandler(NotificationRequestedEvent, value);
            remove => RemoveHandler(NotificationRequestedEvent, value);
        }

        /// <summary>
        /// The running program asked for the user's attention (iTerm2 OSC 1337 RequestAttention).
        /// <see cref="TerminalAttentionEventArgs.Action"/> is the request verbatim: "yes" (bounce
        /// until focused), "once", "fireworks", or "no" (cancel a pending request) — the
        /// application owns the dock/taskbar, so the application interprets it.
        /// </summary>
        public static readonly RoutedEvent<TerminalAttentionEventArgs> AttentionRequestedEvent =
            RoutedEvent.Register<TerminalView, TerminalAttentionEventArgs>(
                nameof(AttentionRequested),
                RoutingStrategies.Bubble);

        public event EventHandler<TerminalAttentionEventArgs> AttentionRequested
        {
            add => AddHandler(AttentionRequestedEvent, value);
            remove => RemoveHandler(AttentionRequestedEvent, value);
        }

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

        // ---- scrollback search, for a host's find box to drive ---------------------------------
        //
        // Methods and properties rather than gestures: the box, the debounce and the keybinding all
        // belong to the host. What lives here is the part only the terminal can do -- matching
        // against the buffer, painting the hits, and moving the viewport to one.

        /// <summary>
        /// Every match, painted so a search reads as a map of the output.
        /// </summary>
        /// <remarks>
        /// Translucent, like <see cref="SelectionBrush"/>, and drawn as an overlay after the text for
        /// the same reason: the glyphs stay exactly as they were and the tint reads through.
        /// </remarks>
        public static readonly StyledProperty<IBrush> SearchHighlightBrushProperty =
            AvaloniaProperty.Register<TerminalView, IBrush>(
                nameof(SearchHighlightBrush),
                defaultValue: new SolidColorBrush(Color.FromArgb(90, 240, 180, 41)));

        public IBrush SearchHighlightBrush
        {
            get => GetValue(SearchHighlightBrushProperty);
            set => SetValue(SearchHighlightBrushProperty, value);
        }

        /// <summary>The match the find box is standing on, distinct from the rest.</summary>
        public static readonly StyledProperty<IBrush> SearchCurrentBrushProperty =
            AvaloniaProperty.Register<TerminalView, IBrush>(
                nameof(SearchCurrentBrush),
                defaultValue: new SolidColorBrush(Color.FromArgb(160, 240, 180, 41)));

        public IBrush SearchCurrentBrush
        {
            get => GetValue(SearchCurrentBrushProperty);
            set => SetValue(SearchCurrentBrushProperty, value);
        }

        private XT.Search.BufferSearch? _search;
        private int _currentMatchId = -1;

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

        /// <summary>How many matches the last search found. See also <see cref="SearchTruncated"/>.</summary>
        public int SearchHitCount => _search?.Count ?? 0;

        /// <summary>Index of the current match, or -1 before one is chosen. The "3" of "3 of 47".</summary>
        public int SearchCurrentIndex => _search?.CurrentIndex ?? -1;

        /// <summary>
        /// Whether the match cap bit, so a find box can say "10,000+" instead of a number that has
        /// quietly stopped being true.
        /// </summary>
        public bool SearchTruncated => _search?.Truncated ?? false;

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

        /// <summary>
        /// Paints the hits on the rows the viewport is showing.
        /// </summary>
        /// <remarks>
        /// An overlay after the text, exactly as the selection is, and cheap for the same structural
        /// reason the emulator stores hits by row: each visible row asks <c>HitsOnRow</c> once and
        /// the answer is almost always an empty span.
        /// </remarks>
        private void RenderSearchHighlights(DrawingContext context, int viewportY, double scale)
        {
            if (_search is null || _search.Count == 0)
                return;

            for (var screenY = 0; screenY < _terminal.Rows; screenY++)
            {
                var absoluteRow = viewportY + screenY;
                var cellScale = RowCellScale(absoluteRow);
                foreach (var hit in _search.HitsOnRow(absoluteRow))
                {
                    var brush = hit.MatchId == _currentMatchId ? SearchCurrentBrush : SearchHighlightBrush;
                    if (brush is null)
                        continue;

                    // Clamped to the grid. A hit is recorded against the buffer at the width the
                    // line had when it was searched, and a resize narrower than that leaves hits
                    // naming columns that no longer exist -- so the tint was painted hundreds of
                    // pixels past the right edge of the control, over whatever the host had there.
                    //
                    // Clamped to the columns VISIBLE on this row, which on a doubled row is half of
                    // them: each is drawn twice as wide, so the same count would reach twice the
                    // width. Clamping to Cols and then scaling would put the right edge of a hit at
                    // the far end of a control twice as wide as this one.
                    var visibleCols = (int)(_terminal.Cols / cellScale);

                    if (!ClampSpanToGrid(hit.Column, hit.EndColumn, visibleCols,
                                         out var startCol, out var endCol))
                        continue;

                    var x1 = Snap(startCol * _charWidth * cellScale, scale);
                    var x2 = Snap(endCol * _charWidth * cellScale, scale);
                    var y1 = Snap(screenY * _charHeight, scale);
                    var y2 = Snap((screenY + 1) * _charHeight, scale);
                    context.FillRectangle(brush, new Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1)));
                }
            }
        }

        /// <summary>
        /// Width in pixels of a margin down the left, for marking where commands began and how they
        /// ended. Zero, the default, means no gutter and no layout change at all.
        /// </summary>
        /// <remarks>
        /// Off unless a host asks for it, and then it is the host's brushes that decide what appears:
        /// a mark with no brush set draws nothing. There is no default glyph and no default colour,
        /// because "an exit status beside the command" is a design decision and this control has no
        /// business making it. A host that wants something other than a bar reads
        /// <see cref="VisibleMarks"/> and draws over the top instead.
        /// </remarks>
        public static readonly StyledProperty<double> GutterWidthProperty =
            AvaloniaProperty.Register<TerminalView, double>(nameof(GutterWidth), 0.0);

        public double GutterWidth
        {
            get => GetValue(GutterWidthProperty);
            set => SetValue(GutterWidthProperty, value);
        }

        /// <summary>Marks a prompt whose command has not finished, or reported no status.</summary>
        public static readonly StyledProperty<IBrush?> GutterPromptBrushProperty =
            AvaloniaProperty.Register<TerminalView, IBrush?>(nameof(GutterPromptBrush));

        public IBrush? GutterPromptBrush
        {
            get => GetValue(GutterPromptBrushProperty);
            set => SetValue(GutterPromptBrushProperty, value);
        }

        /// <summary>Marks a command that exited zero.</summary>
        public static readonly StyledProperty<IBrush?> GutterSuccessBrushProperty =
            AvaloniaProperty.Register<TerminalView, IBrush?>(nameof(GutterSuccessBrush));

        public IBrush? GutterSuccessBrush
        {
            get => GetValue(GutterSuccessBrushProperty);
            set => SetValue(GutterSuccessBrushProperty, value);
        }

        /// <summary>Marks a command that exited non-zero.</summary>
        public static readonly StyledProperty<IBrush?> GutterFailureBrushProperty =
            AvaloniaProperty.Register<TerminalView, IBrush?>(nameof(GutterFailureBrush));

        public IBrush? GutterFailureBrush
        {
            get => GetValue(GutterFailureBrushProperty);
            set => SetValue(GutterFailureBrushProperty, value);
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

        /// <summary>
        /// Every shell-integration mark on the rows currently on screen.
        /// </summary>
        /// <remarks>
        /// What a host draws its own gutter, minimap or margin from. Handed over as data rather than
        /// rendered here, because "an exit status beside the command" is a design decision — a glyph,
        /// a colour, a change bar, nothing at all — and the terminal has no business making it.
        /// <see cref="GutterWidth"/> is the built-in answer for hosts that would rather not.
        /// </remarks>
        public IReadOnlyList<VisibleMark> VisibleMarks
        {
            get
            {
                var found = new List<VisibleMark>();
                var lines = _terminal.Buffer.Lines;
                var top = _terminal.Buffer.ViewportY;

                for (var row = 0; row < _terminal.Rows; row++)
                {
                    var bufferRow = top + row;
                    if (bufferRow < 0 || bufferRow >= lines.Length)
                        continue;

                    if (lines[bufferRow] is not { } line || !line.HasMarks)
                        continue;

                    foreach (var mark in line.Marks)
                        found.Add(new VisibleMark(row, bufferRow, mark.Kind, mark.ExitCode));
                }

                return found;
            }
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

        /// <summary>A shell-integration mark on a row the viewport is showing.</summary>
        /// <param name="ViewportRow">Row on screen, 0 at the top.</param>
        /// <param name="BufferRow">The same row as an absolute buffer index, which survives scrolling.</param>
        /// <param name="Kind">Which of the four OSC 133 marks.</param>
        /// <param name="ExitCode">The status a CommandFinished reported, or null where none was.</param>
        public readonly record struct VisibleMark(
            int ViewportRow,
            int BufferRow,
            XT.Common.ShellIntegrationMark Kind,
            int? ExitCode);

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
                SuppressCursorProperty,   // toggling it must repaint immediately
                // The gutter: a brush change must repaint the marks, and a width change moves the
                // whole grid sideways -- it affects measure below as well, since columns come out
                // of the width.
                GutterWidthProperty,
                GutterPromptBrushProperty,
                GutterSuccessBrushProperty,
                GutterFailureBrushProperty,
                // A brush change must repaint live highlights -- the same missing-invalidation
                // class Copilot found on the gutter properties in the OSC work.
                SearchHighlightBrushProperty,
                SearchCurrentBrushProperty);

            AffectsMeasure<TerminalView>(
                GutterWidthProperty,
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

            // OSC 22 is opt-in in the emulator, and the opt-in is the host saying it has somewhere to put
            // the shape: an emulator that answered the support query on its own would leave programs using
            // shapes that never appear. PointerShapeChanged is wired below, so the yes is true when given.
            options.PointerShapesEnabled = true;

            _terminal = new XT.Terminal(options);

            // Point the property at the emulator's OWN options from here on. XTerm.NET snapshots what it
            // is constructed with, so `options` above is no longer the object the emulator reads -- and a
            // host that went on setting properties on it, which is the ordinary shape of XAML and of any
            // `terminal.Options.CursorBlink = true` after startup, would be writing into a copy nothing
            // consults. No exception, no warning: the setting simply stops working, which is worse than a
            // break that throws because the integration keeps compiling and keeps running.
            //
            // Assigning the live instance rather than special-casing the getter keeps this a real styled
            // property -- bindings, styles and the property system all go on working, and every reader
            // gets the object the emulator actually reads.
            //
            // SetCurrentValue rather than SetValue, to say redirect rather than take ownership. Note
            // that Avalonia is not WPF here: SetValue does NOT clear a binding on a styled property,
            // and a test that binds Options and pushes through it passes either way. So this is about
            // intent rather than a bug being fixed -- the value is being pointed at the emulator's
            // instance, not claimed on the host's behalf.
            //
            // Not everything on it is live even so, and that is XTerm.NET's contract rather than this
            // one's: Cols, Rows, and the initial theme are consumed while the emulator is built. Use
            // Resize for the dimensions. Scrollback, Theme and TabStopWidth ARE live as of XTerm.NET's
            // options audit, and BufferSize here forwards to Scrollback.
            SetCurrentValue(OptionsProperty, _terminal.Options);

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
            // The INSTANCE is captured here, and only here, because this point runs exactly once and
            // the buffer object outlives detach/re-attach. The handler itself goes on through
            // SubscribeTerminalEvents with the rest: it is balanced on detach and re-armed on
            // re-attach against this same remembered object, so a re-parent can neither double the
            // handler — which would move a parked viewport by a MULTIPLE of the evicted count — nor
            // leave one behind. Leaving one behind is not merely untidy: Terminal is public, so a
            // host holding the emulator keeps the whole view alive through the subscription and goes
            // on calling back into a control that is off the tree.
            //
            // Both of those are what a second copy of this line cost while it was here as well: the
            // shared method subscribes it too, so the handler ran twice per eviction.
            _scrollbackBuffer = _terminal.Buffer;

            // Shell integration. A shell that emits OSC 133 says exactly where its prompt ends, which is the
            // one thing the input-start heuristic can only infer. Subscribed here for the same reason as
            // Trimmed above: this point runs exactly once, and the emulator outlives a detach/re-attach.
            _terminal.OscReceived += OnTerminalOscReceived;

            SubscribeTerminalEvents();

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

        /// <summary>
        /// OSC 7: the shell reporting its working directory.
        /// </summary>
        /// <remarks>
        /// POSTED, never Invoked. This is raised from <c>Terminal.Write</c>, which the read loop calls
        /// from inside <c>lock (_terminalLock)</c> on the pty reader thread -- and the UI thread takes
        /// that same lock in <see cref="ClearScreen"/>, <see cref="CurrentLineText"/> and
        /// <c>WriteOwnLine</c>. A blocking Invoke here is therefore a DEADLOCK and not merely a stall:
        /// the reader waits for the UI thread while the UI thread waits for the lock the reader holds.
        /// The application freezes with no exception to look for.
        ///
        /// Nothing waits on the result -- it updates a property and notifies -- so posting costs a
        /// frame's latency and removes one half of the deadlock outright.
        /// </remarks>
        private void OnTerminalDirectoryChanged(object? sender, TerminalEvents.DirectoryChangeEventArgs e)
        {
            var directory = e.Directory;

            Dispatcher.UIThread.Post(() =>
            {
                var oldValue = _currentDirectory;
                _currentDirectory = directory;
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

            // Once the emulator exists, this property IS its options, and assigning any other object
            // cannot reconfigure it -- XTerm.NET took its snapshot at construction and reads nothing
            // else. Rather than accept a write that would quietly go nowhere, the live instance is put
            // back, so every read gives the object the emulator actually consults.
            //
            // This is what keeps the invariant independent of ORDER. TerminalControl hands its own
            // Options down when the template is applied, which can happen after the view has already
            // built its emulator; without this the control's assignment would point the property back
            // at an object nothing reads. The recursion stops on the next pass, where the new value is
            // the instance being restored.
            if (change.Property == OptionsProperty && _terminal != null &&
                !ReferenceEquals(change.NewValue, _terminal.Options))
            {
                SetCurrentValue(OptionsProperty, _terminal.Options);
                return;
            }

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
            // The search subscribes to Buffer.Trimmed, so it has to unhook when the view goes.
            _search?.Dispose();
            _search = null;
            _currentMatchId = -1;

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

        /// <summary>
        /// Whether <see cref="Dispose"/> has run. A disposed view stops taking part in the logical
        /// tree rather than throwing from it — see <see cref="Dispose"/> for why.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Releases the emulator, the process behind this view, and everything held with them.
        /// </summary>
        /// <remarks>
        /// <para>Explicit, and deliberately not wired to <see cref="OnDetachedFromLogicalTree"/>.
        /// Detach is not the end of a view's life here: this control supports being moved between
        /// panels, which is what <see cref="BeginReparent"/> exists for, and a view is also detached
        /// and re-attached during ordinary initialisation. Disposing on detach would kill a terminal
        /// that was only being moved. So whoever owns the view's lifetime calls this.</para>
        /// <para><c>XTerm.Terminal</c> holds parser subscriptions and event handlers that outlive
        /// every view that made one, which is what this is for. The pty, the cancellation source,
        /// the atomic-update timer and the cached bitmaps were already being released on detach;
        /// the emulator never was.</para>
        /// <para>Re-attaching a disposed view is a NO-OP rather than an exception. Avalonia raises
        /// logical-tree notifications during teardown in an order the application does not fully
        /// control, and throwing from a lifecycle hook takes down the app for what is at worst a
        /// view that will not paint. The guards below match the ones already there for a view whose
        /// emulator does not exist yet, which is the same shape of problem from the other end.</para>
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            UnsubscribeTerminalEvents();

            // The two OnInitialized subscribes ONCE and re-attachment never restores, so they
            // belong here and not in the shared method above. Detach drops only what attach puts
            // back; dropping these there would leave a re-parented view permanently deaf to OSC
            // sequences and blind to palette changes, which is a worse bug than the leak.
            //
            // They are the rest of the leak all the same: a host holding the terminal after
            // disposing the view would otherwise keep calling into it through these two.
            //
            // Null-guarded, which the two lines below it were not. Everything OnInitialized builds
            // is absent on a view that was constructed and then dropped without ever being shown --
            // a host that decides against a tab it had already made, or any test that news up a view
            // and disposes it. UnsubscribeTerminalEvents guards for exactly this and so does the
            // _terminal?.Dispose() further down; these two did not, and threw between them. The
            // NullReferenceException was the smaller half of the damage: _disposed is already true
            // by then, so the rest of Dispose never ran and a second call could not run it either,
            // leaving the pty and the emulator held by a view the host believed it had released.
            if (_terminal != null)
            {
                _terminal.OscReceived -= OnTerminalOscReceived;
                _terminal.Colors.ColorChanged -= OnTerminalColorChanged;
            }

            // Both timers, because Dispose is not Unloaded. A view disposed while still on the tree
            // never gets an Unloaded, and DispatcherTimer holds its target through the Tick handler
            // -- so the timers keep the disposed view alive, and keep asking it to blink a cursor
            // and advance animations on an emulator that is about to be disposed underneath them.
            _cursorBlinkTimer?.Stop();
            _animationTimer?.Stop();

            _atomicUpdateTimeout?.Dispose();
            _atomicUpdateTimeout = null;
            _atomicUpdate = false;

            // Takes the pty, the cancellation source and the cached bitmaps with it. An ATTACHED
            // connection is still left alone, for the reason CleanupProcess gives: it belongs to
            // whoever attached it, and disposing it would stop a process this view does not own.
            CleanupProcess();

            _terminal?.Dispose();

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Puts this view's handlers on the emulator.
        /// </summary>
        /// <remarks>
        /// <para>The exact mirror of <see cref="UnsubscribeTerminalEvents"/>, and it exists for the
        /// reason that one already gave for itself: a second copy of a list is a list that goes
        /// stale. There were two copies of the SUBSCRIBE list -- one for the first attach, one for a
        /// re-attach -- and they had drifted by exactly one entry.</para>
        /// <para>SynchronizedOutputChanged was in the re-attach copy and not the other, so DEC mode
        /// 2026 did nothing until a view had been detached and put back. A terminal that was never
        /// re-parented, which is nearly all of them, tore on every atomic update an application
        /// asked it not to tear on.</para>
        /// <para>What is deliberately NOT here: OscReceived and Colors.ColorChanged. Those are
        /// subscribed once, in OnInitialized, because a detach must not drop them -- see Dispose,
        /// which is the only thing that does.</para>
        /// </remarks>
        private void SubscribeTerminalEvents()
        {
            if (_terminal == null)
                return;

            if (_scrollbackBuffer != null)
                _scrollbackBuffer.Trimmed += OnBufferTrimmed;

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
            _terminal.ClipboardWriteRequested += OnTerminalClipboardWriteRequested;
            _terminal.ClipboardReadRequested += OnTerminalClipboardReadRequested;
            _terminal.NotificationReceived += OnTerminalNotificationReceived;
            _terminal.AttentionRequested += OnTerminalAttentionRequested;
            _terminal.PointerShapeChanged += OnTerminalPointerShapeChanged;
            _terminal.WindowInfoRequested += OnTerminalWindowInfoRequested;
        }

        /// <summary>
        /// Drops every handler this view put on the emulator.
        /// </summary>
        /// <remarks>
        /// Shared by detach and by <see cref="Dispose"/>, because they need exactly the same list and
        /// a second copy of it is a list that goes stale. Detach unsubscribes so a re-attached view
        /// can subscribe again; Dispose unsubscribes because there will be no re-attach.
        /// </remarks>
        private void UnsubscribeTerminalEvents()
        {
            if (_terminal == null)
                return;

            // Against the remembered instance, not _terminal.Buffer: unsubscribing while a full-screen
            // app has the alternate buffer active would otherwise let go of the wrong object.
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
            _terminal.ClipboardWriteRequested -= OnTerminalClipboardWriteRequested;
            _terminal.ClipboardReadRequested -= OnTerminalClipboardReadRequested;
            _terminal.NotificationReceived -= OnTerminalNotificationReceived;
            _terminal.AttentionRequested -= OnTerminalAttentionRequested;
            _terminal.PointerShapeChanged -= OnTerminalPointerShapeChanged;
            _terminal.WindowInfoRequested -= OnTerminalWindowInfoRequested;
        }

        protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromLogicalTree(e);

            // Nothing left to unwind, and nothing that wants unwinding twice.
            if (_disposed)
                return;

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

            UnsubscribeTerminalEvents();

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

            // Re-attaching a disposed view does nothing rather than resurrecting handlers onto an
            // emulator that has already let go of its own. See Dispose for why this is not a throw.
            if (_disposed)
                return;

            // _terminal is null during initial attachment (OnInitialized hasn't fired yet).
            // Only re-subscribe when re-parenting after a prior detach.
            if (_terminal == null) return;

            // The same list the once-only path uses, because it IS the same list. Unsubscribe
            // first, so a re-attach that follows an attach cannot double-subscribe.
            UnsubscribeTerminalEvents();
            SubscribeTerminalEvents();
        }

        private void OnCursorBlinkTick(object? sender, EventArgs e)
        {
            if (CursorBlink && IsFocused)
            {
                _cursorBlinkOn = !_cursorBlinkOn;

                // Over the VIEWPORT, not over buffer rows 0..Rows.
                //
                // GetLine takes an absolute buffer row, so 0..Rows is the oldest scrollback the
                // terminal has ever held -- lines nobody is looking at. On a fresh terminal the two
                // ranges coincide, which is why this looked right; the moment anything scrolls off
                // the top they diverge completely, and SGR 5 text stops blinking for the rest of the
                // session because the lines actually on screen keep their cached runs.
                var top = _terminal.Buffer.ViewportY;
                for (int y = 0; y < _terminal.Rows; y++)
                {
                    var line = _terminal.Buffer.GetLine(top + y);
                    if (line == null)
                        continue;

                    // A plain loop rather than Any(): this runs twice a second over every visible
                    // row, and the predicate closure was being allocated for each of them.
                    for (int x = 0; x < line.Length; x++)
                    {
                        if (line[x].Attributes.IsBlink())
                        {
                            line.Cache = null;
                            break;
                        }
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
                        //
                        // Asked SYNCHRONOUSLY, and claimed before the await rather than after it. This
                        // handler is async void: the first await returns to the caller and the routed
                        // event finishes bubbling with Handled still false, so the old placement claimed
                        // an event that had already gone -- Cmd+X cut the selection and reached the
                        // program as well. CanCut asks all three of the questions CutAsync asks
                        // before it does anything, which is what makes it safe to ask them here instead.
                        if (CanCut)
                        {
                            e.Handled = true;
                            await CutAsync().ConfigureAwait(false);
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
                                //
                                // Same correction as the macOS Cmd+X above: the question is asked
                                // synchronously so the claim can be made before the first await, which is
                                // the last moment anything is still listening for it. Here the old
                                // placement meant Ctrl+X cut the line AND handed readline its prefix.
                                if (CanCut)
                                {
                                    e.Handled = true;
                                    await CutAsync().ConfigureAwait(false);
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

                    // Taken HERE and sent LATER, which makes every early return between this line
                    // and the senders a place the deletion can be lost -- while the selection it
                    // belonged to has already gone from the screen. Two of them were losing it: the
                    // Win32 and Kitty paths both claim the key and return, and both now carry it.
                    //
                    // The rest are safe by arithmetic rather than by luck: every other return in
                    // between requires Ctrl, Alt or Meta, and a modified keystroke neither types nor
                    // erases, so `unmodified` is false and this line has already stored empty. That
                    // is also why a value cannot go stale for long -- any non-modifier key
                    // reassigns it on the way past.
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
                //
                // Not while Kitty is negotiated. This is a translation into what a SHELL binds, and
                // it is worth making only for a shell reading legacy sequences. An application that
                // asked for CSI-u reads Cmd+Left as a modified arrow in that encoding, and handing
                // it ESC[H instead sends a key nobody pressed, in a protocol it is no longer
                // reading. Falling through delivers the actual chord.
                if (IsMacOS && e.KeyModifiers == KeyModifiers.Meta && e.Key is Key.Left or Key.Right
                    && !_terminal.KittyKeyboardActive)
                {
                    e.Handled = true;
                    await SendToPtyAsync(e.Key == Key.Left ? "\u001b[H" : "\u001b[F").ConfigureAwait(false);
                    return;
                }

                // A legacy Meta chord belongs to the host application, so leave it unhandled. Once
                // Kitty is active, however, Meta is part of the protocol's key event and must reach
                // TrySendKittyKeyAsync below. Returning here would make Cmd+Left/Right disappear: the
                // shell alias above correctly declines it, then this guard would drop it before CSI-u.
                if ((e.KeyModifiers & KeyModifiers.Meta) != 0 && !_terminal.KittyKeyboardActive)
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
                // And not while Kitty is negotiated, for the same reason as the macOS chord above and
                // the same reason Win32 input mode is already excluded here: every exclusion on this
                // list is a transport the shell is not reading ESC-b through. An application that
                // negotiated CSI-u reads Alt+Left as a modified arrow and binds it itself; ESC-b is
                // a key it never pressed.
                if (e.Key is Key.Left or Key.Right
                    && e.KeyModifiers is KeyModifiers.Alt or KeyModifiers.Control
                    && !_terminal.Win32InputMode
                    && !_terminal.KittyKeyboardActive
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
                    var sequence = GenerateWin32InputSequence(e, isKeyDown: true);
                    if (!string.IsNullOrEmpty(sequence))
                    {
                        e.Handled = true;

                        // Carrying the pending deletion, for the reason given where it is taken: the
                        // selection was consumed several branches above, and an exit that sends the
                        // keystroke WITHOUT it types over a selection that silently never went away.
                        // Backspace and Delete replace their own sequence rather than adding to it --
                        // the selection is what they were asked to remove, not one character past it.
                        var erase = _pendingReplaceKeys;
                        _pendingReplaceKeys = string.Empty;

                        var toSend = erase.Length > 0 && e.Key is Key.Back or Key.Delete
                            ? erase
                            : erase + sequence;

                        await SendToPtyAsync(toSend).ConfigureAwait(false);
                        return;
                    }
                    // If we couldn't generate a Win32 sequence, fall through to normal handling
                    // This can happen for keys that don't have a virtual key mapping
                }

                // The Kitty keyboard protocol, when an application has negotiated it. Ahead of every
                // legacy generator below, because once the flags are set those encodings are not what
                // the application is reading any more -- it asked for CSI-u and the terminal accepted
                // on this host's behalf. Behind the Win32 path above, which is a different protocol
                // for a different transport and keeps its precedence.
                //
                // The pending deletion travels WITH it. Kitty claims the key and returns, so a
                // deletion left behind here is one the shell never receives -- while the selection
                // it belonged to has already been cleared on screen. Handed over rather than taken
                // first, because this can also decline the key, and a deletion consumed by a call
                // that declined is one the legacy paths below would then send without.
                if (await TrySendKittyKeyAsync(e, XT.Input.KittyKeyboardEventType.Press,
                                               _pendingReplaceKeys).ConfigureAwait(false))
                {
                    _pendingReplaceKeys = string.Empty;
                    return;
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

        /// <summary>
        /// Whether <see cref="CutAsync"/> will succeed, asked without starting it.
        /// </summary>
        /// <remarks>
        /// <para>Exists so the key handlers can claim the chord BEFORE their first await, which is
        /// the last moment anything is still listening for the flag. Claiming afterwards means the
        /// event has already finished bubbling and the chord reaches the program as well.</para>
        /// <para>CanRemoveSelection alone was not enough for that. Cut can still decline after it --
        /// with no clipboard to write to, or a selection whose text is empty -- and a chord claimed
        /// on the strength of a cut that then did not happen is swallowed for nothing. Both of those
        /// are answerable here, synchronously, so this asks all three of the questions CutAsync asks
        /// rather than the first one.</para>
        /// <para>The last condition is the one that is easy to miss: the deletion has to be
        /// ENCODABLE. EncodeSyntheticKey answers empty when the live protocol has no way to express
        /// a synthetic Backspace, and a cut claimed past that point would have copied to the
        /// clipboard and then removed nothing -- a cut that silently became a copy, which is the
        /// outcome CutAsync exists to refuse.</para>
        /// <para>What is left is only the write itself failing, which nothing can predict.</para>
        /// </remarks>
        private bool CanCut
            => CanRemoveSelection
               && TopLevel.GetTopLevel(this)?.Clipboard is not null
               && !string.IsNullOrEmpty(_terminal.Selection.GetSelectionText())
               && !string.IsNullOrEmpty(BuildKeyboardSelectionDeletion());

        /// <summary>
        /// The bytes for one press of a key this view is pressing on the user's behalf, encoded for
        /// whichever keyboard protocol is live right now.
        /// </summary>
        /// <remarks>
        /// <para>The selection deletion is made of keystrokes nobody typed: Backspaces and right
        /// arrows standing in for an edit this view cannot perform itself, because the shell owns
        /// the line. They have to be encoded the way the application is currently READING
        /// keystrokes, and there are three answers to that, not one.</para>
        /// <para>Generating the legacy byte unconditionally — which is what this did first — is
        /// wrong twice over. Under Win32 input mode the process is reading INPUT_RECORDs and a bare
        /// 0x08 is not one, so cmd.exe and PSReadLine see nothing. Under a negotiated Kitty
        /// protocol the application asked for CSI-u and stopped reading the legacy encodings the
        /// terminal had been accepting on its behalf.</para>
        /// <para>Empty means this protocol cannot express the key, and the caller must then leave
        /// the selection ALONE rather than clearing it: a deletion that cannot be sent must not be
        /// drawn as though it happened.</para>
        /// </remarks>
        private string EncodeSyntheticKey(Key avaloniaKey, XT.Input.Key xtermKey, string kittyName)
        {
            // Win32 first, matching the precedence the real key path uses a few hundred lines up:
            // it is a different transport rather than a competing encoding, so it wins.
            if (_terminal.Win32InputMode)
            {
                var vk = ConvertAvaloniaKeyToVirtualKey(avaloniaKey);
                if (vk == 0)
                    return string.Empty;

                // A real key produces a down record and an up record, so a synthetic one has to as
                // well -- PSReadLine reads the pair. Scan code 0 for the same reason the real path
                // uses 0: there is no hardware event here to take one from.
                var unicode = avaloniaKey == Key.Back ? 0x08 : 0;

                // Through the same state builder a real key uses, rather than None. No modifier is
                // held for a synthetic key, but the control-key state carries more than modifiers:
                // the ENHANCED flag marks the extended-scan-code keys, and the right arrow this
                // sends for a forward selection is one of them. A record without it describes a
                // different key, and the console layer is entitled to read it as one.
                var state = GetWin32ControlKeyState(KeyModifiers.None, avaloniaKey);

                return Win32Record(vk, 0, unicode, isKeyDown: true, state)
                     + Win32Record(vk, 0, unicode, isKeyDown: false, state);
            }

            if (_terminal.KittyKeyboardActive)
            {
                // Both keys this is called with are in the protocol's fixed name table, so the name
                // is passed in rather than reached for through KittyKeyName -- that one needs a
                // KeyEventArgs for its KeySymbol fallback, and there is no event here.
                var ev = new XT.Options.KeyEvent { Key = kittyName, Code = kittyName };

                // Null is the generator saying the negotiated flags do not change this key, and the
                // legacy encoding is still what the application reads -- not that it sends nothing.
                var press = _terminal.GenerateKittyKeyInput(ev, XT.Input.KittyKeyboardEventType.Press)
                            ?? _terminal.GenerateKeyInput(xtermKey, XT.Input.KeyModifiers.None);

                // A release only if the flags asked for one. Sending it unconditionally would give
                // an application that never opted in a key-up it has no encoding for.
                var release = _terminal.GenerateKittyKeyInput(ev, XT.Input.KittyKeyboardEventType.Release);

                return press + release;
            }

            return _terminal.GenerateKeyInput(xtermKey, XT.Input.KeyModifiers.None);
        }

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

                // Before the await, for the reason given on the key-up path: this handler is async
                // void, so it returns to the routing at the await and the event goes on bubbling
                // while still marked unhandled. Found by review of that other site and fixed here
                // too rather than left as its twin.
                e.Handled = true;
                await SendToPtyAsync(replaceKeys + e.Text).ConfigureAwait(false);
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

        /// <summary>How long the reader waits for the UI thread to answer a window query.</summary>
        /// <remarks>
        /// Generous for a handler that only reads window state, and short enough that a wedged UI
        /// thread costs a pause rather than the session. See <see cref="OnTerminalWindowInfoRequested"/>.
        /// </remarks>
        private static readonly TimeSpan WindowInfoPatience = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Answers a program asking about the window — its size in cells or pixels, its position, or
        /// its title — from the UI thread, without letting the reader wait on it for ever.
        /// </summary>
        /// <remarks>
        /// <para>The answer genuinely needs the UI thread -- only it knows the window -- and the
        /// emulator reads <c>e.Handled</c> the moment this returns, to decide whether it has a reply
        /// to send. So it cannot be posted: Post returns immediately, Handled is still false, and the
        /// answer is written correctly, on the UI thread, after the only reader of it has moved on.
        /// Every window query then goes unanswered and the program asking waits out its timeout.</para>
        /// <para>But it cannot block for ever either. This is raised from <c>Terminal.Write</c>, which
        /// the read loop calls inside <c>lock (_terminalLock)</c> on the pty reader thread, and the UI
        /// thread takes that same lock in <see cref="ClearScreen"/>, <see cref="CurrentLineText"/> and
        /// <c>WriteOwnLine</c>. An unbounded wait is a DEADLOCK, not a stall: the application freezes
        /// with no exception to look for.</para>
        /// <para>So it is bounded. An idle UI thread answers in microseconds and the normal path is
        /// unchanged; a wedged one costs a pause instead of the session. Leaving <c>Handled</c> false
        /// on timeout is not a guess -- it is the answer the emulator already documents for a terminal
        /// that cannot say, and a wrong number would be worse than none.</para>
        /// </remarks>
        private void OnTerminalWindowInfoRequested(object? sender, XT.Events.TerminalEvents.WindowInfoRequestedEventArgs e)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                // Whoever gets here first wins, and the loser does nothing.
                //
                // Timing out does not cancel the queued job -- the UI thread runs it whenever it
                // frees up, which may be long after the emulator gave up waiting and sent its
                // "cannot say". Writing the answer into e THEN is a write to state the emulator has
                // moved past, and on the next query it would find Handled already true from the
                // previous one. So the timeout claims the right to answer, and the late callback
                // finds it taken and returns.
                var claim = 0;

                var pending = Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Interlocked.Exchange(ref claim, 1) == 0)
                        AnswerWindowInfo(e);
                });

                if (!pending.GetTask().Wait(WindowInfoPatience) && Interlocked.Exchange(ref claim, 1) == 0)
                    Debug.WriteLine("[TerminalView] window-info query timed out; answering unhandled");

                return;
            }

            // Inline when this IS the UI thread, so a host driving the terminal directly neither
            // deadlocks on itself nor pays a dispatcher round trip.
            AnswerWindowInfo(e);
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
                // Re-asked after the wait, not just captured before it.
                //
                // The capture above stops the reference changing mid-write, which is a different
                // problem from this one. Waiting on the semaphore is an await, and a queue of
                // keystrokes waiting behind a slow write can sit here across a detach or a relaunch --
                // at which point the captured connection is one the view has handed back to its owner,
                // and writing to it types this view's input into somebody else's process.
                //
                // Dropped rather than redirected to the current connection. Input aimed at a process
                // that is no longer here belongs to nothing: sending it onwards would put half a
                // command line into whatever replaced it.
                if (!ReferenceEquals(_ptyConnection, ptyConnection))
                    return;

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

        /// <summary>
        /// The keys currently held down, so a second key-down for one of them can be reported as a
        /// REPEAT rather than a second press.
        /// </summary>
        /// <remarks>
        /// Avalonia's KeyEventArgs carries no repeat flag, and the protocol distinguishes the two --
        /// an application that negotiated event types asked to be able to tell them apart, and
        /// giving it a press for every auto-repeat answers a question it did not ask. Keyed on the
        /// physical key AND the logical one, because an unrecognised key reports PhysicalKey.None
        /// and several different keys would otherwise share the one entry.
        /// </remarks>
        private readonly HashSet<(PhysicalKey Physical, Key Logical)> _keysHeld = new();

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
                return false;

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
        internal sealed class HoveredUrl
        {
            public HoveredUrl(string url, List<(int Line, int StartCol, int EndCol)> segments,
                              bool fromSequence = false)
            {
                Url = url;
                Segments = segments;
                FromSequence = fromSequence;
            }

            public string Url { get; }

            /// <summary>
            /// Whether the program declared this link with OSC 8, rather than the text merely looking
            /// like a URL.
            /// </summary>
            /// <remarks>
            /// Surfaced to the host because the two deserve different trust. A declared link is a
            /// statement of intent from the program; a matched one is a guess about characters that
            /// happened to be on screen, and its target is whatever the user can already read.
            /// </remarks>
            public bool FromSequence { get; }

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

        // ---- Host seams for the emulator's clipboard, notification, attention and pointer ----
        //
        // Every handler below is raised from Terminal.Write, and the read loop calls that from the pty
        // READER thread (see StartReading), never the UI thread. So they all marshal, exactly like
        // OnTerminalBellRang and the window handlers above: SetCurrentValue verifies dispatcher access
        // and throws off-thread, RaiseEvent does not verify and instead runs an application's handlers
        // on the reader thread, and Avalonia's Win32 clipboard is thread-affine on the set path. An
        // exception here unwinds through Terminal.Write into the read loop's catch-all, which ends the
        // loop - the terminal shows no further output for the rest of its life.

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
            Dispatcher.UIThread.Post(() => _ = WriteToClipboardAsync(text));
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
            if (!e.MimeType.StartsWith("text/", StringComparison.Ordinal) || !IsClipboardTarget(e.Target))
                return;   // declined: OSC 52 stays silent, 5522 counts the mime unavailable

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
            e.Respond(text);
        }

        private void OnTerminalNotificationReceived(object? sender, XT.Events.TerminalEvents.NotificationEventArgs e) =>
            Dispatcher.UIThread.Post(() => RaiseEvent(new TerminalNotificationEventArgs(NotificationRequestedEvent, e)));

        private void OnTerminalAttentionRequested(object? sender, XT.Events.TerminalEvents.AttentionRequestedEventArgs e) =>
            Dispatcher.UIThread.Post(() => RaiseEvent(new TerminalAttentionEventArgs(AttentionRequestedEvent, e.Action)));

        /// <summary>
        /// Kitty OSC 22: the program chose a pointer shape. The link-hover hand keeps the last
        /// word while a link is under the pointer — its save/restore already treats the current
        /// cursor as "whatever the rest of the world wanted", so the program's shape goes into
        /// the saved slot during a hover and takes effect the moment the hover ends.
        /// </summary>
        private void OnTerminalPointerShapeChanged(object? sender, XT.Events.TerminalEvents.PointerShapeEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var cursor = MapPointerShape(e.Shape);

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

            // KNOWN LIMITATION, recorded here because the code gives no hint of it.
            //
            // This does not stop the reader. That thread is parked inside a SYNCHRONOUS Read on the
            // connection's stream, and the only way to make such a read return is to close the stream
            // underneath it — which is exactly what must not happen here, since the stream is what is
            // being handed over. Cancelling _processCts does not touch a blocking read.
            //
            // So for a quiet process the thread stays parked indefinitely, holding this view, its
            // emulator and its scrollback alive through the loop's closure. And when the process does
            // write, that thread takes the chunk: the loop now notices it is no longer the owner and
            // stops without painting it anywhere, which is better than delivering another owner's
            // output into this view, but the bytes are still gone rather than delivered to the new
            // owner.
            //
            // Fixing it properly means deciding what a handover IS at the API level -- the detached
            // connection would have to carry its reader, or carry the bytes already taken off the
            // stream, so the new owner resumes rather than races. That is a public contract change,
            // and it is not being made quietly in a bug fix.
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

                    // Asked AGAIN, on the other side of the read.
                    //
                    // The condition on the while loop is the same question, and asking it there is
                    // not enough on its own: this read blocks for as long as the process stays quiet,
                    // which at an idle prompt is indefinitely. A detach or a relaunch lands in that
                    // window routinely -- it is the window a detach is most likely to land in, being
                    // nearly all of the loop's life -- and the chunk that ends the read then belongs
                    // to a connection this view no longer owns.
                    //
                    // Everything below assumed otherwise. The bytes went into _terminal, so output
                    // meant for whoever now owns the process was painted into this view and lost to
                    // them; OutputReceived fired for it, under a comment asserting the check above
                    // had already made that impossible; and ShellReady could be raised for a process
                    // that is not this view's.
                    //
                    // Breaking rather than continuing: the while condition would stop the loop on the
                    // next pass anyway, and this makes it stop without first reading a SECOND chunk
                    // out of a stream that is not ours to read.
                    if (!ReferenceEquals(_ptyConnection, connection))
                        break;

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
                            // loop does not already provide -- and what provides it is the check after the
                            // READ, not the one in the while condition this comment used to cite. That one
                            // is asked before a read that blocks for the whole idle life of the process, so
                            // it says nothing about who owns the bytes it eventually returns. A sniffer was
                            // being handed another connection's output on the strength of it.
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
                // Only speak for the connection this loop was reading, and only while it is still the
                // one the view owns.
                //
                // The interlock alone could not answer that. InstallConnection RESETS it as it
                // publishes a new connection, so a stale loop -- one whose stream was closed out from
                // under it by exactly that replacement, which is how it got here -- read the flag as
                // 0 and took it for permission to speak. It then wrote "Error reading from process"
                // into the terminal belonging to its SUCCESSOR: a relaunch that worked, reporting a
                // failure, describing a process that is already gone.
                //
                // ReferenceEquals is asked first because it is the question that was missing. The
                // interlock stays behind it for its original job: a stream closing after the process
                // has already exited is expected, and not worth a line of red.
                if (!ReferenceEquals(_ptyConnection, connection))
                    return;

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

        /// <summary>
        /// Paints the marks for the rows on screen into the gutter lane.
        /// </summary>
        /// <remarks>
        /// A bar per prompt, coloured by how its command ended, and nothing at all where the host has
        /// set no brush for that case. There is no built-in palette here on purpose: a terminal
        /// picking its own red and green would be picking them for every theme it ever runs under.
        /// </remarks>
        private void DrawGutter(DrawingContext context, double gutter, double scale)
        {
            if (gutter <= 0 || _charHeight <= 0)
                return;

            // Straight over the visible lines rather than through VisibleMarks, which builds a list
            // -- fine for a host asking once, not for a render path asking per frame.
            var lines = _terminal.Buffer.Lines;
            var top = _terminal.Buffer.ViewportY;

            for (var row = 0; row < _terminal.Rows; row++)
            {
                var bufferRow = top + row;
                if (bufferRow < 0 || bufferRow >= lines.Length)
                    continue;

                if (lines[bufferRow] is not { } line || !line.HasMarks)
                    continue;

                // ONE bar for the row, decided by all of its marks together, rather than one fill
                // per mark into the same rectangle. A shell's prompt string carries OSC 133;D;<code>
                // for the command that just finished immediately before the OSC 133;A that opens the
                // next prompt, so both land on the same line -- and filling per mark painted the exit
                // status and then covered it with the prompt colour, which made success and failure
                // unreachable for any host that set GutterPromptBrush.
                //
                // The status wins the lane, because how a command ended is the one thing here the user
                // cannot read off the screen; where a prompt began is visible in the prompt itself.
                int? exitCode = null;
                bool anyMark = false;

                foreach (var mark in line.Marks)
                {
                    if (mark.Kind != XT.Common.ShellIntegrationMark.PromptStart
                        && mark.Kind != XT.Common.ShellIntegrationMark.CommandFinished)
                        continue;

                    anyMark = true;
                    if (mark.Kind == XT.Common.ShellIntegrationMark.CommandFinished
                        && mark.ExitCode is int code)
                        exitCode ??= code;
                }

                if (!anyMark)
                    continue;

                var brush = exitCode switch
                {
                    0 => GutterSuccessBrush,
                    int => GutterFailureBrush,
                    _ => null,
                };

                // A finish with no status to report is a prompt bar, as it was before; so is a finish
                // whose case the host left unstyled, which keeps a host that styled only the prompt
                // showing its bar on every prompt row rather than losing the ones a command ended on.
                brush ??= GutterPromptBrush;

                if (brush is null)
                    continue;

                // SNAPPED, from the same arithmetic every text row uses. Raw row * _charHeight is a
                // fractional pixel whenever the cell height is, and the rows themselves are snapped
                // to the device grid -- so the bars drifted against the text they annotate, growing
                // to nearly a pixel out by the bottom of the screen and landing between two rows.
                var barTop = Snap(row * _charHeight, scale);
                var barBottom = Snap((row + 1) * _charHeight, scale);

                context.FillRectangle(brush,
                    new Rect(0, barTop, gutter, Math.Max(0, barBottom - barTop)));
            }
        }

        /// <summary>
        /// Narrows a column span to the columns that currently exist, answering false when nothing
        /// of it is left.
        /// </summary>
        /// <remarks>
        /// Spans outlive the grid they were measured against. A search hit is recorded at the width
        /// the line had when it was searched, and a resize narrower than that leaves it naming
        /// columns that are gone -- painted, in the old geometry, past the right edge of the control
        /// and over whatever the host had beside it.
        /// </remarks>
        internal static bool ClampSpanToGrid(int start, int end, int cols, out int clampedStart, out int clampedEnd)
        {
            clampedStart = Math.Clamp(start, 0, cols);
            clampedEnd = Math.Clamp(end, clampedStart, cols);
            return clampedEnd > clampedStart;
        }

        /// <summary>
        /// How much of something starting at <paramref name="x"/> fits before <paramref name="right"/>.
        /// </summary>
        /// <remarks>
        /// For draws whose width is not decided by the grid. An IME composition is as long as the
        /// user makes it -- a whole phrase before it commits -- and drawing it at its measured width
        /// from the cursor ran it off the end of the control.
        /// </remarks>
        internal static double FitWidth(double x, double measured, double right)
            => Math.Min(measured, Math.Max(0, right - x));

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

        protected override Size ArrangeOverride(Size finalSize)
        {
            // Calculate how many columns fit in the allocated width
            if (_charWidth > 0)
            {
                // The gutter is taken out of the width before columns are counted, so turning one on
                // narrows the terminal rather than pushing text off the right-hand edge. Clamped the
                // same way Render and PointerColumn clamp it -- a negative width must not mint columns.
                int newCols = Math.Max(1, (int)((finalSize.Width - Math.Max(0, GutterWidth)) / _charWidth));
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


        /// <summary>
        /// One OSC 66 block waiting for the deferred pass: which line, which cells, and where the
        /// anchor row landed on screen. Deferred because a block taller than one row must paint
        /// AFTER every row's background — rows render top to bottom, and the row below an anchor
        /// would otherwise fill straight over the glyph's lower half.
        /// </summary>
        private readonly record struct SizedBlockDraw(
            XT.Buffer.BufferLine Line, XT.Buffer.LineSizedRun Run, double StartYPos, double RowHeight);

        private readonly List<SizedBlockDraw> _sizedBlockDraws = new();

        public override void Render(DrawingContext context)
        {
            _sizedBlockDraws.Clear();
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

            // Snapshotted with it, and for the same reason: one answer for the whole frame. It is an
            // ordinary settable option, so a program or a host can move it between frames, and half a
            // screen drawn under each rule would be worse than either.
            _boldIsBright = _terminal.Options.DrawBoldTextInBrightColors;

            var surface = GetValue(BackgroundProperty);
            // Through the shared test, which also asks about IBrush.Opacity -- a second way to be
            // translucent that this checked alpha alone for, and missed.
            if (BufferCellExtensions.IsFullyOpaque(surface))
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

            // The gutter, and the shift that keeps the grid out of it.
            //
            // One transform rather than an offset threaded through every column-to-pixel sum in this
            // method. There are sixteen of those and they would each have to be found and changed;
            // a translation catches all of them at once and cannot be half-applied. The pointer maths
            // takes the offset off the other end -- see PointerColumn.
            var gutter = Math.Max(0, GutterWidth);
            DrawGutter(context, gutter, scale);

            using var gutterShift = gutter > 0
                ? context.PushTransform(Matrix.CreateTranslation(gutter, 0))
                : default;

            //Debug.WriteLine("======");
            //Debug.WriteLine(_terminal.Buffer.PrintViewport());

            // Use the terminal buffer's ViewportY to determine what to render
            int viewportY = _terminal.Buffer.ViewportY;
            int viewportLines = _terminal.Rows;
            int startLine = viewportY;
            int endLine = Math.Min(_terminal.Buffer.Length, startLine + viewportLines);
            try
            {
                // A block anchored ABOVE the viewport still hangs into it. The row pass below starts
                // at viewportY, so it never visits such a block's own line and _sizedBlockDraws would
                // never hear about it -- and the rows it covers are deliberately blank in the buffer,
                // SkipCellsCoveredFromAbove having steered text around them, so nothing else paints
                // there either. Scrolling one line through any output holding an s=2 heading would
                // blank the heading rather than clip it.
                //
                // At most MaxScale - 1 rows to walk, and only when the buffer has ever held a tall
                // block. The draw's StartYPos goes NEGATIVE, which is what puts the box back where it
                // belongs, and the PushClip in RenderSizedBlocks trims what falls above the top.
                if (_terminal.Buffer.HasMultiRowSizedRuns)
                {
                    for (int above = 1; above < XT.Common.TextSizing.MaxScale; above++)
                    {
                        int anchorRow = viewportY - above;
                        if (anchorRow < 0)
                            break;

                        var anchorLine = anchorRow < _terminal.Buffer.Length
                            ? _terminal.Buffer.GetLine(anchorRow) : null;
                        if (anchorLine is null || !anchorLine.HasSizedRuns)
                            continue;

                        var hangStart = Snap(-above * _charHeight, scale);
                        var hangEnd = Snap((-above + 1) * _charHeight, scale);

                        foreach (var run in anchorLine.SizedRuns)
                        {
                            // Rows > above is the test TryGetSizedRunCovering applies: a run reaches
                            // this row only if it is taller than the distance up to its anchor.
                            if (run.Rows > above)
                                _sizedBlockDraws.Add(new SizedBlockDraw(
                                    anchorLine, run, hangStart, Math.Max(0, hangEnd - hangStart)));
                        }
                    }
                }

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

                // OSC 66 blocks, after every row's background and text and before the overlays:
                // selection and the cursor still draw over scaled text, as they do over plain text.
                RenderSizedBlocks(context, scale);

                // Search highlights under the selection, so a selected match still reads as selected.
                RenderSearchHighlights(context, viewportY, scale);

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

                    if (run.UnderlineStyle != XT.Common.UnderlineStyle.None)
                        DrawUnderline(context, run, position, rect.Width, rowHeight);
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

            // A line holding OSC 66 blocks is drawn in two stages: everything OUTSIDE the
            // blocks now, the blocks themselves in the deferred pass after every row — a block
            // taller than one row must not be painted over by the next row's background. The
            // anchor cell's Width spans the whole block, so drawing it as a normal run would put
            // the text at base size in a corner of the box. Sized lines are not cached: the
            // cache stores finished draw calls, and the blocks are deliberately NOT drawn here.
            var hasSizedRuns = line.HasSizedRuns;
            if (hasSizedRuns)
                line.Cache = null;

            for (int x = 0; x < _terminal.Cols;)
            {
                if (x >= line.Length)
                    break;

                if (hasSizedRuns && line.TryGetSizedRunAt(x, out var sizedRun) && sizedRun.Covers(x))
                {
                    _sizedBlockDraws.Add(new SizedBlockDraw(line, sizedRun, startYPos, rowHeight));
                    x = sizedRun.EndColumn;
                    continue;
                }

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
                        //
                        // An OSC 66 block is a boundary too, and nothing here would otherwise notice
                        // one. A fractional block is always s=1, so its cells are a single column wide
                        // and carry the SGR that was in force when it was printed; without this,
                        // preceding text with the same attributes swallows the whole run and draws it
                        // at base size, because the outer loop only looks for a run on the column it
                        // starts an iteration on.
                        if (currentCell.Width != 1 || currentCell.Attributes != cell.Attributes
                            || CoveredBySixel(line, x)
                            || (hasSizedRuns && line.TryGetSizedRunAt(x, out _)))
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
                var foreground = cell.GetForegroundBrush(_palette, this.Foreground, _boldIsBright);
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

                // SGR 8. The emulator has recorded it since the parser was written and nothing here
                // ever read it, so concealed text -- a password prompt that echoes, a spoiler --
                // was drawn in full.
                //
                // Applied AFTER the swaps above -- see ApplyConceal, where the ordering is the
                // substance of it rather than a detail.
                foreground = cell.ApplyConceal(foreground);

                var typeface = new Typeface(FontFamily, cell.GetFontStyle(), cell.GetFontWeight());
                var formattedText = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground);
                var td = cell.GetTextDecorations();
                if (td != null)
                    formattedText.SetTextDecorations(td);

                // Underlines are drawn by hand below rather than through TextDecorations, because
                // Avalonia has no curly decoration and SGR 58 gives the underline a colour of its own.
                var underlineStyle = cell.Attributes.GetUnderlineStyle();
                IBrush? underlineBrush = null;
                if (underlineStyle != XT.Common.UnderlineStyle.None)
                {
                    underlineBrush = cell.GetUnderlineColor(_palette) is { } uc
                        ? new ImmutableSolidColorBrush(uc)
                        : foreground;
                }

                var position = new Point(startX, startYPos);
                // A cell that carries no background of its own and was not swapped paints nothing, leaving
                // whatever the view is layered over to show through.
                var fill = swapped || cell.GetBackgroundColor(_palette).HasValue ? background : null;
                // Cache only content-dependent data, not screen position
                // Named rather than positional: the record gained Placement and Image ahead of these
                // when pictures moved onto lines, so position no longer says which is which.
                var run = new CachedTextRun(formattedText, runStartX, cellCount, fill,
                                            UnderlineStyle: underlineStyle, UnderlineBrush: underlineBrush);
                textRuns.Add(run);

                if (fill is not null)
                    context.FillRectangle(fill, rect);
                context.DrawText(formattedText, position);

                // Drawn on the BUILD path as well as the cached replay below. A line is painted here
                // on the frame it changes and replayed afterwards, so wiring only the replay leaves
                // every newly written underline missing until something else invalidates the line.
                if (underlineStyle != XT.Common.UnderlineStyle.None)
                    DrawUnderline(context, run, position, rect.Width, rowHeight);
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
                if (!hasSizedRuns)
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
        /// Draws a run's underline in whatever style it asked for.
        /// </summary>
        /// <remarks>
        /// <para>By hand rather than through Avalonia's TextDecorations, which have no curly form
        /// and no way to give the line SGR 58's colour of its own.</para>
        /// <para>Width and height arrive ALREADY SNAPPED, from the same rect every other draw in
        /// this file paints with -- so an underline's edges land exactly where its neighbours' do.
        /// Re-deriving the width from a raw cell width here put an antialiased seam at every
        /// attribute change inside an underlined span.</para>
        /// <para>The curly geometry and the pens are built once per cached run and replayed with
        /// it; only the rectangles are re-issued per frame, and those allocate nothing. The
        /// geometry is kept relative to the run's origin and translated into place, because the
        /// run's row changes as the screen scrolls while the shape does not.</para>
        /// </remarks>
        private static void DrawUnderline(DrawingContext context, CachedTextRun run, Point position,
                                          double width, double cellHeight)
        {
            var brush = run.UnderlineBrush;
            if (brush is null || width <= 0)
                return;

            var thickness = Math.Max(1.0, cellHeight / 14.0);
            var x = position.X;
            var baseY = position.Y + cellHeight - thickness * 2;

            switch (run.UnderlineStyle)
            {
                case XT.Common.UnderlineStyle.Double:
                    // The pair straddles where a single line would sit; a second line below would
                    // fall out of the cell.
                    context.FillRectangle(brush, new Rect(x, baseY - thickness, width, thickness));
                    context.FillRectangle(brush, new Rect(x, baseY + thickness, width, thickness));
                    break;

                case XT.Common.UnderlineStyle.Curly:
                {
                    // Centred ON baseY, amplitude chosen so a lobe plus half the pen's width ends
                    // exactly at the cell's bottom edge. The first version centred the wave lower
                    // and its lobes fell ~1.5 thicknesses out of the row -- chopped flat when the
                    // row below had its own background fill, bleeding into its glyphs when it did
                    // not: the same escape sequence rendered differently depending on the line
                    // under it.
                    var amplitude = thickness * 1.5;
                    var cellWidth = run.CellCount > 0 ? width / run.CellCount : width;
                    var period = Math.Max(4.0, cellWidth / 2.0);

                    var geometry = run.UnderlineGeometry;
                    if (geometry is null)
                    {
                        // One quadratic bezier per half-period lobe instead of eight line segments
                        // per period: smoother, and a quarter of the verbs. The sine's phase keeps
                        // ABSOLUTE x in its argument so two adjacent runs continue one wave
                        // instead of each restarting their own.
                        double Wave(double dx) => amplitude * Math.Sin((x + dx) / period * Math.PI * 2.0);

                        var half = period / 2.0;
                        var g = new StreamGeometry();
                        using (var ctx = g.Open())
                        {
                            ctx.BeginFigure(new Point(0, Wave(0)), false);

                            // First boundary of a whole lobe at or after the run's left edge.
                            var firstEdge = (Math.Floor(x / half) + 1) * half - x;
                            if (firstEdge > 0 && firstEdge < width)
                                ctx.QuadraticBezierTo(
                                    new Point(firstEdge / 2.0, Wave(0) + (Wave(firstEdge) - Wave(0)) / 2.0
                                              + LobeSign(x, half) * amplitude / 2.0),
                                    new Point(firstEdge, Wave(firstEdge)));

                            var dx = Math.Max(0.0, firstEdge);
                            while (dx + half <= width)
                            {
                                // A full lobe: endpoints on the axis, control at twice the peak.
                                ctx.QuadraticBezierTo(
                                    new Point(dx + half / 2.0, LobeSign(x + dx, half) * amplitude * 2.0),
                                    new Point(dx + half, 0));
                                dx += half;
                            }

                            if (dx < width)
                                ctx.QuadraticBezierTo(
                                    new Point(dx + (width - dx) / 2.0,
                                              LobeSign(x + dx, half) * amplitude / 2.0 + Wave(width) / 2.0),
                                    new Point(width, Wave(width)));

                            ctx.EndFigure(false);
                        }
                        geometry = g;
                        run.UnderlineGeometry = geometry;
                    }

                    var pen = run.UnderlinePen ??= new ImmutablePen(brush.ToImmutable(), thickness);
                    using (context.PushTransform(Matrix.CreateTranslation(x, baseY)))
                        context.DrawGeometry(null, pen, geometry);
                    break;
                }

                case XT.Common.UnderlineStyle.Dotted:
                case XT.Common.UnderlineStyle.Dashed:
                {
                    // What Pen.DashStyle is for: one line and the renderer draws the marks, in
                    // place of a FillRectangle per dot. Dash lengths are in pen-thickness units;
                    // the offset carries the phase-lock, so a run does not restart the pattern
                    // and stamp a mark at every attribute boundary.
                    if (run.UnderlinePen is not { } dashPen)
                    {
                        var pattern = run.UnderlineStyle == XT.Common.UnderlineStyle.Dotted
                            ? new[] { 1.0, 1.0 }
                            : new[] { 3.0, 2.0 };
                        var periodPx = (pattern[0] + pattern[1]) * thickness;
                        var offset = (x % periodPx) / thickness;
                        dashPen = new ImmutablePen(
                            brush.ToImmutable(), thickness,
                            new ImmutableDashStyle(pattern, offset));
                        run.UnderlinePen = dashPen;
                    }

                    var midY = baseY + thickness / 2.0;
                    context.DrawLine(dashPen, new Point(x, midY), new Point(x + width, midY));
                    break;
                }

                default:
                    context.FillRectangle(brush, new Rect(x, baseY, width, thickness));
                    break;
            }
        }

        /// <summary>Which way the sine lobe starting at this absolute position points.</summary>
        private static double LobeSign(double absoluteX, double halfPeriod)
            => Math.Floor(absoluteX / halfPeriod) % 2 == 0 ? 1.0 : -1.0;

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

                var cellScale = RowCellScale(segment.Line);
                var startX = Snap(segment.StartCol * _charWidth * cellScale, scale);
                var endX = Snap((segment.EndCol + 1) * _charWidth * cellScale, scale);
                var y = Snap((screenRow + 1) * _charHeight - 1, scale);

                pen ??= new Pen(Foreground, 1);
                context.DrawLine(pen, new Point(startX, y), new Point(endX, y));
            }
        }

        /// <summary>
        /// Draws every OSC 66 block the row pass recorded. Each cell with content inside a run is
        /// its own block (w=0 gives every grapheme one; a single w&gt;0 block is one wide anchor
        /// cell), drawn at <c>scale * n/d</c> times the base size inside a box of the cell's
        /// columns by the run's rows, aligned per the sizing's v and h — the parts of the
        /// protocol the emulator stores but only a renderer can honour.
        /// </summary>
        private void RenderSizedBlocks(DrawingContext context, double scale)
        {
            if (_sizedBlockDraws.Count == 0)
                return;

            // Clipped to the content area, because a sized block is deliberately BIGGER than the
            // cells it was placed in -- that is what OSC 66 is for. A block near the top row extends
            // above it and one on the last row extends below, and both were painted outside the
            // control entirely, over whatever the host had above or below the terminal.
            //
            // Around the whole pass rather than per block: they are drawn together, after every row,
            // so one clip covers all of them for one push.
            var content = new Rect(0, 0, _terminal.Cols * _charWidth, _terminal.Rows * _charHeight);

            using var clip = context.PushClip(content);

            foreach (var draw in _sizedBlockDraws)
            {
                var line = draw.Line;
                var run = draw.Run;
                var sizing = run.Sizing;

                var fraction = sizing.Numerator > 0 && sizing.Denominator > 0
                    ? sizing.Numerator / (double)sizing.Denominator
                    : 1.0;
                var magnify = sizing.Scale * fraction;
                if (magnify <= 0)
                    continue;

                for (int x = run.Column; x < run.EndColumn && x < line.Length;)
                {
                    var cell = line[x];
                    // Only a continuation cell is skipped outright -- it has no box of its own.
                    if (cell.Width <= 0)
                    {
                        x++;
                        continue;
                    }

                    var boxX = Snap(x * _charWidth, scale);
                    var boxRight = Snap((x + cell.Width) * _charWidth, scale);
                    var box = new Rect(boxX, draw.StartYPos,
                        Math.Max(0, boxRight - boxX), draw.RowHeight * run.Rows);

                    var background = cell.GetBackgroundBrush(_palette, this.Background);
                    var foreground = cell.GetForegroundBrush(_palette, this.Foreground, _boldIsBright);
                    // The same swap ladder the normal run path applies: inverse, DECSCNM, blink.
                    bool swapped = false;
                    if (cell.Attributes.IsInverse())
                        (foreground, background, swapped) = (background, foreground, !swapped);
                    if (_terminal.ReverseVideo)
                        (foreground, background, swapped) = (background, foreground, !swapped);
                    if (cell.Attributes.IsBlink() && this._cursorBlinkOn)
                        (foreground, background, swapped) = (background, foreground, !swapped);

                    // And here, the third place a cell's text is drawn. OSC 66 blocks shape their
                    // own runs too.
                    foreground = cell.ApplyConceal(foreground);

                    if (swapped || cell.GetBackgroundColor(_palette).HasValue)
                        context.FillRectangle(background, box);

                    // A blank cell has nothing to shape, but its background belongs to the block and is
                    // already down. This pass is the ONLY thing that paints inside the run -- the row
                    // pass skipped every column of it -- so skipping a space before the fill punched an
                    // unpainted notch between the words of a coloured heading.
                    if (string.IsNullOrEmpty(cell.Content) || cell.Content == " ")
                    {
                        x += cell.Width;
                        continue;
                    }

                    var typeface = new Typeface(FontFamily, cell.GetFontStyle(), cell.GetFontWeight());
                    var formatted = new FormattedText(
                        cell.Content, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        typeface, FontSize, foreground);

                    // The glyph is drawn at base size under a scale transform, exactly as
                    // DECDWL/DECDHL lines are — the transform is what makes it big, so hinting,
                    // fallback and shaping all behave as they do everywhere else.
                    var drawnWidth = formatted.Width * magnify;
                    var drawnHeight = draw.RowHeight * magnify;

                    var alignX = sizing.HorizontalAlignment switch
                    {
                        XT.Common.TextSizeHorizontalAlignment.Right => box.Right - drawnWidth,
                        XT.Common.TextSizeHorizontalAlignment.Center => box.X + (box.Width - drawnWidth) / 2,
                        _ => box.X,
                    };
                    var alignY = sizing.VerticalAlignment switch
                    {
                        XT.Common.TextSizeVerticalAlignment.Bottom => box.Bottom - drawnHeight,
                        XT.Common.TextSizeVerticalAlignment.Center => box.Y + (box.Height - drawnHeight) / 2,
                        _ => box.Y,
                    };

                    using (context.PushClip(box))
                    {
                        var toOrigin = Matrix.CreateTranslation(-alignX, -alignY);
                        var grow = Matrix.CreateScale(magnify, magnify);
                        var back = Matrix.CreateTranslation(alignX, alignY);
                        using (context.PushTransform(toOrigin * grow * back))
                        {
                            context.DrawText(formatted, new Point(alignX, alignY));
                        }
                    }

                    x += cell.Width;
                }
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
                        var foreground = cell.GetForegroundBrush(_palette, this.Foreground, _boldIsBright);
                        // Apply cell-level inverse attribute
                        bool swapped = false;
                        if (cell.Attributes.IsInverse())
                            (foreground, background, swapped) = (background, foreground, !swapped);
                        // Apply terminal-wide reverse video mode (DECSCNM)
                        if (_terminal.ReverseVideo)
                            (foreground, background, swapped) = (background, foreground, !swapped);
                        if (cell.Attributes.IsBlink() && this._cursorBlinkOn)
                            (foreground, background, swapped) = (background, foreground, !swapped);

                        // After the swaps, as everywhere else. A DECDWL/DECDHL row draws its own
                        // text rather than going through the run path, so conceal has to be applied
                        // here too -- otherwise a concealed password shows in full on any line a
                        // program happened to double.
                        foreground = cell.ApplyConceal(foreground);

                        var typeface = new Typeface(FontFamily, cell.GetFontStyle(), cell.GetFontWeight());
                        var formattedText = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground);
                        var td = cell.GetTextDecorations();
                        if (td != null)
                            formattedText.SetTextDecorations(td);

                        var position = new Point(startX, startYPos);

                        if (swapped || cell.GetBackgroundColor(_palette).HasValue)
                            context.FillRectangle(background, rect);
                        context.DrawText(formattedText, position);

                        // Underlines are drawn by hand everywhere a cell is painted, and this loop
                        // is one of those places: leaving it out silently un-underlined every
                        // DECDWL/DECDHL line, plain SGR 4 included. Untransformed geometry on
                        // purpose -- the matrix pushed above doubles it along with the glyphs. The
                        // run is per-frame because double-width lines are never cached, so the
                        // geometry cache dies with it; these lines are rare enough not to matter.
                        var dwUnderline = cell.Attributes.GetUnderlineStyle();
                        if (dwUnderline != XT.Common.UnderlineStyle.None)
                        {
                            var dwBrush = cell.GetUnderlineColor(_palette) is { } uc
                                ? new ImmutableSolidColorBrush(uc)
                                : foreground;
                            var dwRun = new CachedTextRun(null, runStartX, cellCount, null,
                                                          UnderlineStyle: dwUnderline, UnderlineBrush: dwBrush);
                            DrawUnderline(context, dwRun, position, Math.Max(0, endX - startX), rowHeight);
                        }
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

            // The selection API takes a row relative to the LIVE scroll position -- it adds YDisp
            // itself -- while the frame around this was composed against the viewportY the caller
            // snapshotted. Output arriving mid-frame moves YDisp, and the highlight was then drawn
            // over rows the text underneath it no longer occupied: a band of selection sitting one or
            // two lines away from the selected words.
            //
            // Shifting by the difference asks about the snapshot's rows in the API's own terms.
            int toLiveRows = viewportY - _terminal.Buffer.YDisp;

            for (int screenY = 0; screenY < viewportLines; screenY++)
            {
                // Find cells that are selected in this row
                int? selectionStartX = null;
                int? selectionEndX = null;

                for (int x = 0; x < _terminal.Cols; x++)
                {
                    if (_terminal.Selection.IsCellSelected(x, screenY + toLiveRows))
                    {
                        if (!selectionStartX.HasValue)
                            selectionStartX = x;
                        selectionEndX = x;
                    }
                    else if (selectionStartX.HasValue)
                    {
                        // End of a selection run - draw it
                        DrawSelectionRect(context, selectionStartX.Value, selectionEndX!.Value + 1, screenY, scale,
                                          RowCellScale(viewportY + screenY));
                        selectionStartX = null;
                        selectionEndX = null;
                    }
                }

                // Draw remaining selection at end of row
                if (selectionStartX.HasValue)
                {
                    DrawSelectionRect(context, selectionStartX.Value, selectionEndX!.Value + 1, screenY, scale,
                                      RowCellScale(viewportY + screenY));
                }
            }
        }

        /// <summary>How many columns the cell at this position occupies; 1 when there is nothing there.</summary>
        private int CellWidthAt(int absoluteRow, int col)
        {
            var line = _terminal.Buffer.GetLine(absoluteRow);
            if (line == null || col < 0 || col >= line.Length)
                return 1;

            // GetWidth rather than line[col].Width: the indexer hands back a copy of the whole cell
            // to read one field, which this file has paid for before.
            return Math.Max(1, line.GetWidth(col));
        }

        /// <summary>
        /// How many normal cell widths one cell on <paramref name="absoluteRow"/> actually occupies
        /// on screen: 2 on a DECDWL or DECDHL row, 1 everywhere else.
        /// </summary>
        /// <remarks>
        /// <para>The row pass draws doubled rows inside a 2x transform, so the cells themselves come
        /// out right. Everything drawn as an OVERLAY -- the cursor, the selection, the hovered-link
        /// underline -- is drawn afterwards, outside that transform, and so has to double its own
        /// geometry. None of it did.</para>
        /// <para>The result is visible rather than subtle: on a doubled row the selection covered the
        /// left half of what was selected, the link underline stopped halfway along the link, and the
        /// cursor sat at half the column it marked -- so at column 40 it was twenty cells to the left
        /// of its own character.</para>
        /// </remarks>
        private double RowCellScale(int absoluteRow)
        {
            var line = _terminal.Buffer.GetLine(absoluteRow);
            if (line == null)
                return 1.0;

            var attr = line.LineAttribute;
            return attr == LineAttribute.DoubleWidth || attr.IsDoubleHeight() ? 2.0 : 1.0;
        }

        private void DrawSelectionRect(DrawingContext context, int startX, int endX, int screenY, double scale,
                                       double cellScale)
        {
            var x1 = Snap(startX * _charWidth * cellScale, scale);
            var x2 = Snap(endX * _charWidth * cellScale, scale);
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

            // A doubled row draws each cell twice as wide, and this pass runs outside the transform
            // that does it -- so the caret has to double its own geometry or it lands half a screen
            // to the left of the character it marks.
            double cellScale = RowCellScale(absoluteCursorY);

            // And a WIDE character occupies two cells. A block caret one cell wide over a two-cell
            // glyph repainted the whole glyph in the background colour and then filled only its left
            // half, so the right half of the character was simply erased.
            int cursorCells = Math.Max(1, CellWidthAt(absoluteCursorY, cursorX));

            double posX = Snap(cursorX * _charWidth * cellScale, scale);
            double posY = Snap(screenY * _charHeight, scale);
            double nextX = Snap((cursorX + cursorCells) * _charWidth * cellScale, scale);
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
            var cellScale = RowCellScale(absoluteCursorY);
            double posX = Snap(cursorX * _charWidth * cellScale, scale);
            double posY = Snap(screenY * _charHeight, scale);
            double cellHeight = Snap((screenY + 1) * _charHeight, scale) - posY;

            var typeface = new Typeface(FontFamily, FontStyle, FontWeight);
            var foreground = GetValue(ForegroundProperty) ?? Brushes.White;

            // The same rule the cell renderer follows, which is not "always the palette": a default
            // background resolves to the emulator's colour only when the host's own brush is fully
            // opaque, and otherwise stays the host's. A translucent or gradient host is asking to be
            // seen through, and no RGB palette entry can express that.
            //
            // Claiming to follow that rule and then using the palette unconditionally was worse than
            // either choice on its own: it made the composition box the one opaque rectangle on an
            // otherwise see-through terminal. Through the same IsFullyOpaque the cell path uses, so
            // there is one rule rather than a copy of it here.
            var styled = GetValue(BackgroundProperty) ?? Brushes.Black;
            var background = BufferCellExtensions.IsFullyOpaque(styled)
                ? new SolidColorBrush(BufferCellExtensions.FromRgb(_palette.Background))
                : styled;

            var formattedText = new FormattedText(
                preeditText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                foreground);

            // Bounded by the right edge, and CLIPPED to it. A composition is as long as the user
            // makes it -- an IME buffers a whole phrase before committing -- and this drew it at its
            // full measured width from the cursor, so a long one ran off the end of the control and
            // painted over whatever the host had beside the terminal.
            //
            // The SCALED width is what gets bounded: on a doubled row the composition is drawn twice
            // as wide, so bounding its unscaled measurement would let the drawn glyphs run past an
            // edge the box stopped at.
            double textWidth = FitWidth(posX, formattedText.Width * cellScale, _terminal.Cols * _charWidth);
            if (textWidth <= 0)
                return;

            using (context.PushClip(new Rect(posX, posY, textWidth, cellHeight)))
            {
                // Draw background behind preedit text to cover existing content
                context.FillRectangle(background, new Rect(posX, posY, textWidth, cellHeight));

                // Draw the preedit text
                if (cellScale == 1.0)
                {
                    context.DrawText(formattedText, new Point(posX, posY));
                }
                else
                {
                    // The row's text is drawn under the same horizontal scale. This overlay is
                    // outside that row transform, so apply it around the preedit origin as well;
                    // scaling only the background geometry would leave the composing glyphs at half
                    // width. Inside the clip, so a doubled composition is still bounded by the edge.
                    var toOrigin = Matrix.CreateTranslation(-posX, -posY);
                    var widen = Matrix.CreateScale(cellScale, 1.0);
                    var back = Matrix.CreateTranslation(posX, posY);
                    using (context.PushTransform(toOrigin * widen * back))
                        context.DrawText(formattedText, new Point(posX, posY));
                }

                // Draw underline to indicate uncommitted composition text
                double underlineY = posY + cellHeight - Math.Max(1.0, scale);
                var pen = new Pen(foreground, Math.Max(1.0, scale));
                context.DrawLine(pen, new Point(posX, underlineY), new Point(posX + textWidth, underlineY));
            }
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

            return Win32Record(vk, scanCode, unicodeChar, isKeyDown, controlKeyState);
        }

        /// <summary>
        /// One Win32 INPUT_RECORD, in the wire form ConPTY reads.
        /// </summary>
        /// <remarks>
        /// Split out from <see cref="GenerateWin32InputSequence"/> so a SYNTHETIC key — one this view
        /// presses on the user's behalf, with no <see cref="KeyEventArgs"/> behind it — is encoded by
        /// the same formatting as a real one rather than a second copy of the format string that can
        /// drift from it. See <see cref="EncodeSyntheticKey"/>.
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

                        // The gutter, the third place the offset is needed. This rectangle is in the
                        // control's space, which is the space the render translates the grid within --
                        // without it the composition window sits GutterWidth px to the left of the
                        // caret it is meant to be under, for the whole session. Clamped the way
                        // PointerColumn and ArrangeOverride clamp it.
                        double posX = cursorX * _view._charWidth + Math.Max(0, _view.GutterWidth);
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
