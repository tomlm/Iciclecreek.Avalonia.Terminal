using Avalonia.Media;
using System;
using XTerm.Buffer;
using XTerm.Common;

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

        /// <summary>
        /// Strikethrough and overline, which Avalonia draws well enough.
        /// </summary>
        /// <remarks>
        /// Underline is deliberately not here. Avalonia has no curly decoration, and SGR 58 gives the
        /// underline a colour independent of the text — neither is expressible this way, so it is
        /// drawn by hand in <c>TerminalView.DrawUnderline</c>.
        /// </remarks>
        public static TextDecorationCollection? GetTextDecorations(this BufferCell cell)
        {
            if (cell.Attributes.IsStrikethrough())
                return TextDecorations.Strikethrough;
            if (cell.Attributes.IsOverline())
                return TextDecorations.Overline;
            return null;
        }

        /// <summary>
        /// Gets the background colour of a cell, or <see langword="null"/> when the cell uses the terminal's
        /// DEFAULT background.
        /// </summary>
        /// <remarks>
        /// Default deliberately stays null rather than resolving to the palette's background: the renderer
        /// uses "has a background of its own" to decide whether to paint a rectangle at all, and a cell that
        /// simply wants the default must leave the surface alone so a translucent host still shows through.
        /// </remarks>
        public static Color? GetBackgroundColor(this BufferCell cell, ColorSnapshot palette)
        {
            var color = cell.Attributes.GetBgColor();
            var mode = cell.Attributes.GetBgColorMode();

            if (color == 257) return null;  // Default color

            return cell.ExtractColor(color, mode, palette);
        }

        public static IBrush GetBackgroundBrush(this BufferCell cell, ColorSnapshot palette, IBrush defaultBrush)
        {
            var bgColor = cell.GetBackgroundColor(palette);
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
        public static Color? GetForegroundColor(this BufferCell cell, ColorSnapshot palette)
        {
            var color = cell.Attributes.GetFgColor();
            var mode = cell.Attributes.GetFgColorMode();
            if (color == 256)
                return null;  // Default color

            var realColor = cell.ExtractColor(color, mode, palette);

            if (realColor != null)
            {
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
            }
            return realColor;
        }

        /// <summary>
        /// The brush to draw a cell's text in. A cell using the DEFAULT foreground resolves to the
        /// emulator's own default, which is what OSC 10 sets and OSC 110 resets.
        /// </summary>
        /// <param name="defaultBrush">
        /// Used only when the terminal's default foreground cannot be expressed as one colour — a host that
        /// set a gradient. The palette is RGB, so such a brush cannot round-trip through it.
        /// </param>
        public static IBrush GetForegroundBrush(this BufferCell cell, ColorSnapshot palette, IBrush defaultBrush)
        {
            var fgColor = cell.GetForegroundColor(palette);
            if (fgColor.HasValue)
            {
                if (cell.Attributes.IsDim())
                    return new SolidColorBrush(fgColor.Value, .4);
                return new SolidColorBrush(fgColor.Value);
            }

            // The default foreground is the emulator's, not the control's. They agree until a program
            // changes it — and when one does, the program is the one that should win.
            if (defaultBrush is ISolidColorBrush)
                return new SolidColorBrush(FromRgb(palette.Foreground));

            return defaultBrush;
        }

        /// <summary>
        /// The colour an underline is drawn in, or null when it follows the text.
        /// </summary>
        /// <remarks>
        /// SGR 58 sets this independently of the foreground, which is the whole point: an LSP marks
        /// an error with a red squiggle under text that stays its normal colour.
        /// </remarks>
        public static Color? GetUnderlineColor(this BufferCell cell, ColorSnapshot palette)
        {
            if (!cell.Attributes.TryGetUnderlineColor(out var color, out var mode))
                return null;

            return cell.ExtractColor(color, mode, palette);
        }

        private static Color? ExtractColor(this BufferCell cell, int color, int mode, ColorSnapshot palette)
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
                realColor = PalleteToColor(color, palette);  // Palette mode
            return realColor;
        }

        /// <summary>A 0xRRGGBB value from the emulator's palette, as a colour.</summary>
        internal static Color FromRgb(int rgb) =>
            Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));


        /// <summary>
        /// Resolves an indexed colour against the EMULATOR's palette, so OSC 4 actually reaches the screen.
        /// </summary>
        /// <remarks>
        /// This used to read a static table private to the renderer, which meant a program could set a
        /// palette entry, have the emulator accept it, and see nothing change on screen. That table is gone;
        /// the emulator seeds the same xterm defaults itself.
        /// </remarks>
        private static Color PalleteToColor(int paletteIndex, ColorSnapshot palette)
        {
            if (paletteIndex < 0 || paletteIndex >= 256)
                return Colors.White; // Default fallback

            return FromRgb(palette[paletteIndex]);
        }
    }


}
