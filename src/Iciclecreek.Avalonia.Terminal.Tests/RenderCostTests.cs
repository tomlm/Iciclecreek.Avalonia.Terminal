using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Work done once per escape sequence that should have been done once per frame.
/// </summary>
/// <remarks>
/// The reader thread produces sequences far faster than the UI thread drains them, so anything
/// posted per sequence is a queue that grows under load rather than a cost that is merely paid. Two
/// of these were measured rather than reasoned about: 0.9 MB of output pinning 86 MB of live heap,
/// and a shell profile's sixteen colour sequences each walking the whole scrollback.
/// </remarks>
[TestFixture]
public class RenderCostTests
{
    private static readonly string Esc = ((char)0x1B).ToString();
    private static readonly string Bel = ((char)0x07).ToString();

    private static (TerminalView view, Window window) Realised()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static T Field<T>(TerminalView view, string name)
    {
        var f = typeof(TerminalView).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(f, Is.Not.Null, $"{name} has been renamed; this test needs updating");
        return (T)f!.GetValue(view)!;
    }

    // ------------------------------------------------------- one job, not N

    [AvaloniaTest]
    public void A_burst_of_notifications_shares_one_dispatcher_job()
    {
        // Each of these used to post its own, holding a closure, holding its event args. The reader
        // queues far faster than the UI thread drains, so a burst became a queue that grew.
        var (view, window) = Realised();
        try
        {
            for (var i = 0; i < 50; i++)
                view.Terminal.Write($"{Esc}]99;;notification {i}{Bel}");

            // BEFORE pumping, which is the whole measurement: what is being counted is what is
            // waiting, not what has run.
            var waiting = Field<List<Action>>(view, "_pendingHostCallbacks");
            lock (waiting)
            {
                Assert.That(waiting.Count, Is.EqualTo(50), "sanity: every notification is queued");
            }

            Assert.That(Field<bool>(view, "_hostDrainQueued"), Is.True,
                "and exactly one drain is queued to run them");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Every_queued_notification_still_reaches_the_host()
    {
        // Coalescing the JOBS must not coalesce the events. A notification is something the host
        // wanted to be told about, and there is no version of this where dropping them is right.
        var (view, window) = Realised();
        try
        {
            var seen = 0;
            view.AddHandler(TerminalView.NotificationRequestedEvent, (object? _, TerminalNotificationEventArgs _) => seen++);

            for (var i = 0; i < 20; i++)
                view.Terminal.Write($"{Esc}]99;;notification {i}{Bel}");

            Dispatcher.UIThread.RunJobs();

            Assert.That(seen, Is.EqualTo(20));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void One_failing_callback_does_not_swallow_the_batch()
    {
        // Separately posted, each of these was independent. Batching them must not make them share a
        // fate -- the first handler to throw would otherwise take every event behind it with it.
        var (view, window) = Realised();
        try
        {
            var seen = 0;
            view.AddHandler(TerminalView.NotificationRequestedEvent,
                (object? _, TerminalNotificationEventArgs _) =>
                {
                    seen++;
                    if (seen == 1) throw new InvalidOperationException("first one throws");
                });

            view.Terminal.Write($"{Esc}]99;;one{Bel}");
            view.Terminal.Write($"{Esc}]99;;two{Bel}");
            Dispatcher.UIThread.RunJobs();

            Assert.That(seen, Is.EqualTo(2), "the second must still have run");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_burst_of_pointer_shape_changes_queues_one_job()
    {
        // The exception to "nothing is dropped": a shape is a STATE, not an event, so only the last
        // one was ever going to be visible. Called out in the PR and untested until now, which is
        // how per-sequence queue growth gets reintroduced.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Options.PointerShapesEnabled = true;

            foreach (var shape in new[] { "pointer", "text", "wait", "crosshair", "progress" })
                view.Terminal.Write($"{Esc}]22;{shape}{Bel}");

            var waiting = Field<List<Action>>(view, "_pendingHostCallbacks");
            lock (waiting)
            {
                Assert.That(waiting.Count, Is.EqualTo(1),
                    "five shape changes, one job -- only the last was ever going to be visible");
            }

            // And the shape that survives is the last one asked for.
            var pending = typeof(TerminalView).GetField("_pendingPointerShape",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(pending, Is.Not.Null, "_pendingPointerShape has been renamed; update this test");
            Assert.That(pending!.GetValue(view), Is.EqualTo("progress"));
        }
        finally { window.Close(); }
    }

    /// <summary>Hands the view a chunk the way the pty read loop does.</summary>
    /// <remarks>
    /// Through the private method rather than through <c>Terminal.Write</c>, because the per-chunk
    /// posting under test lives in the read loop's delivery path and not in the emulator. Passing
    /// shellReadyPosted as true keeps the once-per-process signal out of the way.
    /// </remarks>
    private static void FeedChunk(TerminalView view, string text)
    {
        var consume = typeof(TerminalView).GetMethod("ConsumeOutputChunk",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(consume, Is.Not.Null, "ConsumeOutputChunk has been renamed; this test needs updating");

        var args = new object?[]
        {
            new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes(text)),
            true,
            System.Threading.CancellationToken.None,
        };

        consume!.Invoke(view, args);
    }

    [AvaloniaTest]
    public void A_burst_of_output_chunks_queues_one_ime_notification()
    {
        // The one that actually froze a window. Every chunk posted its own IME notification, and on
        // Windows that reaches IMM32, which sends messages and re-enters the window procedure. A
        // full-screen animation redrawing every cell produced chunks faster than the UI thread could
        // retire them, so the queue grew without bound and the message loop never got back to
        // pumping: "Not Responding" while the child process was still writing happily.
        var (view, window) = Realised();
        try
        {
            for (var i = 0; i < 50; i++)
                FeedChunk(view, $"line {i}\r\n");

            Assert.That(Field<int>(view, "_imeNotifyQueued"), Is.EqualTo(1),
                "fifty chunks, one notification -- the IME re-reads state, so the last one is the only one that says anything");
            Assert.That(Field<int>(view, "_animationSyncQueued"), Is.EqualTo(1),
                "and the same for the animation clock, which is a sync and not an event");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_later_output_chunk_queues_another_ime_notification()
    {
        // "One at a time", not "once ever". An IME caches what it was last told, so a chunk arriving
        // after the notification has run still has to say the line moved -- otherwise composition
        // happens against text the buffer no longer holds.
        var (view, window) = Realised();
        try
        {
            FeedChunk(view, "first\r\n");
            Dispatcher.UIThread.RunJobs();
            Assert.That(Field<int>(view, "_imeNotifyQueued"), Is.EqualTo(0), "sanity: the first one ran");

            FeedChunk(view, "second\r\n");

            Assert.That(Field<int>(view, "_imeNotifyQueued"), Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    // ----------------------------------------------- one walk, not one per colour

    [AvaloniaTest]
    public void A_run_of_palette_changes_walks_the_buffer_once()
    {
        // Setting the sixteen ANSI colours is what every theme-setting shell profile does on startup.
        // Each one posted a walk of the WHOLE scrollback, so the cost was sequences x lines for an
        // answer identical after the first.
        var (view, window) = Realised();
        try
        {
            for (var i = 0; i < 16; i++)
                view.Terminal.Write($"{Esc}]4;{i};#00ff00{Bel}");

            Assert.That(Field<bool>(view, "_paletteWalkQueued"), Is.True, "sanity: one is pending");

            var waiting = Field<List<Action>>(view, "_pendingHostCallbacks");
            lock (waiting)
            {
                Assert.That(waiting.Count, Is.EqualTo(1),
                    "sixteen colour changes, one walk queued");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_later_palette_change_queues_another_walk()
    {
        // The guard is "one at a time", not "once ever". A change arriving after the walk has run
        // still has to drop the caches, or the screen keeps the colours it was drawn with.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}]4;1;#00ff00{Bel}");
            Dispatcher.UIThread.RunJobs();
            Assert.That(Field<bool>(view, "_paletteWalkQueued"), Is.False, "sanity: the first one ran");

            view.Terminal.Write($"{Esc}]4;2;#0000ff{Bel}");

            Assert.That(Field<bool>(view, "_paletteWalkQueued"), Is.True);
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------ re-theming drops the caches

    [AvaloniaTest]
    public void A_gradient_foreground_still_drops_the_cached_runs()
    {
        // A repaint REPLAYS cached runs, and they hold brushes resolved from the old default. A
        // solid brush becomes a palette entry and the colour-change handler drops the caches; a
        // brush that cannot be expressed as RGB changes no palette entry, raises nothing, and so
        // dropped nothing -- leaving every line on screen drawn in the previous colours.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("some text");
            Dispatcher.UIThread.RunJobs();

            var line = view.Terminal.Buffer.GetLine(view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y);
            Assert.That(line, Is.Not.Null);
            line!.Cache = new object();

            view.Foreground = new LinearGradientBrush
            {
                GradientStops = { new GradientStop(Colors.Red, 0), new GradientStop(Colors.Blue, 1) },
            };

            // Asserted IMMEDIATELY, with no RunJobs in between, and that is the fix for a flake
            // that reached CI: the invalidation is synchronous in OnPropertyChanged, but it also
            // calls InvalidateVisual -- so running the dispatcher here let a headless render tick
            // rebuild the cache (correctly, from the NEW brushes) before the assert, which then
            // read the rebuilt list as "the invalidation never happened". Same thread, no jobs run:
            // nothing can interleave between the set and this line.
            Assert.That(line.Cache, Is.Null,
                "a re-theme the palette cannot express still has to invalidate what was drawn");
        }
        finally { window.Close(); }
    }

    // ---------------------------------------------- blanks have no ink to put down

    [AvaloniaTest]
    public void A_run_of_blanks_is_recognised_as_having_nothing_to_draw()
    {
        Assert.That(Blank("    "), Is.True);
        Assert.That(Blank("a   "), Is.False);
        Assert.That(Blank(""), Is.False, "an empty run is not a run of blanks");

        // Anything that merely LOOKS blank still occupies its cell in a way a font may render, so it
        // is left to draw rather than assumed invisible.
        Assert.That(Blank("　"), Is.False, "ideographic space");
        Assert.That(Blank("​"), Is.False, "zero-width space");
    }

    private static bool Blank(string text)
    {
        var m = typeof(TerminalView).GetMethod("IsBlankRun",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.That(m, Is.Not.Null, "IsBlankRun has been renamed; this test needs updating");
        return (bool)m!.Invoke(null, new object[] { text })!;
    }
}
