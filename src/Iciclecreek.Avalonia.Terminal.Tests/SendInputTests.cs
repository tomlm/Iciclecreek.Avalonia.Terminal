using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Sending text to the running process, as if typed (issue #19).
///
/// <para>There was no public path to do this. The workaround in the issue thread reaches the private
/// <c>_terminalView</c> field and the private <c>SendToPtyAsync</c> by reflection — code that compiles today
/// and breaks silently the next time either is renamed. That two people arrived at the same need
/// independently, and one of them shipped reflection to meet it, is the argument for the method existing.</para>
///
/// <para>The round-trip test below is the one that matters: it launches a real shell, sends a command, and
/// waits for the shell's own output to come back. It runs on CI, since CI is Linux.</para>
/// </summary>
[TestFixture]
public class SendInputTests
{
    private static bool Posix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Everything the terminal has rendered, as text.</summary>
    private static string ScreenText(TerminalControl control)
    {
        var buffer = control.Terminal.Buffer;
        var sb = new StringBuilder();
        for (var i = 0; i < buffer.Length; i++)
            sb.Append(buffer.GetLine(i)?.TranslateToString(true) ?? string.Empty);
        return sb.ToString();
    }

    /// <summary>
    /// The whole point, end to end: text goes in, the shell acts on it, its output comes back.
    ///
    /// <para>Asserts on a marker the shell itself produces rather than on the echo of what was typed —
    /// echoing proves only that the characters reached the line editor, whereas output proves the command
    /// was submitted and ran, which is what a caller sending input actually wants.</para>
    /// </summary>
    [AvaloniaTest]
    public async Task Input_reaches_the_shell_and_the_command_runs()
    {
        if (!Posix) Assert.Ignore("POSIX only");

        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            await control.LaunchProcess(Path.GetTempPath(), "/bin/sh", "-i");

            // "\r" rather than "\n": that is what a terminal sends for Enter, and what the line editor waits for.
            await control.SendInputAsync("echo mark-6f3a\r");

            var deadline = DateTime.UtcNow.AddSeconds(30);
            var seen = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
                seen = ScreenText(control);

                // The echoed command contains the marker too, so wait until it appears TWICE: once as the
                // echo of the typed line, once as the output of running it.
                if (CountOccurrences(seen, "mark-6f3a") >= 2)
                    break;
            }

            Assert.That(CountOccurrences(seen, "mark-6f3a"), Is.GreaterThanOrEqualTo(2),
                "the shell should have echoed the command and then printed its output. "
                + $"Occurrences seen: {CountOccurrences(seen, "mark-6f3a")}");
        }
        finally
        {
            control.Kill();
            window.Close();
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    // ---- the no-op contracts, which need no process ------------------------------------------------

    /// <summary>
    /// Sending to a terminal with no process running does nothing, rather than throwing. A host reacting to
    /// user action should not have to guard every call against a process that has already exited.
    /// </summary>
    [AvaloniaTest]
    public void Sending_with_no_process_running_is_a_no_op()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            Assert.DoesNotThrowAsync(async () => await control.SendInputAsync("ls\r"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// And on a control whose template has not been applied — the case a null-forgiving forwarder would turn
    /// into a NullReferenceException, which is a mistake this library has made before with ExitCode and Pid.
    /// </summary>
    [AvaloniaTest]
    public void Sending_before_the_template_is_applied_is_a_no_op()
    {
        var control = new TerminalControl { Process = "" };

        Assert.DoesNotThrowAsync(async () => await control.SendInputAsync("ls\r"),
            "a host can hold a control before it is realised; injecting text should not be the call that throws");
    }

    /// <summary>The same, from the window, which is two forwarders deep.</summary>
    [AvaloniaTest]
    public void Sending_from_an_unshown_window_is_a_no_op()
    {
        var window = new TerminalWindow { Process = "" };

        Assert.DoesNotThrowAsync(async () => await window.SendInputAsync("ls\r"));
    }

    /// <summary>Empty input is ignored rather than provoking a zero-byte write.</summary>
    [AvaloniaTest]
    public void Empty_input_is_ignored()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            Assert.DoesNotThrowAsync(async () => await control.SendInputAsync(string.Empty));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Reachable from the window without the caller digging for the inner control.</summary>
    [AvaloniaTest]
    public void A_window_exposes_it_too()
    {
        var window = new TerminalWindow { Process = "" }.Realise();

        try
        {
            Assert.DoesNotThrowAsync(async () => await window.SendInputAsync("ls\r"));
        }
        finally
        {
            window.Close();
        }
    }
}
