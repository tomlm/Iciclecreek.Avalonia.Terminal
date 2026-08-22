using Avalonia.Media;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// A terminal has to be monospace by default.
///
/// <para>This is not a cosmetic preference. The cell grid is derived from a single measured advance width,
/// so in a proportional font every glyph that is not that width drifts out of its column: box drawing
/// breaks up, aligned output stops aligning, and the cursor lands somewhere other than where the text is.
/// <see cref="FontFamily.Default"/> is the system UI font, which is proportional — so defaulting to it
/// produced exactly that, and only looked correct in the demo because its App.axaml happens to style
/// ManagedTerminalWindow by name. TerminalWindow matched no selector and rendered a shell in the UI font.
/// </para>
///
/// <para>These assert the DEFAULT only. A host that sets a font, or a parent that passes one down by
/// inheritance, still wins — the default decides what happens when nobody said anything at all.</para>
/// </summary>
[TestFixture]
public class TerminalFontTests
{
    private static bool LooksMonospace(FontFamily family) =>
        !Equals(family, FontFamily.Default) &&
        family.Name.Contains("Mono", StringComparison.OrdinalIgnoreCase)
            || family.FamilyNames.Any(n => n.Contains("Mono", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("Cascadia", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("Courier", StringComparison.OrdinalIgnoreCase));

    [AvaloniaTest]
    public void A_view_defaults_to_a_monospace_font()
    {
        var view = new TerminalView();

        Assert.That(view.FontFamily, Is.Not.EqualTo(FontFamily.Default),
            "FontFamily.Default is the proportional system UI font");
        Assert.That(LooksMonospace(view.FontFamily), Is.True,
            $"observed '{view.FontFamily}'");
    }

    [AvaloniaTest]
    public void A_control_defaults_to_a_monospace_font()
    {
        var control = new TerminalControl();

        Assert.That(control.FontFamily, Is.Not.EqualTo(FontFamily.Default),
            "FontFamily.Default is the proportional system UI font");
        Assert.That(LooksMonospace(control.FontFamily), Is.True,
            $"observed '{control.FontFamily}'");
    }

    /// <summary>The case actually observed: a bare TerminalWindow rendering a shell in the UI font.</summary>
    [AvaloniaTest]
    public void A_window_defaults_to_a_monospace_font()
    {
        var window = new TerminalWindow { Process = "" };

        Assert.That(window.FontFamily, Is.Not.EqualTo(FontFamily.Default),
            "a bare TerminalWindow rendered its shell in the proportional UI font");
        Assert.That(LooksMonospace(window.FontFamily), Is.True,
            $"observed '{window.FontFamily}'");
    }

    /// <summary>And the monospace default must survive the two hops down to the view that renders.</summary>
    [AvaloniaTest]
    public void The_monospace_default_reaches_the_view_that_renders()
    {
        var window = new TerminalWindow { Process = "" }.Realise();

        try
        {
            var view = window.Control().View();

            Assert.That(LooksMonospace(view.FontFamily), Is.True,
                $"the view doing the drawing is the one that matters. Observed '{view.FontFamily}'");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A host that chooses its own font must still win — this is a default, not a mandate.</summary>
    [AvaloniaTest]
    public void An_explicit_font_still_overrides_the_default()
    {
        var chosen = new FontFamily("Courier New");
        var control = new TerminalControl { Process = "", FontFamily = chosen };
        var window = TerminalHost.Show(control);

        try
        {
            Assert.That(control.View().FontFamily, Is.EqualTo(chosen),
                $"observed '{control.View().FontFamily}'");
        }
        finally
        {
            window.Close();
        }
    }
}
