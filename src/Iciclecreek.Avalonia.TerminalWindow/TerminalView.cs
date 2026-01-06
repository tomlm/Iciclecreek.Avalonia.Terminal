using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
using System.Threading;
using System.Threading.Tasks;
using XTerm.Buffer;
using XT = global::XTerm;

namespace Iciclecreek.Terminal
{

    public class TerminalView : Control
    {
        private XT.Terminal _terminal;
        private FormattedText _measureText;
        private double _charWidth;
        private double _charHeight;
        private int _bufferSize = 1000;
        private bool _isAlternateBuffer;

        // Process management
        private IPtyConnection? _ptyConnection;
        private CancellationTokenSource? _processCts;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private bool _processExitHandled;  // Prevent double notification

        // Cursor blinking
        private DispatcherTimer _cursorBlinkTimer;
        private bool _cursorBlinkOn = true;

        // Unique identifier for this terminal instance (for debugging)
        private readonly Guid _instanceId = Guid.NewGuid();

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

        public static readonly StyledProperty<FontFamily> FontFamilyProperty =
            AvaloniaProperty.Register<TerminalView, FontFamily>(
                nameof(FontFamily),
                defaultValue: FontFamily.Default);

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

        public static readonly StyledProperty<XTerm.Options.TerminalOptions?> OptionsProperty =
            AvaloniaProperty.Register<TerminalControl, XTerm.Options.TerminalOptions?>(
                nameof(Options),
                defaultValue: null);

        #region Terminal Attached Events

        public static readonly RoutedEvent<ProcessExitedEventArgs> ProcessExitedEvent =
            RoutedEvent.Register<TerminalView, ProcessExitedEventArgs>(
                nameof(ProcessExited),
                RoutingStrategies.Bubble);

        public static void AddProcessExitedHandler(Interactive target, EventHandler<ProcessExitedEventArgs> handler) =>
            target.AddHandler(ProcessExitedEvent, handler);

        public static void RemoveProcessExitedHandler(Interactive target, EventHandler<ProcessExitedEventArgs> handler) =>
            target.RemoveHandler(ProcessExitedEvent, handler);

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
        /// Event raised when the PTY process exits.
        /// </summary>
        public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

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

        public TerminalView()
        {
            Focusable = true;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        protected override void OnInitialized()
        {
            // Sync terminal options with styled properties
            var options = Options ?? new XT.Options.TerminalOptions();

            options.CursorStyle = CursorStyle;
            options.CursorBlink = CursorBlink;
            options.CursorBlinkRate = CursorBlinkRate;

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
            // end window events

            // Setup cursor blink timer
            _cursorBlinkTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(CursorBlinkRate)
            };
            _cursorBlinkTimer.Tick += OnCursorBlinkTick;
        }

        public bool IsAlternateBuffer => _isAlternateBuffer;

        public int BufferSize
        {
            get => _bufferSize;
            set
            {
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

        public void WaitForExit(int ms) => _ptyConnection!.WaitForExit(ms);

        public void Kill() => _ptyConnection!.Kill();

        public int ExitCode => _ptyConnection!.ExitCode;

        public int Pid => _ptyConnection!.Pid;

        public FontFamily FontFamily
        {
            get => GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public double FontSize
        {
            get => GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public FontStyle FontStyle
        {
            get => GetValue(FontStyleProperty);
            set => SetValue(FontStyleProperty, value);
        }

        public FontWeight FontWeight
        {
            get => GetValue(FontWeightProperty);
            set => SetValue(FontWeightProperty, value);
        }

        public TextDecorationLocation? TextDecorations
        {
            get => GetValue(TextDecorationsProperty);
            set => SetValue(TextDecorationsProperty, value);
        }

        public IBrush Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public IBrush Background
        {
            get => GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public IBrush SelectionBrush
        {
            get => GetValue(SelectionBrushProperty);
            set => SetValue(SelectionBrushProperty, value);
        }

        public string Process
        {
            get => GetValue(ProcessProperty);
            set => SetValue(ProcessProperty, value);
        }

        public IList<string> Args
        {
            get => GetValue(ArgsProperty);
            set => SetValue(ArgsProperty, value);
        }

        public Color CursorColor
        {
            get => GetValue(CursorColorProperty);
            set => SetValue(CursorColorProperty, value);
        }

        public XT.Common.CursorStyle CursorStyle
        {
            get => GetValue(CursorStyleProperty);
            set => SetValue(CursorStyleProperty, value);
        }

        public bool CursorBlink
        {
            get => GetValue(CursorBlinkProperty);
            set => SetValue(CursorBlinkProperty, value);
        }

        public int CursorBlinkRate
        {
            get => GetValue(CursorBlinkRateProperty);
            set => SetValue(CursorBlinkRateProperty, value);
        }

        public XTerm.Options.TerminalOptions? Options
        {
            get => GetValue(OptionsProperty);
            set => SetValue(OptionsProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

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
            else if (change.Property == CursorBlinkRateProperty)
            {
                var rate = (int)change.NewValue!;
                _terminal.Options.CursorBlinkRate = rate;
                _cursorBlinkTimer.Interval = TimeSpan.FromMilliseconds(rate > 0 ? rate : 530);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(Process))
            {
                LaunchProcess();
            }

            // Start cursor blinking if enabled
            if (CursorBlink)
            {
                _cursorBlinkTimer.Start();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _cursorBlinkTimer.Stop();
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
            _terminal.WindowInfoRequested -= OnTerminalWindowInfoRequested;
            CleanupProcess();
        }

        private void OnCursorBlinkTick(object? sender, EventArgs e)
        {
            if (CursorBlink && IsFocused)
            {
                _cursorBlinkOn = !_cursorBlinkOn;
                for (int y = 0; y < _terminal.Rows; y++)
                {
                    var line = _terminal.Buffer.GetLine(y);
                    if (line.Any(cell => cell.Attributes.IsBlink()))
                    {
                        line.Cache = null;
                    }
                }

                this.RequestInvalidate();
            }
        }

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

            try
            {
                var modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);
                var hasAlt = (modifiers & XT.Input.KeyModifiers.Alt) != 0;

                // Check if Win32 Input Mode is enabled
                if (_terminal.Win32InputMode)
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
            if (ptyConnection == null)
            {
                base.OnKeyUp(e);
                return;
            }

            try
            {
                // Only send KeyUp events in Win32 Input Mode
                if (_terminal.Win32InputMode)
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
            if (ptyConnection == null || string.IsNullOrEmpty(e.Text))
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

            if (_ptyConnection == null)
                return;

            try
            {
                var point = e.GetPosition(this);
                var col = (int)(point.X / _charWidth);
                var row = (int)(point.Y / _charHeight);

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

            if (_ptyConnection == null)
                return;

            try
            {
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

            if (_ptyConnection == null)
                return;

            try
            {
                var point = e.GetPosition(this);
                var col = (int)(point.X / _charWidth);
                var row = (int)(point.Y / _charHeight);
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

        protected override async void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            // Number of lines to scroll per wheel notch
            const int scrollLines = 3;

            // Delta.Y is positive when scrolling up (towards user), negative when scrolling down
            var delta = e.Delta.Y;

            if (_ptyConnection != null && _terminal.MouseTrackingMode != XT.Input.MouseTrackingMode.None)
            {
                var point = e.GetPosition(this);
                var col = (int)(point.X / _charWidth);
                var row = (int)(point.Y / _charHeight);
                var modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);

                var button = delta > 0 ? XT.Input.MouseButton.WheelUp : XT.Input.MouseButton.WheelDown;
                var eventType = delta > 0 ? XT.Input.MouseEventType.WheelUp : XT.Input.MouseEventType.WheelDown;

                var sequence = _terminal.GenerateMouseEvent(button, col, row, eventType, modifiers);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence).ConfigureAwait(false);
                    e.Handled = true;
                    return;
                }
            }

            if (delta != 0)
            {
                // Scroll up (negative delta to ViewportY) when wheel scrolls up (positive delta)
                // Scroll down (positive delta to ViewportY) when wheel scrolls down (negative delta)
                int linesToScroll = (int)(-delta * scrollLines);

                // Calculate new viewport position
                int newViewportY = Math.Clamp(
                    ViewportY + linesToScroll,
                    0,
                    MaxScrollback);

                if (newViewportY != ViewportY)
                {
                    ViewportY = newViewportY;
                }

                e.Handled = true;
            }
        }

        protected override async void OnGotFocus(GotFocusEventArgs e)
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

        protected override async void OnLostFocus(RoutedEventArgs e)
        {
            base.OnLostFocus(e);

            Debug.WriteLine($"[TerminalView] OnLostFocus");

            // Stop blinking when not focused, but keep cursor visible (hollow block)
            _cursorBlinkTimer.Stop();
            _cursorBlinkOn = true;

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

        private async void LaunchProcess()
        {
            CleanupProcess();

            try
            {
                _processCts = new CancellationTokenSource();
                _processExitHandled = false;  // Reset flag for new process

                // Determine the process to launch based on OS if not explicitly set
                string processToLaunch = Process;
                if (string.IsNullOrEmpty(processToLaunch))
                {
                    processToLaunch = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash";
                }

                var options = new PtyOptions
                {
                    Name = processToLaunch,
                    Cols = _terminal.Cols,
                    Rows = _terminal.Rows,
                    Cwd = Environment.CurrentDirectory,
                    App = processToLaunch
                };

                // Add arguments if provided
                if (Args != null && Args.Count > 0)
                {
                    options.CommandLine = Args.ToArray();
                }

                _ptyConnection = await PtyProvider.SpawnAsync(options, _processCts.Token);

                // Subscribe to process exit event for reliable exit detection
                _ptyConnection.ProcessExited += OnPtyProcessExited;

                // Start reading from the PTY connection
                _ = Task.Run(async () => await ReadPtyOutputAsync(_processCts.Token), _processCts.Token);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _terminal.WriteLine($"Error launching process: {ex.Message}\n");
                });
            }
        }

        private async Task ReadPtyOutputAsync(CancellationToken cancellationToken)
        {
            try
            {
                var buffer = new byte[0x40000];
                while (!cancellationToken.IsCancellationRequested && _ptyConnection != null)
                {
                    var bytesRead = await _ptyConnection.ReaderStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0)
                    {
                        // Process has exited - the ProcessExited event handler will handle notification
                        // This is just a fallback in case the event doesn't fire
                        if (!_processExitHandled)
                        {
                            _processExitHandled = true;
                            var exitCode = _ptyConnection?.ExitCode ?? 0;

                            _terminal.WriteLine($"\nProcess exited with code: {exitCode}\n");
                            _terminal.Buffer.ScrollToBottom();

                            this.RequestInvalidate();
                        }
                        break;
                    }

                    var output = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    _terminal.Write(output);

                    // Auto-scroll to bottom when new content arrives
                    var y = _terminal.Buffer.ViewportY;
                    _terminal.Buffer.ScrollToBottom();

                    if (y != Terminal.Buffer.ViewportY)
                        Dispatcher.UIThread.Post(() => RaisePropertyChanged(ViewportYProperty, y, _terminal.Buffer.ViewportY));

                    this.RequestInvalidate();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                _terminal.WriteLine($"\nError reading from process: {ex.Message}\n");
                _terminal.Buffer.ScrollToBottom();
                this.RequestInvalidate();
            }
        }

        private void OnPtyProcessExited(object? sender, PtyExitedEventArgs e)
        {
            // Handle process exit from the PTY event (more reliable than just detecting EOF)
            // Use flag to prevent double notification from both event and EOF detection
            if (_processExitHandled)
                return;
            _processExitHandled = true;

            _terminal.WriteLine($"\nProcess exited with code: {e.ExitCode}\n");
            _terminal.Buffer.ScrollToBottom();
            this.RequestInvalidate();

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Raise event on UI thread so subscribers can safely update UI
                var args = new ProcessExitedEventArgs(e.ExitCode)
                {
                    RoutedEvent = ProcessExitedEvent
                };
                RaiseEvent(args);
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
                    _ptyConnection.Kill();
                    _ptyConnection.Dispose();
                }
                catch
                {
                    // Ignore cleanup errors
                }
                finally
                {
                    _ptyConnection = null;
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
            var scale = VisualRoot?.RenderScaling ?? 1.0;

            // Use the terminal buffer's ViewportY to determine what to render
            int viewportY = _terminal.Buffer.ViewportY;
            int viewportLines = _terminal.Rows;
            int startLine = viewportY;
            int endLine = Math.Min(_terminal.Buffer.Length, startLine + viewportLines);
            try
            {

                for (int y = startLine; y < endLine; y++)
                {
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

                RenderCursor(context, viewportY, scale);
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
            // Try to use cached text runs for this line
            var textRuns = line.Cache as List<CachedTextRun>;
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
            var runs = new List<CachedTextRun>();

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
                        cellCount += currentCell.Width;  // Wide chars add 2, normal chars add 1

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
                if (cell.Attributes.IsInverse())
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
                runs.Add(new CachedTextRun(formattedText, runStartX, cellCount, background));

                context.FillRectangle(background, rect);
                context.DrawText(formattedText, position);
            }

            line.Cache = runs;
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
                        if (cell.Attributes.IsInverse())
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

    }
}
