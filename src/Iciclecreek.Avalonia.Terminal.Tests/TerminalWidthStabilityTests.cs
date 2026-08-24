using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The terminal's width must not change because of anything the terminal decided on its own.
///
/// <para>The process is TOLD the width. A program that writes one screen-width per row and lets the cursor
/// wrap, rather than positioning it explicitly, is trusting that number — so moving it underneath the
/// program puts every row after the first somewhere it did not intend.</para>
///
/// <para>The scrollbar was doing exactly that. It lives in an <c>Auto</c> column, so hiding it with
/// <c>IsVisible</c> collapsed the column and handed its width to the terminal. A full-screen program
/// switches to the alternate buffer as its first action, which hid the scrollbar, which resized the
/// terminal while the program was drawing its opening frame.</para>
/// </summary>
[TestFixture]
public class TerminalWidthStabilityTests
{
    private static ScrollBar ScrollBarOf(TerminalControl control) =>
        control.GetVisualDescendants().OfType<ScrollBar>().First();

    /// <summary>The case a full-screen program hits on its very first action.</summary>
    [AvaloniaTest]
    public void Entering_the_alternate_buffer_does_not_change_the_width()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();

        var view = control.View();
        var before = view.Terminal.Cols;
        Assert.That(before, Is.GreaterThan(1), "sanity: the view was arranged, so there is a width to keep");

        view.Terminal.Write("[?1049h");    // switch to the alternate buffer
        window.UpdateLayout();

        Assert.That(view.Terminal.IsAlternateBufferActive, Is.True, "sanity: the switch took");
        Assert.That(view.Terminal.Cols, Is.EqualTo(before),
            "the terminal changed width when the program switched buffers — it was told one number and given another");

        window.Close();
    }

    /// <summary>And back again on the way out, which is where a shell would find its prompt moved.</summary>
    [AvaloniaTest]
    public void Leaving_the_alternate_buffer_does_not_change_the_width()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();

        var view = control.View();
        var before = view.Terminal.Cols;

        view.Terminal.Write("[?1049h");
        window.UpdateLayout();
        view.Terminal.Write("[?1049l");
        window.UpdateLayout();

        Assert.That(view.Terminal.Cols, Is.EqualTo(before));

        window.Close();
    }

    /// <summary>
    /// The scrollbar still hides — it just stops taking the terminal's width with it when it goes.
    /// </summary>
    [AvaloniaTest]
    public void The_scrollbar_hides_without_giving_up_its_room()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();

        var view = control.View();
        var bar = ScrollBarOf(control);
        var before = view.Terminal.Cols;

        view.Terminal.Write("[?1049h");
        window.UpdateLayout();

        Assert.That(bar.Opacity, Is.EqualTo(0), "it has to actually disappear");
        Assert.That(bar.IsHitTestVisible, Is.False, "and stop swallowing clicks while invisible");
        Assert.That(bar.Bounds.Width, Is.GreaterThan(0), "but keep its column, or the terminal grows into it");
        Assert.That(view.Terminal.Cols, Is.EqualTo(before));

        window.Close();
    }
}
