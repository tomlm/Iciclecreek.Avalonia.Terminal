using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The default font chain has to reach an emoji family, and every path to it has to agree.
/// </summary>
/// <remarks>
/// <para>Naming the families in the control theme was not enough, and the way it failed is the reason this
/// exists. TerminalWindow assigns FontFamily in its constructor, and a local value outranks a style setter —
/// so the theme's chain never applied to it. Three places decide this and they must not drift.</para>
/// <para>Asserted on the chain rather than on pixels: whether a glyph comes out in colour depends on the
/// machine's installed fonts, which a test cannot rely on. What it CAN pin is that we asked.</para>
/// </remarks>
[TestFixture]
public class FontFallbackTests
{
    private static readonly string[] EmojiFamilies =
        { "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji" };

    [Test]
    public void The_default_chain_reaches_an_emoji_family()
    {
        var chain = TerminalView.DefaultFontFamily.ToString();

        foreach (var family in EmojiFamilies)
            Assert.That(chain, Does.Contain(family), $"{family} is missing, so that platform has no emoji");
    }

    /// <summary>
    /// And they come last. The cell grid is measured from the first family that exists, and these are
    /// proportional — one in front would break the grid rather than fix the glyphs.
    /// </summary>
    [Test]
    public void The_emoji_families_come_after_the_monospace_ones()
    {
        var chain = TerminalView.DefaultFontFamily.ToString();

        var firstEmoji = EmojiFamilies.Select(f => chain.IndexOf(f, StringComparison.Ordinal)).Min();
        foreach (var mono in new[] { "Cascadia Mono", "Consolas", "Menlo", "DejaVu Sans Mono", "Courier New" })
            Assert.That(chain.IndexOf(mono, StringComparison.Ordinal), Is.LessThan(firstEmoji),
                $"{mono} must be tried before any emoji family, or the grid is measured from a proportional font");
    }

    /// <summary>
    /// A TerminalWindow gets the same chain. It assigns FontFamily in its constructor, and a local value
    /// beats the theme — which is exactly how the theme's chain came to be ignored.
    /// </summary>
    [AvaloniaTest]
    public void A_terminal_window_gets_the_default_chain()
    {
        var window = new TerminalWindow { Process = "" }.Realise();

        var chain = window.Control().View().FontFamily.ToString();
        foreach (var family in EmojiFamilies)
            Assert.That(chain, Does.Contain(family), "the window overrode the chain with one that has no emoji");

        window.Close();
    }

    /// <summary>And so does a bare TerminalControl, which reaches it through the theme instead.</summary>
    [AvaloniaTest]
    public void A_terminal_control_gets_the_default_chain()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        var chain = control.View().FontFamily.ToString();
        foreach (var family in EmojiFamilies)
            Assert.That(chain, Does.Contain(family));

        window.Close();
    }
}
