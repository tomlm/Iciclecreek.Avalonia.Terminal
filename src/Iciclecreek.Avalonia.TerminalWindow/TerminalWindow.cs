using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Iciclecreek.Avalonia.TerminalWindow.Buffer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Iciclecreek.Avalonia.TerminalWindow
{
    public class TerminalWindow : Control
    {
        private PixelBuffer _pixels { get; set; } = new PixelBuffer(80, 25);

        // Backing field for direct property
        private PixelSize _bufferSize = new PixelSize(80, 25);

        public static readonly DirectProperty<TerminalWindow, PixelSize> BufferSizeProperty =
            AvaloniaProperty.RegisterDirect<TerminalWindow, PixelSize>(
                nameof(BufferSize),
                o => o._bufferSize,
                (o, v) => o._bufferSize = v);

        public static readonly StyledProperty<FontFamily> FontFamilyProperty =
            AvaloniaProperty.Register<TerminalWindow, FontFamily>(
                nameof(FontFamily),
                defaultValue: FontFamily.Default);

        public static readonly StyledProperty<double> FontSizeProperty =
            AvaloniaProperty.Register<TerminalWindow, double>(
                nameof(FontSize),
                defaultValue: 12);

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

        protected override Size MeasureOverride(Size availableSize)
        {
            // Create a typeface to measure character dimensions
            var typeface = new Typeface(FontFamily);
            var formattedText = new FormattedText(
                "W", // Use 'W' as a wide character for measurement
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                Brushes.Black);

            // Calculate the size needed for the character grid
            var charWidth = formattedText.Width;
            var charHeight = formattedText.Height;

            // Desired size = buffer dimensions * character size
            var desiredWidth = _bufferSize.Width * charWidth;
            var desiredHeight = _bufferSize.Height * charHeight;

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
            // Fill background
            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

            // Calculate cell dimensions based on actual control size
            var cellWidth = Bounds.Width / _bufferSize.Width;
            var cellHeight = Bounds.Height / _bufferSize.Height;

            var typeface = new Typeface(FontFamily);

            // Render each character in the buffer
            for (int y = 0; y < _pixels.Height; y++)
            {
                for (int x = 0; x < _pixels.Width; x++)
                {
                    var pixel = _pixels[x, y];
                    
                    // Calculate cell position
                    var cellX = x * cellWidth;
                    var cellY = y * cellHeight;
                    var cellRect = new Rect(cellX, cellY, cellWidth, cellHeight);

                    // Draw background if not black
                    if (pixel.Background != default)
                    {
                        var bgBrush = new SolidColorBrush(pixel.Background);
                        context.FillRectangle(bgBrush, cellRect);
                    }

                    // Draw character if present
                    if (pixel.Symbol.Character != '\0' && pixel.Symbol.Character != ' ')
                    {
                        var fgBrush = new SolidColorBrush(pixel.Foreground != default ? pixel.Foreground : Colors.White);
                        var formattedText = new FormattedText(
                            pixel.Symbol.GetText(),
                            System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            typeface,
                            FontSize,
                            fgBrush);

                        // Center the character in the cell
                        var textX = cellX + (cellWidth - formattedText.Width) / 2;
                        var textY = cellY + (cellHeight - formattedText.Height) / 2;

                        context.DrawText(formattedText, new Point(textX, textY));
                    }
                }
            }
        }
    }
}
