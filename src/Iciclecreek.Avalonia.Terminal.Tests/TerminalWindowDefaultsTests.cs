using System.Runtime.InteropServices;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The defaults README.md publishes for <see cref="TerminalWindow"/>, plus the properties it inherits in
/// spirit from TerminalControl, asserted on a fresh window that has never been shown.
/// </summary>
[TestFixture]
public class TerminalWindowDefaultsTests
{
    private static bool Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>README: CloseOnProcessExit default true.</summary>
    [AvaloniaTest]
    public void CloseOnProcessExit_defaults_to_true()
    {
        var window = new TerminalWindow();

        Assert.That(window.CloseOnProcessExit, Is.True, $"observed {window.CloseOnProcessExit}");
    }

    /// <summary>README: UpdateTitleFromTerminal default true.</summary>
    [AvaloniaTest]
    public void UpdateTitleFromTerminal_defaults_to_true()
    {
        var window = new TerminalWindow();

        Assert.That(window.UpdateTitleFromTerminal, Is.True, $"observed {window.UpdateTitleFromTerminal}");
    }

    /// <summary>Same platform-shell contract as TerminalControl; the two must not disagree.</summary>
    [AvaloniaTest]
    public void Process_defaults_to_the_platform_shell()
    {
        var window = new TerminalWindow();

        var expected = Windows ? "cmd.exe" : "bash";
        Assert.That(window.Process, Is.EqualTo(expected),
            $"TerminalWindow and TerminalControl must agree on the default shell. Observed '{window.Process}'");
    }

    /// <summary>
    /// TerminalWindow already gets this right — it defaults to the current directory where TerminalControl
    /// defaults to null. Locked in so the two converge rather than the correct one regressing to match.
    /// </summary>
    [AvaloniaTest]
    public void StartingDirectory_defaults_to_the_current_directory()
    {
        var window = new TerminalWindow();

        Assert.That(window.StartingDirectory, Is.EqualTo(Environment.CurrentDirectory),
            $"observed '{window.StartingDirectory ?? "null"}'");
    }

    /// <summary>Window properties set before Show() must survive both hops: window -> control -> view.</summary>
    [AvaloniaTest]
    public void Properties_set_before_showing_reach_the_inner_view()
    {
        var expected = Path.GetTempPath();
        var window = new TerminalWindow { Process = "", StartingDirectory = expected, FontSize = 19 }.Realise();

        try
        {
            var control = window.Control();
            var view = control.View();

            Assert.Multiple(() =>
            {
                Assert.That(control.StartingDirectory, Is.EqualTo(expected),
                    $"first hop (window -> control) observed '{control.StartingDirectory ?? "null"}'");
                Assert.That(view.StartingDirectory, Is.EqualTo(expected),
                    $"second hop (control -> view) observed '{view.StartingDirectory ?? "null"}'");
                Assert.That(view.FontSize, Is.EqualTo(19),
                    $"second hop (control -> view) observed {view.FontSize}");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The convenience overload documented as "sets StartingDirectory, Process, and Args, then launches".
    /// The write-through half is assertable without launching anything.
    /// </summary>
    [AvaloniaTest]
    public void LaunchProcess_overload_writes_through_to_the_properties()
    {
        var window = new TerminalWindow { Process = "" }.Realise();
        var dir = Path.GetTempPath();

        // Deliberately not awaited: the launch itself needs a real process, but the documented
        // property write-through happens before that and is what this asserts.
        _ = window.LaunchProcess(dir, "/bin/sh", "-c", "exit 0");

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(window.StartingDirectory, Is.EqualTo(dir), $"observed '{window.StartingDirectory ?? "null"}'");
                Assert.That(window.Process, Is.EqualTo("/bin/sh"), $"observed '{window.Process}'");
                Assert.That(window.Args, Is.EqualTo(new[] { "-c", "exit 0" }),
                    $"observed [{string.Join(", ", window.Args ?? [])}]");
            });
        }
        finally
        {
            window.Close();
        }
    }
}
