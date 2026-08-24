using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Iciclecreek.Avalonia.Terminal;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Foreground and Background are the terminal's DEFAULT colour pair, not a backdrop the control paints.
///
/// <para>That is what SGR 39/49 resolve to, what OSC 10/11 report and set, and what OSC 110/111 reset to.
/// Before this the control kept its own idea of the default in an Avalonia brush while the emulator kept a
/// separate one in its palette, and neither knew about the other: a program could set a colour, have the
/// emulator accept it, and see nothing change — the renderer was reading a different table entirely.</para>
/// </summary>
[TestFixture]
public class DefaultColorTests
{
    private const string Esc = "";

    private static (TerminalView view, Window window) Realised(Color foreground, Color background)
    {
        var control = new TerminalControl
        {
            Process = "",
            Foreground = new SolidColorBrush(foreground),
            Background = new SolidColorBrush(background),
        };
        var window = TerminalHost.Show(control);
        return (control.View(), window);
    }

    /// <summary>A cell that asked for no colour draws in the terminal's default, which is the host's.</summary>
    [AvaloniaTest]
    public void A_default_cell_draws_in_the_hosts_colour()
    {
        var (view, window) = Realised(Color.FromRgb(0x11, 0x22, 0x33), Colors.Black);

        view.Terminal.Write("x");
        var cell = view.Terminal.Buffer.Lines[0]![0];
        var brush = cell.GetForegroundBrush(view.Terminal.Colors.Take(), view.Foreground);

        Assert.That((brush as ISolidColorBrush)?.Color, Is.EqualTo(Color.FromRgb(0x11, 0x22, 0x33)));

        window.Close();
    }

    /// <summary>
    /// OSC 10 sets the default foreground. The renderer has to follow it, or a program retheming the
    /// terminal is accepted and then ignored.
    /// </summary>
    [AvaloniaTest]
    public void Osc_10_moves_what_a_default_cell_draws_in()
    {
        var (view, window) = Realised(Colors.White, Colors.Black);

        view.Terminal.Write("x");
        view.Terminal.Write($"{Esc}]10;#FF0000{Esc}\\");

        var cell = view.Terminal.Buffer.Lines[0]![0];
        var brush = cell.GetForegroundBrush(view.Terminal.Colors.Take(), view.Foreground);

        Assert.That((brush as ISolidColorBrush)?.Color, Is.EqualTo(Colors.Red),
            "the program set the default foreground and the renderer kept painting the old one");

        window.Close();
    }

    /// <summary>
    /// OSC 111 resets the background to the DEFAULT — which must be the colour the host chose, not the
    /// emulator's built-in. This is why the pair is seeded before the emulator is built rather than
    /// assigned afterwards: an assignment moves the current value and leaves the reset target behind.
    /// </summary>
    [AvaloniaTest]
    public void Resetting_returns_to_the_hosts_colour_not_a_builtin()
    {
        var (view, window) = Realised(Colors.White, Color.FromRgb(0x20, 0x20, 0x20));

        view.Terminal.Write($"{Esc}]11;#FF00FF{Esc}\\");
        Assert.That(view.Terminal.Colors.Background, Is.EqualTo(0xFF00FF), "sanity: the program moved it");

        view.Terminal.Write($"{Esc}]111{Esc}\\");

        Assert.That(view.Terminal.Colors.Background, Is.EqualTo(0x202020),
            "reset landed on a colour the host never chose");

        window.Close();
    }

    /// <summary>
    /// OSC 4 sets an indexed palette entry. Indexed colours used to resolve against a static table private
    /// to the renderer, so this was accepted by the emulator and invisible on screen.
    /// </summary>
    [AvaloniaTest]
    public void Osc_4_reaches_the_screen()
    {
        var (view, window) = Realised(Colors.White, Colors.Black);

        view.Terminal.Write($"{Esc}]4;1;#00FF00{Esc}\\");   // colour 1 (red) redefined as green
        view.Terminal.Write($"{Esc}[31mx");                 // SGR 31 — draw in colour 1

        var cell = view.Terminal.Buffer.Lines[0]![0];
        var brush = cell.GetForegroundBrush(view.Terminal.Colors.Take(), view.Foreground);

        Assert.That((brush as ISolidColorBrush)?.Color, Is.EqualTo(Colors.Lime),
            "the renderer resolved colour 1 against its own table instead of the emulator's palette");

        window.Close();
    }

    /// <summary>
    /// A light host must read as light. This is the answer programs use to pick a theme, and getting it
    /// wrong is how a dark theme ends up painted onto a white terminal.
    /// </summary>
    [AvaloniaTest]
    public void A_light_host_reads_as_light()
    {
        var (view, window) = Realised(Colors.Black, Colors.White);

        Assert.That(view.Terminal.Colors.IsLightBackground, Is.True);
        Assert.That(view.Terminal.Colors.Background, Is.EqualTo(0xFFFFFF));

        window.Close();
    }
}
