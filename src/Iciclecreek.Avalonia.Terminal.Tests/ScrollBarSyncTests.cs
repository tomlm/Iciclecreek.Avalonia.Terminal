using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The scrollbar has to keep tracking the viewport (issue #20).
///
/// <para>The drag bug itself — the thumb outrunning the cursor upwards — is not reachable from here: it needs
/// a real pointer gesture through Avalonia's Track, and `ScrollBar.Scroll` is raised internally rather than
/// being something a test can trigger. What these cover is the regression the FIX could introduce. The fix
/// stops the control writing ScrollBar.Value while a drag is being applied, and getting that suppression
/// wrong would leave the scrollbar silently ignoring the viewport — a worse bug than the one being fixed,
/// and one that would not be obvious until someone scrolled.</para>
/// </summary>
[TestFixture]
public class ScrollBarSyncTests
{
    private static ScrollBar ScrollBarOf(TerminalControl control)
    {
        var bar = control.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault();
        Assert.That(bar, Is.Not.Null, "PART_ScrollBar was not realised, so nothing below is meaningful");
        return bar!;
    }

    /// <summary>
    /// Fill the buffer so there is something to scroll through, then touch the viewport so the control is
    /// told about it.
    /// </summary>
    /// <remarks>
    /// The nudge is not ceremony. UpdateScrollBar only runs off TerminalView's property changes, and those
    /// are raised by the PTY read loop — writing straight to the emulator, as this does, notifies nobody, so
    /// the scrollbar would still be reporting an empty buffer. That gap is real but it is not what #20 is
    /// about, so these tests go through the viewport rather than pretending direct writes update the bar.
    /// </remarks>
    private static void FillScrollback(TerminalControl control, int lines = 200)
    {
        for (var i = 0; i < lines; i++)
            control.Terminal.Write($"line {i}\r\n");

        // Two steps, because one of them may be a no-op and a no-op raises nothing: writing to the emulator
        // already leaves the viewport pinned at the bottom, so assigning that same value changes nothing.
        control.ViewportY = control.MaxScrollback;
        control.ViewportY = 0;
    }

    [AvaloniaTest]
    public void The_scrollbar_range_matches_the_buffer()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            FillScrollback(control);
            var bar = ScrollBarOf(control);

            Assert.Multiple(() =>
            {
                Assert.That(bar.Minimum, Is.EqualTo(0), "0 is the top of the scrollback");
                Assert.That(bar.Maximum, Is.EqualTo(control.MaxScrollback),
                    $"observed Maximum={bar.Maximum} MaxScrollback={control.MaxScrollback}");
                Assert.That(bar.ViewportSize, Is.EqualTo(control.ViewportLines),
                    "the thumb size comes from this, so a wrong value makes the thumb the wrong length");
                Assert.That(bar.IsVisible, Is.True, "there is scrollback, so the bar should show");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The case the fix could have broken: a viewport change that did NOT come from the scrollbar must still
    /// move the thumb. Scrolling with the wheel, or a host setting ViewportY, has to be reflected.
    /// </summary>
    [AvaloniaTest]
    public void Scrolling_the_viewport_moves_the_thumb()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            FillScrollback(control);
            var bar = ScrollBarOf(control);
            Assume.That(control.MaxScrollback, Is.GreaterThan(10), "needs room to scroll");

            control.ViewportY = 7;
            Assert.That(bar.Value, Is.EqualTo(7),
                $"the thumb ignored a viewport change. observed Value={bar.Value} ViewportY={control.ViewportY}");

            control.ViewportY = 0;
            Assert.That(bar.Value, Is.EqualTo(0), $"observed Value={bar.Value}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>As the buffer grows the range has to grow with it, or the thumb maps to the wrong lines.</summary>
    [AvaloniaTest]
    public void The_scrollbar_range_grows_with_the_buffer()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            FillScrollback(control, 50);
            var bar = ScrollBarOf(control);
            var rangeBefore = bar.Maximum;

            FillScrollback(control, 150);

            Assert.Multiple(() =>
            {
                Assert.That(bar.Maximum, Is.GreaterThan(rangeBefore),
                    $"observed Maximum={bar.Maximum} was {rangeBefore}");
                Assert.That(bar.Maximum, Is.EqualTo(control.MaxScrollback),
                    $"observed Maximum={bar.Maximum} MaxScrollback={control.MaxScrollback}");
                Assert.That(bar.Value, Is.EqualTo(control.ViewportY),
                    $"the thumb drifted from the viewport. Value={bar.Value} ViewportY={control.ViewportY}");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A full-screen application takes over the alternate buffer, where there is no scrollback to speak of,
    /// so the bar hides rather than offering a range that means nothing.
    /// </summary>
    [AvaloniaTest]
    public void The_scrollbar_hides_with_no_scrollback()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            var bar = ScrollBarOf(control);

            Assert.That(control.MaxScrollback, Is.EqualTo(0), "a fresh terminal has nothing above the screen");
            Assert.That(bar.IsVisible, Is.False, "nothing to scroll, so nothing to show");
        }
        finally
        {
            window.Close();
        }
    }
}
