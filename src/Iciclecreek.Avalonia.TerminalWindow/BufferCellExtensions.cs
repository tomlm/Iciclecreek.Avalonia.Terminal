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

        /// <summary>
        /// The brush to fill a cell's background with. A cell using the DEFAULT background resolves to
        /// the emulator's own default, which is what OSC 11 sets and OSC 111 resets.
        /// </summary>
        /// <param name="defaultBrush">
        /// Used only when the terminal's default background cannot be expressed as one colour — a host
        /// that set a gradient. The palette is RGB, so such a brush cannot round-trip through it.
        /// </param>
        /// <remarks>
        /// <para>The mirror of <see cref="GetForegroundBrush"/>, which it was not. The foreground
        /// resolved a default to the emulator's palette and this resolved it to the CONTROL's brush,
        /// so OSC 10 reached the screen and OSC 11 did not.</para>
        /// <para>It shows up on inverse text, which is where a background becomes something that gets
        /// PAINTED rather than left alone: a program that set its own default background and then
        /// printed inverse got the host's original colour for the glyphs. Same for the block cursor,
        /// which draws the character under it in the background colour.</para>
        /// <para>This does not make a default-background cell start painting. That decision is made by
        /// the renderer from <see cref="GetBackgroundColor"/>, which still answers null for a default
        /// and still lets a translucent host show through.</para>
        /// </remarks>
        public static IBrush GetBackgroundBrush(this BufferCell cell, ColorSnapshot palette, IBrush defaultBrush)
        {
            var bgColor = cell.GetBackgroundColor(palette);
            if (bgColor.HasValue)
            {
                return new SolidColorBrush(bgColor.Value);
            }

            // Only an OPAQUE host brush is replaced, matching the rule Render already applies when it
            // paints the surface. A background carrying alpha is a host asking to be seen through,
            // and no RGB palette entry can express that -- substituting one silently makes a
            // deliberately translucent terminal opaque wherever a cell inverts or the block cursor
            // lands. Two different answers to "is the default background the emulator's or the
            // host's" in one renderer would be worse than either answer.
            // BOTH transparency channels. A brush is see-through if its colour carries alpha or if
            // the brush itself is set below full opacity, and those are independent -- a host that
            // writes Opacity = 0.8 on an opaque colour is asking for the same thing as one that
            // writes alpha, and only the second was being honoured.
            if (IsFullyOpaque(defaultBrush))
                return new SolidColorBrush(FromRgb(palette.Background));

            return defaultBrush;
        }

        /// <summary>
        /// A cell's foreground colour, or <see langword="null"/> when it uses the terminal's DEFAULT
        /// foreground.
        /// </summary>
        /// <param name="boldIsBright">
        /// The emulator's <c>DrawBoldTextInBrightColors</c>. Passed in because this is a RENDERER
        /// option: XTerm.NET carries the setting and has no renderer to apply it, so the host is the
        /// only place it can mean anything.
        /// </param>
        /// <remarks>
        /// <para>Bold selects the BRIGHT half of the sixteen, which is all it has ever meant. It used
        /// to add 85 to each channel of whatever the colour turned out to be — including a 24-bit
        /// colour a program had asked for exactly, and including a 256-palette index the user had
        /// chosen. #8B0000 bold is not #DC5555; it is the colour the program named.</para>
        /// <para>So it applies to palette indices 0-7 only, and becomes index + 8: the same rule
        /// xterm.js applies, and the reason the option is named after bright COLORS rather than
        /// brightness. A default foreground is not a palette index and is left alone, which is also
        /// what xterm.js does.</para>
        /// <para>Dim is no longer applied here at all. It was applied twice — 0.6 on each channel
        /// here and again as opacity in <see cref="GetForegroundBrush"/> — and never at all to text
        /// using the default foreground, which returns before reaching this. Opacity is the one that
        /// survives, because it is the one that composites correctly over whatever is behind the
        /// text.</para>
        /// </remarks>
        public static Color? GetForegroundColor(this BufferCell cell, ColorSnapshot palette,
                                                bool boldIsBright = true)
        {
            var color = cell.Attributes.GetFgColor();
            var mode = cell.Attributes.GetFgColorMode();
            if (color == 256)
                return null;  // Default color

            // Mode 0 is the palette, for both the basic sixteen and the 256 index; mode 1 is direct
            // RGB, which has no bright counterpart to select.
            if (boldIsBright && mode == 0 && color < 8 && cell.Attributes.IsBold())
                color += 8;

            return cell.ExtractColor(color, mode, palette);
        }

        /// <summary>
        /// The brush to draw a cell's text in. A cell using the DEFAULT foreground resolves to the
        /// emulator's own default, which is what OSC 10 sets and OSC 110 resets.
        /// </summary>
        /// <param name="defaultBrush">
        /// Used only when the terminal's default foreground cannot be expressed as one colour — a host that
        /// set a gradient. The palette is RGB, so such a brush cannot round-trip through it.
        /// </param>
        public static IBrush GetForegroundBrush(this BufferCell cell, ColorSnapshot palette, IBrush defaultBrush,
                                               bool boldIsBright = true)
        {
            // The one place dim is applied. It used to be applied here AND as a 0.6 multiply on each
            // channel while the colour was resolved, so dim coloured text was dimmed twice over --
            // while dim text using the DEFAULT foreground fell straight past both, because the
            // resolver returns before it reaches the multiply, and was not dimmed at all.
            var dim = cell.Attributes.IsDim() ? DimOpacity : 1.0;

            var fgColor = cell.GetForegroundColor(palette, boldIsBright);
            if (fgColor.HasValue)
                return new SolidColorBrush(fgColor.Value, dim);

            // The default foreground is the emulator's, not the control's. They agree until a program
            // changes it — and when one does, the program is the one that should win.
            //
            // Dimmed here too, which is the half that was missing: SGR 2 with no SGR 3x before it is
            // the ordinary way to write de-emphasised text, and it did nothing.
            if (defaultBrush is ISolidColorBrush)
                return new SolidColorBrush(FromRgb(palette.Foreground), dim);

            // A gradient or image foreground reaches here, and it is returned unchanged. Dim COULD be
            // pushed onto a copy of it, but every other property of such a brush would have to be
            // copied with it to avoid mutating the host's own object, and a host that set a gradient
            // foreground on a terminal is already outside what any of this reasons about.
            return defaultBrush;
        }

        /// <summary>
        /// How much of a dim cell's foreground survives.
        /// </summary>
        /// <remarks>
        /// Kept at the value this host has always used rather than moved to xterm.js's 0.5. The bug
        /// was that dim was applied twice to one kind of text and not at all to another; the strength
        /// of a single application is a separate question and not one this change answers.
        /// </remarks>
        private const double DimOpacity = 0.4;

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

        /// <summary>
        /// Whether <paramref name="brush"/> hides what is behind it completely.
        /// </summary>
        /// <remarks>
        /// Two independent channels have to agree: the colour's own alpha, and the brush's Opacity.
        /// Either one below full means the host is asking to be seen through, and no RGB palette
        /// entry can express that -- so the emulator's default colour must not be substituted for it.
        /// </remarks>
        internal static bool IsFullyOpaque(IBrush? brush)
            => brush is ISolidColorBrush { Color.A: 255 } solid && solid.Opacity >= 1.0;

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
