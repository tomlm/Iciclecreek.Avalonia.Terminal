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
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XT = global::XTerm;

namespace Iciclecreek.Terminal
{
    public class TerminalControl : Control
    {
        private XT.Terminal _terminal = new XT.Terminal(null);
        private FormattedText _measureText;
        private double _charWidth;
        private double _charHeight;
        private int _bufferSize = 100;

        // Process management
        private IPtyConnection _ptyConnection;
        private CancellationTokenSource _processCts;

        public static readonly DirectProperty<TerminalControl, int> BufferSizeProperty =
            AvaloniaProperty.RegisterDirect<TerminalControl, int>(
                nameof(BufferSize),
                o => o._bufferSize,
                (o, v) => o._bufferSize = v);

        public static readonly StyledProperty<FontFamily> FontFamilyProperty =
            AvaloniaProperty.Register<TerminalControl, FontFamily>(
                nameof(FontFamily),
                defaultValue: FontFamily.Default);

        public static readonly StyledProperty<double> FontSizeProperty =
            AvaloniaProperty.Register<TerminalControl, double>(
                nameof(FontSize),
                defaultValue: 12);

        public static readonly StyledProperty<FontStyle> FontStyleProperty =
            AvaloniaProperty.Register<TerminalControl, FontStyle>(
                nameof(FontStyle),
                defaultValue: FontStyle.Normal);

        public static readonly StyledProperty<FontWeight> FontWeightProperty =
            AvaloniaProperty.Register<TerminalControl, FontWeight>(
                nameof(FontWeight),
                defaultValue: FontWeight.Normal);

        public static readonly StyledProperty<TextDecorationLocation?> TextDecorationsProperty =
            AvaloniaProperty.Register<TerminalControl, TextDecorationLocation?>(
                nameof(TextDecorations),
                defaultValue: null);

        public static readonly StyledProperty<Color> ForegroundColorProperty =
            AvaloniaProperty.Register<TerminalControl, Color>(
                nameof(ForegroundColor),
                defaultValue: Colors.White);

        public static readonly StyledProperty<Color> BackgroundColorProperty =
            AvaloniaProperty.Register<TerminalControl, Color>(
                nameof(BackgroundColor),
                defaultValue: Colors.Transparent);

        public static readonly StyledProperty<IBrush> SelectionBrushProperty =
            AvaloniaProperty.Register<TerminalControl, IBrush>(
                nameof(SelectionBrush),
                defaultValue: new SolidColorBrush(Color.FromArgb(128, 0, 120, 215)));

        public static readonly StyledProperty<string> ProcessProperty =
            AvaloniaProperty.Register<TerminalControl, string>(
                nameof(Process),
                defaultValue: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "sh");

        public static readonly StyledProperty<IList<string>> ArgsProperty =
            AvaloniaProperty.Register<TerminalControl, IList<string>>(
                nameof(Args),
                defaultValue: Array.Empty<string>());


        static TerminalControl()
        {
            AffectsRender<TerminalControl>(
                FontFamilyProperty,
                FontSizeProperty,
                FontStyleProperty,
                FontWeightProperty,
                TextDecorationsProperty,
                //ForegroundColorProperty,
                //BackgroundColorProperty,
                SelectionBrushProperty);

            AffectsMeasure<TerminalControl>(
                FontFamilyProperty,
                FontSizeProperty,
                FontStyleProperty,
                FontWeightProperty,
                BufferSizeProperty);

            FocusableProperty.OverrideDefaultValue<TerminalControl>(true);
        }

        public TerminalControl()
        {
            Focusable = true;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            
            // Subscribe to terminal data event - this is fired when terminal wants to send data (user input)
            _terminal.DataReceived += OnTerminalDataReceived;
        }

        public int BufferSize
        {
            get => _bufferSize;
            set
            {
                _terminal.Resize(_terminal.Cols, value);
                SetAndRaise(BufferSizeProperty, ref _bufferSize, value);
                InvalidateVisual();
            }
        }

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

        public Color ForegroundColor
        {
            get => GetValue(ForegroundColorProperty);
            set => SetValue(ForegroundColorProperty, value);
        }

        public Color BackgroundColor
        {
            get => GetValue(BackgroundColorProperty);
            set => SetValue(BackgroundColorProperty, value);
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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(Process))
            {
                LaunchProcess();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _terminal.DataReceived -= OnTerminalDataReceived;
            CleanupProcess();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (_ptyConnection == null)
                return;

            try
            {
                // Convert Avalonia key to XTerm key
                var xtermKey = ConvertAvaloniaKeyToXTermKey(e.Key);
                var modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);

                string sequence;
                
                // Handle printable characters
                if (!string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol.Length == 1 && 
                    !char.IsControl(e.KeySymbol[0]) && xtermKey == null)
                {
                    // Regular character input
                    sequence = _terminal.GenerateCharInput(e.KeySymbol[0], modifiers);
                }
                else if (xtermKey != null)
                {
                    // Special key (arrows, function keys, etc.)
                    sequence = _terminal.GenerateKeyInput(xtermKey.Value, modifiers);
                }
                else
                {
                    return; // Unknown key
                }

                if (!string.IsNullOrEmpty(sequence))
                {
                    SendToPty(sequence);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling key input: {ex.Message}");
            }
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);

            if (_ptyConnection == null || string.IsNullOrEmpty(e.Text))
                return;

            try
            {
                // For text input, send it directly (handles composition, IME, etc.)
                SendToPty(e.Text);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling text input: {ex.Message}");
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
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
                    SendToPty(sequence);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling mouse press: {ex.Message}");
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_ptyConnection == null)
                return;

            try
            {
                var point = e.GetPosition(this);
                var col = (int)(point.X / _charWidth);
                var row = (int)(point.Y / _charHeight);
                
                var button = ConvertPointerButton(e.GetCurrentPoint(this).Properties);
                var modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);
                
                var sequence = _terminal.GenerateMouseEvent(button, col, row, XT.Input.MouseEventType.Up, modifiers);
                if (!string.IsNullOrEmpty(sequence))
                {
                    SendToPty(sequence);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling mouse release: {ex.Message}");
            }
        }

        protected override void OnGotFocus(GotFocusEventArgs e)
        {
            base.OnGotFocus(e);

            if (_ptyConnection != null && _terminal.SendFocusEvents)
            {
                var sequence = _terminal.GenerateFocusEvent(true);
                if (!string.IsNullOrEmpty(sequence))
                {
                    SendToPty(sequence);
                }
            }

            InvalidateVisual();
        }

        protected override void OnLostFocus(RoutedEventArgs e)
        {
            base.OnLostFocus(e);

            if (_ptyConnection != null && _terminal.SendFocusEvents)
            {
                var sequence = _terminal.GenerateFocusEvent(false);
                if (!string.IsNullOrEmpty(sequence))
                {
                    SendToPty(sequence);
                }
            }

            InvalidateVisual();
        }

        private void OnTerminalDataReceived(object? sender, XT.Events.TerminalEvents.DataEventArgs e)
        {
            // Terminal wants to send data (typically in response to device status queries, etc.)
            SendToPty(e.Data);
        }

        private void SendToPty(string data)
        {
            if (_ptyConnection == null || string.IsNullOrEmpty(data))
                return;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(data);
                _ptyConnection.WriterStream.Write(bytes, 0, bytes.Length);
                _ptyConnection.WriterStream.Flush();
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

        private XT.Input.MouseButton ConvertPointerButton(PointerPointProperties props)
        {
            if (props.IsLeftButtonPressed)
                return XT.Input.MouseButton.Left;
            if (props.IsMiddleButtonPressed)
                return XT.Input.MouseButton.Middle;
            if (props.IsRightButtonPressed)
                return XT.Input.MouseButton.Right;
                
            return XT.Input.MouseButton.None;
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

            var desiredWidth = availableSize.Width;
            var desiredHeight = _bufferSize * _charHeight;

            return new Size(desiredWidth, desiredHeight);
        }

        protected override Size ArrangeOverride(Size finalSize) => finalSize;


        public override void Render(DrawingContext context)
        {
            StringBuilder sb = new StringBuilder();
            for (int y = _terminal.Buffer.ScrollTop; y < _terminal.Buffer.ScrollBottom; y++)
            {
                var line = _terminal.Buffer.GetLine(y)!;
                for (int x = 0; x < _terminal.Cols;)
                {
                    var cell = line[x];

                    var text = String.Join(String.Empty, line.Skip(x)
                        .TakeWhile(cell2 => cell.Attributes == cell2.Attributes)
                        .Select(cell2 => cell2.Content).ToArray());

                    double posX = x * _charWidth;
                    double posY = y * _charHeight;

                    // draw rectangle for line
                    var bgBrush = new SolidColorBrush(cell.GetBackground() ?? this.BackgroundColor);
                    context.FillRectangle(bgBrush, new Rect(posX, posY, text.Length * _charWidth, _charHeight));

                    // draw text
                    var typeface = new Typeface(FontFamily, cell.GetFontStyle(), cell.GetFontWeight());
                    var fgBrush = new SolidColorBrush(cell.GetForegroundColor() ?? this.ForegroundColor);
                    var formattedText = new FormattedText(
                        text,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        FontSize,
                        fgBrush);
                    context.DrawText(formattedText, new Point(posX, posY));
                    x += text.Length;
                }
            }
        }

    }

}
