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
    /// The scrollbar keeps its column when it has nothing to do, rather than handing it to the terminal.
    /// </summary>
    /// <remarks>
    /// <para>Overlaying it on the terminal would keep the width fixed too, and would look tidier when there
    /// is nothing to scroll. It is not worth it: the bar would sit over real cells and eat the mouse events
    /// belonging to them, and full-screen programs turn mouse reporting on. Windows Terminal and xterm both
    /// keep the bar in place for the same reason.</para>
    /// </remarks>
    [AvaloniaTest]
    public void The_scrollbar_keeps_its_column_when_it_goes_inert()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();

        var view = control.View();
        var bar = ScrollBarOf(control);
        var before = view.Terminal.Cols;
        var barWidth = bar.Bounds.Width;

        Assert.That(barWidth, Is.GreaterThan(0), "sanity: the bar has a column to keep");

        view.Terminal.Write("\u001b[?1049h");
        window.UpdateLayout();

        Assert.That(bar.IsEnabled, Is.False, "nothing to scroll in the alternate buffer");
        Assert.That(bar.Bounds.Width, Is.EqualTo(barWidth).Within(0.5), "but the column is still its own");
        Assert.That(view.Terminal.Cols, Is.EqualTo(before), "so the terminal never moved");

        window.Close();
    }
}
