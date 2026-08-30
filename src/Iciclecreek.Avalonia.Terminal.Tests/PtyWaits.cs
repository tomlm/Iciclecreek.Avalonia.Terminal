using System;
using System.Threading.Tasks;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Waits for what a keystroke sent, instead of guessing how long sending takes.
/// </summary>
/// <remarks>
/// <para>The key handlers are async void and complete on a thread pool, so a fixed delay after
/// raising an event is a bet about machine speed — and the macOS CI runners lost it, coldest test
/// first: JIT, thread-pool spin-up and the first headless window can outlast any constant that is
/// still short enough to run a suite in. Two tests failed exactly this way before this existed, one
/// per harness style, and every fixed delay in front of an output assertion was carrying the same
/// bug unexercised.</para>
///
/// <para>Two waits, because there are two kinds of assertion. An assertion that expects OUTPUT
/// waits for it to arrive and then to stop — as long as it needs, no longer, with a deadline so a
/// silent implementation still fails. An assertion that expects NOTHING keeps a fixed window,
/// because "nothing was sent" can only ever mean "nothing within this long"; the window is the
/// assertion's meaning, not a guess.</para>
/// </remarks>
internal static class PtyWaits
{
    /// <summary>
    /// Waits until <paramref name="pty"/> has more than <paramref name="alreadyWritten"/> characters
    /// and the growth has stopped, then returns everything written.
    /// </summary>
    public static async Task<string> AwaitOutput(RecordingConnection pty, int alreadyWritten = 0,
                                                 int deadlineSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(deadlineSeconds);

        while (DateTime.UtcNow < deadline && pty.Written.Length <= alreadyWritten)
            await Task.Delay(10);

        var seen = pty.Written;
        var quiet = 0;
        while (DateTime.UtcNow < deadline && quiet < 5)
        {
            await Task.Delay(5);
            var now = pty.Written;
            quiet = now == seen ? quiet + 1 : 0;
            seen = now;
        }

        return seen;
    }
}
