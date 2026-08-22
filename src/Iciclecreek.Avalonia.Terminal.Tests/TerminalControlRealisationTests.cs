using Avalonia.Media;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The pattern README.md itself demonstrates: construct a control, set properties, and only then show it.
///
/// <para>This is the case most likely to break silently. TerminalControl's inner TerminalView does not exist
/// until the template is applied, so a property implemented as a plain forwarder guarded by
/// <c>if (_terminalView != null)</c> discards anything set beforehand and never reports it. A consumer
/// following the README's own example gets a control that ignores half of what they configured.</para>
///
/// <para>Assertions go through <c>control.Terminal.Options</c> wherever the emulator is what matters, never
/// through <c>view.Options</c>: those are two different objects. When Options is null the view builds the
/// emulator from one fresh TerminalOptions in OnInitialized, and TerminalControl.OnApplyTemplate then
/// assigns a SECOND fresh instance to view.Options. Only the first one reaches the terminal.</para>
/// </summary>
[TestFixture]
public class TerminalControlRealisationTests
{
    /// <summary>
    /// The smoke test the rest of the fixture depends on. If Show() does not realise the template, every
    /// other assertion here is vacuous, so this failing first is the useful outcome.
    /// </summary>
    [AvaloniaTest]
    public void Showing_the_control_realises_its_template()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            var view = control.View();

            Assert.That(view, Is.Not.Null);
            Assert.That(control.Terminal, Is.Not.Null,
                "the emulator is built in TerminalView.OnInitialized, which the template runs via EndInit");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Process set before realisation must reach the view — it is what gets launched.</summary>
    [AvaloniaTest]
    public void Process_set_before_realisation_reaches_the_view()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            Assert.That(control.View().Process, Is.EqualTo(""),
                $"observed '{control.View().Process}'");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Args set before realisation must reach the view.</summary>
    [AvaloniaTest]
    public void Args_set_before_realisation_reach_the_view()
    {
        var control = new TerminalControl { Process = "", Args = new[] { "-NoLogo", "-Interactive" } };
        var window = TerminalHost.Show(control);

        try
        {
            Assert.That(control.View().Args, Is.EqualTo(new[] { "-NoLogo", "-Interactive" }),
                $"observed [{string.Join(", ", control.View().Args ?? [])}]");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>StartingDirectory set before realisation must reach the view — it is where the shell starts.</summary>
    [AvaloniaTest]
    public void StartingDirectory_set_before_realisation_reaches_the_view()
    {
        var expected = Path.GetTempPath();
        var control = new TerminalControl { Process = "", StartingDirectory = expected };
        var window = TerminalHost.Show(control);

        try
        {
            Assert.That(control.View().StartingDirectory, Is.EqualTo(expected),
                $"observed '{control.View().StartingDirectory ?? "null"}'");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>FontSize set before realisation must reach the view — it drives the cell grid.</summary>
    [AvaloniaTest]
    public void FontSize_set_before_realisation_reaches_the_view()
    {
        var control = new TerminalControl { Process = "", FontSize = 22 };
        var window = TerminalHost.Show(control);

        try
        {
            Assert.That(control.View().FontSize, Is.EqualTo(22),
                $"observed {control.View().FontSize}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>SelectionBrush set before realisation must reach the view.</summary>
    [AvaloniaTest]
    public void SelectionBrush_set_before_realisation_reaches_the_view()
    {
        var brush = new SolidColorBrush(Colors.Magenta);
        var control = new TerminalControl { Process = "", SelectionBrush = brush };
        var window = TerminalHost.Show(control);

        try
        {
            Assert.That(control.View().SelectionBrush, Is.SameAs(brush),
                "the brush instance itself should reach the view, not a copy");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// README: BufferSize is the "Scrollback buffer size (number of lines)".
    ///
    /// <para>Asserted against <c>Terminal.Options.Scrollback</c>, because that is the only place the value has
    /// any effect. BufferSize is a real StyledProperty on TerminalControl, but it is absent from the control
    /// template's TemplateBindings and never assigned in OnApplyTemplate, so today it reaches nothing.</para>
    /// </summary>
    [AvaloniaTest]
    public void BufferSize_set_before_realisation_reaches_the_emulator()
    {
        var control = new TerminalControl { Process = "", BufferSize = 5000 };
        var window = TerminalHost.Show(control);

        try
        {
            Assert.That(control.Terminal.Options.Scrollback, Is.EqualTo(5000),
                $"a documented scrollback size that never reaches the emulator is not a scrollback size. "
                + $"Observed {control.Terminal.Options.Scrollback}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The default BufferSize must reach the emulator too, not just a non-default one.</summary>
    [AvaloniaTest]
    public void Default_BufferSize_reaches_the_emulator()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            Assert.That(control.Terminal.Options.Scrollback, Is.EqualTo(1000),
                $"observed {control.Terminal.Options.Scrollback}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A caller-supplied Options object must be the one the emulator actually runs on, not a fresh
    /// default manufactured alongside it.
    /// </summary>
    [AvaloniaTest]
    public void Caller_supplied_Options_reach_the_emulator()
    {
        var options = new XTerm.Options.TerminalOptions();
        options.WindowOptions.MaximizeWin = true;

        var control = new TerminalControl { Process = "", Options = options };
        var window = TerminalHost.Show(control);

        try
        {
            Assert.That(control.Terminal.Options.WindowOptions.MaximizeWin, Is.True,
                "the caller's Options object must be what the emulator runs on; a fresh default silently "
                + "discards every flag they set");
        }
        finally
        {
            window.Close();
        }
    }
}
