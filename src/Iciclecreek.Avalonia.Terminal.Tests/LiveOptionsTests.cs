using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// That <c>Options</c> reaches the emulator after the control is running, not just while it is
/// being built.
/// </summary>
/// <remarks>
/// XTerm.NET snapshots the options it is constructed with, so the object a caller hands in stops
/// being the one the emulator reads the moment the terminal exists. Left pointing at that object,
/// this property went on accepting writes that nothing would ever consult — no exception and no
/// warning, which is worse than a break that throws, because the integration keeps compiling and
/// keeps running while quietly ignoring its configuration.
/// </remarks>
[TestFixture]
public class LiveOptionsTests
{
    private static (TerminalView view, Window window) RealisedView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    [AvaloniaTest]
    public void The_view_exposes_the_options_the_emulator_actually_reads()
    {
        var (view, window) = RealisedView();
        try
        {
            Assert.That(view.Options, Is.SameAs(view.Terminal.Options));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Setting_an_option_through_the_view_after_startup_reaches_the_emulator()
    {
        var (view, window) = RealisedView();
        try
        {
            view.Options!.CursorBlink = !view.Terminal.Options.CursorBlink;

            Assert.That(view.Options.CursorBlink, Is.EqualTo(view.Terminal.Options.CursorBlink),
                "the property and the emulator must be looking at one object, not two");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_object_a_caller_constructed_with_is_no_longer_what_the_emulator_reads()
    {
        // Not a regression, but the reason this change is needed: XTerm.NET copies at construction,
        // so this is the behaviour the property has to route around rather than inherit.
        var mine = new XTerm.Options.TerminalOptions();
        var view = new TerminalView { Process = "", Options = mine };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            Assert.That(view.Terminal.Options, Is.Not.SameAs(mine));
            Assert.That(view.Options, Is.Not.SameAs(mine),
                "and the property must have followed the emulator rather than the caller's copy");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void An_option_set_before_realisation_still_reaches_the_emulator()
    {
        // The construction-time path has to keep working: the snapshot is taken FROM this object.
        var view = new TerminalView
        {
            Process = "",
            Options = new XTerm.Options.TerminalOptions { TermName = "xterm-testing" },
        };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            Assert.That(view.Terminal.Options.TermName, Is.EqualTo("xterm-testing"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_control_exposes_the_same_live_object_as_the_view()
    {
        // A host holding the CONTROL -- which is most of them -- must not be left pointing at the
        // object it handed down at template time.
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.That(control.Options, Is.SameAs(control.Terminal.Options));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Setting_an_option_through_the_control_after_startup_reaches_the_emulator()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            control.Options!.CursorBlink = !control.Terminal.Options.CursorBlink;

            Assert.That(control.Options.CursorBlink, Is.EqualTo(control.Terminal.Options.CursorBlink));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_binding_on_Options_survives_the_view_adopting_the_live_instance()
    {
        // A GUARD rather than a regression test, and worth saying so. Copilot read the seeding
        // below as WPF would -- where SetValue writes a local value that detaches a binding for
        // good -- but Avalonia does not work that way: flipping both call sites back to plain
        // SetValue leaves this passing. So nothing here is currently broken, and this exists to
        // notice if that stops being true, or if the seeding starts competing with a host's
        // binding some other way.
        //
        // Asserted on the CONTROL rather than the view, because the view deliberately snaps any
        // foreign value back to the emulator's instance, which would mask the question entirely.
        var source = new SourceHolder { Value = new XTerm.Options.TerminalOptions { TermName = "bound-one" } };
        var control = new TerminalControl { Process = "" };
        control.Bind(TerminalControl.OptionsProperty,
                     new global::Avalonia.Data.Binding(nameof(SourceHolder.Value)) { Source = source });

        var window = TerminalHost.Show(control);
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var replacement = new XTerm.Options.TerminalOptions { TermName = "second-one" };
            source.Value = replacement;
            Dispatcher.UIThread.RunJobs();

            Assert.That(control.Options, Is.SameAs(replacement),
                "a host that bound Options must still own the property after the control seeds "
                + "itself from the view");
        }
        finally { window.Close(); }
    }

    /// <summary>A bindable source, so the test can push a new value through a live binding.</summary>
    private sealed class SourceHolder : global::Avalonia.AvaloniaObject
    {
        public static readonly global::Avalonia.StyledProperty<XTerm.Options.TerminalOptions?> ValueProperty =
            global::Avalonia.AvaloniaProperty.Register<SourceHolder, XTerm.Options.TerminalOptions?>(nameof(Value));

        public XTerm.Options.TerminalOptions? Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }
    }

    [AvaloniaTest]
    public void A_live_option_written_after_startup_changes_what_the_terminal_does()
    {
        // Reference equality proves the plumbing; this proves it matters. Scrollback is live in
        // XTerm.NET as of its options audit, and resizing the history is observable.
        var (view, window) = RealisedView();
        try
        {
            var before = view.Terminal.Buffer.Lines.MaxLength;

            view.Options!.Scrollback = view.Options.Scrollback + 500;

            Assert.That(view.Terminal.Buffer.Lines.MaxLength, Is.GreaterThan(before));
        }
        finally { window.Close(); }
    }
}
