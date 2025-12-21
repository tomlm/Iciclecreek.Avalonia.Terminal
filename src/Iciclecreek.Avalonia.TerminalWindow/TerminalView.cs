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

        // Cursor blinking
        private DispatcherTimer _cursorBlinkTimer;
        private bool _cursorBlinkOn = true;

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
            base.OnKeyDown(e);

            if (_ptyConnection == null)
                return;

            try
            {
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
                        e.Handled = true;
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
                            e.Handled = true;
                        }
                    }
                }
                // Regular characters without Ctrl/Alt: let TextInput handle them
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling key input: {ex.Message}");
            }
        }

        protected override async void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);

            if (_ptyConnection == null || string.IsNullOrEmpty(e.Text))
                return;

            try
            {
                await SendToPtyAsync(e.Text);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling text input: {ex.Message}");
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

        private async void OnTerminalDataReceived(object? sender, XT.Events.TerminalEvents.DataEventArgs e)
        {
            // Terminal wants to send data (typically in response to device status queries, etc.)
            await SendToPtyAsync(e.Data);
        }

        private async Task SendToPtyAsync(string data)
        {
            if (_ptyConnection == null || string.IsNullOrEmpty(data))
                return;

            try
            {
                var bytes = Utf8NoBom.GetBytes(data);
                await _ptyConnection.WriterStream.WriteAsync(bytes, 0, bytes.Length);
                await _ptyConnection.WriterStream.FlushAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error writing to PTY: {ex.Message}");
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
                        // Process has exited
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _terminal.WriteLine($"\nProcess exited with code: {_ptyConnection?.ExitCode ?? 0}\n");
                        });
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

        private void CleanupProcess()
        {
            _processCts?.Cancel();

            if (_ptyConnection != null)
            {
                try
                {
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

                    var text = String.Join(String.Empty, line.Skip(x)
                        .TakeWhile(cell2 => cell.Attributes == cell2.Attributes)
                        .Select(cell2 => cell2.Content).ToArray());

                    var startX = Snap(x * _charWidth, scale);
                    var endX = Snap((x + text.Length) * _charWidth, scale);
                    var startY = Snap((y - startLine) * _charHeight, scale);
                    var endY = Snap((y - startLine + 1) * _charHeight, scale);
                    var rect = new Rect(startX, startY, Math.Max(0, endX - startX), Math.Max(0, endY - startY));

                    // draw rectangle for line
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
                    context.DrawText(formattedText, new Point(startX, startY));
                    x += text.Length;
                    Debug.Assert(text.Length > 0);
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
    }
}
