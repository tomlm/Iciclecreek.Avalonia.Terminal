using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Avalonia.Terminal;
using Pty.Net;
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
                defaultValue: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "sh");

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

            _terminal = new XT.Terminal(new XT.Options.TerminalOptions()
            {
                Cols = 80,
                Rows = 24,
                Scrollback = BufferSize,
            });
            _isAlternateBuffer = _terminal.IsAlternateBufferActive;

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
            _terminal.WindowInfoRequested += OnTerminalWindowInfoRequested;

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
                InvalidateVisual();
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
                    InvalidateVisual();
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
            // Sync terminal options with styled properties
            _terminal.Options.CursorStyle = CursorStyle;
            _terminal.Options.CursorBlink = CursorBlink;
            _terminal.Options.CursorBlinkRate = CursorBlinkRate;

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
                InvalidateVisual();
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
                base.OnKeyDown(e);
                return;
            }

            // Mark as handled early to prevent other controls from processing
            e.Handled = true;

            Debug.WriteLine($"[{_instanceId}] OnKeyDown: Key={e.Key}, IsFocused={IsFocused}");

            try
            {
                // Check if Win32 Input Mode is enabled
                if (_terminal.Win32InputMode)
                {
                    var sequence = GenerateWin32InputSequence(e, isKeyDown: true);
                    if (!string.IsNullOrEmpty(sequence))
                    {
                        await SendToPtyAsync(sequence);
                    }
                    return;
                }

                // Convert Avalonia key to XTerm key
                var xtermKey = ConvertAvaloniaKeyToXTermKey(e.Key);
                var modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);

                // Special keys (arrows, function keys, etc.) - always handle in KeyDown
                if (xtermKey != null)
                {
                    var sequence = _terminal.GenerateKeyInput(xtermKey.Value, modifiers);
                    if (!string.IsNullOrEmpty(sequence))
                    {
                        await SendToPtyAsync(sequence);
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
                            await SendToPtyAsync(sequence);
                        }
                    }
                }
                // Regular characters without Ctrl/Alt: let TextInput handle them
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
                        await SendToPtyAsync(sequence);
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
                base.OnTextInput(e);
                return;
            }

            // Capture the connection reference locally
            var ptyConnection = _ptyConnection;
            if (ptyConnection == null || string.IsNullOrEmpty(e.Text))
            {
                base.OnTextInput(e);
                return;
            }

            // In Win32 Input Mode, text input is handled via KeyDown/KeyUp events
            if (_terminal.Win32InputMode)
                return;

            Debug.WriteLine($"[{_instanceId}] OnTextInput: Text='{e.Text}', IsFocused={IsFocused}");

            try
            {
                await SendToPtyAsync(e.Text);
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
                    await SendToPtyAsync(sequence);
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
                    await SendToPtyAsync(sequence);
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
                    await SendToPtyAsync(sequence);
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
                    await SendToPtyAsync(sequence);
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

            Debug.WriteLine($"[{_instanceId}] OnGotFocus");

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

            InvalidateVisual();
        }

        protected override async void OnLostFocus(RoutedEventArgs e)
        {
            base.OnLostFocus(e);

            Debug.WriteLine($"[{_instanceId}] OnLostFocus");

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

            InvalidateVisual();
        }

        private void OnTerminalBufferChanged(object? sender, XT.Events.TerminalEvents.BufferChangedEventArgs e)
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
            InvalidateVisual();
        }

        private void OnTerminalCursorStyleChanged(object? sender, XT.Events.TerminalEvents.CursorStyleChangedEventArgs e)
        {
            if (!Equals(CursorStyle, e.Style))
            {
                SetValue(CursorStyleProperty, e.Style);
            }

            if (!Equals(CursorBlink, e.Blink))
            {
                SetValue(CursorBlinkProperty, e.Blink);
            }

            InvalidateVisual();
        }

        private void OnTerminalTitleChanged(object? sender, XT.Events.TerminalEvents.TitleChangeEventArgs e)
        {
            TitleChanged?.Invoke(this, new TitleChangedEventArgs(e.Title));
        }

        private void OnTerminalWindowMoved(object? sender, XT.Events.TerminalEvents.WindowMovedEventArgs e)
        {
            WindowMoved?.Invoke(this, new WindowMovedEventArgs(e.X, e.Y));
        }

        private void OnTerminalWindowResized(object? sender, XT.Events.TerminalEvents.WindowResizedEventArgs e)
        {
            WindowResized?.Invoke(this, new WindowResizedEventArgs(e.Width, e.Height));
        }

        private void OnTerminalWindowMinimized(object? sender, EventArgs e)
        {
            WindowMinimized?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalWindowMaximized(object? sender, EventArgs e)
        {
            WindowMaximized?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalWindowRestored(object? sender, EventArgs e)
        {
            WindowRestored?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalWindowRaised(object? sender, EventArgs e)
        {
            WindowRaised?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalWindowLowered(object? sender, EventArgs e)
        {
            WindowLowered?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalWindowFullscreened(object? sender, EventArgs e)
        {
            WindowFullscreened?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalBellRang(object? sender, EventArgs e)
        {
            BellRang?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalWindowInfoRequested(object? sender, XT.Events.TerminalEvents.WindowInfoRequestedEventArgs e)
        {
            // Create our own event args and forward to subscribers
            var args = new WindowInfoRequestedEventArgs(e.Request);
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

        private async void OnTerminalDataReceived(object? sender, XT.Events.TerminalEvents.DataEventArgs e)
        {
            // Terminal wants to send data (typically in response to device status queries, etc.)
            await SendToPtyAsync(e.Data);
        }

        private async Task SendToPtyAsync(string data)
        {
            // Capture the connection reference locally to avoid any potential race conditions
            var ptyConnection = _ptyConnection;
            if (ptyConnection == null || string.IsNullOrEmpty(data))
                return;

            try
            {
                var bytes = Utf8NoBom.GetBytes(data);
                await ptyConnection.WriterStream.WriteAsync(bytes, 0, bytes.Length);
                await ptyConnection.WriterStream.FlushAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_instanceId}] Error writing to PTY: {ex.Message}");
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

            // Fallback mapping for cases where KeySymbol is empty (e.g., Alt+<char> on some platforms)
            return TryMapKeyToChar(e.Key, e.KeyModifiers, out character);
        }

        private bool TryMapKeyToChar(Key key, KeyModifiers modifiers, out char character)
        {
            character = default;

            if (key >= Key.A && key <= Key.Z)
            {
                var offset = key - Key.A;
                var isUpper = modifiers.HasFlag(KeyModifiers.Shift);
                character = (char)((isUpper ? 'A' : 'a') + offset);
                return true;
            }

            if (key >= Key.D0 && key <= Key.D9 && !modifiers.HasFlag(KeyModifiers.Shift))
            {
                var offset = key - Key.D0;
                character = (char)('0' + offset);
                return true;
            }

            if (key == Key.Space)
            {
                character = ' ';
                return true;
            }

            return false;
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
                    processToLaunch = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "sh";
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
                var buffer = new byte[8192];
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
                            
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                _terminal.WriteLine($"\nProcess exited with code: {exitCode}\n");
                                _terminal.Buffer.ScrollToBottom();
                                InvalidateVisual();
                                ProcessExited?.Invoke(this, new ProcessExitedEventArgs(exitCode));
                            });
                        }
                        break;
                    }

                    var output = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _terminal.Write(output);
                        // Auto-scroll to bottom when new content arrives
                        _terminal.Buffer.ScrollToBottom();
                        RaisePropertyChanged(MaxScrollbackProperty, default(int), MaxScrollback);
                        RaisePropertyChanged(ViewportLinesProperty, default(int), ViewportLines);
                        RaisePropertyChanged(ViewportYProperty, default(int), ViewportY);
                        InvalidateVisual();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _terminal.WriteLine($"\nError reading from process: {ex.Message}\n");
                    _terminal.Buffer.ScrollToBottom();
                });
            }
        }

        private void OnPtyProcessExited(object? sender, PtyExitedEventArgs e)
        {
            // Handle process exit from the PTY event (more reliable than just detecting EOF)
            // Use flag to prevent double notification from both event and EOF detection
            if (_processExitHandled)
                return;
            _processExitHandled = true;

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                _terminal.WriteLine($"\nProcess exited with code: {e.ExitCode}\n");
                _terminal.Buffer.ScrollToBottom();
                InvalidateVisual();
                
                // Raise event on UI thread so subscribers can safely update UI
                ProcessExited?.Invoke(this, new ProcessExitedEventArgs(e.ExitCode));
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
            for (int y = startLine; y < endLine; y++)
            {
                var line = _terminal.Buffer.GetLine(y);
                if (line == null)
                    continue;

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
                    var startY = Snap((y - startLine) * _charHeight, scale);
                    var endY = Snap((y - startLine + 1) * _charHeight, scale);
                    var rect = new Rect(startX, startY, Math.Max(0, endX - startX), Math.Max(0, endY - startY));

                    // draw rectangle for background of textrun
                    context.FillRectangle(cell.GetBackgroundBrush(this.Background), rect);

                    // draw text
                    var typeface = new Typeface(FontFamily, cell.GetFontStyle(), cell.GetFontWeight());
                    var formattedText = new FormattedText(
                        text,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        FontSize,
                        cell.GetForegroundBrush(this.Foreground));
                    var td = cell.GetTextDecorations();
                    if (td != null)
                        formattedText.SetTextDecorations(td);
                    //Debug.WriteLine($"[{runStartX},{y - startLine}] '{text}' {text.Length}");
                    context.DrawText(formattedText, new Point(startX, startY));
                }
            }

            // Render the cursor
            RenderCursor(context, viewportY, scale);
        }

        private void RenderCursor(DrawingContext context, int viewportY, double scale)
        {
            // Only show cursor if terminal says it's visible (controlled by escape sequences)
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
            if (vk == 0 && string.IsNullOrEmpty(e.KeySymbol))
                return string.Empty;

            // Get scan code (we use 0 as we don't have direct access to hardware scan codes)
            var scanCode = 0;

            // Get unicode character
            int unicodeChar = 0;
            if (!string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol.Length >= 1)
            {
                unicodeChar = char.ConvertToUtf32(e.KeySymbol, 0);
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

    /// <summary>
    /// EventArgs for the ProcessExited event.
    /// </summary>
    public class ProcessExitedEventArgs : EventArgs
    {
        public int ExitCode { get; }

        public ProcessExitedEventArgs(int exitCode)
        {
            ExitCode = exitCode;
        }
    }

    /// <summary>
    /// EventArgs for the TitleChanged event.
    /// </summary>
    public class TitleChangedEventArgs : EventArgs
    {
        public string Title { get; }

        public TitleChangedEventArgs(string title)
        {
            Title = title;
        }
    }

    /// <summary>
    /// EventArgs for the WindowMoved event.
    /// </summary>
    public class WindowMovedEventArgs : EventArgs
    {
        public int X { get; }
        public int Y { get; }

        public WindowMovedEventArgs(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// EventArgs for the WindowResized event.
    /// </summary>
    public class WindowResizedEventArgs : EventArgs
    {
        public int Width { get; }
        public int Height { get; }

        public WindowResizedEventArgs(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// EventArgs for the WindowInfoRequested event.
    /// The handler should set the appropriate response properties based on the Request type.
    /// </summary>
    public class WindowInfoRequestedEventArgs : EventArgs
    {
        /// <summary>
        /// The type of window information being requested.
        /// </summary>
        public XT.Common.WindowInfoRequest Request { get; }

        /// <summary>
        /// Set to true if the request was handled and a response should be sent.
        /// </summary>
        public bool Handled { get; set; }

        /// <summary>
        /// For State request: true if window is iconified (minimized), false otherwise.
        /// </summary>
        public bool IsIconified { get; set; }

        /// <summary>
        /// For Position request: X coordinate of window position in pixels.
        /// </summary>
        public int X { get; set; }

        /// <summary>
        /// For Position request: Y coordinate of window position in pixels.
        /// </summary>
        public int Y { get; set; }

        /// <summary>
        /// For SizePixels/ScreenSizePixels request: Width in pixels.
        /// </summary>
        public int WidthPixels { get; set; }

        /// <summary>
        /// For SizePixels/ScreenSizePixels request: Height in pixels.
        /// </summary>
        public int HeightPixels { get; set; }

        /// <summary>
        /// For CellSizePixels request: Cell width in pixels.
        /// </summary>
        public int CellWidth { get; set; }

        /// <summary>
        /// For CellSizePixels request: Cell height in pixels.
        /// </summary>
        public int CellHeight { get; set; }

        /// <summary>
        /// For Title/IconTitle request: The title string.
        /// </summary>
        public string? Title { get; set; }

        public WindowInfoRequestedEventArgs(XT.Common.WindowInfoRequest request)
        {
            Request = request;
        }
    }
}
