using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// README lists "Terminal window manipulation commands (resize, move, minimize, maximize, etc.)" as a
/// headline feature of this library, and <see cref="TerminalWindow"/> wires up eleven handlers for exactly
/// that. None of them can fire.
///
/// <para>EnsureTerminalControl sets Options and then turns on seventeen WindowOptions flags — the flags that
/// tell the emulator to honour those escape sequences at all. Several lines later it binds OptionsProperty
/// to the window's own Options, which defaults to null, and the binding pushes that null straight over the
/// configured object. TerminalControl.OnApplyTemplate then manufactures a fresh, unconfigured
/// TerminalOptions. The handlers remain subscribed and remain unreachable.</para>
///
/// <para>Nothing in this repo caught it because nothing in this repo uses TerminalWindow — the Demo app uses
/// ManagedTerminalWindow, which escapes the bug only because it never binds OptionsProperty. A feature
/// documented, wired, and shipped, with no consumer in-tree to notice it never worked.</para>
///
/// <para>Asserted against <c>Terminal.Options</c>, never <c>view.Options</c>: those are different objects,
/// and only the former is what the emulator actually consults.</para>
/// </summary>
[TestFixture]
public class TerminalWindowOptionsTests
{
    /// <summary>
    /// The flags TerminalWindow explicitly turns on so its own handlers can be reached. Each is named
    /// individually rather than looped, so a failure says which capability was lost.
    /// </summary>
    [AvaloniaTest]
    public void A_default_window_can_actually_receive_the_commands_it_handles()
    {
        var window = new TerminalWindow { Process = "" }.Realise();

        try
        {
            var options = window.Control().Terminal.Options.WindowOptions;

            Assert.Multiple(() =>
            {
                Assert.That(options.MaximizeWin, Is.True, "TerminalWindow handles maximize but never asked for it");
                Assert.That(options.MinimizeWin, Is.True, "TerminalWindow handles minimize but never asked for it");
                Assert.That(options.RestoreWin, Is.True, "TerminalWindow handles restore but never asked for it");
                Assert.That(options.FullscreenWin, Is.True, "TerminalWindow handles fullscreen but never asked for it");
                Assert.That(options.RaiseWin, Is.True, "TerminalWindow handles raise but never asked for it");
                Assert.That(options.LowerWin, Is.True, "TerminalWindow handles lower but never asked for it");
                Assert.That(options.SetWinPosition, Is.True, "TerminalWindow handles move but never asked for it");
                Assert.That(options.SetWinSizePixels, Is.True, "TerminalWindow handles resize but never asked for it");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The query half. TerminalWindow answers WindowInfoRequested for title, position, size and state, which
    /// is equally dead without the corresponding Get flags.
    /// </summary>
    [AvaloniaTest]
    public void A_default_window_can_actually_receive_the_queries_it_answers()
    {
        var window = new TerminalWindow { Process = "" }.Realise();

        try
        {
            var options = window.Control().Terminal.Options.WindowOptions;

            Assert.Multiple(() =>
            {
                Assert.That(options.GetWinTitle, Is.True, "TerminalWindow answers title queries but never asked for them");
                Assert.That(options.GetIconTitle, Is.True, "TerminalWindow answers icon-title queries but never asked for them");
                Assert.That(options.GetWinPosition, Is.True, "TerminalWindow answers position queries but never asked for them");
                Assert.That(options.GetWinSizePixels, Is.True, "TerminalWindow answers size queries but never asked for them");
                Assert.That(options.GetWinState, Is.True, "TerminalWindow answers state queries but never asked for them");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A caller who supplies their own Options must keep the flags TerminalWindow adds on top — the window's
    /// own handlers depend on them regardless of who created the object.
    /// </summary>
    [AvaloniaTest]
    public void Caller_supplied_Options_still_gain_the_window_command_flags()
    {
        var options = new XTerm.Options.TerminalOptions();
        var window = new TerminalWindow { Process = "", Options = options }.Realise();

        try
        {
            Assert.That(window.Control().Terminal.Options.WindowOptions.MaximizeWin, Is.True,
                "supplying Options must not cost you the window-command support TerminalWindow advertises");
        }
        finally
        {
            window.Close();
        }
    }
}
