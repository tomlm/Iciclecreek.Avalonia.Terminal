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

    private static TerminalView ViewOf(TerminalControl control)
        => control.GetVisualDescendants().OfType<TerminalView>().Single();

    /// <summary>Every ViewportY change the view announces, as (old, new).</summary>
    private static List<(int Old, int New)> RecordViewportY(TerminalView view)
    {
        var raised = new List<(int, int)>();
        view.PropertyChanged += (_, e) =>
        {
            if (e.Property == TerminalView.ViewportYProperty)
                raised.Add(((int)e.OldValue!, (int)e.NewValue!));
        };
        return raised;
    }

    /// <summary>
    /// ClearScreen moves the viewport itself. A host bound to ViewportY has to hear it, not wait for output.
    /// </summary>
    [AvaloniaTest]
    public void Clearing_the_screen_announces_the_viewport_it_moved()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            FillScrollback(control);
            var view = ViewOf(control);
            var bar = ScrollBarOf(control);
            control.ViewportY = 7;
            Assume.That(bar.Value, Is.EqualTo(7));
            var raised = RecordViewportY(view);

            view.ClearScreen();

            Assert.Multiple(() =>
            {
                Assert.That(view.ViewportY, Is.EqualTo(0), "sanity: the scrollback is gone");
                Assert.That(raised, Is.EqualTo(new[] { (7, 0) }));
                Assert.That(bar.Value, Is.EqualTo(view.ViewportY));
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The case where the thumb itself went stale: at the scrollback cap a line moves no YBase, so nothing
    /// else tells the bar. The view is parked, so the trim moves its position instead. This line is written
    /// off the UI thread, on the reader's exit path, so the notification has to get back onto it too.
    /// </summary>
    [AvaloniaTest]
    public async Task The_exit_line_trimming_a_parked_view_moves_the_thumb()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            var view = ViewOf(control);
            view.Options!.Scrollback = 50;
            FillScrollback(control);
            var bar = ScrollBarOf(control);
            control.ViewportY = 20;
            var max = view.MaxScrollback;
            Assume.That(max, Is.EqualTo(50), "needs a scrollback already at its cap");
            var raised = RecordViewportY(view);

            var connection = new PushConnection();
            view.AttachConnection(connection);
            connection.Done();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (raised.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }

            Assert.Multiple(() =>
            {
                Assert.That(view.MaxScrollback, Is.EqualTo(max), "sanity: YBase did not move, so only ViewportY can tell the bar");
                Assert.That(view.ViewportY, Is.LessThan(20), "sanity: the trim moved the parked view");
                Assert.That(raised.Select(r => r.New), Does.Contain(view.ViewportY));
                Assert.That(bar.Value, Is.EqualTo(view.ViewportY), $"observed Value={bar.Value}");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The launch error is one more of the view's own lines, and at the tail it carries the viewport along.</summary>
    [AvaloniaTest]
    public async Task A_launch_error_announces_the_viewport_it_moved()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            FillScrollback(control);
            var view = ViewOf(control);
            var bar = ScrollBarOf(control);
            control.ViewportY = control.MaxScrollback;
            var before = view.ViewportY;
            var raised = RecordViewportY(view);

            view.SpawnOverride = (_, _) => throw new IOException("no such process");
            await view.LaunchProcess();

            Assert.Multiple(() =>
            {
                Assert.That(view.ViewportY, Is.GreaterThan(before), "sanity: the line scrolled the view");
                Assert.That(raised, Is.EqualTo(new[] { (before, view.ViewportY) }));
                Assert.That(bar.Value, Is.EqualTo(view.ViewportY));
            });
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
    /// With nothing above the screen the bar goes inert — no range, not accepting input — but it STAYS.
    /// </summary>
    /// <remarks>
    /// It used to hide. It lives in the template's <c>Auto</c> column, so hiding it collapsed the column and
    /// handed its width to the terminal, which resized the emulator and told the process about it. See
    /// <c>TerminalWidthStabilityTests</c> for what that did to a program drawing its first frame. Windows
    /// Terminal and xterm both leave the bar in place for the same reason.
    /// </remarks>
    [AvaloniaTest]
    public void The_scrollbar_goes_inert_with_no_scrollback()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            var bar = ScrollBarOf(control);

            Assert.That(control.MaxScrollback, Is.EqualTo(0), "a fresh terminal has nothing above the screen");
            Assert.That(bar.Maximum, Is.EqualTo(0), "nothing to scroll, so no range to offer");
            Assert.That(bar.IsEnabled, Is.False, "and it should not pretend otherwise");
            Assert.That(bar.IsVisible, Is.True, "but it keeps its column — taking it away moves the terminal");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Shrinking the rows with the cursor near the top scrolls nothing off the screen, so there is still no
    /// scrollback and the bar must stay inert.
    /// </summary>
    /// <remarks>
    /// XTerm.NET's resize keeps the buffer's lines and leaves the ones that no longer fit as blank lines
    /// BELOW the screen. MaxScrollback used to be <c>Buffer.Length - Rows</c>, which counted those as
    /// scrollback, so the bar showed a range of 12 the viewport could never move through (the emulator clamps
    /// ViewportY to YBase): a solid thumb that ignored every drag.
    /// </remarks>
    [AvaloniaTest]
    public void Shrinking_the_rows_with_nothing_scrolled_off_leaves_no_scrollback()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            var view = control.View();
            var cell = view.CharHeight;
            Assume.That(cell, Is.GreaterThan(0), "needs measured cell metrics to size the grid");

            control.Height = 24.5 * cell;
            window.UpdateLayout();
            Assume.That(view.Terminal.Rows, Is.EqualTo(24), "the grid should start at 24 rows");

            control.Terminal.Write("prompt\r\n");
            Assume.That(view.Terminal.Buffer.Y, Is.EqualTo(1), "the cursor should be on row 1");

            control.Height = 12.5 * cell;
            window.UpdateLayout();
            Assume.That(view.Terminal.Rows, Is.EqualTo(12), "the grid should have shrunk to 12 rows");

            var bar = ScrollBarOf(control);

            Assert.Multiple(() =>
            {
                Assert.That(control.MaxScrollback, Is.EqualTo(0),
                    $"nothing scrolled off the top. observed MaxScrollback={control.MaxScrollback} "
                    + $"Buffer.Length={view.Terminal.Buffer.Length} YBase={view.Terminal.Buffer.YBase}");
                Assert.That(bar.Maximum, Is.EqualTo(0), $"no range to offer. observed Maximum={bar.Maximum}");
                Assert.That(bar.IsEnabled, Is.False, "and nothing to drag");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A re-grid moves YBase, so it has to tell a binding to MaxScrollback, not only the built-in scrollbar.
    /// </summary>
    /// <remarks>
    /// The resize used to raise ViewportLines alone. The scrollbar re-reads MaxScrollback off that, so it
    /// stayed right, but anything bound to MaxScrollback kept the old value until the next output arrived.
    /// </remarks>
    [AvaloniaTest]
    public void Resizing_the_rows_raises_MaxScrollback_when_YBase_moves()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            var view = control.View();
            var cell = view.CharHeight;
            Assume.That(cell, Is.GreaterThan(0), "needs measured cell metrics to size the grid");

            control.Height = 24.5 * cell;
            window.UpdateLayout();
            Assume.That(view.Terminal.Rows, Is.EqualTo(24), "the grid should start at 24 rows");

            // Cursor onto the bottom row, so a shrink has to push the rows above it into the scrollback.
            for (var i = 0; i < 23; i++)
                control.Terminal.Write($"line {i}\r\n");
            Assume.That(view.MaxScrollback, Is.EqualTo(0), "nothing has scrolled off yet");

            var raised = new List<int>();
            view.PropertyChanged += (_, e) =>
            {
                if (e.Property == TerminalView.MaxScrollbackProperty)
                    raised.Add((int)e.NewValue!);
            };

            control.Height = 12.5 * cell;
            window.UpdateLayout();
            Assume.That(view.Terminal.Rows, Is.EqualTo(12), "the grid should have shrunk to 12 rows");
            Assume.That(view.MaxScrollback, Is.GreaterThan(0), "the shrink should have moved rows into the scrollback");
            var afterShrink = view.MaxScrollback;

            Assert.That(raised, Is.EqualTo(new[] { afterShrink }),
                $"a shrink that moved YBase to {afterShrink} should say so once, with the new value");

            raised.Clear();
            control.Height = 24.5 * cell;
            window.UpdateLayout();
            Assume.That(view.Terminal.Rows, Is.EqualTo(24), "the grid should have grown back to 24 rows");
            Assume.That(view.MaxScrollback, Is.LessThan(afterShrink), "growing should pull scrollback back on screen");

            Assert.That(raised, Is.EqualTo(new[] { view.MaxScrollback }),
                $"a grow that moved YBase from {afterShrink} to {view.MaxScrollback} should say so too");
        }
        finally
        {
            window.Close();
        }
    }
}
