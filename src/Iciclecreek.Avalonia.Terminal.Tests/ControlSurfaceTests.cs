using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// What the control promises a host, and what it was actually delivering.
/// </summary>
/// <remarks>
/// Each of these fails the same way: quietly. A handler that is never called, a property that
/// resolves to the wrong owner, a template part whose absence disables things unrelated to it.
/// None of them throws, so an integration keeps compiling and keeps running while doing less than
/// it says.
/// </remarks>
[TestFixture]
public class ControlSurfaceTests
{
    // ------------------------------------------- handlers added before the template

    [AvaloniaTest]
    public void A_handler_added_before_the_template_still_receives_input()
    {
        // Subscribing early is the ORDINARY case: it is what a XAML attribute does, and what any
        // host that news up the control and wires it does. The add accessor was an if with no else,
        // so those handlers went nowhere -- and += appeared to have worked.
        var control = new TerminalControl { Process = "" };

        var seen = new System.Text.StringBuilder();
        control.InputSent += (_, data) => { lock (seen) seen.Append(data); };

        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            control.Terminal.Write("");   // force the view to exist before sending
            control.SendInputAsync("hello").GetAwaiter().GetResult();
            Thread.Sleep(150);

            lock (seen)
                Assert.That(seen.ToString(), Does.Contain("hello"),
                    "a handler attached before the template must be moved onto the view, not dropped");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_handler_removed_before_the_template_is_not_attached_afterwards()
    {
        // The other half: holding handlers is only correct if letting go of them works too.
        var control = new TerminalControl { Process = "" };

        var seen = new System.Text.StringBuilder();
        EventHandler<string> handler = (_, data) => { lock (seen) seen.Append(data); };
        control.InputSent += handler;
        control.InputSent -= handler;

        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            control.SendInputAsync("hello").GetAwaiter().GetResult();
            Thread.Sleep(150);

            lock (seen)
                Assert.That(seen.ToString(), Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_handler_survives_the_template_being_re_applied()
    {
        // The other end of the same problem, and the one that made buffering pending handlers the
        // wrong fix rather than an incomplete one. A handler added AFTER the template went straight
        // onto that view, so re-applying the template left it on an orphan: leaked, and no longer
        // firing for the control.
        //
        // The control now owns the event and forwards from whichever view is current, which is how
        // the four events beside it already worked.
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var seen = new System.Text.StringBuilder();
            control.InputSent += (_, data) => { lock (seen) seen.Append(data); };

            // A second template application, which OnApplyTemplate explicitly supports -- it
            // unsubscribes the old parts at the top.
            control.Template = ScrollBarlessTemplate();
            control.ApplyTemplate();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            control.SendInputAsync("hello").GetAwaiter().GetResult();
            Thread.Sleep(150);

            lock (seen)
                Assert.That(seen.ToString(), Does.Contain("hello"),
                    "a handler must follow the control, not the view it happened to be added under");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_control_is_the_sender_of_its_own_event()
    {
        // Consequence of owning the event rather than forwarding the subscription, and worth
        // asserting because it is a visible change: the sender used to be the view.
        var control = new TerminalControl { Process = "" };
        object? sender = null;
        control.InputSent += (s, _) => sender ??= s;

        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            control.SendInputAsync("hello").GetAwaiter().GetResult();
            Thread.Sleep(150);

            Assert.That(sender, Is.SameAs(control), "matching ProcessExited, ShellReady and the rest");
        }
        finally { window.Close(); }
    }

    // --------------------------------------------- a template with no scrollbar

    [AvaloniaTest]
    public void A_template_without_a_scrollbar_still_forwards_the_views_events()
    {
        // Not wanting a scrollbar is a reasonable thing to want, and it used to cost the host every
        // event this control forwards, the Options bridge and the current directory -- because all
        // of it sat inside one `if` with the scrollbar.
        var control = new TerminalControl { Process = "" };
        control.Template = ScrollBarlessTemplate();

        var seen = new System.Text.StringBuilder();
        control.InputSent += (_, data) => { lock (seen) seen.Append(data); };

        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            control.SendInputAsync("hello").GetAwaiter().GetResult();
            Thread.Sleep(150);

            lock (seen)
                Assert.That(seen.ToString(), Does.Contain("hello"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_template_without_a_scrollbar_still_gets_the_live_options()
    {
        var control = new TerminalControl { Process = "" };
        control.Template = ScrollBarlessTemplate();

        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.That(control.Options, Is.SameAs(control.Terminal.Options),
                "the Options bridge has nothing to do with whether there is a scrollbar");
        }
        finally { window.Close(); }
    }

    /// <summary>A template with the view and no PART_ScrollBar.</summary>
    private static FuncControlTemplate ScrollBarlessTemplate()
        => new FuncControlTemplate((_, scope) =>
        {
            var view = new TerminalView { Name = "PART_TerminalView" };
            view.RegisterInNameScope(scope);
            return view;
        });

    // ------------------------------------------------------ property ownership

    [AvaloniaTest]
    public void The_views_Options_property_is_owned_by_the_view()
    {
        // It was registered against TerminalControl, which registers an Options of its OWN under
        // that same owner and name -- two StyledProperty objects claiming one registry entry. So a
        // style or setter aimed at TerminalControl.Options could resolve to whichever was reached
        // first, and nothing aimed at TerminalView.Options was aimed at this view at all.
        Assert.That(TerminalView.OptionsProperty.OwnerType, Is.EqualTo(typeof(TerminalView)));
        Assert.That(TerminalControl.OptionsProperty.OwnerType, Is.EqualTo(typeof(TerminalControl)));
        Assert.That(TerminalView.OptionsProperty, Is.Not.SameAs(TerminalControl.OptionsProperty),
            "sanity: they are and should be two properties -- the bug was the owner, not the pair");
    }

    // ------------------------------- the finding that turned out not to be one

    [AvaloniaTest]
    public void Synchronized_output_really_does_suppress_painting()
    {
        // DEC 2026 was dead on the ordinary path. There were two copies of the subscribe list --
        // one for the first attach, one for a re-attach -- and SynchronizedOutputChanged was in the
        // re-attach copy only. So a terminal did not honour an atomic update until it had been
        // detached and put back, which nearly none of them ever are.
        //
        // Worth saying how this was nearly missed: the subscription is plainly there in the source,
        // in OnAttachedToLogicalTree, and reading that was enough to convince me the finding was
        // wrong. What it does not show is that the method returns two lines earlier on a FIRST
        // attach, because the emulator does not exist yet. This test is what settled it.
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            var esc = ((char)0x1B).ToString();

            view.Terminal.Write($"{esc}[?2026h");
            Dispatcher.UIThread.RunJobs();
            Assert.That(AtomicUpdate(view), Is.True, "BSU must have begun an atomic update");

            view.Terminal.Write($"{esc}[?2026l");
            Dispatcher.UIThread.RunJobs();
            Assert.That(AtomicUpdate(view), Is.False, "and ESU must have ended it");
        }
        finally { window.Close(); }
    }

    private static bool AtomicUpdate(TerminalView view)
    {
        var f = typeof(TerminalView).GetField("_atomicUpdate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(f, Is.Not.Null, "_atomicUpdate has been renamed; this test needs updating");
        return (bool)f!.GetValue(view)!;
    }

    // ------------------------------------------------ measuring with no constraint

    [AvaloniaTest]
    public void The_view_can_be_measured_with_an_infinite_height()
    {
        // A ScrollViewer measures its content with an infinite dimension to ask how big it wants to
        // be, and so do StackPanel, an auto-sized Grid row and a WrapPanel. Handing that infinity
        // back as a desired size makes Avalonia throw out of the layout pass -- so the view could
        // not be put inside any of them at all.
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            view.Measure(new Size(800, double.PositiveInfinity));

            Assert.That(double.IsInfinity(view.DesiredSize.Height), Is.False,
                "a desired size of infinity is not a size");
            Assert.That(view.DesiredSize.Height, Is.GreaterThan(0));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_view_survives_being_put_in_a_StackPanel()
    {
        // The same thing said the way a host would hit it.
        var view = new TerminalView { Process = "" };
        var panel = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Vertical };
        panel.Children.Add(view);

        var window = new Window { Width = 800, Height = 600, Content = panel };
        window.Show();
        try
        {
            Assert.DoesNotThrow(() => window.UpdateLayout());
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_finite_constraint_is_still_taken_in_full()
    {
        // The terminal takes whatever it is given; the change is only about what it says when it is
        // given nothing to go on.
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            view.Measure(new Size(640, 480));

            Assert.That(view.DesiredSize, Is.EqualTo(new Size(640, 480)));
        }
        finally { window.Close(); }
    }
}
