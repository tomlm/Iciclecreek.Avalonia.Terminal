using Avalonia.Media;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// That the forwarded members actually work, rather than merely existing.
///
/// <para><see cref="SurfaceParityTests"/> proves the surface is complete by walking it with reflection —
/// but a member that exists and returns nonsense passes that test perfectly. These assert the values go
/// where they are supposed to, which is the half reflection cannot see.</para>
/// </summary>
[TestFixture]
public class ForwardedMemberTests
{
    // ---- cursor appearance: real properties, so a value set before realisation must survive ----------

    [AvaloniaTest]
    public void Cursor_appearance_set_before_realisation_reaches_the_view()
    {
        var control = new TerminalControl
        {
            Process = "",
            CursorColor = Colors.Magenta,
            CursorStyle = XTerm.Common.CursorStyle.Block,
            CursorBlink = false,
            CursorBlinkRate = 250,
        };
        var host = TerminalHost.Show(control);

        try
        {
            var view = control.View();
            Assert.Multiple(() =>
            {
                Assert.That(view.CursorColor, Is.EqualTo(Colors.Magenta), $"observed {view.CursorColor}");
                Assert.That(view.CursorStyle, Is.EqualTo(XTerm.Common.CursorStyle.Block), $"observed {view.CursorStyle}");
                Assert.That(view.CursorBlink, Is.False, $"observed {view.CursorBlink}");
                Assert.That(view.CursorBlinkRate, Is.EqualTo(250), $"observed {view.CursorBlinkRate}");
            });
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>And through both hops from the window.</summary>
    [AvaloniaTest]
    public void Cursor_appearance_set_on_a_window_reaches_the_view()
    {
        var window = new TerminalWindow
        {
            Process = "",
            CursorColor = Colors.Lime,
            CursorStyle = XTerm.Common.CursorStyle.Underline,
        }.Realise();

        try
        {
            var view = window.Control().View();
            Assert.Multiple(() =>
            {
                Assert.That(view.CursorColor, Is.EqualTo(Colors.Lime), $"observed {view.CursorColor}");
                Assert.That(view.CursorStyle, Is.EqualTo(XTerm.Common.CursorStyle.Underline), $"observed {view.CursorStyle}");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// TextDecorations was registered from the start with no CLR property and no template binding — settable
    /// from XAML, stored, and read by nothing.
    /// </summary>
    [AvaloniaTest]
    public void TextDecorations_set_before_realisation_reaches_the_view()
    {
        var control = new TerminalControl { Process = "", TextDecorations = TextDecorationLocation.Underline };
        var host = TerminalHost.Show(control);

        try
        {
            Assert.That(control.View().TextDecorations, Is.EqualTo(TextDecorationLocation.Underline),
                $"observed {control.View().TextDecorations?.ToString() ?? "null"}");
        }
        finally
        {
            host.Close();
        }
    }

    // ---- viewport state: live values, so these forward rather than store ----------------------------

    [AvaloniaTest]
    public void Viewport_state_reports_the_views_values()
    {
        var control = new TerminalControl { Process = "" };
        var host = TerminalHost.Show(control);

        try
        {
            var view = control.View();
            Assert.Multiple(() =>
            {
                Assert.That(control.ViewportLines, Is.EqualTo(view.ViewportLines).And.GreaterThan(0),
                    $"control={control.ViewportLines} view={view.ViewportLines}");
                Assert.That(control.MaxScrollback, Is.EqualTo(view.MaxScrollback));
                Assert.That(control.ViewportY, Is.EqualTo(view.ViewportY));
                Assert.That(control.IsAlternateBuffer, Is.EqualTo(view.IsAlternateBuffer));
            });
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>Writing ViewportY has to move the view, or a host cannot drive its own scrollbar.</summary>
    [AvaloniaTest]
    public void Setting_ViewportY_moves_the_view()
    {
        var control = new TerminalControl { Process = "" };
        var host = TerminalHost.Show(control);

        try
        {
            // Fill the buffer so there is somewhere to scroll to.
            for (var i = 0; i < 200; i++)
                control.Terminal.Write($"line {i}\r\n");

            Assume.That(control.MaxScrollback, Is.GreaterThan(0), "needs a scrollback to test against");

            control.ViewportY = 5;

            Assert.That(control.View().ViewportY, Is.EqualTo(5),
                $"observed {control.View().ViewportY}");
        }
        finally
        {
            host.Close();
        }
    }

    // ---- the no-op contracts before realisation ----------------------------------------------------

    /// <summary>
    /// Every forwarder that can sensibly do nothing does nothing, rather than throwing, on a control whose
    /// template has not been applied. This is the shape of bug ExitCode and Pid had.
    /// </summary>
    [AvaloniaTest]
    public void Forwarders_are_safe_before_the_template_is_applied()
    {
        var control = new TerminalControl { Process = "" };

        Assert.Multiple(() =>
        {
            Assert.That(control.ViewportY, Is.EqualTo(0));
            Assert.That(control.MaxScrollback, Is.EqualTo(0));
            Assert.That(control.ViewportLines, Is.EqualTo(0));
            Assert.That(control.IsAlternateBuffer, Is.False);
            Assert.That(control.IsLive, Is.False);
            Assert.DoesNotThrow(() => control.ViewportY = 3);
            Assert.DoesNotThrow(() => control.DetachConnection());
            Assert.DoesNotThrowAsync(async () => await control.PasteAsync());
            Assert.DoesNotThrowAsync(async () => await control.CopyAsync());
        });
    }

    /// <summary>The same from an unshown window, which is two forwarders deep.</summary>
    [AvaloniaTest]
    public void Window_forwarders_are_safe_before_it_is_shown()
    {
        var window = new TerminalWindow { Process = "" };

        Assert.Multiple(() =>
        {
            Assert.That(window.ViewportLines, Is.EqualTo(0));
            Assert.That(window.IsLive, Is.False);
            Assert.DoesNotThrow(() => window.DetachConnection());
            Assert.DoesNotThrowAsync(async () => await window.CopyAsync());
        });
    }

    /// <summary>
    /// CopyAsync reports false when there is no selection, rather than throwing — the return value is the
    /// signal, so a host can decide whether to fall back to something else.
    /// </summary>
    [AvaloniaTest]
    public async Task CopyAsync_reports_false_with_nothing_selected()
    {
        var control = new TerminalControl { Process = "" };
        var host = TerminalHost.Show(control);

        try
        {
            Assert.That(await control.CopyAsync(), Is.False, "nothing was selected");
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>
    /// Attaching is the one forwarder that refuses to fail quietly: handing over a live PTY and having it
    /// ignored would leave the caller believing a process is on screen when it is not.
    /// </summary>
    [AvaloniaTest]
    public void Attaching_to_an_unrealised_control_reports_why_it_cannot()
    {
        var control = new TerminalControl { Process = "" };

        Assert.Throws<InvalidOperationException>(() => control.AttachConnection(null!),
            "an unrealised control has no terminal to attach to, and should say so rather than no-op");
    }
}
