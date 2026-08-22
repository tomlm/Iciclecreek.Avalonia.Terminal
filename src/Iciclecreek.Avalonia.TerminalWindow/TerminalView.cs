using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
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

        // IME (Input Method Editor) support
        private TerminalInputMethodClient? _inputMethodClient;

        // Unique identifier for this terminal instance (for debugging)
        private readonly Guid _instanceId = Guid.NewGuid();

        // When true, OnDetachedFromLogicalTree skips CleanupProcess so the PTY
        // survives a visual-tree re-parent (e.g. floating window pop-out/dock-back).
        private bool _suppressCleanupOnDetach;

        private sealed record CachedTextRun(FormattedText Text, int StartX, int CellCount, IBrush Background);

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
        public static readonly FontFamily DefaultFontFamily = new FontFamily(
            "Cascadia Mono,Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,Liberation Mono,Courier New,monospace");

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
        /// <para><c>TERM</c> does not need setting here: the PTY layer already gives the child
        /// <c>TERM=xterm-256color</c> on both Windows and Unix.</para>
        /// </remarks>
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
                CursorBlinkProperty);

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

            _terminal = new XT.Terminal(options);

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

            // Initialize IME client
            _inputMethodClient = new TerminalInputMethodClient(this);
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
                this.RequestInvalidate();
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
                    this.RequestInvalidate();
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

                await SendToPtyAsync(text);
            }
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

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            // _terminal and _cursorBlinkTimer are built in OnInitialized, and a cursor property can arrive
            // before that: the control template applies its bindings while the view is still initialising.
            // Nothing set these that early until TerminalControl began forwarding them, at which point this
            // threw a NullReferenceException from inside Avalonia's property machinery. Skipping here loses
            // nothing, because OnInitialized reads the current values when it builds the emulator.
            if (_terminal == null || _cursorBlinkTimer == null)
                return;

            if (change.Property == CursorStyleProperty)
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
        }

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            _cursorBlinkTimer.Stop();
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

            _terminal.DataReceived -= OnTerminalDataReceived;
            _terminal.BufferChanged -= OnTerminalBufferChanged;
            _terminal.CursorStyleChanged -= OnTerminalCursorStyleChanged;
            _terminal.TitleChanged -= OnTerminalTitleChanged;
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
            _terminal.DataReceived -= OnTerminalDataReceived;
            _terminal.BufferChanged -= OnTerminalBufferChanged;
            _terminal.CursorStyleChanged -= OnTerminalCursorStyleChanged;
            _terminal.TitleChanged -= OnTerminalTitleChanged;
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

                this.RequestInvalidate();
            }
        }

        // macOS uses the Command (⌘ / Meta) key for clipboard shortcuts, following native
        // platform conventions (Terminal.app, iTerm2, etc.). Windows and Linux terminals use
        // Ctrl+Shift+C / Ctrl+Shift+V instead, because plain Ctrl+C is reserved for SIGINT.
        private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        // True when the key is a modifier pressed on its own (no associated character),
        // e.g. the ⌘/Ctrl/Shift/Alt keys. Used so a bare modifier press doesn't clear
        // an active selection before the rest of a copy shortcut is typed.
        private static bool IsModifierKey(Key key) => key switch
        {
            Key.LeftShift or Key.RightShift or
            Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or
            Key.LWin or Key.RWin => true,
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
            if (_processExitHandled != 0)
            {
                bool isCopy = e.Key == Key.C &&
                              (e.KeyModifiers == KeyModifiers.Control ||
                               e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) ||
                               (IsMacOS && e.KeyModifiers == KeyModifiers.Meta));
                if (isCopy && _terminal.Selection.HasSelection)
                {
                    e.Handled = true;
                    await CopyAsync();
                    _terminal.Selection.ClearSelection();
                    this.RequestInvalidate();
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
                if (IsMacOS && e.KeyModifiers == KeyModifiers.Meta)
                {
                    // Cmd+C - copy the selection (no-op when nothing is selected, matching macOS)
                    if (e.Key == Key.C)
                    {
                        e.Handled = true;
                        if (_terminal.Selection.HasSelection)
                        {
                            await CopyAsync();
                            _terminal.Selection.ClearSelection();
                            this.RequestInvalidate();
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

                // Handle Ctrl+C - copy if there's a selection, otherwise send SIGINT
                if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
                {
                    if (_terminal.Selection.HasSelection)
                    {
                        e.Handled = true;
                        await CopyAsync();
                        _terminal.Selection.ClearSelection();
                        this.RequestInvalidate();
                        return;
                    }
                    // No selection - fall through to send Ctrl+C (SIGINT) to the process
                }

                // Handle Ctrl+Shift+C for copy (always copies, doesn't send SIGINT)
                if (e.Key == Key.C && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
                {
                    if (_terminal.Selection.HasSelection)
                    {
                        e.Handled = true;
                        await CopyAsync();
                        _terminal.Selection.ClearSelection();
                        this.RequestInvalidate();
                        return;
                    }
                }

                // Clear selection for any other keystroke - but ignore bare modifier
                // presses. Pressing ⌘/Ctrl/Shift on its own fires a KeyDown before the
                // shortcut's letter arrives; clearing here would lose the selection
                // before Cmd+C / Ctrl+Shift+C could copy it.
                if (_terminal.Selection.HasSelection && !IsModifierKey(e.Key))
                {
                    _terminal.Selection.ClearSelection();
                    this.RequestInvalidate();
                }

                // Handle Ctrl+Shift+V for paste (standard terminal shortcut)
                // Ctrl+V is NOT intercepted - it gets passed to the application
                // (some apps use Ctrl+V for literal character input mode)
                if (e.Key == Key.V && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
                {
                    e.Handled = true;
                    await PasteAsync();
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
                    await SendToPtyAsync(printableChar.ToString()).ConfigureAwait(false);
                    return;
                }

                // If we couldn't handle it, let TextInput try (for desktop Avalonia)
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_instanceId}] Error handling key input: {ex.Message}");
            }
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

            // Clear selection when text is being input
            if (_terminal.Selection.HasSelection)
            {
                _terminal.Selection.ClearSelection();
                this.RequestInvalidate();
            }

            try
            {
                Debug.WriteLine($"[TerminalView] OnTextInput: Sending '{e.Text}' to PTY");
                await SendToPtyAsync(e.Text).ConfigureAwait(false);
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
                            this.RequestInvalidate();
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
                        this.RequestInvalidate();
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
                        this.RequestInvalidate();
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
                    this.RequestInvalidate();
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

            this.RequestInvalidate();
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

            this.RequestInvalidate();
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
                this.RequestInvalidate();
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

                this.RequestInvalidate();
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

        private void OnTerminalWindowInfoRequested(object? sender, XT.Events.TerminalEvents.WindowInfoRequestedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
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

        private async Task SendToPtyAsync(string data, CancellationToken ct = default)
        {
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
            this.RequestInvalidate();
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
            this.RequestInvalidate();
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
            _ = Task.Run(() => ReadPtyOutputAsync(connection, _processCts.Token), _processCts.Token);
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

                SetAndRaise(CurrentDirectoryProperty, ref _currentDirectory, StartingDirectory ?? Environment.CurrentDirectory);

                var options = new PtyOptions
                {
                    Name = processToLaunch,
                    Cols = _terminal.Cols,
                    Rows = _terminal.Rows,
                    Cwd = _currentDirectory,
                    App = processToLaunch,
                    VerbatimCommandLine = VerbatimCommandLine
                };

                // Merged by the PTY layer into the environment the child would otherwise inherit, so a caller
                // adding one variable does not have to rebuild the rest. Left unset when null so the launch
                // path is byte-for-byte what it was before this existed.
                if (EnvironmentVariables != null)
                {
                    options.Environment = EnvironmentVariables;
                }


                // Add arguments if provided
                if (Args != null && Args.Count > 0)
                {
                    options.CommandLine = Args.ToArray();
                }

                var spawned = await PtyProvider.SpawnAsync(options, _processCts.Token);
                InstallConnection(spawned);

                // Subscribe to process exit event for reliable exit detection
                spawned.ProcessExited += OnPtyProcessExited;

                // Start reading from the PTY connection. The loop is handed THIS connection so a
                // relaunch cannot redirect it onto the next one — see ReadPtyOutputAsync.
                _ = Task.Run(async () => await ReadPtyOutputAsync(spawned, _processCts.Token), _processCts.Token);
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
        private async Task ReadPtyOutputAsync(IPtyConnection connection, CancellationToken cancellationToken)
        {
            try
            {
                var buffer = new byte[0x40000];

                // Local rather than a field: this method runs once per launch, so the flag is
                // per-process by construction and a chunk still in flight from a previous process
                // cannot consume the current one's signal.
                var shellReadyPosted = false;

                while (!cancellationToken.IsCancellationRequested && ReferenceEquals(_ptyConnection, connection))
                {
                    var bytesRead = await connection.ReaderStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
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

                            lock (_terminalLock)
                            {
                                _terminal.WriteLine($"\nProcess exited with code: {exitCode}\n");
                                _terminal.Buffer.ScrollToBottom();
                            }

                            this.RequestInvalidate();
                            
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

                    lock (_terminalLock)
                    {
                        _terminal.Write(output);
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
                        _terminal.Buffer.ScrollToBottom();
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

                    this.RequestInvalidate();
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

                lock (_terminalLock)
                {
                    _terminal.WriteLine($"\nError reading from process: {ex.Message}\n");
                    _terminal.Buffer.ScrollToBottom();
                }

                this.RequestInvalidate();
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

                lock (_terminalLock)
                {
                    _terminal.WriteLine(code is { } c
                        ? $"\nProcess exited with code: {c}\n"
                        : "\nProcess exited\n");
                    _terminal.Buffer.ScrollToBottom();
                }
                this.RequestInvalidate();

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

            lock (_terminalLock)
            {
                _terminal.WriteLine($"\nProcess exited with code: {e.ExitCode}\n");
                _terminal.Buffer.ScrollToBottom();
            }
            this.RequestInvalidate();

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

                    context.FillRectangle(run.Background, rect);
                    context.DrawText(run.Text, position);
                }
                return;
            }

            // Build and cache text runs for this line
            textRuns = new List<CachedTextRun>();

            for (int x = 0; x < _terminal.Cols;)
            {
                if (x >= line.Length)
                    break;
                var cell = line[x];
                string text = String.Empty;
                int cellCount = 0;
                int runStartX = 0;

                // Skip placeholder cells (width 0) that follow wide characters
                if (cell.Width == 0)
                {
                    Debug.Assert(cell.Content == BufferCell.Empty.Content, "Placeholder cell should be null content");
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

                        // Stop if we hit a different attribute or a placeholder cell mid-run
                        if (currentCell.Width != 1 || currentCell.Attributes != cell.Attributes)
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

                var startX = Snap(runStartX * _charWidth, scale);
                var endX = Snap((runStartX + cellCount) * _charWidth, scale);
                var rect = new Rect(startX, startYPos, Math.Max(0, endX - startX), rowHeight);
                var background = cell.GetBackgroundBrush(this.Background);
                var foreground = cell.GetForegroundBrush(this.Foreground);
                // Apply cell-level inverse attribute
                if (cell.Attributes.IsInverse())
                    (foreground, background) = (background, foreground);
                // Apply terminal-wide reverse video mode (DECSCNM)
                if (_terminal.ReverseVideo)
                    (foreground, background) = (background, foreground);
                if (cell.Attributes.IsBlink() && this._cursorBlinkOn)
                    (foreground, background) = (background, foreground);

                var typeface = new Typeface(FontFamily, cell.GetFontStyle(), cell.GetFontWeight());
                var formattedText = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground);
                var td = cell.GetTextDecorations();
                if (td != null)
                    formattedText.SetTextDecorations(td);

                var position = new Point(startX, startYPos);
                // Cache only content-dependent data, not screen position
                textRuns.Add(new CachedTextRun(formattedText, runStartX, cellCount, background));

                context.FillRectangle(background, rect);
                context.DrawText(formattedText, position);
            }

            // Cache the text runs (but not when ReverseVideo mode is active)
            if (!_terminal.ReverseVideo)
                line.Cache = textRuns;
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
                        var background = cell.GetBackgroundBrush(this.Background);
                        var foreground = cell.GetForegroundBrush(this.Foreground);
                        // Apply cell-level inverse attribute
                        if (cell.Attributes.IsInverse())
                            (foreground, background) = (background, foreground);
                        // Apply terminal-wide reverse video mode (DECSCNM)
                        if (_terminal.ReverseVideo)
                            (foreground, background) = (background, foreground);
                        if (cell.Attributes.IsBlink() && this._cursorBlinkOn)
                            (foreground, background) = (background, foreground);

                        var typeface = new Typeface(FontFamily, cell.GetFontStyle(), cell.GetFontWeight());
                        var formattedText = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground);
                        var td = cell.GetTextDecorations();
                        if (td != null)
                            formattedText.SetTextDecorations(td);

                        var position = new Point(startX, startYPos);

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

        private void RenderCursor(DrawingContext context, int viewportY, double scale)
        {
            // Only show cursor if terminal wants it visible (controlled by escape sequences)
            if (!_terminal.CursorVisible)
                return;

            // Only show cursor if in "on" phase of blink cycle (or not blinking)
            if (!_cursorBlinkOn)
                return;

            // Get cursor position relative to viewport
            int cursorX = _terminal.Buffer.X;
            int cursorY = _terminal.Buffer.Y;

            // The cursor Y is relative to the active screen area, need to check if it's visible
            // when scrolled. Cursor is at absolute position: Buffer.YBase + Buffer.Y
            int absoluteCursorY = _terminal.Buffer.YBase + cursorY;

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
                            var invertedBrush = cell.GetBackgroundBrush(this.Background);
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
