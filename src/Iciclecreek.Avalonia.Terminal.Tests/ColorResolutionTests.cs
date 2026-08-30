using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Avalonia.Terminal;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// How a cell's attributes become the colours it is drawn in.
/// </summary>
/// <remarks>
/// <para>Three things were wrong here and they pull in the same direction: the renderer was deciding
/// colours the emulator had already decided. Bold invented a brighter shade of whatever it found;
/// dim was applied twice to one kind of text and not at all to another; and a default background
/// resolved to the control's brush rather than the terminal's, so OSC 11 stopped at the emulator.</para>
/// <para>Asserted through the extension methods the renderer calls rather than through pixels. What
/// is under test is the resolution, and a pixel test would be answering a question about Avalonia's
/// compositor instead.</para>
/// </remarks>
[TestFixture]
public class ColorResolutionTests
{
    private static readonly string Esc = ((char)0x1B).ToString();

    private static (TerminalView view, Window window) Realised()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    /// <summary>Writes <paramref name="sgrAndText"/> and hands back the first cell it produced.</summary>
    private static (XTerm.Buffer.BufferCell cell, XTerm.Common.ColorSnapshot palette) FirstCell(
        TerminalView view, string sgrAndText)
    {
        view.Terminal.Write(sgrAndText);
        Dispatcher.UIThread.RunJobs();

        var line = view.Terminal.Buffer.GetLine(view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y);
        Assert.That(line, Is.Not.Null, "the write produced no line");
        return (line![0], view.Terminal.Colors.Take());
    }

    private static Color Of(IBrush brush) => ((ISolidColorBrush)brush).Color;

    /// <summary>A 0xRRGGBB palette entry as a colour. The library's own copy of this is internal.</summary>
    private static Color Rgb(int v)
        => Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    private static double OpacityOf(IBrush brush) => ((ISolidColorBrush)brush).Opacity;

    // -------------------------------------------------------------- bold

    [AvaloniaTest]
    public void Bold_selects_the_bright_half_of_the_sixteen()
    {
        // SGR 31 is palette index 1, and bold means index 9 -- not "index 1, but lighter". That is
        // what the option is named after and what xterm.js does.
        var (view, window) = Realised();
        try
        {
            var (cell, palette) = FirstCell(view, $"{Esc}[1;31mX");

            var expected = Rgb(palette[9]);
            Assert.That(cell.GetForegroundColor(palette), Is.EqualTo(expected),
                "bold red is palette 9, whatever the theme has put there");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Bold_leaves_a_24_bit_colour_exactly_as_the_program_asked_for_it()
    {
        // The old code added 85 to each channel of whatever it resolved, so a program that named a
        // colour precisely got a different one back the moment it also asked for bold. #8B0000 is
        // not #DC5555.
        var (view, window) = Realised();
        try
        {
            var (cell, palette) = FirstCell(view, $"{Esc}[1;38;2;139;0;0mX");

            Assert.That(cell.GetForegroundColor(palette), Is.EqualTo(Color.FromRgb(139, 0, 0)),
                "a 24-bit colour has no bright counterpart to select, so bold changes nothing about it");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Bold_leaves_a_256_palette_index_alone()
    {
        // Index 100 is a colour the user chose from the 256. Brightening it invented a shade that is
        // in no palette at all.
        var (view, window) = Realised();
        try
        {
            var (cell, palette) = FirstCell(view, $"{Esc}[1;38;5;100mX");

            Assert.That(cell.GetForegroundColor(palette),
                Is.EqualTo(Rgb(palette[100])));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Bold_stays_in_the_dim_half_when_the_option_is_off()
    {
        // The option exists to be turned off, and until now it was carried by the emulator and read
        // by nobody -- a setting that compiled, persisted, and did nothing.
        var (view, window) = Realised();
        try
        {
            var (cell, palette) = FirstCell(view, $"{Esc}[1;31mX");

            Assert.That(cell.GetForegroundColor(palette, boldIsBright: false),
                Is.EqualTo(Rgb(palette[1])),
                "with the option off, bold red stays palette 1");
        }
        finally { window.Close(); }
    }

    // --------------------------------------------------------------- dim

    [AvaloniaTest]
    public void Dim_is_applied_once_to_coloured_text()
    {
        // It used to be applied twice: 0.6 on each channel while resolving, and again as opacity on
        // the brush. The colour must come back untouched, with the dimming entirely in the opacity.
        var (view, window) = Realised();
        try
        {
            var (cell, palette) = FirstCell(view, $"{Esc}[2;31mX");
            var brush = cell.GetForegroundBrush(palette, Brushes.White);

            Assert.That(Of(brush), Is.EqualTo(Rgb(palette[1])),
                "the channels must be left alone -- dim lives in the opacity");
            Assert.That(OpacityOf(brush), Is.LessThan(1.0), "and the opacity must actually carry it");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Dim_reaches_text_using_the_default_foreground()
    {
        // SGR 2 with no colour before it is the ordinary way to write de-emphasised text, and it did
        // nothing at all: the resolver returns early for a default foreground, so the multiply it
        // used to reach was never run, and the default branch of the brush never applied opacity.
        var (view, window) = Realised();
        try
        {
            var (cell, palette) = FirstCell(view, $"{Esc}[2mX");
            var brush = cell.GetForegroundBrush(palette, Brushes.White);

            Assert.That(OpacityOf(brush), Is.LessThan(1.0),
                "dim with no colour set is the commonest way to use it");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Text_that_is_not_dim_is_fully_opaque()
    {
        var (view, window) = Realised();
        try
        {
            var (cell, palette) = FirstCell(view, $"{Esc}[31mX");

            Assert.That(OpacityOf(cell.GetForegroundBrush(palette, Brushes.White)), Is.EqualTo(1.0));
        }
        finally { window.Close(); }
    }

    // -------------------------------------------------------- default background

    [AvaloniaTest]
    public void A_default_background_resolves_to_the_colour_OSC_11_set()
    {
        // Where it matters is inverse text and the block cursor -- the two places a BACKGROUND
        // becomes something that gets painted. The foreground already resolved a default against the
        // emulator; this did not, so OSC 10 reached the screen and OSC 11 stopped at the emulator.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}]11;#112233{Esc}\\");
            Dispatcher.UIThread.RunJobs();

            var (cell, palette) = FirstCell(view, "X");
            var brush = cell.GetBackgroundBrush(palette, Brushes.Black);

            Assert.That(Of(brush), Is.EqualTo(Color.FromRgb(0x11, 0x22, 0x33)),
                "a program that set its own default background must get it back when it inverts");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_translucent_host_background_is_left_alone()
    {
        // The limit of the rule above. A background carrying alpha is a host asking to be seen
        // through, and no RGB palette entry can express that -- so substituting one would make a
        // deliberately translucent terminal opaque wherever a cell inverts. Render already draws
        // this line when it paints the surface, and this is the same line in the same place.
        var (view, window) = Realised();
        try
        {
            var (cell, palette) = FirstCell(view, "X");
            var byAlpha = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
            Assert.That(cell.GetBackgroundBrush(palette, byAlpha), Is.SameAs(byAlpha));

            // The OTHER way to be translucent, and independent of the first: a host that writes
            // Opacity = 0.8 on an opaque colour is asking for exactly the same thing, and only the
            // alpha form was being honoured.
            var byOpacity = new SolidColorBrush(Colors.Black) { Opacity = 0.8 };
            Assert.That(cell.GetBackgroundBrush(palette, byOpacity), Is.SameAs(byOpacity));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_default_background_still_paints_nothing_of_its_own()
    {
        // The transparency behaviour is a SEPARATE decision, made by the renderer from
        // GetBackgroundColor -- and it must not have moved. A default-background cell still declares
        // no colour, so the renderer still skips the fill and a translucent host still shows through.
        var (view, window) = Realised();
        try
        {
            var (cell, palette) = FirstCell(view, "X");

            Assert.That(cell.GetBackgroundColor(palette), Is.Null,
                "resolving the brush must not have turned every cell into one that paints");
        }
        finally { window.Close(); }
    }
}
