using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The Skia layer has to be handed the whole font chain, not the first name in it.
/// </summary>
/// <remarks>
/// <para>It takes a string where the rest of the control takes a <see cref="FontFamily"/>, and
/// <see cref="FontFamily.Name"/> is only the first of the comma-separated names. Handed that, the layer
/// splits it back into candidates and gets exactly one — and SKTypeface.FromFamilyName does not fail on
/// a name it cannot find, it SUBSTITUTES. The default chain opens with Cascadia Mono, which no stock
/// Linux has, so every cell was drawn in the platform's proportional default while the cell grid, which
/// goes through Avalonia's own resolution, was measured from a monospace face further down the chain.
/// Columns landed correctly and the characters inside them did not.</para>
/// <para>Asserted on the chain and not on pixels, for the reason <c>FontFallbackTests</c> gives: which
/// face wins depends on what the machine has installed, and a test cannot rely on that. What it can pin
/// is that we asked with every name.</para>
/// <para>Read off the SNAPSHOT rather than off the field it is built from, because the defect was the
/// wiring between them: the chain was computed correctly and then not the thing passed along.</para>
/// </remarks>
[TestFixture]
public class SkiaFontChainTests
{
    /// <summary>
    /// Renders through the direct path and returns the snapshot it drew from.
    /// </summary>
    /// <remarks>
    /// The reflection, the forced CharWidth and the bitmap target are all as
    /// <c>RendererViewportTests</c> does it, and for the reasons given there: headless layout does not
    /// measure the cell, and a recording DrawingGroup rejects the custom draw operation so the layer
    /// under test is quietly dropped.
    /// </remarks>
    private static Skia.TerminalSnapshot SnapshotOf(FontFamily? family)
    {
        var view = new TerminalView { Process = "", UseSkiaRenderer = true };
        if (family is not null)
            view.FontFamily = family;

        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.That(view.CharWidth, Is.GreaterThan(0), "sanity: the cell metrics can be measured");

            using var target = new RenderTargetBitmap(new PixelSize(800, 600));
            using (var context = target.CreateDrawingContext())
                view.Render(context);

            var layer = typeof(TerminalView)
                .GetField("_lastSkiaLayer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(view);
            Assert.That(layer, Is.Not.Null, "the render built no Skia layer; the direct path did not run");

            return (Skia.TerminalSnapshot)layer!.GetType()
                .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(layer)!;
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void Every_family_in_the_default_chain_reaches_the_skia_layer()
    {
        var chain = SnapshotOf(null).FontFamily;

        foreach (var family in TerminalView.DefaultFontFamily.FamilyNames)
            Assert.That(chain, Does.Contain(family),
                $"{family} never reaches the Skia layer, so it cannot be resolved there");

        Assert.That(chain, Is.Not.EqualTo(TerminalView.DefaultFontFamily.Name),
            "the first name alone is the defect this exists to catch");
    }

    /// <summary>
    /// And for a chain the HOST supplies, which is the case a consumer naming a ligature face in front
    /// of fallbacks actually hits.
    /// </summary>
    [AvaloniaTest]
    public void A_host_supplied_chain_arrives_whole()
    {
        var chain = SnapshotOf(new FontFamily("No Such Face,DejaVu Sans Mono,monospace")).FontFamily;

        Assert.That(chain.Split(',').Select(n => n.Trim()),
            Is.EqualTo(new[] { "No Such Face", "DejaVu Sans Mono", "monospace" }));
    }
}
