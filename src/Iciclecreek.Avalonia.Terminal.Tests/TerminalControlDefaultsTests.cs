using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The defaults README.md publishes for <see cref="TerminalControl"/>, asserted on a freshly constructed
/// control that has never been shown.
///
/// <para>These matter more than they look. The property table in the README is the contract a consumer
/// codes against before they have run anything, and a default that quietly differs is discovered only
/// once their terminal launches in the wrong directory or scrolls back the wrong distance.</para>
///
/// <para>Nothing here shows a window, so nothing here needs closing.</para>
/// </summary>
[TestFixture]
public class TerminalControlDefaultsTests
{
    private static bool Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// README: "cmd.exe (Windows) / sh (Unix)".
    ///
    /// <para>The Unix default is actually <c>bash</c>, and this test asserts <c>bash</c> deliberately —
    /// here the DOC is the thing that is wrong, not the code. bash is the better default for an
    /// interactive terminal (line editing, history) and changing it to sh to satisfy a table would
    /// degrade every Unix consumer to satisfy a typo. The README is corrected instead.</para>
    /// </summary>
    [AvaloniaTest]
    public void Process_defaults_to_the_platform_shell()
    {
        var control = new TerminalControl();

        var expected = Windows ? "cmd.exe" : "bash";
        Assert.That(control.Process, Is.EqualTo(expected),
            $"a consumer reads this default from the README before launching anything. Observed '{control.Process}'");
    }

    /// <summary>README: ProcessArgs default "Empty".</summary>
    [AvaloniaTest]
    public void Args_defaults_to_empty()
    {
        var control = new TerminalControl();

        Assert.That(control.ProcessArgs, Is.Empty,
            $"observed {control.ProcessArgs?.Count.ToString() ?? "null"} argument(s)");
    }

    /// <summary>
    /// README: StartingDirectory default "Current working directory".
    ///
    /// <para>Unlike the Process default above, this one is a CODE bug. TerminalView and TerminalWindow both
    /// default to <see cref="Environment.CurrentDirectory"/>; only TerminalControl defaults to null, and
    /// because the template binds the control's value onto the view, that null overwrites the view's own
    /// sensible default. Three types, one concept, two answers.</para>
    /// </summary>
    [AvaloniaTest]
    public void StartingDirectory_defaults_to_the_current_directory()
    {
        var control = new TerminalControl();

        Assert.That(control.StartingDirectory, Is.EqualTo(Environment.CurrentDirectory),
            $"TerminalView and TerminalWindow both default to the current directory; TerminalControl must agree. "
            + $"Observed '{control.StartingDirectory ?? "null"}'");
    }

    /// <summary>README: BufferSize default 1000.</summary>
    [AvaloniaTest]
    public void BufferSize_defaults_to_1000()
    {
        var control = new TerminalControl();

        Assert.That(control.BufferSize, Is.EqualTo(1000), $"observed {control.BufferSize}");
    }

    /// <summary>README: SelectionBrush default "Semi-transparent blue".</summary>
    [AvaloniaTest]
    public void SelectionBrush_defaults_to_semi_transparent_blue()
    {
        var control = new TerminalControl();

        Assert.That(control.SelectionBrush, Is.TypeOf<SolidColorBrush>(),
            $"observed {control.SelectionBrush?.GetType().Name ?? "null"}");

        var brush = (SolidColorBrush)control.SelectionBrush!;
        Assert.That(brush.Color, Is.EqualTo(Color.FromArgb(128, 0, 120, 215)),
            $"observed {brush.Color}");
    }
}
