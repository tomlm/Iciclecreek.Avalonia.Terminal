using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Avalonia.Terminal;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// BoldColor: bold text using the DEFAULT foreground drawn in a colour of the theme's choosing,
/// Ghostty's <c>bold-color</c>.
/// </summary>
/// <remarks>
/// Asserted through the extension methods the renderer calls, as <see cref="ColorResolutionTests"/>
/// does, plus the emulator's palette for where the value has to land.
/// </remarks>
[TestFixture]
public class BoldColorTests
{
    private static readonly string Esc = ((char)0x1B).ToString();
    private static readonly Color Orange = Color.FromRgb(0xFF, 0xAA, 0x00);

    private static (TerminalView view, Window window) Realised(Color? boldColor)
    {
        var view = new TerminalView { Process = "", BoldColor = boldColor };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    /// <summary>Writes <paramref name="sgrAndText"/> on a line of its own and hands back its first cell.</summary>
    private static XTerm.Buffer.BufferCell Cell(TerminalView view, string sgrAndText)
    {
        view.Terminal.Write($"\r\n{sgrAndText}{Esc}[0m");
        Dispatcher.UIThread.RunJobs();

        var line = view.Terminal.Buffer.GetLine(view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y);
        Assert.That(line, Is.Not.Null, "the write produced no line");
        return line![0];
    }

    private static Color Rgb(int v)
        => Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));

    [AvaloniaTest]
    public void Bold_default_foreground_takes_the_bold_colour()
    {
        var (view, window) = Realised(Orange);
        try
        {
            var cell = Cell(view, $"{Esc}[1mX");
            var palette = view.Terminal.Colors.Take();

            Assert.Multiple(() =>
            {
                Assert.That(palette.Bold, Is.EqualTo(0xFFAA00), "the property must reach the emulator's theme");
                Assert.That(cell.GetForegroundColor(palette), Is.EqualTo(Orange));
                Assert.That(cell.GetForegroundColor(palette, boldIsBright: false), Is.EqualTo(Orange),
                    "the bold colour does not depend on brightening");
                Assert.That(((ISolidColorBrush)cell.GetForegroundBrush(palette, Brushes.White)).Color,
                    Is.EqualTo(Orange));
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Unset_leaves_bold_default_text_in_the_default()
    {
        var (view, window) = Realised(null);
        try
        {
            var cell = Cell(view, $"{Esc}[1mX");
            var palette = view.Terminal.Colors.Take();

            Assert.Multiple(() =>
            {
                Assert.That(view.BoldColor, Is.Null, "unset by default");
                Assert.That(palette.Bold, Is.Null);
                Assert.That(cell.GetForegroundColor(palette), Is.Null, "a default cell still reports as default");
                Assert.That(((ISolidColorBrush)cell.GetForegroundBrush(palette, Brushes.White)).Color,
                    Is.EqualTo(Rgb(palette.Foreground)));
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Bold_palette_colour_still_follows_the_bright_option()
    {
        // The bold colour is for the DEFAULT foreground only. Bold red is a colour the program
        // named, and stays the option's business.
        var (view, window) = Realised(Orange);
        try
        {
            var cell = Cell(view, $"{Esc}[1;31mX");
            var palette = view.Terminal.Colors.Take();

            Assert.Multiple(() =>
            {
                Assert.That(cell.GetForegroundColor(palette, boldIsBright: true), Is.EqualTo(Rgb(palette[9])));
                Assert.That(cell.GetForegroundColor(palette, boldIsBright: false), Is.EqualTo(Rgb(palette[1])));
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Bold_24_bit_colour_is_left_as_named()
    {
        var (view, window) = Realised(Orange);
        try
        {
            var cell = Cell(view, $"{Esc}[1;38;2;139;0;0mX");

            Assert.That(cell.GetForegroundColor(view.Terminal.Colors.Take()),
                Is.EqualTo(Color.FromRgb(139, 0, 0)));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// #000100 packs to the value 256, the default foreground's number, but in RGB mode. Bold must
    /// not mistake it for a default cell and hand it the bold colour.
    /// </summary>
    [AvaloniaTest]
    public void Bold_24_bit_000100_is_not_taken_for_the_default()
    {
        var (view, window) = Realised(Orange);
        try
        {
            var cell = Cell(view, $"{Esc}[1;38;2;0;1;0mX");

            Assert.That(cell.GetForegroundColor(view.Terminal.Colors.Take()),
                Is.EqualTo(Color.FromRgb(0, 1, 0)));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Text_that_is_not_bold_is_unaffected()
    {
        var (view, window) = Realised(Orange);
        try
        {
            var palette = view.Terminal.Colors.Take();
            var plain = Cell(view, "X");
            var dim = Cell(view, $"{Esc}[2mX");

            Assert.Multiple(() =>
            {
                Assert.That(plain.GetForegroundColor(palette), Is.Null);
                Assert.That(((ISolidColorBrush)plain.GetForegroundBrush(palette, Brushes.White)).Color,
                    Is.EqualTo(Rgb(palette.Foreground)));
                Assert.That(((ISolidColorBrush)dim.GetForegroundBrush(palette, Brushes.White)).Color,
                    Is.EqualTo(Rgb(palette.Foreground)));
            });
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Changing it after startup must reach lines already on screen, whose cached runs hold brushes
    /// resolved under the old colour and are replayed rather than rebuilt.
    /// </summary>
    [AvaloniaTest]
    public void A_live_change_reaches_lines_already_on_screen()
    {
        var (view, window) = Realised(null);
        try
        {
            var cell = Cell(view, $"{Esc}[1mX");
            var line = view.Terminal.Buffer.GetLine(view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y)!;
            line.Cache = new object();

            view.BoldColor = Orange;

            Assert.Multiple(() =>
            {
                Assert.That(line.Cache, Is.Null, "the cached runs were resolved under the old colour");
                Assert.That(cell.GetForegroundColor(view.Terminal.Colors.Take()), Is.EqualTo(Orange));
            });

            line.Cache = new object();
            view.BoldColor = null;

            Assert.Multiple(() =>
            {
                Assert.That(line.Cache, Is.Null, "unsetting it is a change too");
                Assert.That(view.Terminal.Colors.Take().Bold, Is.Null);
            });
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The live change goes through a new theme, which re-seeds the default pair. It has to be the
    /// host's CURRENT pair, not the one the emulator was built with.
    /// </summary>
    [AvaloniaTest]
    public void A_live_change_keeps_a_foreground_set_after_startup()
    {
        var (view, window) = Realised(null);
        try
        {
            view.Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56));
            view.BoldColor = Orange;

            Assert.That(view.Terminal.Colors.Take().Foreground, Is.EqualTo(0x123456));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Set_on_a_control_reaches_the_view_and_the_emulator()
    {
        var control = new TerminalControl { Process = "", BoldColor = Orange };
        var host = TerminalHost.Show(control);
        try
        {
            var view = control.View();
            Assert.Multiple(() =>
            {
                Assert.That(view.BoldColor, Is.EqualTo(Orange));
                Assert.That(view.Terminal.Colors.Take().Bold, Is.EqualTo(0xFFAA00));
            });

            control.BoldColor = Colors.Lime;
            Assert.That(view.BoldColor, Is.EqualTo(Colors.Lime), "and live, after the template applied");
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public void Set_on_a_window_reaches_the_view()
    {
        var window = new TerminalWindow { Process = "", BoldColor = Orange }.Realise();
        try
        {
            var view = window.Control().View();
            Assert.That(view.BoldColor, Is.EqualTo(Orange));

            window.BoldColor = null;
            Assert.That(view.BoldColor, Is.Null);
        }
        finally { window.Close(); }
    }
}
