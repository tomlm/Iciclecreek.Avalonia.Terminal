using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Handing a live connection to a new owner without losing what the process said — issue #123.
///
/// <para>Two modes, two guarantees. A connection that supports cancellable reads hands over
/// deterministically: detach cancels the parked read before it consumes anything, waits for the
/// loop to finish, and returns a connection nobody is reading. A blocking connection cannot be
/// unparked, so its stale reader may steal one chunk — which is now parked and replayed by the next
/// owner, in order, instead of lost. Only a chunk stolen after the new owner attached is dropped,
/// because late delivery could reorder, and reordered output corrupts where a gap merely gaps.</para>
/// </summary>
[TestFixture]
public class HandoverTests
{
    private static (TerminalView view, Window window) Realised()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static string Text(TerminalView v)
    {
        var b = v.Terminal.Buffer;
        var sb = new System.Text.StringBuilder();
        for (var row = 0; row < b.Length; row++)
        {
            var line = b.GetLine(row);
            if (line is null) continue;
            for (var col = 0; col < line.Length; col++)
                sb.Append(line[col].Content);
        }
        return sb.ToString();
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline && !condition())
            await Task.Delay(20);
        Assert.That(condition(), Is.True, what);
    }

    // ---- cancellable mode: the deterministic handover -------------------------------------

    /// <summary>
    /// Detaching a cancellable connection consumes nothing: whatever the process says next belongs
    /// entirely to the new owner.
    /// </summary>
    [AvaloniaTest]
    public async Task A_cancellable_detach_hands_over_without_stealing()
    {
        var (view1, w1) = Realised();
        var (view2, w2) = Realised();
        try
        {
            var pty = new CancellablePushConnection();
            view1.AttachConnection(pty);
            await Task.Delay(150);   // the loop parks its first read

            var detached = view1.DetachConnection();
            Assert.That(detached, Is.SameAs(pty));

            // Speak AFTER the detach. A stale reader would steal this; a cancelled one cannot.
            pty.Push("AFTER-HANDOVER");

            view2.AttachConnection(pty);
            await WaitUntil(() => Text(view2).Contains("AFTER-HANDOVER"),
                "the new owner must receive everything said after the handover");
            Assert.That(Text(view1), Does.Not.Contain("AFTER-HANDOVER"),
                "the old owner must not have painted the new owner's output");
        }
        finally { w1.Close(); w2.Close(); }
    }

    /// <summary>Detach returns promptly even with a read parked — the cancel unparks it.</summary>
    [AvaloniaTest]
    public async Task A_cancellable_detach_does_not_wait_for_the_process_to_speak()
    {
        var (view, window) = Realised();
        try
        {
            var pty = new CancellablePushConnection();
            view.AttachConnection(pty);
            await Task.Delay(150);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            view.DetachConnection();
            sw.Stop();

            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2000),
                "a parked cancellable read must unpark on the cancel, not on the next output");
            Assert.That(pty.PendingChunks, Is.Zero,
                "and nothing may have been pushed-and-consumed to achieve it");
        }
        finally { window.Close(); }
    }

    // ---- blocking mode: the bounded fallback ----------------------------------------------

    /// <summary>
    /// A chunk the stale blocking reader steals BEFORE the new owner attaches is replayed to that
    /// owner, first and in order — not lost.
    /// </summary>
    [AvaloniaTest]
    public async Task A_chunk_stolen_before_attach_reaches_the_new_owner()
    {
        var (view1, w1) = Realised();
        var (view2, w2) = Realised();
        try
        {
            var pty = new PushConnection();
            view1.AttachConnection(pty);
            await Task.Delay(150);   // the blocking read parks

            view1.DetachConnection();

            // The process speaks while nobody owns the connection. The stale reader wakes, takes
            // the chunk, discovers it is not the owner, and parks the bytes instead of painting
            // or dropping them.
            pty.Push("STOLEN-BUT-SAVED");
            await Task.Delay(300);

            view2.AttachConnection(pty);
            await WaitUntil(() => Text(view2).Contains("STOLEN-BUT-SAVED"),
                "the parked chunk must be replayed to the new owner");
            Assert.That(Text(view1), Does.Not.Contain("STOLEN-BUT-SAVED"),
                "the stale view must not have painted a chunk it no longer owned");
        }
        finally { w1.Close(); w2.Close(); }
    }

    /// <summary>Pumps the UI thread until <paramref name="condition"/> holds or five seconds pass; the caller asserts.</summary>
    private static void Await(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5) && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// The stale reader wins the race AFTER the new owner attached: the chunk it took still reaches the
    /// new owner, and ahead of everything the process says after it.
    /// </summary>
    /// <remarks>
    /// <para>This is the case that used to be dropped. Once the new owner attached, its reader joined the
    /// old one on the same descriptor; whichever woke first took the chunk, and when that was the stale
    /// one, the chunk could not be handed over without risking reordering, so it went nowhere.</para>
    /// <para>Staged rather than raced for: <see cref="TurnConnection"/> serves the OLDEST parked read,
    /// which is the stale view's, since it parked before the detach. What the new owner's reader does
    /// meanwhile is the fix's business -- before it, a second read parked beside the first; now it waits
    /// its turn behind the stale one and replays what that one took before reading on.</para>
    /// </remarks>
    [AvaloniaTest]
    public void A_chunk_stolen_after_the_new_owner_attached_reaches_it_in_order()
    {
        var (view1, w1) = Realised();
        var (view2, w2) = Realised();
        var pty = new TurnConnection();
        try
        {
            view1.AttachConnection(pty);
            pty.Emit("<before>");
            Await(() => Text(view1).Contains("<before>") && pty.WaitingReads == 1);
            Assert.That(Text(view1), Does.Contain("<before>"), "sanity: the first owner's loop is delivering");
            Assert.That(pty.WaitingReads, Is.EqualTo(1), "sanity: and is parked in its next read");

            view1.DetachConnection();
            view2.AttachConnection(pty);

            // The stale view's read is the oldest, so it takes this one.
            pty.Emit("<stolen>");
            pty.Emit("<after>");

            Await(() => Text(view2).Contains("<after>") && Text(view2).Contains("<stolen>"));

            var shown = Text(view2);
            Assert.That(shown, Does.Contain("<stolen>"), "the chunk the stale reader took must reach the new owner, not be dropped");
            Assert.That(shown.IndexOf("<stolen>", StringComparison.Ordinal),
                Is.LessThan(shown.IndexOf("<after>", StringComparison.Ordinal)),
                "and ahead of what the process said after it");
            Assert.That(Text(view1), Does.Not.Contain("<stolen>"), "the detached view must not have painted it");
        }
        finally { w1.Close(); w2.Close(); pty.Dispose(); }
    }

    /// <summary>
    /// A detached view is not kept alive by its blocking reader, which stays parked until the process
    /// next speaks.
    /// </summary>
    /// <remarks>
    /// That can be indefinitely at an idle prompt, and it used to hold the view -- and with it the
    /// emulator and the whole scrollback -- for all of that time, because the parked loop was an
    /// instance method of the view. Now the loop reaches its view only through a lease the detach
    /// revokes.
    /// </remarks>
    [AvaloniaTest]
    public void A_detached_view_is_not_kept_alive_by_its_parked_reader()
    {
        var pty = new TurnConnection();
        try
        {
            var view = AttachThenDetach(pty);

            for (var i = 0; i < 5 && view.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Dispatcher.UIThread.RunJobs();
            }

            Assert.That(pty.WaitingReads, Is.EqualTo(1), "sanity: the stale reader is still parked, which is the case under test");
            Assert.That(view.IsAlive, Is.False, "the parked reader is keeping the detached view alive");
        }
        finally { pty.Dispose(); }   // EOF lets the stale reader finish rather than leak a parked thread
    }

    /// <summary>
    /// A title handler that moves the session to another view mid-chunk does not cost that chunk its
    /// OutputReceived: the view that painted it reports it, once, and ahead of the title.
    /// </summary>
    /// <remarks>
    /// <para>A host moving a tab when the program renames it is ordinary. Parsing the chunk posts the
    /// title, and OutputReceived used to be posted after the write -- so the title handler ran first,
    /// handed the session away, and the OutputReceived callback then found its session gone and
    /// discarded a chunk this view had painted. Neither view reported it.</para>
    /// <para>On the default dispatcher path, which is the one where the two events share a queue.</para>
    /// </remarks>
    [AvaloniaTest]
    public void A_title_handler_that_hands_the_session_over_does_not_cost_the_chunk_its_report()
    {
        var (view1, w1) = Realised();
        var (view2, w2) = Realised();
        var pty = new TurnConnection();
        try
        {
            var heard = new List<string>();
            view1.OutputReceived += (_, e) => { if (e.Output.Contains("<payload>")) lock (heard) heard.Add("view1:output"); };
            view2.OutputReceived += (_, e) => { if (e.Output.Contains("<payload>")) lock (heard) heard.Add("view2:output"); };
            view1.TitleChanged += (_, e) =>
            {
                if (e.Title != "moved") return;
                lock (heard) heard.Add("view1:title");
                view2.AttachConnection(view1.DetachConnection()!);
            };

            view1.AttachConnection(pty);
            pty.Emit("\u001b]2;moved\u0007<payload>");

            // The handover runs inside the title handler, so once the title is heard it is done; one
            // more drain gives a late or duplicate report the chance to show up.
            Await(() => { lock (heard) return heard.Contains("view1:title") && heard.Contains("view1:output"); });
            Dispatcher.UIThread.RunJobs();

            Assert.That(Text(view1), Does.Contain("<payload>"), "sanity: the view that owned the chunk painted it");
            Assert.That(Text(view2), Does.Not.Contain("<payload>"), "sanity: and the view it moved to did not");
            lock (heard)
                Assert.That(heard, Is.EqualTo(new[] { "view1:output", "view1:title" }),
                    "reported once, by the view that painted it, ahead of the title its parsing raised");
        }
        finally { w1.Close(); w2.Close(); pty.Dispose(); }
    }

    /// <summary>
    /// A chunk the view painted is reported even when the view gives the session up before the
    /// dispatcher gets round to the report.
    /// </summary>
    /// <remarks>
    /// The report used to carry a guard that dropped it once the session had changed. Its job was to
    /// stop a dead process's bytes being reported as a live one's -- but only accepted, painted chunks
    /// are reported now, each stamped with its SessionId, so all the guard could still do was lose
    /// output a listener should have heard.
    /// </remarks>
    [AvaloniaTest]
    public void A_painted_chunk_is_reported_even_if_the_session_moves_before_the_report_runs()
    {
        var (view, window) = Realised();
        var pty = new TurnConnection();
        try
        {
            var heard = 0;
            view.OutputReceived += (_, e) => { if (e.Output.Contains("<painted>")) Interlocked.Increment(ref heard); };

            view.AttachConnection(pty);
            pty.Emit("<painted>");

            // Not pumped: the report stays queued while the chunk is painted and the session moves.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(5) && !Text(view).Contains("<painted>"))
                Thread.Sleep(5);
            Assert.That(Text(view), Does.Contain("<painted>"), "sanity: the chunk was painted");
            Assert.That(Volatile.Read(ref heard), Is.Zero, "sanity: and its report has not run yet");

            view.DetachConnection();
            Await(() => Volatile.Read(ref heard) > 0);
            Dispatcher.UIThread.RunJobs();

            Assert.That(Volatile.Read(ref heard), Is.EqualTo(1), "a painted chunk is reported exactly once");
        }
        finally { window.Close(); pty.Dispose(); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AttachThenDetach(TurnConnection pty)
    {
        var view = new TerminalView { Process = "" };
        view.AttachConnection(pty);
        Await(() => pty.WaitingReads == 1);
        Assert.That(pty.WaitingReads, Is.EqualTo(1), "sanity: the reader parked in its first read");
        view.DetachConnection();
        return new WeakReference(view);
    }

    // ---- a view's own spawn: UseAsyncIo -------------------------------------------------------

    /// <summary>
    /// A process the view SPAWNED, handed back and forth while it writes, arrives whole and in order.
    /// </summary>
    /// <remarks>
    /// <para>The guarantee above was only reachable through AttachConnection: LaunchProcess spawned
    /// blocking-mode, so a view's own process was exactly the case that could still drop a chunk.
    /// UseAsyncIo is the opt-in. The output comes in bursts, so most handovers land while the
    /// outgoing reader is parked -- the window a blocking reader steals from after the new owner has
    /// attached.</para>
    /// <para>Collected on the read task, in one sink shared by both views, so the sink's order IS
    /// the stream order: a cancellable detach waits for the outgoing loop before the next attach.</para>
    /// </remarks>
    [AvaloniaTest]
    [Platform(Exclude = "Win", Reason = "uses sh; the handover under test is not platform-specific")]
    public async Task A_spawned_process_handed_over_under_load_loses_nothing()
    {
        const int lines = 4000;
        var seen = new System.Text.StringBuilder();
        string Seen() { lock (seen) return seen.ToString(); }

        (TerminalView view, Window window) Make()
        {
            var view = new TerminalView { Process = "", UseAsyncIo = true, OutputReceivedOnReadTask = true };
            view.OutputReceived += (_, e) => { lock (seen) seen.Append(e.Output); };
            var window = new Window { Width = 800, Height = 600, Content = view };
            window.Show();
            window.UpdateLayout();

            // Loaded is posted, and OnLoaded launches Process whenever no connection is attached --
            // which, mid-handover, is a detached view whose Process the launch below has set. Let it
            // run now, while Process is still empty, or it starts a second process part-way through.
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.IsLoaded, Is.True);
            return (view, window);
        }

        var (view1, w1) = Make();
        var (view2, w2) = Make();
        Porta.Pty.IPtyConnection? connection = null;
        try
        {
            await view1.LaunchProcess(null, "sh", "-c",
                $"i=0; while [ $i -lt {lines} ]; do echo $i; i=$((i+1)); [ $((i % 100)) -eq 0 ] && sleep 0.01; done; echo END; sleep 5");

            var (from, to) = (view1, view2);
            var handovers = 0;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!Seen().Contains("END") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
                connection = from.DetachConnection();
                Assert.That(connection, Is.Not.Null, "the process is still running");
                Assert.That(connection!.SupportsCancellableRead, Is.True, "UseAsyncIo reached the spawn");
                to.AttachConnection(connection);
                (from, to) = (to, from);
                handovers++;
            }

            Assert.That(Seen(), Does.Contain("END"), "the process finished writing within the deadline");
            Assert.That(handovers, Is.GreaterThan(5), "the output was handed over while it was being written");

            var numbers = System.Text.RegularExpressions.Regex.Matches(Seen(), @"\d+").Select(m => int.Parse(m.Value)).ToList();
            var firstWrong = Enumerable.Range(0, lines).FirstOrDefault(i => i >= numbers.Count || numbers[i] != i, -1);
            Assert.That(firstWrong, Is.EqualTo(-1),
                $"every line once, in order, across {handovers} handovers; {numbers.Count} numbers seen, first wrong at index {firstWrong}: " +
                string.Join(" ", numbers.Skip(Math.Max(0, firstWrong - 3)).Take(8)));
            Assert.That(numbers.Count, Is.EqualTo(lines));
        }
        finally
        {
            connection = view1.DetachConnection() ?? view2.DetachConnection() ?? connection;
            connection?.Kill();
            connection?.Dispose();
            w1.Close();
            w2.Close();
        }
    }
}
