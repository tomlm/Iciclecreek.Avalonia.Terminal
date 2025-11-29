using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Iciclecreek.Avalonia.Terminal.Buffer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        public static readonly DirectProperty<TerminalControl, PixelSize> BufferSizeProperty =
            AvaloniaProperty.RegisterDirect<TerminalControl, PixelSize>(
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

        public static readonly StyledProperty<IBrush> SelectionBrushProperty =
            AvaloniaProperty.Register<TerminalControl, IBrush>(
                nameof(SelectionBrush),
                defaultValue: new SolidColorBrush(Color.FromArgb(128, 0, 120, 215)));

        static TerminalControl()
        {
            AffectsRender<TerminalControl>(FontFamilyProperty, FontSizeProperty, FontStyleProperty, FontWeightProperty, SelectionBrushProperty);
            AffectsMeasure<TerminalControl>(FontFamilyProperty, FontSizeProperty, FontStyleProperty, FontWeightProperty, BufferSizeProperty);
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

                // Resize underlying pixel buffer by recreating with new dimensions
                var newWidth = (int)value.Width;
                var newHeight = (int)value.Height;
                _pixels = new PixelBuffer((ushort)newWidth, (ushort)newHeight);

                SetAndRaise(BufferSizeProperty, ref _bufferSize, value);
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

                for (int x = xStart; x <= xEnd; x++)
                {
                    var pixel = _pixels[x, y];
                    if (pixel.Width > 0)
                    {
                        sb.Append(pixel.Symbol.GetText());
                    }
                    else
                    {
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

            // Normalize selection (start should be before end)
            if (start.Y > end.Y || (start.Y == end.Y && start.X > end.X))
            {
                (start, end) = (end, start);
            }

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

            // Clamp to buffer bounds
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

                // Copy to clipboard if there's a selection
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

            // Handle Ctrl+C for copy
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
            // Handle Ctrl+A for select all
            else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _selectionStart = new Point(0, 0);
                _selectionEnd = new Point(_pixels.Width - 1, _pixels.Height - 1);
                InvalidateVisual();
                e.Handled = true;
            }
            // Handle Escape to clear selection
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
                "W", // Use 'W' as a wide character for measurement
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

            // Desired size = buffer dimensions * character size
            var desiredWidth = _bufferSize.Width * _charWidth;
            var desiredHeight = _bufferSize.Height * _charHeight;

            return new Size(desiredWidth, desiredHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // Terminal uses all available space for the character grid
            // The actual character sizing will be handled in Render
            return finalSize;
        }

        public override void Render(DrawingContext context)
        {
            // Capture control-level properties into locals so rendering does not read or modify control properties later.
            var localFontFamily = FontFamily;
            var localFontSize = FontSize;
            var localFontStyle = FontStyle;
            var localFontWeight = FontWeight;
            var localSelectionBrush = SelectionBrush;

            // Fill background
            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

            // Calculate cell dimensions based on actual control size
            var cellWidth = Bounds.Width / _bufferSize.Width;
            var cellHeight = Bounds.Height / _bufferSize.Height;

            // Use default typeface for characters without specific styling
            var defaultTypeface = new Typeface(localFontFamily, localFontStyle, localFontWeight);
            
            // Get normalized selection for rendering
            var (selStart, selEnd) = GetNormalizedSelection();
            bool hasSelection = HasSelection();
            
            // Group consecutive cells with same styling for optimized rendering
            var textBuilder = new StringBuilder();
            Color? currentFg = null;
            Color? currentBg = null;
            FontStyle? currentStyle = null;
            FontWeight? currentWeight = null;
            TextDecorationLocation? currentDecoration = null;
            double startX = 0;
            int startCol = 0;

            // Render each row
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

                    // Check if this cell is selected (selection rendering is handled later)
                    bool isSelected = hasSelection && IsInSelection(x, y, selStart, selEnd);
                    
                    // Check if we need to flush the current text run (also flush on decoration/style/weight changes)
                    bool needsFlush = (currentFg.HasValue && pixelFg != currentFg.Value) ||
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
                    
                    // Flush accumulated text
                    if (needsFlush && textBuilder.Length > 0)
                    {
                        // Draw background for the text run (respect run background)
                        if (currentBg.HasValue && currentBg.Value != default)
                        {
                            var bgBrush = new SolidColorBrush(currentBg.Value);
                            var bgRect = new Rect(startX, cellY, (x - startCol + 1) * cellWidth, cellHeight);
                            context.FillRectangle(bgBrush, bgRect);
                        }
                        
                        // Create typeface with the run's styling (use local control defaults, not mutate them)
                        var runTypeface = new Typeface(localFontFamily, currentStyle.Value, currentWeight.Value);
                        
                        // Draw the text run
                        var fgBrush = new SolidColorBrush(currentFg.Value);
                        var formattedText = new FormattedText(
                            textBuilder.ToString(),
                            System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            runTypeface,
                            localFontSize,
                            fgBrush);
                        
                        // Apply text decorations for the run if present
                        if (currentDecoration.HasValue)
                        {
                            var decorations = new TextDecorationCollection();
                            if (currentDecoration.Value.HasFlag(TextDecorationLocation.Underline))
                                decorations.Add(TextDecorations.Underline[0]);
                            if (currentDecoration.Value.HasFlag(TextDecorationLocation.Strikethrough))
                                decorations.Add(TextDecorations.Strikethrough[0]);
                            if (currentDecoration.Value.HasFlag(TextDecorationLocation.Overline))
                                decorations.Add(TextDecorations.Overline[0]);
                            formattedText.SetTextDecorations(decorations);
                        }
                        
                        context.DrawText(formattedText, new Point(startX, cellY));
                        
                        // Reset for next run
                        textBuilder.Clear();
                        currentFg = null;
                        currentBg = null;
                        currentStyle = null;
                        currentWeight = null;
                        currentDecoration = null;
                    }
                    
                    // Draw standalone background (no text)
                    if (pixel.Width == 0 && pixelBg != default)
                    {
                        var bgBrush = new SolidColorBrush(pixelBg);
                        var cellRect = new Rect(x * cellWidth, cellY, cellWidth, cellHeight);
                        context.FillRectangle(bgBrush, cellRect);
                    }
                }
                
                // Reset for next row
                textBuilder.Clear();
                currentFg = null;
                currentBg = null;
                currentStyle = null;
                currentWeight = null;
                currentDecoration = null;
            }
            
            // Draw selection overlay using captured selection brush (do not mutate control properties)
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

        private bool IsInSelection(int x, int y, (int X, int Y) selStart, (int X, int Y) selEnd)
        {
            if (y < selStart.Y || y > selEnd.Y)
                return false;
            
            if (y == selStart.Y && y == selEnd.Y)
                return x >= selStart.X && x <= selEnd.X;
            
            if (y == selStart.Y)
                return x >= selStart.X;
            
            if (y == selEnd.Y)
                return x <= selEnd.X;
            
            return true;
        }
    }
}
