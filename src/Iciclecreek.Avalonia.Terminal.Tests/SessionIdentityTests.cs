using System.Text;
using Porta.Pty;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Every output and exit event says WHICH process it came from.
///
/// <para>Without it a subscriber has only <see cref="TerminalView.IsLive"/>, which answers a different
/// question: "is something running". After a relaunch that is true for the replacement while the previous
/// process's last bytes and its exit are still in flight — the events are raised off the read task and hop
/// to the UI thread — so a host keying off it acts on the dead shell's output as though it belonged to the
/// new one. Promoting a pane to "live" on a dying shell's last byte is the concrete symptom.</para>
/// </summary>
[TestFixture]
public class SessionIdentityTests
{
    private static Window Show(Control content)
    {
        var window = new Window { Width = 520, Height = 900, Content = content };
        window.Show();
        window.UpdateLayout();
        global::Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return window;
    }

    private static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out after {timeoutMs}ms waiting until {because}");
            await Task.Delay(10);
        }
    }

    /// <summary>A connection that hands over one chunk and then reads EOF.</summary>
    private sealed class Says : IPtyConnection
    {
        public Says(string text) => ReaderStream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        public Stream ReaderStream { get; }
        public Stream WriterStream { get; } = new MemoryStream();
        public int Pid => -1;
        public int ExitCode => 0;
        public bool WaitForExit(int milliseconds) => true;
        public void Kill() { }
        public void Resize(int columns, int rows) { }
        public void Dispose() { }
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
    }

    // [AvaloniaTest] already runs the body on the headless UI thread; this just unwraps the lambda.
    private static Task RunAsync(Func<Task> body) => body();

    [AvaloniaTest]
    public Task Output_carries_the_session_that_produced_it_and_the_id_changes_per_connection() => RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        Assert.That(view.SessionId, Is.EqualTo(0), "nothing is installed before the first connection");

        var seen = new List<long>();
        view.OutputReceived += (_, e) => { lock (seen) seen.Add(e.SessionId); };

        view.AttachConnection(new Says("one"));
        var first = view.SessionId;
        Assert.That(first, Is.Not.EqualTo(0), "installing a connection mints an id");
        await WaitUntil(() => { lock (seen) return seen.Count > 0; }, "the first connection's output arrives");
        lock (seen) Assert.That(seen, Is.All.EqualTo(first), "output must carry the session that produced it");

        lock (seen) seen.Clear();
        view.AttachConnection(new Says("two"));
        var second = view.SessionId;
        Assert.That(second, Is.Not.EqualTo(first), "a new connection is a new session, never a reused id");
        await WaitUntil(() => { lock (seen) return seen.Count > 0; }, "the second connection's output arrives");
        lock (seen) Assert.That(seen, Is.All.EqualTo(second), "the replacement's output carries the replacement's id");

        window.Close();
    });
}
