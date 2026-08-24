using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Foreground and Background have to survive every hop from the host down to the emulator.
///
/// <para>The chain is four links long — window to control, control to view, view to what is painted, and
/// view to the emulator's own palette — and a break in any one of them looks identical from the outside:
/// the terminal renders in its default white-on-black and assignments appear to do nothing. The demo's
/// ManagedTerminalWindow had exactly that break, with the two colour bindings commented out while every
/// other property was bound, so a host could set a colour and watch nothing happen.</para>
///
/// <para>Asserted against the property values that the render path and the emulator actually read, rather
/// than against the bindings being present — a binding to the wrong property would still be a binding.</para>
/// </summary>
[TestFixture]
public class ColorBindingTests
{
    private static readonly IBrush Pink = Brushes.Pink;
    private static readonly IBrush Blue = Brushes.Blue;

    /// <summary>
    /// The colours a caller passes in an object initialiser must reach the view.
    ///
    /// <para>This is the case that was broken: initialisation runs during construction, BEFORE a caller's
    /// object initialiser, so a control that reads the colours once while building itself captures the
    /// defaults and never sees what the caller asked for.</para>
    /// </summary>
    [AvaloniaTest]
    public void A_colour_set_in_an_object_initialiser_reaches_the_view()
    {
        var window = new TerminalWindow { Process = "", Background = Pink, Foreground = Blue }.Realise();
        var view = window.Control().View();

        Assert.That(view.Background, Is.EqualTo(Pink), "Background never arrived at the view");
        Assert.That(view.Foreground, Is.EqualTo(Blue), "Foreground never arrived at the view");

        window.Close();
    }

    /// <summary>A colour assigned after the window exists must follow, not just the one it was built with.</summary>
    [AvaloniaTest]
    public void A_colour_assigned_later_still_follows()
    {
        var window = new TerminalWindow { Process = "" }.Realise();
        var view = window.Control().View();

        window.Background = Pink;
        window.Foreground = Blue;

        Assert.That(view.Background, Is.EqualTo(Pink), "a later assignment was dropped — is the binding one-shot?");
        Assert.That(view.Foreground, Is.EqualTo(Blue));

        window.Close();
    }

    /// <summary>
    /// The middle hop on its own: a TerminalControl's colours reach the view its template realises.
    /// </summary>
    [AvaloniaTest]
    public void The_control_passes_its_colours_to_the_view()
    {
        var control = new TerminalControl { Process = "", Background = Pink, Foreground = Blue };
        var window = TerminalHost.Show(control);

        var view = control.View();
        Assert.That(view.Background, Is.EqualTo(Pink));
        Assert.That(view.Foreground, Is.EqualTo(Blue));

        window.Close();
    }

    /// <summary>
    /// The emulator's palette is what answers OSC 10/11, and programs query OSC 11 to decide whether they
    /// are on a light or a dark terminal. Nothing synced it, so a light-themed host answered "dark".
    /// </summary>
    [AvaloniaTest]
    public void The_emulator_palette_learns_the_real_colours()
    {
        var control = new TerminalControl
        {
            Process = "",
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x22, 0x33)),
        };
        var window = TerminalHost.Show(control);
        var view = control.View();

        Assert.That(view.Terminal.Colors.Background, Is.EqualTo(0xFFFFFF),
            "the emulator still reports its built-in background, so OSC 11 answers a lie");
        Assert.That(view.Terminal.Colors.Foreground, Is.EqualTo(0x112233));
        Assert.That(view.Terminal.Colors.IsLightBackground, Is.True,
            "a white host must read as light, or every program that asks picks the wrong theme");

        window.Close();
    }

    /// <summary>Re-theming after the emulator exists has to move the palette too, not just the repaint.</summary>
    [AvaloniaTest]
    public void Re_theming_moves_the_emulator_palette()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        var view = control.View();

        control.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x40));

        Assert.That(view.Terminal.Colors.Background, Is.EqualTo(0x008040),
            "the palette kept the colour the emulator was built with");

        window.Close();
    }

    /// <summary>
    /// A gradient has no single colour to report, so the palette keeps what it had. Answering with an
    /// arbitrary stop would be a confident wrong answer to "are you light or dark".
    /// </summary>
    [AvaloniaTest]
    public void A_gradient_background_leaves_the_palette_alone()
    {
        var control = new TerminalControl { Process = "", Background = Brushes.Black };
        var window = TerminalHost.Show(control);
        var view = control.View();

        control.Background = new LinearGradientBrush
        {
            GradientStops = { new GradientStop(Colors.Red, 0), new GradientStop(Colors.Blue, 1) },
        };

        Assert.That(view.Terminal.Colors.Background, Is.EqualTo(0x000000),
            "no single colour to report, so the previous one stands");

        window.Close();
    }
}
