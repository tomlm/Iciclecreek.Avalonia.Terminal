using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Releasing the emulator, and — just as much the point — NOT releasing it when a view is merely
/// being moved.
/// </summary>
/// <remarks>
/// <c>XTerm.Terminal</c> implements <c>IDisposable</c> and holds parser subscriptions and event
/// handlers. Nothing here ever disposed one, so every view that made a terminal left it behind.
/// The care needed is in where the call goes: detaching from the logical tree is how this control
/// gets re-parented, so tearing down there would kill a terminal being moved between panels.
/// </remarks>
[TestFixture]
public class DisposalTests
{
    // Built rather than written as a literal: an ESC byte in a source file does not survive every
    // tool that touches it, and a silently empty Esc turns every sequence below into plain text
    // that prints itself. This form cannot be mangled in transit.
    private static readonly string Esc = ((char)0x1B).ToString();

    private static (TerminalView view, Window window) Realised()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static int _probe;

    /// <summary>
    /// Whether the emulator still accepts writes, which is what a disposed one stops doing.
    /// </summary>
    /// <remarks>
    /// A DIFFERENT character each call, and that is not decoration. A disposed terminal ignores the
    /// write rather than clearing anything, so the cell keeps whatever was last put there — probing
    /// with a fixed character would read back the previous probe and call a dead terminal alive.
    /// </remarks>
    private static bool StillLive(XTerm.Terminal terminal)
    {
        var ch = (char)('A' + (_probe++ % 26));
        terminal.Write($"{Esc}[1;1H{ch}");
        return terminal.Buffer.Lines[terminal.Buffer.YBase]?[0].Content == ch.ToString();
    }

    [AvaloniaTest]
    public void Disposing_the_view_disposes_the_emulator()
    {
        var (view, window) = Realised();
        try
        {
            var terminal = view.Terminal;
            Assert.That(StillLive(terminal), Is.True, "the terminal should answer before disposal");

            view.Dispose();

            Assert.That(StillLive(terminal), Is.False,
                "a disposed terminal ignores writes, which is how its parser subscriptions being "
                + "released shows from the outside");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Disposing_drops_the_subscriptions_re_attachment_never_restores()
    {
        // OscReceived and Colors.ColorChanged are subscribed ONCE, in OnInitialized, and the
        // detach path leaves them alone -- correctly, since re-attachment does not put them back
        // and dropping them there would leave a re-parented view deaf to OSC and blind to palette
        // changes. That makes Dispose the only place they can go, and until they did, a host
        // holding the terminal afterwards went on calling into a disposed view through them.
        var (view, window) = Realised();
        try
        {
            var terminal = view.Terminal;
            view.Dispose();

            // Both handlers would throw or touch disposed state if they were still attached; the
            // terminal is disposed, so the sequences below reach nothing either way. What is being
            // asserted is that driving the palette and an OSC sequence after disposal is quiet.
            Assert.DoesNotThrow(() =>
            {
                terminal.Colors.SetForeground(0x00FF00);
                terminal.Write($"{Esc}]0;after disposal{Esc}\\");
                Dispatcher.UIThread.RunJobs();
            });
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Disposing_twice_is_harmless()
    {
        var (view, window) = Realised();
        try
        {
            view.Dispose();
            Assert.DoesNotThrow(() => view.Dispose());
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Detaching_from_the_logical_tree_does_not_dispose_the_emulator()
    {
        // The whole reason disposal is explicit. A view is detached and re-attached during ordinary
        // initialisation, and BeginReparent exists because moving one between panels must not end
        // the session behind it.
        var (view, window) = Realised();
        try
        {
            var terminal = view.Terminal;

            window.Content = null;                    // detach
            Dispatcher.UIThread.RunJobs();

            Assert.That(StillLive(terminal), Is.True,
                "detaching is how this view gets MOVED; the terminal has to survive it");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_view_survives_being_re_parented()
    {
        var (view, window) = Realised();
        var second = new Window { Width = 800, Height = 600 };
        try
        {
            var terminal = view.Terminal;

            view.BeginReparent();
            window.Content = null;
            second.Content = view;
            second.Show();
            second.UpdateLayout();
            view.EndReparent();
            Dispatcher.UIThread.RunJobs();

            Assert.That(StillLive(terminal), Is.True);
        }
        finally { second.Close(); window.Close(); }
    }

    [AvaloniaTest]
    public void Re_attaching_a_disposed_view_does_nothing_rather_than_throwing()
    {
        // Avalonia raises logical-tree notifications during teardown in an order the application
        // does not fully control, and throwing from a lifecycle hook takes down the app for what is
        // at worst a view that will not paint.
        var (view, window) = Realised();
        var second = new Window { Width = 800, Height = 600 };
        try
        {
            view.Dispose();
            window.Content = null;

            Assert.DoesNotThrow(() =>
            {
                second.Content = view;
                second.Show();
                second.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            });
        }
        finally { second.Close(); window.Close(); }
    }

    [AvaloniaTest]
    public void Disposing_the_control_reaches_the_view_inside_it()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            var terminal = control.Terminal;

            control.Dispose();

            Assert.That(StillLive(terminal), Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Disposing_a_control_that_never_got_a_template_is_harmless()
    {
        // There is no inner view before the template is applied, and a caller disposing early
        // should not have to know that.
        var control = new TerminalControl { Process = "" };
        Assert.DoesNotThrow(() => control.Dispose());
    }
}
