using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The MinimumContrastRatio floor: unreadable foregrounds are moved until they read.
/// </summary>
/// <remarks>
/// <para>The option sat on <c>TerminalOptions</c> since the port with nothing reading it — the same
/// shape <c>DrawBoldTextInBrightColors</c> was in before #115. The model is xterm.js's: WCAG
/// contrast, foreground-only adjustment toward black or white, ratio 1 meaning off.</para>
///
/// <para>The maths is asserted on the enforcer directly; the wiring is asserted through the run
/// cache the way the underline tests do it, using the fact that an underline with no SGR 58 colour
/// borrows the EFFECTIVE foreground — which makes the adjusted colour observable without asking
/// Avalonia's compositor anything.</para>
/// </remarks>
[TestFixture]
public class MinimumContrastTests
{
    private const string Esc = "\u001b";

    // ------------------------------------------------------------- the model

    [AvaloniaTest]
    public void Ratio_of_one_is_off()
    {
        var mc = new MinimumContrast();
        mc.SnapshotRatio(1);

        var murky = Color.FromRgb(40, 40, 40);
        Assert.That(mc.Active, Is.False);
        Assert.That(mc.Apply(murky, Colors.Black), Is.EqualTo(murky),
            "ratio 1 is the default and must change nothing at all");
    }

    [AvaloniaTest]
    public void An_unreadable_foreground_is_lifted_to_the_ratio()
    {
        // The pain the option exists for: SGR 38;5;236-grade dark grey on a dark theme.
        var mc = new MinimumContrast();
        mc.SnapshotRatio(4.5);

        var adjusted = mc.Apply(Color.FromRgb(40, 40, 40), Colors.Black);

        var ratio = MinimumContrast.ContrastRatio(
            MinimumContrast.Luminance(adjusted), MinimumContrast.Luminance(Colors.Black));
        Assert.That(ratio, Is.GreaterThanOrEqualTo(4.5), "the floor is the contract");
        Assert.That(adjusted, Is.Not.EqualTo(Color.FromRgb(40, 40, 40)));
    }

    [AvaloniaTest]
    public void A_readable_foreground_is_left_exactly_alone()
    {
        var mc = new MinimumContrast();
        mc.SnapshotRatio(4.5);

        Assert.That(mc.Apply(Colors.White, Colors.Black), Is.EqualTo(Colors.White),
            "text that already clears the floor must come back byte-identical");
    }

    [AvaloniaTest]
    public void The_adjustment_keeps_the_hue_it_was_given()
    {
        // Moving toward white is a BLEND, not a replacement: dark red must come back as a
        // readable red, or the feature repaints programs in greyscale.
        var mc = new MinimumContrast();
        mc.SnapshotRatio(4.5);

        var adjusted = mc.Apply(Color.FromRgb(80, 0, 0), Colors.Black);

        Assert.That((int)adjusted.R, Is.GreaterThan(adjusted.G), "red should still lead");
        Assert.That((int)adjusted.R, Is.GreaterThan(adjusted.B));
    }

    [AvaloniaTest]
    public void Light_on_light_moves_toward_black()
    {
        var mc = new MinimumContrast();
        mc.SnapshotRatio(4.5);

        var adjusted = mc.Apply(Color.FromRgb(0xDD, 0xDD, 0xDD), Colors.White);

        Assert.That(MinimumContrast.Luminance(adjusted),
            Is.LessThan(MinimumContrast.Luminance(Color.FromRgb(0xDD, 0xDD, 0xDD))),
            "on a light background the readable direction is down");
    }

    [AvaloniaTest]
    public void An_unreachable_ratio_takes_the_endpoint()
    {
        // A mid-grey background cannot give 21:1 against anything. The closest the model
        // allows is the better endpoint, not an infinite search.
        var mc = new MinimumContrast();
        mc.SnapshotRatio(21);

        var adjusted = mc.Apply(Color.FromRgb(100, 100, 100), Color.FromRgb(127, 127, 127));

        Assert.That(adjusted == Colors.White || adjusted == Colors.Black, Is.True,
            $"got {adjusted}; the endpoint is the only honest answer");
    }

    [AvaloniaTest]
    public void The_foreground_alpha_rides_through()
    {
        // A translucent solid can reach the floor: a translucent host background swapped into
        // the foreground by inverse. The model moves channels; how much of the colour is shown
        // is the host's already-made decision, and making it opaque overrules the host.
        var mc = new MinimumContrast();
        mc.SnapshotRatio(4.5);

        var translucent = Color.FromArgb(0x80, 40, 40, 40);
        var adjusted = mc.Apply(translucent, Colors.Black);

        Assert.That(adjusted.A, Is.EqualTo(0x80), "the computed path must keep the alpha");

        // And the CACHE must not alias the same RGB at a different alpha -- the second call is
        // the cache-hit path, which packed the colour separately from the first.
        var opaque = mc.Apply(Color.FromRgb(40, 40, 40), Colors.Black);
        Assert.That(opaque.A, Is.EqualTo(255), "an opaque twin must not inherit the translucent answer");
        Assert.That((opaque.R, opaque.G, opaque.B), Is.EqualTo((adjusted.R, adjusted.G, adjusted.B)),
            "while the channels agree, because the contrast maths saw the same RGB");

        var translucentAgain = mc.Apply(translucent, Colors.Black);
        Assert.That(translucentAgain, Is.EqualTo(adjusted), "and the cache-hit path returns alpha intact");
    }

    [AvaloniaTest]
    public void The_ratio_is_sanitised_at_the_door()
    {
        // The option is a bare settable double, so a host can write anything into it. NaN must
        // mean off -- and it needs catching explicitly, because NaN also answers false to the
        // "did the ratio change" comparison and would otherwise leave a stale ratio in force.
        var mc = new MinimumContrast();
        mc.SnapshotRatio(4.5);
        mc.SnapshotRatio(double.NaN);
        Assert.That(mc.Active, Is.False, "NaN is off, not 'whatever was set before'");

        // Out-of-range values clamp to the 1..21 the model defines.
        mc.SnapshotRatio(500);
        var adjusted = mc.Apply(Color.FromRgb(100, 100, 100), Colors.Black);
        Assert.That(adjusted, Is.EqualTo(Colors.White), "500 behaves as 21: black-on-white");

        mc.SnapshotRatio(-3);
        Assert.That(mc.Active, Is.False, "below 1 clamps to 1, which is off");
    }

    // --------------------------------------------------------- the exemption

    [AvaloniaTest]
    public void Connected_glyphs_are_exempt_and_text_is_not()
    {
        // The xterm.js carve-out: box drawing, blocks, and Powerline arrows join into shapes
        // with their neighbours, and adjusting one cell of a shape breaks the joint.
        Assert.Multiple(() =>
        {
            Assert.That(MinimumContrast.IsExemptRun("─│┌┘"), Is.True, "box drawing");
            Assert.That(MinimumContrast.IsExemptRun("█▓▒░"), Is.True, "block elements");
            Assert.That(MinimumContrast.IsExemptRun("\uE0B0\uE0B2"), Is.True, "Powerline arrows");
            Assert.That(MinimumContrast.IsExemptRun(" ─ "), Is.True, "blanks between them do not un-exempt");
            Assert.That(MinimumContrast.IsExemptRun("a"), Is.False, "text is the point of the feature");
            Assert.That(MinimumContrast.IsExemptRun("a─"), Is.False, "a mixed run is adjusted for its text");
            Assert.That(MinimumContrast.IsExemptRun(""), Is.False);
        });
    }

    // ----------------------------------------------------------- the wiring

    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    /// <summary>Render a frame and hand back the runs the first row decided on.</summary>
    private static IReadOnlyList<TerminalView.CachedTextRun> RunsForFirstRow(TerminalView view)
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
        return runs!;
    }

    /// <summary>
    /// The effective foreground of the first underlined run: with no SGR 58, the underline
    /// borrows the foreground AFTER every per-run colour decision, adjustment included.
    /// </summary>
    private static Color EffectiveForeground(TerminalView view)
    {
        var run = RunsForFirstRow(view).First(r => r.UnderlineStyle != XTerm.Common.UnderlineStyle.None);
        Assert.That(run.UnderlineBrush, Is.Not.Null);
        return ((ISolidColorBrush)run.UnderlineBrush!).Color;
    }

    [AvaloniaTest]
    public void The_frame_honours_the_option()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Options.MinimumContrastRatio = 4.5;
            view.Terminal.Write($"{Esc}[4;38;2;40;40;40mabc");

            var fg = EffectiveForeground(view);
            var bg = view.Terminal.Colors.Take().Background;
            var bgColor = Color.FromRgb((byte)((bg >> 16) & 0xFF), (byte)((bg >> 8) & 0xFF), (byte)(bg & 0xFF));

            var ratio = MinimumContrast.ContrastRatio(
                MinimumContrast.Luminance(fg), MinimumContrast.Luminance(bgColor));
            Assert.That(ratio, Is.GreaterThanOrEqualTo(4.5),
                "the option must reach the paint, not stop at the emulator");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void At_the_default_ratio_the_paint_is_untouched()
    {
        // The control for everything above: the shipped default must render byte-identically
        // to the renderer before this feature existed.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[4;38;2;40;40;40mabc");

            Assert.That(EffectiveForeground(view), Is.EqualTo(Color.FromRgb(40, 40, 40)),
                "ratio 1 means off, and off means exactly the colour the program asked for");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Box_drawing_keeps_its_colour_beside_adjusted_text()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Options.MinimumContrastRatio = 4.5;
            view.Terminal.Write($"{Esc}[4;38;2;40;40;40m───");

            Assert.That(EffectiveForeground(view), Is.EqualTo(Color.FromRgb(40, 40, 40)),
                "a border's job is to match the panel it joins, not to be read");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Only_the_foreground_moves()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Options.MinimumContrastRatio = 4.5;
            view.Terminal.Write($"{Esc}[4;48;2;10;10;10;38;2;40;40;40mab");

            var runs = RunsForFirstRow(view);
            var run = runs.First(r => r.UnderlineStyle != XTerm.Common.UnderlineStyle.None);

            Assert.That(run.Background, Is.Not.Null, "the cell declared a background, so it paints one");
            Assert.That(((ISolidColorBrush)run.Background!).Color, Is.EqualTo(Color.FromRgb(10, 10, 10)),
                "the background is the theme's and stays put");
            Assert.That(((ISolidColorBrush)run.UnderlineBrush!).Color,
                Is.Not.EqualTo(Color.FromRgb(40, 40, 40)),
                "while the unreadable foreground over it was adjusted");
        }
        finally { window.Close(); }
    }
}
