using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// <see cref="TerminalWindow"/> must expose what <see cref="TerminalControl"/> exposes.
///
/// <para>README.md already promised this — its property table is headed "Additional Properties (beyond
/// TerminalControl)", and its methods table lists Kill and WaitForExit — but the members did not exist, so
/// a consumer following the documentation met a compile error. These lock the parity in so it cannot drift
/// apart again silently.</para>
/// </summary>
[TestFixture]
public class TerminalWindowParityTests
{
    /// <summary>BufferSize must reach the emulator through both hops, not just sit on the window.</summary>
    [AvaloniaTest]
    public void BufferSize_reaches_the_emulator()
    {
        var window = new TerminalWindow { Process = "", BufferSize = 4096 }.Realise();

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(window.Control().BufferSize, Is.EqualTo(4096),
                    $"first hop observed {window.Control().BufferSize}");
                Assert.That(window.Terminal.Options.Scrollback, Is.EqualTo(4096),
                    $"a scrollback size that never reaches the emulator is not a scrollback size. "
                    + $"Observed {window.Terminal.Options.Scrollback}");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ShowCaretOnClick set before the window is shown must survive.
    ///
    /// <para>This is the case that used to be lost. On TerminalControl it was a plain forwarder whose setter
    /// was guarded by a null check on the inner view, so anything assigned from XAML or an object
    /// initializer — i.e. everything a consumer would naturally write — was dropped and never re-applied.
    /// It is a real StyledProperty on both types now.</para>
    /// </summary>
    [AvaloniaTest]
    public void ShowCaretOnClick_set_before_realisation_reaches_the_view()
    {
        var window = new TerminalWindow { Process = "", ShowCaretOnClick = true }.Realise();

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(window.ShowCaretOnClick, Is.True, "the window dropped its own value");
                Assert.That(window.Control().ShowCaretOnClick, Is.True, "first hop dropped it");
                Assert.That(window.Control().View().ShowCaretOnClick, Is.True,
                    "the view doing the hit-testing is the one that has to know");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The same, one level down, since the control had the original defect.</summary>
    [AvaloniaTest]
    public void A_control_keeps_ShowCaretOnClick_set_before_realisation()
    {
        var control = new TerminalControl { Process = "", ShowCaretOnClick = true };
        var host = TerminalHost.Show(control);

        try
        {
            Assert.That(control.View().ShowCaretOnClick, Is.True,
                "set before the template existed, and silently discarded before this was a StyledProperty");
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>Terminal is the emulator the window is actually driving, not a separate instance.</summary>
    [AvaloniaTest]
    public void Terminal_is_the_same_emulator_the_control_drives()
    {
        var window = new TerminalWindow { Process = "" }.Realise();

        try
        {
            Assert.That(window.Terminal, Is.SameAs(window.Control().Terminal),
                "two different emulators would mean writes and reads disagreeing about state");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Kill on a window with no process running is a no-op, as it is on the control.</summary>
    [AvaloniaTest]
    public void Kill_is_safe_when_no_process_is_running()
    {
        var window = new TerminalWindow { Process = "" }.Realise();

        try
        {
            Assert.DoesNotThrow(() => window.Kill(),
                "killing a terminal that never launched is a no-op, not an error");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Reparent support forwards rather than throwing, matching the control's null-safe behaviour.</summary>
    [AvaloniaTest]
    public void Reparent_calls_forward_without_throwing()
    {
        var window = new TerminalWindow { Process = "" }.Realise();

        try
        {
            Assert.DoesNotThrow(() =>
            {
                window.BeginReparent();
                window.EndReparent();
            });
        }
        finally
        {
            window.Close();
        }
    }
}
