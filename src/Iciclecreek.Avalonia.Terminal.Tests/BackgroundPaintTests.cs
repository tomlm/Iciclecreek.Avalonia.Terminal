using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// What <see cref="TerminalView.Render"/> actually draws for the background.
///
/// <para>Asserted by recording the draw calls rather than by capturing a pixel: the headless platform has no
/// rasteriser, and a <see cref="DrawingGroup"/> opened for writing hands back a real
/// <see cref="DrawingContext"/> that keeps every operation issued into it. That is enough to answer the only
/// question that matters here — did a fill covering the whole control go out, and in which brush.</para>
/// </summary>
[TestFixture]
public class BackgroundPaintTests
{
    /// <summary>Every drawing the view issued, flattened.</summary>
    private static IReadOnlyList<Drawing> Capture(TerminalView view)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            view.Render(context);
        }
        return group.Children;
    }

    private static (TerminalView view, Window window) Realised(IBrush background)
    {
        var control = new TerminalControl { Process = "", Background = background };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    /// <summary>
    /// The whole point: an opaque Background must produce a fill covering the control.
    ///
    /// <para>It did not before. TerminalView is a plain Control, so Avalonia paints no background for it, and
    /// the control template is a bare Grid with no Border — the only thing that ever filled the surface was
    /// the per-cell fill, which stopped running for cells using the default background.</para>
    /// </summary>
    [AvaloniaTest]
    public void An_opaque_background_fills_the_whole_control()
    {
        var (view, window) = Realised(Brushes.Blue);

        var drawings = Capture(view);

        Assert.That(view.Bounds.Width, Is.GreaterThan(0), "sanity: the view was arranged, so there is a surface to fill");

        var fills = drawings.OfType<GeometryDrawing>().ToList();
        Assert.That(fills, Is.Not.Empty, "Render issued no fills at all");

        var surface = fills.FirstOrDefault(d =>
            d.Brush is ISolidColorBrush b && b.Color == Colors.Blue &&
            d.Geometry?.Bounds.Width >= view.Bounds.Width &&
            d.Geometry?.Bounds.Height >= view.Bounds.Height);

        Assert.That(surface, Is.Not.Null,
            "no fill in the Background brush covering the control — setting Background does nothing. "
            + $"Issued {fills.Count} fill(s): "
            + string.Join(", ", fills.Select(f => $"{(f.Brush as ISolidColorBrush)?.Color} {f.Geometry?.Bounds}")));

        window.Close();
    }

    /// <summary>
    /// The surface fill is the FIRST thing drawn, or it paints over the text it is supposed to sit behind.
    /// </summary>
    [AvaloniaTest]
    public void The_background_is_painted_before_anything_else()
    {
        var (view, window) = Realised(Brushes.Blue);

        var first = Capture(view).OfType<GeometryDrawing>().FirstOrDefault();

        Assert.That((first?.Brush as ISolidColorBrush)?.Color, Is.EqualTo(Colors.Blue),
            "the background must go down first, underneath every cell");

        window.Close();
    }

    /// <summary>
    /// A host layering the terminal over its own surface asks for Transparent, and must still see through —
    /// that is what the per-cell change was for, and it has to survive painting the surface once.
    /// </summary>
    [AvaloniaTest]
    public void A_transparent_background_paints_nothing_opaque()
    {
        var (view, window) = Realised(Brushes.Transparent);

        var opaque = Capture(view).OfType<GeometryDrawing>()
            .Any(d => d.Brush is ISolidColorBrush { Color.A: 255 } &&
                      d.Geometry?.Bounds.Width >= view.Bounds.Width);

        Assert.That(opaque, Is.False, "a transparent terminal must not stamp an opaque rectangle");

        window.Close();
    }

    /// <summary>
    /// A cell using the terminal's DEFAULT background paints nothing of its own, whatever that default is.
    /// The surface is painted once, and below a transparent one it is the parent that shows through — the
    /// cells never assert a colour they were not given.
    /// </summary>
    [AvaloniaTest]
    public void A_default_background_cell_paints_nothing_of_its_own()
    {
        var (view, window) = Realised(Brushes.Transparent);

        view.Terminal.Write("hello");          // plain text: no SGR, so every cell keeps the default
        window.UpdateLayout();

        var fills = Capture(view).OfType<GeometryDrawing>()
            .Where(d => d.Brush is ISolidColorBrush { Color.A: > 0 })
            .ToList();

        Assert.That(fills, Is.Empty,
            "a run that asked for no background painted one anyway: "
            + string.Join(", ", fills.Select(f => $"{(f.Brush as ISolidColorBrush)?.Color} {f.Geometry?.Bounds}")));

        window.Close();
    }

    /// <summary>
    /// The mirror of it: a cell that DID ask for a background still paints, transparent surface or not.
    /// Otherwise "see through" would quietly mean "lose the colours the program chose".
    /// </summary>
    [AvaloniaTest]
    public void A_cell_with_its_own_background_still_paints()
    {
        var (view, window) = Realised(Brushes.Transparent);

        view.Terminal.Write("[41mhello");   // SGR 41 — an explicit red background
        window.UpdateLayout();

        var painted = Capture(view).OfType<GeometryDrawing>()
            .Any(d => d.Brush is ISolidColorBrush { Color.A: 255 });

        Assert.That(painted, Is.True, "an explicitly coloured cell must still paint over a transparent surface");

        window.Close();
    }
}
