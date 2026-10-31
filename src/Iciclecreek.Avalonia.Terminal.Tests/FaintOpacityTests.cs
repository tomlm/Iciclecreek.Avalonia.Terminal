using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Iciclecreek.Avalonia.Terminal;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// FaintOpacity: how much of an SGR 2 cell's foreground survives.
/// </summary>
/// <remarks>
/// <para>It was a constant, 0.4, with nothing a host could set. Ghostty exposes the same knob as
/// <c>faint-opacity</c> (default 0.5, clamped to [0, 1]), and a host mapping its config needs
/// somewhere to put it.</para>
/// <para>The wiring is asserted through the run cache, as MinimumContrastTests does: a run's
/// <c>Foreground</c> is the brush the renderer resolved, so its opacity is the faint strength the
/// frame actually used.</para>
/// </remarks>
[TestFixture]
public class FaintOpacityTests
{
    private const string Esc = "\u001b";

    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    /// <summary>Render a frame and hand back the opacity of the first row's first run.</summary>
    private static double FirstRunOpacity(TerminalView view)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            view.Render(context);
        }

        var line = view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY];
        Assert.That(line, Is.Not.Null);
        var runs = line!.Cache as List<TerminalView.CachedTextRun>;
        Assert.That(runs, Is.Not.Null, "the row produced no cached runs");

        var run = runs!.First(r => r.StartX == 0);
        Assert.That(run.Foreground, Is.InstanceOf<ISolidColorBrush>());
        return run.Foreground!.Opacity;
    }

    // ----------------------------------------------------------- the default

    [AvaloniaTest]
    public void Defaults_to_what_the_control_has_always_drawn()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new TerminalView { Process = "" }.FaintOpacity, Is.EqualTo(0.4));
            Assert.That(new TerminalControl { Process = "" }.FaintOpacity, Is.EqualTo(0.4));
            Assert.That(new TerminalWindow { Process = "" }.FaintOpacity, Is.EqualTo(0.4));
        });
    }

    [AvaloniaTest]
    public void A_faint_run_is_drawn_at_the_default_strength()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[2mfaint");

            Assert.That(FirstRunOpacity(view), Is.EqualTo(0.4).Within(1e-9));
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------------------ the setting

    [AvaloniaTest]
    public void Setting_it_changes_the_faint_runs_opacity()
    {
        var (view, window) = Realised();
        try
        {
            view.FaintOpacity = 0.7;
            view.Terminal.Write($"{Esc}[2;31mfaint");

            Assert.That(FirstRunOpacity(view), Is.EqualTo(0.7).Within(1e-9));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The cached runs hold brushes resolved at the old strength and are replayed, not rebuilt — so a
    /// change that did not drop them would reach only lines written afterwards.
    /// </summary>
    [AvaloniaTest]
    public void Changing_it_live_reaches_lines_already_on_screen()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[2mfaint");
            Assert.That(FirstRunOpacity(view), Is.EqualTo(0.4).Within(1e-9), "the cache is built at the old strength");

            view.FaintOpacity = 0.5;

            Assert.That(view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY]!.Cache, Is.Null,
                "the runs cached under the old strength must be dropped");
            Assert.That(FirstRunOpacity(view), Is.EqualTo(0.5).Within(1e-9));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Text_that_is_not_faint_is_untouched()
    {
        var (view, window) = Realised();
        try
        {
            view.FaintOpacity = 0.1;
            view.Terminal.Write($"{Esc}[31mplain");

            Assert.That(FirstRunOpacity(view), Is.EqualTo(1.0));
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------- the glyph, never the fill

    /// <summary>
    /// Render a frame and collect every fill brush and every glyph brush it drew, through any
    /// transforms -- the doubled rows and sized blocks draw inside one.
    /// </summary>
    private static (List<IBrush> Fills, List<IBrush> Glyphs) Frame(TerminalView view)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            view.Render(context);
        }

        var fills = new List<IBrush>();
        var glyphs = new List<IBrush>();
        void Walk(DrawingGroup g)
        {
            foreach (var child in g.Children)
            {
                switch (child)
                {
                    case DrawingGroup inner: Walk(inner); break;
                    case GeometryDrawing { Brush: { } b }: fills.Add(b); break;
                    // Ink only. The doubled-row path shapes the blank rest of its row too, in the
                    // default foreground, and spaces are not what faint is about.
                    case GlyphRunDrawing { Foreground: { } f, GlyphRun: { } run }
                        when !string.IsNullOrWhiteSpace(run.Characters.ToString()):
                        glyphs.Add(f);
                        break;
                }
            }
        }
        Walk(group);
        return (fills, glyphs);
    }

    private static Color Rgb(int v)
        => Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));

    /// <summary>
    /// The shape of the bug: faint was baked into the foreground BEFORE the inverse swap, so it went
    /// with the brush to the fill. The glyph must carry it and every fill must be opaque.
    /// </summary>
    private static void AssertGlyphFaintAndFillsOpaque(TerminalView view, double faint, string where)
    {
        var palette = view.Terminal.Colors.Take();
        var (fills, glyphs) = Frame(view);

        Assert.Multiple(() =>
        {
            Assert.That(glyphs, Is.Not.Empty, $"{where}: the frame drew no glyph");
            Assert.That(glyphs.Select(g => g.Opacity), Is.All.EqualTo(faint).Within(1e-9),
                $"{where}: faint dims the glyph");
            Assert.That(fills.Select(f => f.Opacity), Is.All.EqualTo(1.0),
                $"{where}: and never the fill behind it");
            Assert.That(fills.OfType<ISolidColorBrush>().Select(f => f.Color), Has.Member(Rgb(palette.Foreground)),
                $"{where}: the inverted fill is painted in the foreground colour");
        });
    }

    private static (TerminalView view, Window window) RealisedNoCursor()
    {
        var (view, window) = Realised();
        view.SuppressCursor = true;
        return (view, window);
    }

    [AvaloniaTest]
    public void Inverse_faint_dims_the_glyph_not_the_fill()
    {
        var (view, window) = RealisedNoCursor();
        try
        {
            view.FaintOpacity = 0.25;
            view.Terminal.Write($"{Esc}[2;7mX");

            AssertGlyphFaintAndFillsOpaque(view, 0.25, "SGR 2;7");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Faint_under_DECSCNM_dims_the_glyph_not_the_fill()
    {
        var (view, window) = RealisedNoCursor();
        try
        {
            view.FaintOpacity = 0.25;
            view.Terminal.Write($"{Esc}[?5h{Esc}[2mX");
            Assert.That(view.Terminal.ReverseVideo, Is.True, "sanity: the screen is inverted");

            AssertGlyphFaintAndFillsOpaque(view, 0.25, "DECSCNM");
        }
        finally { window.Close(); }
    }

    /// <summary>At 0 the glyph vanishes and the inverse bar stays -- not a cell that draws nothing.</summary>
    [AvaloniaTest]
    public void At_zero_an_inverse_faint_cell_keeps_its_bar_and_loses_its_glyph()
    {
        var (view, window) = RealisedNoCursor();
        try
        {
            view.FaintOpacity = 0;
            view.Terminal.Write($"{Esc}[2;7mX");

            AssertGlyphFaintAndFillsOpaque(view, 0.0, "opacity 0");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_double_width_row_dims_the_glyph_not_the_fill()
    {
        var (view, window) = RealisedNoCursor();
        try
        {
            view.FaintOpacity = 0.25;
            view.Terminal.Write($"{Esc}#6{Esc}[2;7mX");

            AssertGlyphFaintAndFillsOpaque(view, 0.25, "DECDWL");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_sized_block_dims_the_glyph_not_the_fill()
    {
        var (view, window) = RealisedNoCursor();
        try
        {
            view.FaintOpacity = 0.25;
            view.Terminal.Write($"{Esc}[2;7m{Esc}]66;s=2;X{Esc}\\");

            AssertGlyphFaintAndFillsOpaque(view, 0.25, "OSC 66");
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------------------- the range

    [AvaloniaTest]
    public void Out_of_range_values_are_clamped_on_every_surface()
    {
        (double set, double expected)[] cases =
        {
            (-0.5, 0.0), (1.5, 1.0),
            (double.PositiveInfinity, 1.0), (double.NegativeInfinity, 0.0),
            (double.NaN, 0.4), // not a strength anyone asked for, so the default
            (0.0, 0.0), (1.0, 1.0),
        };

        var view = new TerminalView { Process = "" };
        var control = new TerminalControl { Process = "" };
        var window = new TerminalWindow { Process = "" };

        Assert.Multiple(() =>
        {
            foreach (var (set, expected) in cases)
            {
                view.FaintOpacity = set;
                control.FaintOpacity = set;
                window.FaintOpacity = set;
                Assert.That(view.FaintOpacity, Is.EqualTo(expected), $"view, set {set}");
                Assert.That(control.FaintOpacity, Is.EqualTo(expected), $"control, set {set}");
                Assert.That(window.FaintOpacity, Is.EqualTo(expected), $"window, set {set}");
            }
        });
    }

    [AvaloniaTest]
    public void A_direct_caller_cannot_produce_an_out_of_range_brush()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[2mX");
            var line = view.Terminal.Buffer.GetLine(view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y)!;
            var palette = view.Terminal.Colors.Take();

            Assert.Multiple(() =>
            {
                Assert.That(line[0].GetForegroundBrush(palette, Brushes.White, true, 3).Opacity, Is.EqualTo(1.0));
                Assert.That(line[0].GetForegroundBrush(palette, Brushes.White, true, -3).Opacity, Is.EqualTo(0.0));
                Assert.That(line[0].GetForegroundBrush(palette, Brushes.White).Opacity, Is.EqualTo(0.4));
            });
        }
        finally { window.Close(); }
    }

    // ---------------------------------------------------------- the forwarding

    [AvaloniaTest]
    public void Set_on_a_control_before_realisation_it_reaches_the_view()
    {
        var control = new TerminalControl { Process = "", FaintOpacity = 0.5 };
        var host = TerminalHost.Show(control);
        try
        {
            Assert.That(control.View().FaintOpacity, Is.EqualTo(0.5));

            control.FaintOpacity = 0.8;
            Assert.That(control.View().FaintOpacity, Is.EqualTo(0.8), "and live, after realisation");
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public void Set_on_a_window_it_reaches_the_view_through_both_hops()
    {
        var window = new TerminalWindow { Process = "", FaintOpacity = 0.5 }.Realise();
        try
        {
            var view = window.Control().View();
            Assert.That(view.FaintOpacity, Is.EqualTo(0.5));

            window.FaintOpacity = 0.25;
            Assert.That(view.FaintOpacity, Is.EqualTo(0.25), "and live, after realisation");
        }
        finally { window.Close(); }
    }
}
