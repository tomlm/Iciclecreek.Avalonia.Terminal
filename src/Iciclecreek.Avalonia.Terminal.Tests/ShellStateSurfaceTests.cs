using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;
using XTerm.Common;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The emulator's OSC 133 shell state, as a host sees it: <c>ShellState</c> on the view, the control and
/// the window, and <c>ShellStateChanged</c> reaching a handler at each level on the UI thread. The
/// transitions themselves are the emulator's and are covered there.
/// </summary>
[TestFixture]
public class ShellStateSurfaceTests
{
    private const string Esc = "\u001b";
    private const string Bel = "\u0007";

    private static string Mark(string mark) => $"{Esc}]133;{mark}{Bel}";

    [AvaloniaTest]
    public void The_view_reports_a_running_command_and_raises_the_event()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            var view = control.View();
            var seen = new List<(ShellState Old, ShellState New, int? Exit)>();
            view.ShellStateChanged += (_, e) => seen.Add((e.OldState, e.NewState, e.ExitCode));

            Assert.That(view.ShellState, Is.EqualTo(ShellState.Unknown));

            view.Terminal.Write(Mark("A") + Mark("B") + Mark("C"));
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.ShellState, Is.EqualTo(ShellState.Running));
            Assert.That(seen, Is.EqualTo(new[]
            {
                (ShellState.Unknown, ShellState.Prompt, (int?)null),
                (ShellState.Prompt, ShellState.Input, (int?)null),
                (ShellState.Input, ShellState.Running, (int?)null),
            }));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_control_sees_the_state_and_the_exit_code()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            var seen = new List<ShellStateChangedEventArgs>();
            control.ShellStateChanged += (_, e) => seen.Add(e);

            control.View().Terminal.Write(Mark("C"));
            Dispatcher.UIThread.RunJobs();
            Assert.That(control.ShellState, Is.EqualTo(ShellState.Running),
                "a host deciding whether to confirm a close reads this");

            control.View().Terminal.Write(Mark("D;130"));
            Dispatcher.UIThread.RunJobs();

            Assert.That(control.ShellState, Is.EqualTo(ShellState.Prompt));
            Assert.That(seen.Select(e => e.NewState), Is.EqualTo(new[] { ShellState.Running, ShellState.Prompt }));
            Assert.That(seen[1].ExitCode, Is.EqualTo(130));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_window_sees_the_state_through_both_hops()
    {
        var window = new TerminalWindow { Process = "" }.Realise();
        try
        {
            var seen = new List<ShellState>();
            window.ShellStateChanged += (_, e) => seen.Add(e.NewState);

            window.Control().View().Terminal.Write(Mark("C"));
            Dispatcher.UIThread.RunJobs();

            Assert.That(window.ShellState, Is.EqualTo(ShellState.Running));
            Assert.That(seen, Is.EqualTo(new[] { ShellState.Running }));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void An_unrealised_control_and_window_report_unknown()
    {
        // Before the template runs there is no emulator to ask; "cannot say" is the honest answer.
        Assert.That(new TerminalControl { Process = "" }.ShellState, Is.EqualTo(ShellState.Unknown));
        Assert.That(new TerminalWindow { Process = "" }.ShellState, Is.EqualTo(ShellState.Unknown));
    }

    [AvaloniaTest]
    public void The_event_reaches_the_host_on_the_UI_thread_when_written_off_it()
    {
        // The read loop calls Terminal.Write on the pty reader thread, which is where the emulator raises
        // its event. The host's handler must still run on the UI thread.
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            var view = control.View();
            var onUiThread = new List<bool>();
            control.ShellStateChanged += (_, _) => onUiThread.Add(Dispatcher.UIThread.CheckAccess());

            Exception? thrown = null;
            var writer = new Thread(() =>
            {
                try { view.Terminal.Write(Mark("C")); }
                catch (Exception ex) { thrown = ex; }
            });
            writer.Start();
            writer.Join();
            Dispatcher.UIThread.RunJobs();

            Assert.That(thrown, Is.Null);
            Assert.That(onUiThread, Is.EqualTo(new[] { true }));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_event_still_arrives_after_the_view_is_reparented()
    {
        // A host moving a terminal between panes detaches and re-attaches it; the subscription has to
        // follow the same path as every other emulator event.
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            var view = control.View();
            view.Terminal.Write(Mark("C"));
            Dispatcher.UIThread.RunJobs();

            window.Content = null;
            window.Content = control;
            window.UpdateLayout();

            Assert.That(control.View(), Is.SameAs(view), "the re-attach must reuse the view, or this proves nothing");
            Assert.That(control.ShellState, Is.EqualTo(ShellState.Running),
                "a visual detach is the same session, so the command is still running");

            var seen = 0;
            control.ShellStateChanged += (_, _) => seen++;

            view.Terminal.Write(Mark("D;0"));
            Dispatcher.UIThread.RunJobs();

            Assert.That(seen, Is.EqualTo(1), "exactly once: a re-attach must not double-subscribe either");
        }
        finally { window.Close(); }
    }

    // ---- a new session -----------------------------------------------------------------------

    [AvaloniaTest]
    [TestCase("B", ShellState.Input)]
    [TestCase("C", ShellState.Running)]
    public async Task A_new_session_without_integration_does_not_inherit_the_old_shells_state(string lastMark, ShellState left)
    {
        // The emulator outlives the process. Without the reset the new shell, which never sends a mark,
        // was described forever by the old one's last: idle from a B, running from a C.
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        var first = new PushConnection();
        var second = new PushConnection();
        try
        {
            var view = control.View();
            var seen = new List<(ShellState Old, ShellState New)>();
            control.ShellStateChanged += (_, e) => seen.Add((e.OldState, e.NewState));

            view.AttachConnection(first);
            first.Push(Mark("A") + "old$ " + Mark(lastMark));
            await WaitUntil(() => view.ShellState == left, $"the first shell reached {left}");

            view.AttachConnection(second);
            Assert.That(view.ShellState, Is.EqualTo(ShellState.Unknown), "the new session starts unknown");

            second.Push("plain$ ");
            await WaitUntil(() => view.Terminal.Buffer.X >= 7, "the new shell's prompt was written");
            Dispatcher.UIThread.RunJobs();

            Assert.That(control.ShellState, Is.EqualTo(ShellState.Unknown));
            Assert.That(view.Terminal.Buffer.Lines[view.Terminal.Buffer.YBase]!.TranslateToString(true),
                Does.StartWith("old$ "), "the reset is the shell state's alone; the screen stays");
            Assert.That(seen.Last(), Is.EqualTo((left, ShellState.Unknown)), "the host is told the state went");
        }
        finally
        {
            first.Done();
            second.Done();
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task A_new_session_hears_its_own_marks_from_scratch()
    {
        // The old shell's last mark was B, and so is the new one's first: without the reset the emulator
        // saw no change and raised nothing, and the host never heard the new shell at all.
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        var first = new PushConnection();
        var second = new PushConnection();
        try
        {
            var view = control.View();
            view.AttachConnection(first);
            first.Push(Mark("B"));
            await WaitUntil(() => view.ShellState == ShellState.Input, "the first shell reached Input");

            view.AttachConnection(second);
            Dispatcher.UIThread.RunJobs();
            var seen = new List<ShellState>();
            control.ShellStateChanged += (_, e) => seen.Add(e.NewState);

            second.Push(Mark("B"));
            await WaitUntil(() => view.ShellState == ShellState.Input, "the second shell reached Input");
            Dispatcher.UIThread.RunJobs();

            Assert.That(seen, Is.EqualTo(new[] { ShellState.Input }));
        }
        finally
        {
            first.Done();
            second.Done();
            window.Close();
        }
    }

    [AvaloniaTest]
    public void A_change_still_queued_from_the_old_session_is_not_reported_as_the_new_ones()
    {
        // The old reader's C was parsed, and its notification posted, before the host installed the next
        // session; the UI thread only gets to it afterwards. Delivered then it would say the NEW session
        // is running a command.
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        var first = new PushConnection();
        var second = new PushConnection();
        try
        {
            var view = control.View();
            view.AttachConnection(first);
            Dispatcher.UIThread.RunJobs();

            var seen = new List<(ShellState Old, ShellState New)>();
            control.ShellStateChanged += (_, e) => seen.Add((e.OldState, e.NewState));

            view.Terminal.Write(Mark("C"));     // parsed during session one; its post is still queued
            view.AttachConnection(second);
            Dispatcher.UIThread.RunJobs();

            Assert.That(seen, Is.EqualTo(new[] { (ShellState.Running, ShellState.Unknown) }),
                "only the new session's reset reaches the host");
            Assert.That(control.ShellState, Is.EqualTo(ShellState.Unknown));
        }
        finally
        {
            first.Done();
            second.Done();
            window.Close();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out waiting until {because}");
            await Task.Delay(10);
        }
    }
}
