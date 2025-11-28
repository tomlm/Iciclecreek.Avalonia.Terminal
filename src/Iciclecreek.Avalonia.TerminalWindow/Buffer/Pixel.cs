using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable NotResolvedInText
namespace Iciclecreek.Avalonia.TerminalWindow.Buffer
{
    internal readonly struct Pixel : IEquatable<Pixel>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Pixel()
            : this(Colors.Transparent, Colors.Transparent, Symbol.Space)
        {
        }

        /// <summary>
        ///     Make a pixel foreground with transparent background
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="foregroundColor"></param>
        /// <param name="style"></param>
        /// <param name="weight"></param>
        /// <param name="textDecorations"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Pixel(Color background,
            Color foreground,
            in Symbol symbol,
            FontStyle style = FontStyle.Normal,
            FontWeight weight = FontWeight.Normal,
            TextDecorationLocation? textDecorations = null) 
        {
            Foreground = foreground;
            Background = background;
            Symbol = symbol;
            Weight = weight;
            Style = style;
            TextDecoration = textDecorations;
        }


        // Pixel empty is a non-pixel. It has no symbol, no color, no weight, no style, no text decoration, and no background.
        // it is used only when a multichar sequence overlaps a pixel making it a non-entity.
        public static Pixel Space => new(Colors.Transparent, Colors.Transparent, Symbol.Space);

#pragma warning disable CA1051 // Do not declare visible instance fields
        public readonly Color Background;
        public readonly Color Foreground;
        public readonly Symbol Symbol;
        public readonly FontWeight? Weight;
        public readonly FontStyle? Style;
        public readonly TextDecorationLocation? TextDecoration;
#pragma warning restore CA1051 // Do not declare visible instance fields

        [JsonIgnore] public ushort Width => Symbol.Width;

        public bool Equals(Pixel other)
        {
            return Background.Equals(other.Background) &&
                   Foreground.Equals(other.Foreground) &&
                   Symbol.Equals(other.Symbol) &&
                   Weight == other.Weight &&
                   Style == other.Style &&
                   TextDecoration == other.TextDecoration ;
        }

        //public Pixel Shade()
        //{
        //    return new Pixel(Foreground.Shade(), Background.Shade(), CaretStyle);
        //}

        //public Pixel Brighten()
        //{
        //    return new Pixel(Foreground.Brighten(), Background.Brighten(), CaretStyle);
        //}

        //public Pixel Invert()
        //{
        //    return new Pixel(new PixelForeground(Foreground.Symbol,
        //            Background.Color, // background color becomes the new foreground color
        //            Foreground.Weight,
        //            Foreground.Style,
        //            Foreground.TextDecoration),
        //        new PixelBackground(Foreground.Color),
        //        CaretStyle);
        //}

        ///// <summary>
        /////     Blend the pixelAbove with this pixel.
        ///// </summary>
        ///// <param name="pixelAbove"></param>
        ///// <returns></returns>
        ///// <exception cref="ArgumentOutOfRangeException"></exception>
        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        //public Pixel Blend(Pixel pixelAbove)
        //{
        //    PixelForeground newForeground;
        //    CaretStyle newCaretStyle;

        //    Color aboveBgColor = pixelAbove.Background.Color;
        //    byte aboveBgA = aboveBgColor.A;

        //    bool isNoForegroundOnTop;

        //    switch (aboveBgA)
        //    {
        //        // Fast path: fully opaque overlay -> just return the overlay pixel
        //        case 0xFF:
        //            return pixelAbove;
        //        // Fast path: fully transparent overlay with no foreground and no caret change -> nothing to do
        //        case 0x0:
        //        {
        //            isNoForegroundOnTop = pixelAbove.Foreground.IsNothingToDraw();
        //            if (isNoForegroundOnTop && pixelAbove.CaretStyle == CaretStyle.None)
        //                return this;
        //            newForeground = isNoForegroundOnTop ? Foreground : Foreground.Blend(pixelAbove.Foreground);
        //            newCaretStyle = CaretStyle.Blend(pixelAbove.CaretStyle);
        //        }
        //            break;
        //        default:
        //            newCaretStyle = pixelAbove.CaretStyle;
        //            isNoForegroundOnTop = pixelAbove.Foreground.IsNothingToDraw();
        //            if (isNoForegroundOnTop)
        //                // merge the PixelForeground color with the pixelAbove background color
        //                newForeground = new PixelForeground(Foreground.Symbol,
        //                    MergeColors(Foreground.Color, aboveBgColor),
        //                    Foreground.Weight,
        //                    Foreground.Style,
        //                    Foreground.TextDecoration);
        //            else
        //                newForeground = pixelAbove.Foreground;

        //            break;
        //    }

        //    // Background is always blended
        //    var newBackground = new PixelBackground(MergeColors(Background.Color, aboveBgColor));

        //    return new Pixel(newForeground, newBackground, newCaretStyle);
        //}

        ///// <summary>
        /////     merge colors with alpha blending
        ///// </summary>
        ///// <param name="target"></param>
        ///// <param name="source"></param>
        ///// <returns>source blended into target</returns>
        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        //private static Color MergeColors(in Color target, in Color source)
        //{
        //    // Fast paths to avoid calling into the ConsoleColorMode when not needed
        //    byte a = source.A;
        //    if (a == 0x00) return target; // fully transparent source
        //    if (a == 0xFF) return source; // fully opaque source

        //    return source; // ConsoleColorMode.Value.Blend(target, source);
        //}

        public override bool Equals([NotNullWhen(true)] object obj)
        {
            return obj is Pixel pixel && Equals(pixel);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Foreground, Background, Symbol, Style, Weight, TextDecoration);
        }

        public static bool operator ==(Pixel left, Pixel right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Pixel left, Pixel right)
        {
            return !left.Equals(right);
        }
    }
}