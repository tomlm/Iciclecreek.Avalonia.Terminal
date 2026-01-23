using Avalonia.Media;
using System;
using XTerm.Buffer;

namespace Iciclecreek.Avalonia.Terminal
{
    public static class BufferCellExtensions
    {

        public static FontWeight GetFontWeight(this BufferCell cell)
        {
            if (cell.Attributes.IsBold())
                return FontWeight.Bold;
            if (cell.Attributes.IsDim())
                return FontWeight.Thin;
            return FontWeight.Normal;
        }

        public static FontStyle GetFontStyle(this BufferCell cell)
        {
            if (cell.Attributes.IsItalic())
                return FontStyle.Italic;
            return FontStyle.Normal;
        }

        public static TextDecorationCollection? GetTextDecorations(this BufferCell cell)
        {
            var decorations = new TextDecorationCollection();
            if (cell.Attributes.IsUnderline())
                return TextDecorations.Underline;
            if (cell.Attributes.IsStrikethrough())
                return TextDecorations.Strikethrough;
            if (cell.Attributes.IsOverline())
                return TextDecorations.Overline;
            return null;
        }

        /// <summary>
        /// Gets the background color as RGB values.
        /// Returns null if using default color or palette mode.
        /// </summary>
        /// <returns>A tuple (R, G, B) with RGB values 0-255, or null if not using RGB mode.</returns>
        public static Color? GetBackgroundColor(this BufferCell cell)
        {
            var color = cell.Attributes.GetBgColor();
            var mode = cell.Attributes.GetBgColorMode();

            if (color == 257) return null;  // Default color

            return cell.ExtractColor(color, mode);
        }

        public static IBrush GetBackgroundBrush(this BufferCell cell, IBrush defaultBrush)
        {
            var bgColor = cell.GetBackgroundColor();
            if (bgColor.HasValue)
            {
                return new SolidColorBrush(bgColor.Value);
            }
            return defaultBrush;
        }

        /// <summary>
        /// Gets the foreground color as RGB values.
        /// Returns null if using default color or palette mode.
        /// </summary>
        /// <returns>A tuple (R, G, B) with RGB values 0-255, or null if not using RGB mode.</returns>
        public static Color? GetForegroundColor(this BufferCell cell)
        {
            var color = cell.Attributes.GetFgColor();
            var mode = cell.Attributes.GetFgColorMode();
            if (color == 256)
                return null;  // Default color

            var realColor = cell.ExtractColor(color, mode);

            if (cell.Attributes.IsBold())
            {
                // Increase brightness for bold text
                var c = realColor.Value;
                byte r = (byte)Math.Min(255, c.R + 85);
                byte g = (byte)Math.Min(255, c.G + 85);
                byte b = (byte)Math.Min(255, c.B + 85);
                return Color.FromRgb(r, g, b);
            }
            else if (cell.Attributes.IsDim())
            {
                // Decrease brightness for dim text
                var c = realColor.Value;
                byte r = (byte)(c.R * 0.6);
                byte g = (byte)(c.G * 0.6);
                byte b = (byte)(c.B * 0.6);
                return Color.FromRgb(r, g, b);
            }
            return realColor;
        }

        public static IBrush GetForegroundBrush(this BufferCell cell, IBrush defaultBrush)
        {
            var fgColor = cell.GetForegroundColor();
            if (fgColor.HasValue)
            {
                if (cell.Attributes.IsDim())
                    return new SolidColorBrush(fgColor.Value, .4);
                return new SolidColorBrush(fgColor.Value);
            }
            return defaultBrush;
        }

        private static Color? ExtractColor(this BufferCell cell, int color, int mode)
        {
            Color? realColor;
            if (mode == 1)  // RGB mode
            {
                int r = (color >> 16) & 0xFF;
                int g = (color >> 8) & 0xFF;
                int b = color & 0xFF;
                realColor = Color.FromRgb((byte)r, (byte)g, (byte)b);
            }
            else
                realColor = PalleteToColor(color);  // Palette mode
            return realColor;
        }

        // XTerm 256 color palette
        private static readonly Color[] _xtermPalette = InitializeXTermPalette();

        private static Color[] InitializeXTermPalette()
        {
            var palette = new Color[256];

            // 0-15: Basic 16 colors (standard xterm color scheme)
            // Normal colors (0-7)
            palette[0] = Color.FromRgb(0, 0, 0);         // Black
            palette[1] = Color.FromRgb(205, 0, 0);       // Red
            palette[2] = Color.FromRgb(0, 205, 0);       // Green
            palette[3] = Color.FromRgb(205, 205, 0);     // Yellow
            palette[4] = Color.FromRgb(0, 0, 238);       // Blue
            palette[5] = Color.FromRgb(205, 0, 205);     // Magenta
            palette[6] = Color.FromRgb(0, 205, 205);     // Cyan
            palette[7] = Color.FromRgb(229, 229, 229);   // White (Light Gray)
            // Bright colors (8-15)
            palette[8] = Color.FromRgb(127, 127, 127);   // Bright Black (Gray)
            palette[9] = Color.FromRgb(255, 0, 0);       // Bright Red
            palette[10] = Color.FromRgb(0, 255, 0);      // Bright Green
            palette[11] = Color.FromRgb(255, 255, 0);    // Bright Yellow
            palette[12] = Color.FromRgb(92, 92, 255);    // Bright Blue
            palette[13] = Color.FromRgb(255, 0, 255);    // Bright Magenta
            palette[14] = Color.FromRgb(0, 255, 255);    // Bright Cyan
            palette[15] = Color.FromRgb(255, 255, 255);  // Bright White

            // 16-231: 216 color cube (6x6x6)
            int index = 16;
            for (int r = 0; r < 6; r++)
            {
                for (int g = 0; g < 6; g++)
                {
                    for (int b = 0; b < 6; b++)
                    {
                        byte rv = (byte)(r > 0 ? r * 40 + 55 : 0);
                        byte gv = (byte)(g > 0 ? g * 40 + 55 : 0);
                        byte bv = (byte)(b > 0 ? b * 40 + 55 : 0);
                        palette[index++] = Color.FromRgb(rv, gv, bv);
                    }
                }
            }

            // 232-255: Grayscale ramp
            for (int i = 0; i < 24; i++)
            {
                byte gray = (byte)(8 + i * 10);
                palette[232 + i] = Color.FromRgb(gray, gray, gray);
            }

            return palette;
        }

        private static Color PalleteToColor(int paletteIndex)
        {
            if (paletteIndex < 0 || paletteIndex >= 256)
                return Colors.White; // Default fallback

            return _xtermPalette[paletteIndex];
        }
    }


}
