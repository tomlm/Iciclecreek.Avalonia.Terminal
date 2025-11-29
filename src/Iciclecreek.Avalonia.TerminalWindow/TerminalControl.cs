using Avalonia;
using Avalonia.Controls;
using media = Avalonia.Media;
using Avalonia.Input;
using Iciclecreek.Avalonia.Terminal.Buffer;
using System;
using System.Linq;
using System.Text;
using Avalonia.Media;

namespace Iciclecreek.Avalonia.Terminal
{
    public class TerminalControl : Control
    {
        private PixelBuffer _pixels { get; set; } = new PixelBuffer(80, 25);
        private FormattedText _measureText;
        private double _charWidth;
        private double _charHeight;

        // Selection state
        private Point? _selectionStart;
        private Point? _selectionEnd;
        private bool _isSelecting;

        // Buffer size backing
        private PixelSize _bufferSize = new PixelSize(80, 25);

        // Write/cursor position backing
        private PixelPoint _position;

        public static readonly DirectProperty<TerminalControl, PixelSize> BufferSizeProperty =
            AvaloniaProperty.RegisterDirect<TerminalControl, PixelSize>(
                nameof(BufferSize),
                o => o._bufferSize,
                (o, v) => o._bufferSize = v);

        public static readonly DirectProperty<TerminalControl, PixelPoint> PositionProperty =
            AvaloniaProperty.RegisterDirect<TerminalControl, PixelPoint>(
                nameof(Position),
                o => o._position,
                (o, v) => o._position = o.CoercePosition(v));

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

        static TerminalControl()
        {
            AffectsRender<TerminalControl>(
                FontFamilyProperty,
                FontSizeProperty,
                FontStyleProperty,
                FontWeightProperty,
                TextDecorationsProperty,
                ForegroundColorProperty,
                BackgroundColorProperty,
                SelectionBrushProperty,
                PositionProperty);

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
        }

        public PixelSize BufferSize
        {
            get => _bufferSize;
            set
            {
                if (value.Width == _bufferSize.Width && value.Height == _bufferSize.Height)
                    return;

                _pixels = new PixelBuffer((ushort)value.Width, (ushort)value.Height);
                SetAndRaise(BufferSizeProperty, ref _bufferSize, value);
                Position = CoercePosition(Position); // clamp after resize
                InvalidateVisual();
            }
        }

        public PixelPoint Position
        {
            get => _position;
            set => SetAndRaise(PositionProperty, ref _position, value);
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

        public string GetSelectedText()
        {
            if (!HasSelection())
                return string.Empty;

            var (start, end) = GetNormalizedSelection();
            var sb = new StringBuilder();

            for (int y = start.Y; y <= end.Y; y++)
            {
                int xStart = (y == start.Y) ? start.X : 0;
                int xEnd = (y == end.Y) ? end.X : _pixels.Width - 1;

                for (int x = xStart; x <= xEnd;)
                {
                    var pixel = _pixels[x, y];
                    if (pixel.Width > 0)
                    {
                        sb.Append(pixel.Symbol.GetText());
                        x += pixel.Width;
                    }
                    else
                    {
                        x++;
                        sb.Append(' ');
                    }
                }

                if (y < end.Y)
                    sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        public void ClearSelection()
        {
            _selectionStart = null;
            _selectionEnd = null;
            _isSelecting = false;
            InvalidateVisual();
        }

        private bool HasSelection()
        {
            return _selectionStart.HasValue && _selectionEnd.HasValue &&
                   (_selectionStart.Value != _selectionEnd.Value);
        }

        private ((int X, int Y) start, (int X, int Y) end) GetNormalizedSelection()
        {
            if (!_selectionStart.HasValue || !_selectionEnd.HasValue)
                return ((0, 0), (0, 0));

            var start = _selectionStart.Value;
            var end = _selectionEnd.Value;

            if (start.Y > end.Y || (start.Y == end.Y && start.X > end.X))
                (start, end) = (end, start);

            return (((int)start.X, (int)start.Y), ((int)end.X, (int)end.Y));
        }

        private Point? ScreenToGrid(Point screenPos)
        {
            if (_charWidth <= 0 || _charHeight <= 0)
                return null;

            var cellWidth = Bounds.Width / _bufferSize.Width;
            var cellHeight = Bounds.Height / _bufferSize.Height;

            int x = (int)(screenPos.X / cellWidth);
            int y = (int)(screenPos.Y / cellHeight);

            x = Math.Max(0, Math.Min(x, _pixels.Width - 1));
            y = Math.Max(0, Math.Min(y, _pixels.Height - 1));

            return new Point(x, y);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed)
            {
                Focus();

                var gridPos = ScreenToGrid(point.Position);
                if (gridPos.HasValue)
                {
                    _selectionStart = gridPos.Value;
                    _selectionEnd = gridPos.Value;
                    _isSelecting = true;
                    e.Pointer.Capture(this);
                    InvalidateVisual();
                }
            }

            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (_isSelecting)
            {
                var point = e.GetCurrentPoint(this);
                var gridPos = ScreenToGrid(point.Position);
                if (gridPos.HasValue)
                {
                    _selectionEnd = gridPos.Value;
                    InvalidateVisual();
                }
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_isSelecting)
            {
                _isSelecting = false;
                e.Pointer.Capture(null);

                if (HasSelection())
                {
                    var selectedText = GetSelectedText();
                    if (!string.IsNullOrEmpty(selectedText))
                    {
                        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(selectedText);
                    }
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (HasSelection())
                {
                    var selectedText = GetSelectedText();
                    if (!string.IsNullOrEmpty(selectedText))
                    {
                        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(selectedText);
                    }
                }
                e.Handled = true;
            }
            else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _selectionStart = new Point(0, 0);
                _selectionEnd = new Point(_pixels.Width - 1, _pixels.Height - 1);
                InvalidateVisual();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClearSelection();
                e.Handled = true;
            }
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

            var desiredWidth = _bufferSize.Width * _charWidth;
            var desiredHeight = _bufferSize.Height * _charHeight;

            return new Size(desiredWidth, desiredHeight);
        }

        protected override Size ArrangeOverride(Size finalSize) => finalSize;

        // Cursor coercion
        private PixelPoint CoercePosition(PixelPoint p)
        {
            int x = Math.Clamp(p.X, 0, _pixels.Width - 1);
            int y = Math.Clamp(p.Y, 0, _pixels.Height - 1);
            return new PixelPoint(x, y);
        }

        // DrawText API
        public void DrawText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            int x = Position.X;
            int y = Position.Y;

            var fg = ForegroundColor;
            var bg = BackgroundColor;
            var style = FontStyle;
            var weight = FontWeight;
            var decorations = TextDecorations;

            foreach (var rune in text.EnumerateRunes())
            {
                // Line feed
                if (rune.Value == '\n')
                {
                    x = 0;
                    y++;
                    if (y >= _pixels.Height)
                    {
                        _pixels.ScrollUp(); 
                        y = _pixels.Height - 1;
                    }
                    continue;
                }

                var symbol = new Symbol(rune);

                // Wrap if wide glyph would overflow
                if (symbol.Width > 1 && x + symbol.Width - 1 >= _pixels.Width)
                {
                    x = 0;
                    y++;
                }

                // Vertical scroll if needed
                if (y >= _pixels.Height)
                {
                    _pixels.ScrollUp();
                    y = _pixels.Height - 1;
                }

                // Write lead cell
                _pixels[x, y] = new Pixel(bg, fg, symbol, style, weight, decorations);

                // Mark continuation cells for wide glyphs with empty zero-width symbols
                if (symbol.Width > 1)
                {
                    for (int i = 1; i < symbol.Width && x + i < _pixels.Width; i++)
                    {
                        _pixels[x + i, y] = new Pixel(Colors.Transparent, Colors.Transparent, Symbol.Empty, style, weight, decorations);
                    }
                }

                x += symbol.Width;

                // Wrap end of line
                if (x >= _pixels.Width)
                {
                    x = 0;
                    y++;
                }

                // Scroll if beyond last row
                if (y >= _pixels.Height)
                {
                    _pixels.ScrollUp();
                    y = _pixels.Height - 1;
                }
            }

            Position = new PixelPoint(x, y);
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            var localFontFamily = FontFamily;
            var localFontSize = FontSize;
            var localFontStyle = FontStyle;
            var localFontWeight = FontWeight;
            var localSelectionBrush = SelectionBrush;

            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

            var cellWidth = Bounds.Width / _bufferSize.Width;
            var cellHeight = Bounds.Height / _bufferSize.Height;

            var (selStart, selEnd) = GetNormalizedSelection();
            bool hasSelection = HasSelection();

            var textBuilder = new StringBuilder();
            Color? currentFg = null;
            Color? currentBg = null;
            FontStyle? currentStyle = null;
            FontWeight? currentWeight = null;
            TextDecorationLocation? currentDecoration = null;
            double startX = 0;
            int startCol = 0;

            for (int y = 0; y < _pixels.Height; y++)
            {
                var cellY = y * cellHeight;

                for (int x = 0; x < _pixels.Width; x++)
                {
                    var pixel = _pixels[x, y];
                    var pixelFg = pixel.Foreground != default ? pixel.Foreground : Colors.White;
                    var pixelBg = pixel.Background;
                    var pixelStyle = pixel.Style ?? localFontStyle;
                    var pixelWeight = pixel.Weight ?? localFontWeight;
                    var pixelDecoration = pixel.TextDecoration;

                    bool needsFlush =
                        (currentFg.HasValue && pixelFg != currentFg.Value) ||
                        (currentBg.HasValue && pixelBg != currentBg.Value) ||
                        (currentStyle.HasValue && pixelStyle != currentStyle.Value) ||
                        (currentWeight.HasValue && pixelWeight != currentWeight.Value) ||
                        (currentDecoration.HasValue && pixelDecoration != currentDecoration) ||
                        x == _pixels.Width - 1;

                    if (pixel.Width > 0)
                    {
                        textBuilder.Append(pixel.Symbol.GetText());
                        if (!currentFg.HasValue)
                        {
                            currentFg = pixelFg;
                            currentBg = pixelBg;
                            currentStyle = pixelStyle;
                            currentWeight = pixelWeight;
                            currentDecoration = pixelDecoration;
                            startX = x * cellWidth;
                            startCol = x;
                        }
                    }
                    else if (textBuilder.Length > 0)
                    {
                        needsFlush = true;
                    }

                    if (needsFlush && textBuilder.Length > 0)
                    {
                        if (currentBg.HasValue && currentBg.Value != default)
                        {
                            var bgBrush = new SolidColorBrush(currentBg.Value);
                            var bgRect = new Rect(startX, cellY, (x - startCol + 1) * cellWidth, cellHeight);
                            context.FillRectangle(bgBrush, bgRect);
                        }

                        var runTypeface = new Typeface(localFontFamily, currentStyle!.Value, currentWeight!.Value);
                        var fgBrush = new SolidColorBrush(currentFg!.Value);
                        var formattedText = new FormattedText(
                            textBuilder.ToString(),
                            System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            runTypeface,
                            localFontSize,
                            fgBrush);

                        if (currentDecoration.HasValue)
                        {
                            var decorations = new TextDecorationCollection();
                            if (currentDecoration.Value.HasFlag(TextDecorationLocation.Underline))
                                decorations.AddRange(media.TextDecorations.Underline);
                            if (currentDecoration.Value.HasFlag(TextDecorationLocation.Strikethrough))
                                decorations.AddRange(media.TextDecorations.Strikethrough);
                            if (currentDecoration.Value.HasFlag(TextDecorationLocation.Overline))
                                decorations.AddRange(media.TextDecorations.Overline);
                            formattedText.SetTextDecorations(decorations);
                        }

                        context.DrawText(formattedText, new Point(startX, cellY));

                        textBuilder.Clear();
                        currentFg = null;
                        currentBg = null;
                        currentStyle = null;
                        currentWeight = null;
                        currentDecoration = null;
                    }

                    if (pixel.Width == 0 && pixelBg != default)
                    {
                        var bgBrush = new SolidColorBrush(pixelBg);
                        var cellRect = new Rect(x * cellWidth, cellY, cellWidth, cellHeight);
                        context.FillRectangle(bgBrush, cellRect);
                    }
                }

                textBuilder.Clear();
                currentFg = null;
                currentBg = null;
                currentStyle = null;
                currentWeight = null;
                currentDecoration = null;
            }

            if (hasSelection)
            {
                for (int y = selStart.Y; y <= selEnd.Y; y++)
                {
                    int xStart = (y == selStart.Y) ? selStart.X : 0;
                    int xEnd = (y == selEnd.Y) ? selEnd.X : _pixels.Width - 1;

                    var selRect = new Rect(
                        xStart * cellWidth,
                        y * cellHeight,
                        (xEnd - xStart + 1) * cellWidth,
                        cellHeight);

                    context.FillRectangle(localSelectionBrush, selRect);
                }
            }
        }
    }
}
