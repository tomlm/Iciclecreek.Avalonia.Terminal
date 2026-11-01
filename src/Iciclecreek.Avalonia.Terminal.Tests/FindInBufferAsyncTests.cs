using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;
using XTerm.Search;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The search that runs off the UI thread: a slice of rows at a time under the terminal lock, its
/// result published on the UI thread.
/// </summary>
/// <remarks>
/// Nothing here waits on timing. <c>SearchSliceScanned</c> runs on the scanning thread between slices
/// with the lock released, so a test parks the scan there, does what the reader or the user would do
/// in that gap, and lets it go. The buffers are just big enough for several 4,096-row slices.
/// </remarks>
[TestFixture]
public class FindInBufferAsyncTests
{
    private static readonly string Esc = ((char)0x1B).ToString();

    /// <summary>How long a parked scan or a wait for the reader gets before the test calls it stuck.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static (TerminalView view, Window window) Realised(int bufferSize = 20_000)
    {
        var view = new TerminalView { Process = "", BufferSize = bufferSize };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    /// <summary>
    /// Enough lines for more than three slices, with matches on short lines and matches straddling the
    /// wrap of long ones -- some of which land on a slice boundary.
    /// </summary>
    private static void FillWithWrappedMatches(TerminalView view, int lines = 13_000)
    {
        var cols = view.Terminal.Cols;
        var text = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            if (i % 40 == 7)
                text.Append(new string('x', cols - 3)).Append("needle").Append(new string('y', cols));
            else if (i % 9 == 0)
                text.Append("needle ").Append(i).Append(" short");
            else
                text.Append("line ").Append(i);
            text.Append("\r\n");
        }
        view.Terminal.Write(text.ToString());
    }

    /// <summary>Short lines only, so trimming any number of them never leaves half a match at the top.</summary>
    private static string ShortLines(int from, int count)
    {
        var text = new StringBuilder();
        for (var i = from; i < from + count; i++)
            text.Append(i % 7 == 0 ? $"needle {i}\r\n" : $"line {i}\r\n");
        return text.ToString();
    }

    /// <summary>
    /// Every published run, with match ids renumbered from zero in order. Renumbered because ids are
    /// only an order: a scan that dropped trimmed matches starts its survivors at a later id than a
    /// fresh scan of the same rows would.
    /// </summary>
    private static List<(int Row, int Column, int Cols, int Match)> Hits(TerminalView view)
    {
        var search = view.SearchResults!;
        var hits = new List<(int, int, int, int)>();
        var ids = new Dictionary<int, int>();
        for (var row = 0; row < view.Terminal.Buffer.Lines.Length; row++)
        {
            foreach (var hit in search.HitsOnRow(row))
            {
                if (!ids.TryGetValue(hit.MatchId, out var match))
                    ids[hit.MatchId] = match = ids.Count;
                hits.Add((hit.BufferRow, hit.Column, hit.Cols, match));
            }
        }
        return hits;
    }

    private static async Task<bool> WasCancelled(Task task)
    {
        try { await task; return false; }
        catch (OperationCanceledException) { return true; }
    }

    /// <summary>
    /// Runs <see cref="WhileParked"/> after the scan's first slice, and with <c>hold</c> keeps it there
    /// until released. Later slices run straight on.
    /// </summary>
    private sealed class ParkAfterFirstSlice(bool hold = true)
    {
        private int _slices;
        public readonly ManualResetEventSlim Parked = new(false);
        public readonly ManualResetEventSlim Release = new(!hold);
        public Action? WhileParked;

        /// <summary>Slices scanned so far, across every search the view runs.</summary>
        public int Slices => Volatile.Read(ref _slices);

        /// <summary>Whether the first slice boundary was reached on the UI thread.</summary>
        public bool RanOnUiThread { get; private set; }

        public void OnSlice(bool last)
        {
            if (Interlocked.Increment(ref _slices) != 1)
                return;

            RanOnUiThread = Dispatcher.UIThread.CheckAccess();
            WhileParked?.Invoke();
            Parked.Set();
            Release.Wait(Patience);
        }
    }

    [AvaloniaTest]
    public async Task Finds_what_the_synchronous_search_finds()
    {
        var (view, window) = Realised();
        try
        {
            FillWithWrappedMatches(view);
            Assert.That(view.Terminal.Buffer.Lines.Length, Is.GreaterThan(3 * TerminalView.SearchSliceRows),
                "the buffer has to span several slices for this to test slicing at all");

            var slices = 0;
            view.SearchSliceScanned = _ => Interlocked.Increment(ref slices);

            // A current match, so the publish has something to reset.
            view.FindInBuffer("needle");
            view.FindNext();
            Assert.That(view.SearchCurrentIndex, Is.Not.EqualTo(-1));

            var count = await view.FindInBufferAsync("needle");
            var sliced = Hits(view);

            Assert.That(slices, Is.GreaterThanOrEqualTo(4));
            Assert.That(view.SearchCurrentIndex, Is.EqualTo(-1), "no match chosen until the box steps");
            Assert.That(view.SearchHitCount, Is.EqualTo(count));

            Assert.That(view.FindInBuffer("needle"), Is.EqualTo(count));
            Assert.That(sliced, Is.EqualTo(Hits(view)));
            Assert.That(sliced.Select(h => h.Match).Distinct().Count(),
                        Is.LessThan(sliced.Count), "some matches should have straddled a wrap");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Truncates_where_the_synchronous_search_does()
    {
        var (view, window) = Realised();
        try
        {
            FillWithWrappedMatches(view);

            var count = await view.FindInBufferAsync("e");
            var sliced = Hits(view);

            Assert.That(count, Is.EqualTo(BufferSearch.MaxHits));
            Assert.That(view.SearchTruncated, Is.True);

            Assert.That(view.FindInBuffer("e"), Is.EqualTo(count));
            Assert.That(sliced, Is.EqualTo(Hits(view)));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The call does not take the terminal lock on the UI thread. If it did, a find box searching per
    /// keystroke would wait out every chunk the reader is writing.
    /// </summary>
    /// <remarks>
    /// The lock is held on another thread until a gate that only the UI thread opens, and only once
    /// the call has returned. A call that waited for the lock could never open it, and the holder
    /// would give up after its patience and say so.
    /// </remarks>
    [AvaloniaTest]
    public async Task The_call_does_not_wait_for_the_terminal_lock()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("needle one\r\nfine\r\nneedle two\r\n");

            var terminalLock = typeof(TerminalView)
                .GetField("_terminalLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(view)!;

            var held = new ManualResetEventSlim(false);
            var gate = new ManualResetEventSlim(false);
            var holderGaveUp = false;
            var holder = new Thread(() =>
{
                lock (terminalLock)
                {
                    held.Set();
                    holderGaveUp = !gate.Wait(Patience);
                }
            }) { IsBackground = true };
            holder.Start();
            Assert.That(held.Wait(Patience), Is.True, "the holder never took the lock");

            var search = view.FindInBufferAsync("needle");
            var finishedWhileHeld = search.IsCompleted;
            gate.Set();
            holder.Join();

            Assert.That(holderGaveUp, Is.False, "the call waited on the UI thread for the terminal lock");
            Assert.That(finishedWhileHeld, Is.False, "the scan needs the lock, so it cannot have run yet");
            Assert.That(await search, Is.EqualTo(2));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The lock is let go between slices: the real read loop writes a chunk while the scan is parked
    /// there, and the UI thread takes the lock too. And the call itself returned before the scan was
    /// done, so the UI thread never waited for it.
    /// </summary>
    [AvaloniaTest]
    public async Task The_reader_and_the_UI_thread_get_the_lock_between_slices()
    {
        var (view, window) = Realised();
        try
        {
            var pty = new PushConnection();
            view.AttachConnection(pty);
            FillWithWrappedMatches(view);

            var park = new ParkAfterFirstSlice();
            view.SearchSliceScanned = park.OnSlice;

            var search = view.FindInBufferAsync("needle");

            Assert.That(park.Parked.Wait(Patience), Is.True, "the scan never reached its first slice boundary");
            Assert.That(park.RanOnUiThread, Is.False, "the search ran on the UI thread");
            Assert.That(search.IsCompleted, Is.False, "the call waited for the search");

            const string chunk = "written between slices";
            var before = Interlocked.Read(ref view.Counters.Bytes);
            pty.Push(chunk);
            var landed = SpinWait.SpinUntil(
                () => Interlocked.Read(ref view.Counters.Bytes) >= before + chunk.Length, Patience);

            Assert.That(landed, Is.True, "the reader could not write while the scan was between slices");
            Assert.That(view.CurrentLineText, Does.Contain(chunk), "CurrentLineText takes the lock on the UI thread");

            park.Release.Set();
            var count = await search;

            Assert.That(view.FindInBuffer("needle"), Is.EqualTo(count));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Lines trimmed off the top between slices move what the earlier slices found, drop what went
    /// with them, and move where the next slice starts.
    /// </summary>
    [AvaloniaTest]
    public async Task Lines_trimmed_mid_scan_are_rebased()
    {
        const int bufferSize = 13_000;
        var (view, window) = Realised(bufferSize);
        try
        {
            // Past capacity, so the buffer is full and every line written from here on trims one.
            view.Terminal.Write(ShortLines(0, bufferSize + view.Terminal.Rows + 100));
            var lines = view.Terminal.Buffer.Lines.Length;

            const int trimmed = 500;
            var park = new ParkAfterFirstSlice(hold: false)
            {
                WhileParked = () => Dispatcher.UIThread.Invoke(
                    () => view.Terminal.Write(ShortLines(1_000_000, trimmed))),
            };
            view.SearchSliceScanned = park.OnSlice;

            var top = view.Terminal.Buffer.GetLine(0)!.TranslateToString(true);
            var count = await view.FindInBufferAsync("needle");
            var sliced = Hits(view);

            Assert.That(view.Terminal.Buffer.Lines.Length, Is.EqualTo(lines), "the buffer should still be full");
            Assert.That(view.Terminal.Buffer.GetLine(0)!.TranslateToString(true), Is.Not.EqualTo(top),
                "nothing was trimmed, so this tested nothing");

            Assert.That(view.FindInBuffer("needle"), Is.EqualTo(count));
            Assert.That(sliced, Is.EqualTo(Hits(view)));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Switching screens mid-scan starts it again on the screen there is now; the rows found so far
    /// were in a buffer that is no longer the one shown.
    /// </summary>
    [AvaloniaTest]
    public async Task A_screen_switch_mid_scan_starts_again()
    {
        var (view, window) = Realised();
        try
        {
            FillWithWrappedMatches(view);

            var park = new ParkAfterFirstSlice(hold: false)
            {
                WhileParked = () => Dispatcher.UIThread.Invoke(
                    () => view.Terminal.Write($"{Esc}[?1049h{Esc}[Hneedle on the alternate screen\r\nand needle again")),
            };
            view.SearchSliceScanned = park.OnSlice;

            var count = await view.FindInBufferAsync("needle");
            var sliced = Hits(view);

            Assert.That(view.Terminal.IsAlternateBufferActive, Is.True);
            Assert.That(count, Is.EqualTo(2));
            Assert.That(view.FindInBuffer("needle"), Is.EqualTo(count));
            Assert.That(sliced, Is.EqualTo(Hits(view)));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A resize mid-scan reflows every row, so the scan starts again at the new width rather than
    /// mixing rows from before the reflow with rows from after it.
    /// </summary>
    [AvaloniaTest]
    public async Task A_resize_mid_scan_starts_again()
    {
        var (view, window) = Realised();
        try
        {
            FillWithWrappedMatches(view);
            var before = view.Terminal.Cols;
            var during = before;

            var park = new ParkAfterFirstSlice(hold: false)
            {
                WhileParked = () =>
                {
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        window.Width = 560;
                        window.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();   // the re-grid is posted from the layout pass
                    });
                    during = view.Terminal.Cols;
                },
            };
            view.SearchSliceScanned = park.OnSlice;

            var count = await view.FindInBufferAsync("needle");
            var sliced = Hits(view);

            Assert.That(during, Is.LessThan(before), "the resize has to land between slices for this to test it");
            Assert.That(view.Terminal.Cols, Is.EqualTo(during), "and nothing may resize again after it");
            Assert.That(view.FindInBuffer("needle"), Is.EqualTo(count));
            Assert.That(sliced, Is.EqualTo(Hits(view)));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Cancelling_publishes_nothing()
    {
        var (view, window) = Realised();
        try
        {
            FillWithWrappedMatches(view);
            var before = view.FindInBuffer("line 12");

            using var cancel = new CancellationTokenSource();
            var park = new ParkAfterFirstSlice(hold: false) { WhileParked = cancel.Cancel };
            view.SearchSliceScanned = park.OnSlice;

            Assert.That(await WasCancelled(view.FindInBufferAsync("needle", default, cancel.Token)), Is.True);
            Assert.That(park.Slices, Is.EqualTo(1), "the scan went on after it was cancelled");
            Assert.That(view.SearchHitCount, Is.EqualTo(before), "the earlier result should still stand");
        }
        finally { window.Close(); }
    }

    /// <summary>A newer search cancels one mid-scan, and only the newer one publishes.</summary>
    [AvaloniaTest]
    public async Task A_newer_search_supersedes_one_mid_scan()
    {
        var (view, window) = Realised();
        try
        {
            FillWithWrappedMatches(view);

            var park = new ParkAfterFirstSlice();
            view.SearchSliceScanned = park.OnSlice;

            var older = view.FindInBufferAsync("needle");
            Assert.That(park.Parked.Wait(Patience), Is.True);

            var newer = view.FindInBufferAsync("line 12");
            park.Release.Set();

            // Without pumping: a scan stopped at its next slice ends on the worker, while one left to
            // run on would sit waiting for the UI thread to publish.
            Assert.That(SpinWait.SpinUntil(() => older.IsCompleted, Patience), Is.True,
                "the older scan was not stopped when the newer one began");

            var count = await newer;
            Assert.That(await WasCancelled(older), Is.True);
            Assert.That(view.SearchHitCount, Is.EqualTo(count));
            Assert.That(view.FindInBuffer("line 12"), Is.EqualTo(count));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The race the UI-thread check exists for: the older scan has FINISHED and its result is on its
    /// way to the UI thread when the newer search starts. Nothing on the worker can stop it now.
    /// </summary>
    [AvaloniaTest]
    public async Task A_newer_search_supersedes_one_already_on_its_way_to_publish()
    {
        var (view, window) = Realised();
        try
        {
            FillWithWrappedMatches(view);

            var finished = new ManualResetEventSlim(false);
            view.SearchSliceScanned = last => { if (last) finished.Set(); };

            var older = view.FindInBufferAsync("needle");
            Assert.That(finished.Wait(Patience), Is.True, "the older scan never finished");

            // The UI thread has not pumped since, so the older publish cannot have run.
            view.SearchSliceScanned = null;
            var newer = view.FindInBufferAsync("line 12");

            var count = await newer;
            Assert.That(await WasCancelled(older), Is.True, "the older search published after a newer one began");
            Assert.That(view.SearchHitCount, Is.EqualTo(count));
            Assert.That(view.FindInBuffer("line 12"), Is.EqualTo(count));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task A_synchronous_search_supersedes_one_in_flight()
    {
        var (view, window) = Realised();
        try
        {
            FillWithWrappedMatches(view);

            var park = new ParkAfterFirstSlice();
            view.SearchSliceScanned = park.OnSlice;

            var older = view.FindInBufferAsync("needle");
            Assert.That(park.Parked.Wait(Patience), Is.True);

            var count = view.FindInBuffer("line 12");
            park.Release.Set();

            Assert.That(await WasCancelled(older), Is.True);
            Assert.That(view.SearchHitCount, Is.EqualTo(count), "the older search published over the newer one");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Clearing_cancels_the_search_in_flight()
    {
        var (view, window) = Realised();
        try
        {
            FillWithWrappedMatches(view);

            var park = new ParkAfterFirstSlice();
            view.SearchSliceScanned = park.OnSlice;

            var search = view.FindInBufferAsync("needle");
            Assert.That(park.Parked.Wait(Patience), Is.True);

            view.ClearSearch();
            park.Release.Set();

            Assert.That(await WasCancelled(search), Is.True);
            Assert.That(park.Slices, Is.EqualTo(1), "the scan went on after it was cleared");
            Assert.That(view.SearchHitCount, Is.Zero);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task The_control_forwards_it()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            control.View().Terminal.Write("error one\r\nfine\r\nerror two\r\n");

            Assert.That(await control.FindInBufferAsync("error"), Is.EqualTo(2));
            Assert.That(control.SearchHitCount, Is.EqualTo(2));
        }
        finally { window.Close(); }
    }
}
