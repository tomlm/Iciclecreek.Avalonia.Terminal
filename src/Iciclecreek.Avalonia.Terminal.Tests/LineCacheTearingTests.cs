using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using NUnit.Framework;
using XTerm.Buffer;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// What the renderer is allowed to remember about a row the pty is writing to at the same time.
///
/// <para>The reader thread writes into the buffer while <see cref="TerminalView.Render"/> reads it on the UI
/// thread, and the two are not synchronised — the render does not take the lock the read loop writes under,
/// because holding it for a whole frame would stop the reader for that long. So a row CAN be rewritten
/// mid-read. Drawing a mixture
/// of before and after for one frame is survivable; storing that mixture as the line's cache is not, because a
/// cache is dropped only by the next write to that line. A sprite that has moved on leaves a row nothing
/// writes to again, and the mixture stays on screen — the trail of leftover glyphs asciiquarium draws.</para>
/// </summary>
[TestFixture]
public class LineCacheTearingTests
{
    /// <summary>
    /// The writer only ever leaves the row in one of two states, so a cached row matching neither was
    /// assembled from both and describes a row that never existed.
    /// </summary>
    /// <remarks>
    /// Every column carries its own colour, which is what makes the window wide enough to hit: a run per
    /// cell is a shaped text run per cell, so reading the row takes long enough for a write to land in the
    /// middle of it. That is not a contrivance — it is what a colour-per-character full-screen program like
    /// asciiquarium produces on every line.
    /// </remarks>
    [AvaloniaTest]
    public void A_row_rewritten_mid_render_is_never_cached_half_written()
    {
        var control = new TerminalControl { Process = "", Background = Brushes.Black };
        var window = TerminalHost.Show(control);

        try
        {
            window.UpdateLayout();

            var view = control.View();
            var terminal = view.Terminal;
            var cols = terminal.Cols;

            Assert.That(cols, Is.GreaterThan(8), "sanity: a degenerate grid would make every assertion vacuous");

            // Home, then exactly one row wide, so the cursor is left pending-wrap and the row never
            // scrolls out from under the reader.
            var allA = Row(cols, 'A', 41, 45);
            var allB = Row(cols, 'B', 44, 46);

            // What each of the two states looks like once cached, read back with nothing else running.
            terminal.Write(allA);
            var stateA = Signature(Render(view, terminal));
            terminal.Write(allB);
            var stateB = Signature(Render(view, terminal));

            Assert.That(stateA, Is.Not.Empty, "sanity: the row was not cached at all, so there is nothing to compare");
            Assert.That(stateA, Is.Not.EqualTo(stateB), "sanity: the two states must be distinguishable");

            using var stop = new CancellationTokenSource();
            var writer = Task.Factory.StartNew(
                () =>
                {
                    // Paced, not flat out. A writer with no gaps clears the cache again before the
                    // reader below can look at it, and every sample comes back "no cache at all".
                    while (!stop.IsCancellationRequested)
                    {
                        terminal.Write(allA);
                        Thread.Sleep(1);
                        terminal.Write(allB);
                        Thread.Sleep(1);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            var interleaved = 0;
            var torn = 0;
            var renders = 0;
            var uncached = 0;
            var clock = Stopwatch.StartNew();

            while (clock.ElapsedMilliseconds < 3000)
            {
                var line = terminal.Buffer.GetLine(terminal.Buffer.ViewportY);
                if (line is null)
                    continue;

                var before = RowText(line, cols);
                var runs = Render(view, terminal);
                renders++;

                // Proof the race was actually run rather than merely allowed for.
                if (RowText(line, cols) != before)
                    interleaved++;

                if (runs is null)
                {
                    uncached++;
                    continue;
                }

                var signature = Signature(runs);
                if (signature != stateA && signature != stateB)
                    torn++;
            }

            stop.Cancel();
            writer.Wait(TimeSpan.FromSeconds(5));

            Assert.That(interleaved, Is.GreaterThan(0),
                $"no write landed during any of {renders} renders, so nothing about tearing was tested");
            Assert.That(torn, Is.Zero,
                $"{torn} of {renders - uncached} cached rows matched neither state the writer left behind: "
                + "the renderer stored a row that never existed");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>One full row, a colour per column so the row costs a shaped run per cell to read.</summary>
    private static string Row(int cols, char ch, int even, int odd)
    {
        var sb = new StringBuilder("[1;1H");
        for (var x = 0; x < cols; x++)
            sb.Append("[").Append(x % 2 == 0 ? even : odd).Append('m').Append(ch);
        return sb.Append("[0m").ToString();
    }

    /// <summary>Renders the view and hands back the runs the top row was left cached with.</summary>
    private static List<TerminalView.CachedTextRun>? Render(TerminalView view, XTerm.Terminal terminal)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
            view.Render(context);

        return terminal.Buffer.GetLine(terminal.Buffer.ViewportY)?.Cache as List<TerminalView.CachedTextRun>;
    }

    /// <summary>A cached row's runs as text: where each starts, how wide it is and what colour it carries.</summary>
    private static string Signature(List<TerminalView.CachedTextRun>? runs)
    {
        if (runs is null)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            var colour = run.Background is ISolidColorBrush brush ? brush.Color.ToString() : "none";
            sb.Append(run.StartX).Append(':').Append(run.CellCount).Append(':').Append(colour).Append(' ');
        }
        return sb.ToString();
    }

    private static string RowText(BufferLine line, int cols)
    {
        var chars = new char[cols];
        for (var x = 0; x < cols && x < line.Length; x++)
        {
            var content = line[x].Content;
            chars[x] = string.IsNullOrEmpty(content) ? ' ' : content[0];
        }
        return new string(chars);
    }
}
