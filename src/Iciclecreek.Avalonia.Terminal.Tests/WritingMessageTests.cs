using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// <see cref="TerminalView.WritingMessage"/> — the host's say over the lines the view writes on its own behalf.
///
/// <para>Driven through <see cref="TerminalView.AttachConnection"/> with a <see cref="PushConnection"/>, so the
/// exit (EOF) and the read failure (a thrown read) both happen at a point the test chooses. A real shell would
/// only give the exit; a read error is not something a live pty can be asked for.</para>
/// </summary>
[TestFixture]
public class WritingMessageTests
{
    private const string ExitLine = "Process exited with code: 0";

    private static Window Show(Control content)
    {
        var window = new Window { Width = 800, Height = 600, Content = content };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out after {timeoutMs}ms waiting until {because}");
            await Task.Delay(10);
        }
    }

    /// <summary>Everything the emulator has, as text.</summary>
    private static string BufferText(TerminalView view)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < view.Terminal.Buffer.Length; y++)
        {
            var line = view.Terminal.Buffer.GetLine(y);
            if (line == null) continue;
            for (int x = 0; x < line.Length; x++)
                sb.Append(string.IsNullOrEmpty(line[x].Content) ? " " : line[x].Content);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Attach, end the stream with EOF, and wait for the exit to be reported.</summary>
    private static async Task<(TerminalView View, Window Window)> RunToExit(Action<TerminalView> subscribe)
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        var connection = new PushConnection();

        var exited = false;
        view.ProcessExited += (_, _) => exited = true;
        subscribe(view);
        view.AttachConnection(connection);

        connection.Done();
        await WaitUntil(() => exited, "the exit was reported");
        await Task.Delay(50);   // let any posted writes drain
        return (view, window);
    }

    [Test]
    public void The_default_lines_are_the_ones_the_view_has_always_written()
    {
        // Pinned, because hosts and tests alike match on these words, and the factories now own them.
        Assert.That(TerminalMessageEventArgs.ProcessExited(3).Text, Is.EqualTo("\nProcess exited with code: 3\n"));
        Assert.That(TerminalMessageEventArgs.ProcessExited(null).Text, Is.EqualTo("\nProcess exited\n"));
        Assert.That(TerminalMessageEventArgs.ReadError(new IOException("Input/output error")).Text,
            Is.EqualTo("\nError reading from process: Input/output error\n"));
    }

    [Test]
    public void An_unreadable_exit_code_is_null_rather_than_zero()
    {
        // 0 is the one wrong answer that reads as success; see ProcessExitedEventArgs.ExitCodeKnown.
        Assert.That(TerminalMessageEventArgs.ProcessExited(null).ExitCode, Is.Null);
        Assert.That(TerminalMessageEventArgs.ProcessExited(0).ExitCode, Is.EqualTo(0));
    }

    [Test]
    public void Null_text_is_taken_as_empty()
    {
        var message = TerminalMessageEventArgs.ProcessExited(0);
        message.Text = null!;
        Assert.That(message.Text, Is.EqualTo(string.Empty));
    }

    [AvaloniaTest]
    public async Task With_no_handler_the_exit_line_is_written_as_before()
    {
        var (view, window) = await RunToExit(_ => { });

        Assert.That(BufferText(view), Does.Contain(ExitLine));
        window.Close();
    }

    [AvaloniaTest]
    public async Task A_handled_message_is_not_written()
    {
        TerminalMessageEventArgs? seen = null;
        var (view, window) = await RunToExit(v => v.WritingMessage += (_, e) => { seen = e; e.Handled = true; });

        Assert.That(seen?.Kind, Is.EqualTo(TerminalMessageKind.ProcessExited));
        Assert.That(seen?.ExitCode, Is.EqualTo(0));
        Assert.That(BufferText(view), Does.Not.Contain("Process exited"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task Rewritten_text_is_written_in_place_of_the_default()
    {
        var (view, window) = await RunToExit(v => v.WritingMessage += (_, e) => e.Text = "\n[done]\n");

        var text = BufferText(view);
        Assert.That(text, Does.Contain("[done]"));
        Assert.That(text, Does.Not.Contain("Process exited"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task A_throwing_handler_still_gets_the_default_line_written()
    {
        // The handler changed the text and then threw. Neither its half-done rewrite nor its exception
        // should be what the user is left with.
        var (view, window) = await RunToExit(v => v.WritingMessage += (_, e) =>
        {
            e.Text = "\nhalf-done\n";
            e.Handled = true;
            throw new InvalidOperationException("host bug");
        });

        var text = BufferText(view);
        Assert.That(text, Does.Contain(ExitLine));
        Assert.That(text, Does.Not.Contain("half-done"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task The_message_comes_before_ProcessExited()
    {
        // The buffer has always had the notice in it by the time the host hears the process ended; a host
        // that clears the buffer on exit depends on that order.
        var order = new List<string>();
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        var connection = new PushConnection();

        view.WritingMessage += (_, _) => order.Add("message");
        view.ProcessExited += (_, _) => order.Add("exited");
        view.AttachConnection(connection);

        connection.Done();
        await WaitUntil(() => order.Contains("exited"), "the exit was reported");

        Assert.That(order, Is.EqualTo(new[] { "message", "exited" }));
        window.Close();
    }

    [AvaloniaTest]
    public async Task A_failed_read_is_offered_to_the_host_with_its_exception()
    {
        // What a Unix pty's default blocking stream throws when the child closes its end.
        var failure = new IOException("Input/output error");
        TerminalMessageEventArgs? seen = null;

        var view = new TerminalView { Process = "" };
        var window = Show(view);
        var connection = new PushConnection();
        view.WritingMessage += (_, e) => { seen = e; e.Handled = true; };
        view.AttachConnection(connection);

        connection.Fail(failure);
        await WaitUntil(() => seen != null, "the read error was offered to the host");
        await Task.Delay(50);

        Assert.That(seen!.Kind, Is.EqualTo(TerminalMessageKind.ReadError));
        Assert.That(seen.Exception, Is.SameAs(failure));
        Assert.That(seen.ExitCode, Is.Null);
        Assert.That(BufferText(view), Does.Not.Contain("Error reading from process"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task With_no_handler_a_failed_read_is_written_as_before()
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);
        var connection = new PushConnection();
        view.AttachConnection(connection);

        connection.Fail(new IOException("Input/output error"));
        await WaitUntil(() => BufferText(view).Contains("Error reading from process: Input/output error"),
            "the default read-error line was written");

        window.Close();
    }
}
