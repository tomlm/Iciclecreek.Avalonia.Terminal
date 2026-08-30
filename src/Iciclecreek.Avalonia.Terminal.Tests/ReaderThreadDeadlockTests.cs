using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The reader thread must never block on the UI thread while holding <c>_terminalLock</c>.
/// </summary>
/// <remarks>
/// <para>The read loop calls <c>Terminal.Write</c> from inside <c>lock (_terminalLock)</c>. Write
/// parses synchronously and raises host seams on that same thread, so a seam that answers with a
/// blocking <c>Dispatcher.UIThread.Invoke</c> waits for the UI thread -- while the UI thread waits
/// for the same lock in <c>ClearScreen</c>, <c>CurrentLineText</c> or <c>WriteOwnLine</c>.</para>
/// <para>That is a deadlock rather than a stall: the application freezes with no exception to
/// search for. These tests drive the emulator from a NON-UI thread, which is how the read loop
/// drives it, because the seam runs inline and harmlessly when Invoke is called from the UI thread
/// itself -- a test that writes from the UI thread cannot see this bug at all.</para>
/// </remarks>
[TestFixture]
public class ReaderThreadDeadlockTests
{
    private static readonly string Esc = ((char)0x1B).ToString();

    /// <summary>How long a seam gets before we call it hung rather than slow.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static (TerminalView view, Window window) Realised()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    /// <summary>
    /// Writes to the terminal off the UI thread, the way the read loop does, and reports whether the
    /// write RETURNED while the UI thread was never pumped.
    /// </summary>
    /// <remarks>
    /// Not pumping is the whole test. A posted seam does not need the UI thread to make progress, so
    /// Write returns immediately; a blocking Invoke cannot complete until somebody services the
    /// dispatcher, so it sits there. An earlier version of this pumped while waiting and therefore
    /// passed against the blocking version too -- the pump was doing the very thing the real
    /// deadlock prevents, which made the test agree with any implementation.
    ///
    /// This stands in for the real deadlock rather than reproducing it. Reproducing it needs the
    /// actual read loop holding _terminalLock while the UI thread calls ClearScreen, which is not
    /// reachable from here -- the lock is private and only the read loop takes it around Write. What
    /// is asserted instead is the property that makes the deadlock impossible: the reader is never
    /// made to wait for the UI thread at all.
    /// </remarks>
    private static bool WriteReturnsWithoutTheUIThread(TerminalView view, string data)
    {
        var done = new ManualResetEventSlim(false);

        var worker = new Thread(() =>
        {
            view.Terminal.Write(data);
            done.Set();
        }) { IsBackground = true };

        worker.Start();
        return done.Wait(Patience);      // deliberately no RunJobs
    }

    [AvaloniaTest]
    public void An_OSC7_directory_report_does_not_block_the_thread_that_wrote_it()
    {
        var (view, window) = Realised();
        try
        {
            var finished = WriteReturnsWithoutTheUIThread(view, $"{Esc}]7;file:///tmp/somewhere{Esc}\\");

            Assert.That(finished, Is.True,
                "Write did not return while the UI thread was idle, so the seam blocked on it. A "
                + "reader thread holding _terminalLock would now be waiting on the UI thread while "
                + "the UI thread waits for that lock -- the deadlock.");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_window_query_does_not_block_the_thread_that_wrote_it_for_ever()
    {
        // CSI 18 t asks for the text-area size. The answer genuinely needs the UI thread -- only it
        // knows the window -- so this one is bounded rather than posted: an idle UI thread answers
        // in microseconds, and a wedged one costs a pause instead of the session.
        var (view, window) = Realised();
        try
        {
            // The window-op gates default to false, so without this the sequence is ignored and the
            // seam never fires -- the test would pass by never asking anything.
            view.Terminal.Options.WindowOptions.GetWinSizeChars = true;
            view.Terminal.Options.WindowOptions.GetWinSizePixels = true;

            var finished = WriteReturnsWithoutTheUIThread(view, $"{Esc}[18t");

            Assert.That(finished, Is.True,
                "the window-info seam waited on the UI thread with no bound, so a reader holding "
                + "_terminalLock would wait for ever on a thread waiting for that lock");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_reported_directory_still_arrives()
    {
        // Posting must not mean losing it -- the property still has to land, one frame later.
        var (view, window) = Realised();
        try
        {
            WriteReturnsWithoutTheUIThread(view, $"{Esc}]7;file:///tmp/elsewhere{Esc}\\");

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < Patience && string.IsNullOrEmpty(view.CurrentDirectory))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Assert.That(view.CurrentDirectory, Does.Contain("elsewhere"));
        }
        finally { window.Close(); }
    }
}
