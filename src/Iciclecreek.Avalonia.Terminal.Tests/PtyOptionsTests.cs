using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The two <c>PtyOptions</c> members the launch path never exposed: <c>VerbatimCommandLine</c> (issue #17,
/// where the reporter had forked the repo to set it) and <c>Environment</c>.
///
/// <para>Both defaults must leave the launch exactly as it was before these existed — false, and null — so
/// that adding the properties changes nothing for anyone who does not set them. That is the assertion that
/// matters most here; the rest is that a value set before the control is realised still arrives, which is
/// where this library has lost values before.</para>
///
/// <para>What these do NOT cover is the launch behaviour itself, and the reason differs per property.
/// <c>VerbatimCommandLine</c> is observable only on Windows — only Windows builds a command line string for
/// the child, while Unix hands <c>exec</c> an argument vector — so asserting it needs a purpose-built echo
/// child that CI, running on Linux, would never execute. <c>Environment</c> would need the same. Both were
/// measured by hand instead, on both platforms:</para>
/// <code>
/// args: hello world | a"b | plain
///   Windows  VerbatimCommandLine=false -> hello world | a"b | plain
///            VerbatimCommandLine=true  -> hello | world | ab plain
///   Linux    either                    -> hello world | a"b | plain
///
/// env:  one variable added
///   Windows  child env 88 -> 89 entries, inherited vars intact, MYVAR=hello
///   Linux    child env 31 -> 32 entries, PATH intact,           MYVAR=hello
/// </code>
/// <para>The environment result is the one worth recording: the PTY layer MERGES these into what the child
/// would otherwise inherit rather than replacing it, so a caller setting one variable does not silently lose
/// <c>PATH</c>. Had it replaced, this property would need a much louder warning than it has.</para>
/// </summary>
[TestFixture]
public class PtyOptionsTests
{
    // ---- VerbatimCommandLine ---------------------------------------------------------------------

    [AvaloniaTest]
    public void A_view_does_not_use_a_verbatim_command_line_by_default()
    {
        var view = new TerminalView();

        Assert.That(view.VerbatimCommandLine, Is.False,
            "every consumer that predates this property relies on arguments arriving as written");
    }

    [AvaloniaTest]
    public void A_control_does_not_use_a_verbatim_command_line_by_default()
    {
        var control = new TerminalControl();

        Assert.That(control.VerbatimCommandLine, Is.False, $"observed {control.VerbatimCommandLine}");
    }

    [AvaloniaTest]
    public void A_window_does_not_use_a_verbatim_command_line_by_default()
    {
        var window = new TerminalWindow { Process = "" };

        Assert.That(window.VerbatimCommandLine, Is.False, $"observed {window.VerbatimCommandLine}");
    }

    /// <summary>
    /// Opting in before the control is realised has to survive — a value set from XAML or an object
    /// initializer arrives before the inner view exists, and the view is what builds the launch options.
    /// </summary>
    [AvaloniaTest]
    public void VerbatimCommandLine_set_before_realisation_reaches_the_view()
    {
        var control = new TerminalControl { Process = "", VerbatimCommandLine = true };
        var host = TerminalHost.Show(control);

        try
        {
            Assert.That(control.View().VerbatimCommandLine, Is.True,
                $"observed {control.View().VerbatimCommandLine}");
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>And through both hops from the window.</summary>
    [AvaloniaTest]
    public void VerbatimCommandLine_set_on_a_window_reaches_the_view()
    {
        var window = new TerminalWindow { Process = "", VerbatimCommandLine = true }.Realise();

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(window.Control().VerbatimCommandLine, Is.True,
                    $"first hop observed {window.Control().VerbatimCommandLine}");
                Assert.That(window.Control().View().VerbatimCommandLine, Is.True,
                    $"second hop observed {window.Control().View().VerbatimCommandLine}");
            });
        }
        finally
        {
            window.Close();
        }
    }

    // ---- EnvironmentVariables --------------------------------------------------------------------

    /// <summary>
    /// Null by default, and that matters more than it looks: null is what makes the launch path identical
    /// to what it was before this property existed. An empty dictionary would not be the same thing.
    /// </summary>
    [AvaloniaTest]
    public void Environment_variables_are_null_by_default()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new TerminalView().EnvironmentVariables, Is.Null, "view");
            Assert.That(new TerminalControl().EnvironmentVariables, Is.Null, "control");
            Assert.That(new TerminalWindow { Process = "" }.EnvironmentVariables, Is.Null, "window");
        });
    }

    [AvaloniaTest]
    public void Environment_variables_set_before_realisation_reach_the_view()
    {
        var env = new Dictionary<string, string> { ["MYVAR"] = "hello" };
        var control = new TerminalControl { Process = "", EnvironmentVariables = env };
        var host = TerminalHost.Show(control);

        try
        {
            Assert.That(control.View().EnvironmentVariables, Is.SameAs(env),
                "the dictionary itself should reach the view, not a copy — a caller may still be holding it");
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaTest]
    public void Environment_variables_set_on_a_window_reach_the_view()
    {
        var env = new Dictionary<string, string> { ["MYVAR"] = "hello" };
        var window = new TerminalWindow { Process = "", EnvironmentVariables = env }.Realise();

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(window.Control().EnvironmentVariables, Is.SameAs(env), "first hop");
                Assert.That(window.Control().View().EnvironmentVariables, Is.SameAs(env), "second hop");
            });
        }
        finally
        {
            window.Close();
        }
    }
}
