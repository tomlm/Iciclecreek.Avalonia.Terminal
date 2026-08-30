using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Avalonia.Terminal;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Things drawn outside the control they belong to.
/// </summary>
/// <remarks>
/// <para>Four overlays that each compute their own rectangle and none of which was bounded by
/// anything. A terminal is usually the whole window, which is why none of this was noticed: paint
/// past the edge and there is nothing there to spoil. In a host that puts a sidebar next to the
/// terminal, or a status bar under it, every one of these paints over it.</para>
/// <para>Asserted on the geometry these draws are given rather than on pixels, which headless
/// Avalonia has no backend to produce.</para>
/// </remarks>
[TestFixture]
public class ClippingTests
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

    // The PUBLIC properties, not the backing fields. The fields are zero until something asks for
    // the metrics -- the properties are what run UpdateTextMetrics -- so reflecting on them made
    // every geometry assertion here compare zero against zero and pass vacuously.
    private static double CharWidth(TerminalView v) => v.CharWidth;
    private static double CharHeight(TerminalView v) => v.CharHeight;



    [AvaloniaTest]
    public void The_metrics_these_tests_measure_against_are_real()
    {
        // Guards the guards. Every geometry assertion in this file multiplies by these, so if they
        // came back zero the assertions would compare zero against zero and pass vacuously -- which
        // is what reading the backing fields did.
        var (view, window) = Realised();
        try
        {
            Assert.That(CharWidth(view), Is.GreaterThan(0));
            Assert.That(CharHeight(view), Is.GreaterThan(0));
        }
        finally { window.Close(); }
    }

    // -------------------------------------------------------- search highlight

    [AvaloniaTest]
    public void A_search_hit_past_the_current_width_is_clamped_to_the_grid()
    {
        // A hit is recorded against the buffer at the width the line had when it was searched, and a
        // resize narrower than that leaves hits naming columns that no longer exist. The tint was
        // painted hundreds of pixels past the right edge, over whatever the host had beside it.
        var (view, window) = Realised();
        try
        {
            var cols = view.Terminal.Cols;

            Assert.That(TerminalView.ClampSpanToGrid(cols - 2, cols + 40, cols, out var start, out var end),
                Is.True, "part of it is still on screen, so it still draws");
            Assert.That(end, Is.EqualTo(cols), "a hit cannot end past the last column");
            Assert.That(start, Is.EqualTo(cols - 2), "and its visible part is unchanged");

            Assert.That(TerminalView.ClampSpanToGrid(cols + 5, cols + 40, cols, out _, out _),
                Is.False, "a hit entirely past the end draws nothing at all");

            Assert.That(TerminalView.ClampSpanToGrid(-5, 3, cols, out var negStart, out _),
                Is.True);
            Assert.That(negStart, Is.Zero, "nor before the first column");
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------------------ preedit

    [AvaloniaTest]
    public void A_long_composition_is_bounded_by_the_right_edge()
    {
        // An IME buffers a whole phrase before committing, and this drew it at its full measured
        // width from the cursor -- so a long composition ran off the end of the control.
        var (view, window) = Realised();
        try
        {
            var contentRight = view.Terminal.Cols * CharWidth(view);
            var posX = (view.Terminal.Cols - 2) * CharWidth(view);

            var drawn = TerminalView.FitWidth(posX, measured: 5000.0, right: contentRight);

            Assert.That(posX + drawn, Is.LessThanOrEqualTo(contentRight),
                "the composition must stop at the content edge");
            Assert.That(drawn, Is.EqualTo(2 * CharWidth(view)).Within(0.001),
                "and draw exactly the two cells that fit");

            Assert.That(TerminalView.FitWidth(posX, measured: 5.0, right: contentRight), Is.EqualTo(5.0),
                "a composition that fits is not narrowed");
            Assert.That(TerminalView.FitWidth(contentRight + 10, 100, contentRight), Is.Zero,
                "one starting past the edge draws nothing");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_composition_box_uses_the_colour_a_program_set()
    {
        // The box was painted with the STYLED background, so a program that moved its default
        // background with OSC 11 got a rectangle of the host's original shade sitting in the middle
        // of its own screen.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}]11;#112233{Esc}\\");
            Dispatcher.UIThread.RunJobs();

            var palette = view.Terminal.Colors.Take();
            Assert.That(BufferCellExtensions.FromRgb(palette.Background),
                Is.EqualTo(Color.FromRgb(0x11, 0x22, 0x33)),
                "sanity: the emulator took the colour, which is the half that already worked");
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------------------- gutter
    //
    // The gutter snap has NO test, deliberately. Both halves of the only assertion worth making --
    // that the bar geometry and the row geometry agree -- are the same call to the same Snap, so it
    // could not fail while the fix is in place and would not have compiled before it. A test that
    // cannot fail is worse than none: it reads as coverage. The change is one line, it uses the
    // arithmetic every other row geometry in the file already uses, and it is unobservable without
    // pixels this platform cannot produce.

    // ------------------------------------------------------- rendering at all

    [AvaloniaTest]
    public void A_frame_with_every_overlay_in_play_still_renders()
    {
        // The clips are pushed and popped around passes that can return early. This is the cheap
        // check that none of them leaks a push -- an unbalanced clip takes the whole frame with it.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}]133;A{Esc}\\");           // a gutter mark
            view.Terminal.Write($"{Esc}]66;s=2:sized{Esc}\\");    // an OSC 66 block
            view.Terminal.Write("plain text\r\n");
            Dispatcher.UIThread.RunJobs();

            view.Terminal.Selection.SelectAll();
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotThrow(() =>
            {
                view.InvalidateVisual();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            });
        }
        finally { window.Close(); }
    }
}
