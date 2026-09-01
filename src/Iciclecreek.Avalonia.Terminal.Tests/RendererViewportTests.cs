using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Avalonia.Terminal;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Five places where a screen position was worked out against the wrong frame of reference.
/// </summary>
/// <remarks>
/// They are unrelated as code and identical as mistakes: a buffer row used where a viewport row was
/// meant, a viewport row used where a buffer row was meant, a live scroll position used where a
/// snapshotted one was meant, and two pointer coordinates used with no bounds at all. Each is
/// invisible on a fresh terminal, because before anything scrolls every one of these frames of
/// reference agrees.
/// </remarks>
[TestFixture]
public class RendererViewportTests
{
    private static readonly string Esc = ((char)0x1B).ToString();

    private static (TerminalView view, Window window) Realised()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    /// <summary>Fills the buffer past the viewport, so scrollback exists and the frames diverge.</summary>
    private static void ScrollPast(TerminalView view)
    {
        for (var i = 0; i < view.Terminal.Rows + 20; i++)
            view.Terminal.Write($"filler {i}\r\n");
        Dispatcher.UIThread.RunJobs();

        Assert.That(view.Terminal.Buffer.ViewportY, Is.GreaterThan(0),
            "the whole point is that the viewport is no longer at buffer row 0");
    }

    // ------------------------------------------------------------ blinking

    [AvaloniaTest]
    public void Blinking_text_keeps_being_invalidated_once_there_is_scrollback()
    {
        // The blink tick dropped the cached run for every line carrying SGR 5 -- looked up by BUFFER
        // row 0..Rows, which is the oldest scrollback rather than what is on screen. So it cleared
        // caches nobody was looking at and left the visible blinking line cached, and blinking
        // stopped for the rest of the session the moment anything scrolled off the top.
        var (view, window) = Realised();
        try
        {
            view.CursorBlink = true;
            ScrollPast(view);

            view.Terminal.Write($"{Esc}[5mblinking{Esc}[0m");
            Dispatcher.UIThread.RunJobs();

            var row = view.Terminal.Buffer.ViewportY + view.Terminal.Buffer.Y;
            var line = view.Terminal.Buffer.GetLine(row);
            Assert.That(line, Is.Not.Null);

            // Something has to be IN the cache before dropping it can be observed.
            line!.Cache = new object();
            view.Focus();
            Tick(view);

            Assert.That(line.Cache, Is.Null,
                "the visible blinking line must have its cached run dropped, or it never repaints");
        }
        finally { window.Close(); }
    }

    /// <summary>Fires the cursor-blink timer the way the dispatcher would.</summary>
    /// <remarks>
    /// Reached by reflection rather than by waiting out the real interval: the handler is private and
    /// the alternative is a test that sleeps half a second to observe one tick.
    /// </remarks>
    private static void Tick(TerminalView view)
    {
        var tick = typeof(TerminalView).GetMethod("OnCursorBlinkTick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(tick, Is.Not.Null, "OnCursorBlinkTick has been renamed; this test needs updating");
        tick!.Invoke(view, new object?[] { null, EventArgs.Empty });
    }

    // ------------------------------------------------------------- conceal

    [AvaloniaTest]
    public void Concealed_text_is_drawn_in_nothing()
    {
        // SGR 8 has been recorded by the emulator since the parser was written and read by nothing
        // here, so a password prompt that echoes was drawn in full.
        var (view, window) = Realised();
        try
        {
            var cell = CellAfter(view, $"{Esc}[8mhunter2");
            Assert.That(cell.Attributes.IsInvisible(), Is.True,
                "sanity: the emulator recorded the attribute, which was never the missing half");

            var concealed = cell.ApplyConceal(Brushes.Red);
            Assert.That(((ISolidColorBrush)concealed).Color.A, Is.EqualTo(0));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Text_that_is_not_concealed_keeps_the_brush_it_was_given()
    {
        var (view, window) = Realised();
        try
        {
            var cell = CellAfter(view, "hunter2");

            Assert.That(cell.ApplyConceal(Brushes.Red), Is.SameAs(Brushes.Red));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Conceal_takes_the_foreground_AFTER_an_inverse_swap_not_before()
    {
        // The ordering is the substance of this one. Inverse exchanges the two brushes, so
        // concealing first hands back a transparent BACKGROUND -- which conceals nothing and punches
        // a hole in the fill instead. The renderer passes the swapped pair, and this is the contract
        // that says so.
        var (view, window) = Realised();
        try
        {
            var cell = CellAfter(view, $"{Esc}[7;8mhunter2");
            Assert.That(cell.Attributes.IsInverse(), Is.True, "sanity: both attributes are set");
            Assert.That(cell.Attributes.IsInvisible(), Is.True);

            // What the renderer does: resolve, swap, then conceal.
            var palette = view.Terminal.Colors.Take();
            IBrush foreground = cell.GetForegroundBrush(palette, Brushes.White);
            IBrush background = cell.GetBackgroundBrush(palette, Brushes.Black);
            (foreground, background) = (background, foreground);

            foreground = cell.ApplyConceal(foreground);

            Assert.That(((ISolidColorBrush)foreground).Color.A, Is.EqualTo(0),
                "the glyph is what disappears");
            Assert.That(((ISolidColorBrush)background).Color.A, Is.Not.EqualTo(0),
                "and the fill behind it is still painted");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Conceal_is_applied_everywhere_a_cell_is_drawn_not_only_the_run_path()
    {
        // There are three places a cell's text is shaped: the ordinary run path, DECDWL/DECDHL rows,
        // and OSC 66 sized blocks. Conceal landed in the first only, so a concealed password showed
        // in full on any line a program happened to double.
        //
        // Counted rather than exercised through each renderer, which needs pixels this platform has
        // no backend for. What it guards is the thing that actually went wrong: a fourth draw site
        // appearing without the call.
        // TerminalView is partial classes now, and the call sites this counts live in more than
        // one of its files -- so read them ALL, or a move-only split flips this test.
        var source = string.Concat(
            System.IO.Directory.GetFiles(
                    System.IO.Path.GetDirectoryName(SourcePath("TerminalView.cs"))!, "TerminalView*.cs")
                .OrderBy(f => f)
                .Select(System.IO.File.ReadAllText));

        Assert.That(Occurrences(source, "ApplyConceal(foreground)"), Is.EqualTo(3),
            "every path that shapes a cell's text must conceal it; add the call, then update this count");
    }

    [AvaloniaTest]
    public void An_inverse_cell_under_DECSCNM_still_paints_its_own_background()
    {
        // The cancelled double swap. SGR 7 inverts the cell and DECSCNM inverts the screen, so the
        // two come out even and the cell is "not swapped" -- but its background is the ORDINARY
        // default while the surface behind it is the inverted one. Skipping the fill on the grounds
        // that nothing was swapped drew the glyph in the normal foreground on an inverted sheet:
        // white on white, and vttest's four `negative` rows vanished from the light-background
        // rendition pattern while the buffer held them all along.
        var (view, window) = Realised();
        try
        {
            var cell = CellAfter(view, $"{Esc}[?5h{Esc}[7mnegative");

            Assert.That(cell.Attributes.IsInverse(), Is.True, "sanity: the cell carries SGR 7");
            Assert.That(view.Terminal.ReverseVideo, Is.True, "sanity: and the screen is inverted");

            // What the renderer resolves, in its own order: the two swaps cancel.
            var palette = view.Terminal.Colors.Take();
            IBrush foreground = cell.GetForegroundBrush(palette, Brushes.White);
            IBrush background = cell.GetBackgroundBrush(palette, Brushes.Black);
            var swapped = false;
            if (cell.Attributes.IsInverse()) (foreground, background, swapped) = (background, foreground, !swapped);
            if (view.Terminal.ReverseVideo) (foreground, background, swapped) = (background, foreground, !swapped);

            Assert.That(swapped, Is.False, "the two inversions cancel -- that is what made this invisible");
            Assert.That(cell.GetBackgroundColor(palette).HasValue, Is.False,
                "and the cell carries no background of its own, so the old rule painted nothing");

            // The rule the renderer now applies at all three draw sites.
            var fills = swapped || view.Terminal.ReverseVideo || cell.GetBackgroundColor(palette).HasValue;
            Assert.That(fills, Is.True, "under DECSCNM the fill is not optional");

            Assert.That(((ISolidColorBrush)background).Color,
                Is.Not.EqualTo(((ISolidColorBrush)foreground).Color),
                "and what it paints has to differ from the text, or the cell is invisible either way");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Blink_hides_the_glyph_it_does_not_swap_the_cells_colours()
    {
        // SGR 5 used to be drawn by exchanging foreground and background, which made blinking text
        // spend half its life looking exactly like SGR 7 text -- and made `blink negative` swap twice
        // back to looking plain. vttest's rendition pattern shows both misreadings side by side.
        var (view, window) = Realised();
        try
        {
            var cell = CellAfter(view, $"{Esc}[5malarm");
            Assert.That(cell.Attributes.IsBlink(), Is.True, "sanity: the emulator recorded the attribute");

            Assert.That(cell.ApplyBlinkPhase(Brushes.Red, blinkOn: true), Is.SameAs(Brushes.Red),
                "the lit half of the phase draws the text exactly as it was resolved");

            var hidden = cell.ApplyBlinkPhase(Brushes.Red, blinkOn: false);
            Assert.That(((ISolidColorBrush)hidden).Color.A, Is.EqualTo(0),
                "and the dark half hides the glyph, leaving the background it sits on alone");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Text_that_does_not_blink_keeps_its_brush_on_both_halves_of_the_phase()
    {
        var (view, window) = Realised();
        try
        {
            var cell = CellAfter(view, "steady");

            Assert.That(cell.ApplyBlinkPhase(Brushes.Red, blinkOn: false), Is.SameAs(Brushes.Red));
            Assert.That(cell.ApplyBlinkPhase(Brushes.Red, blinkOn: true), Is.SameAs(Brushes.Red));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_blink_phase_is_applied_everywhere_a_cell_is_drawn()
    {
        // The same guard as the conceal count above, and for the same reason: the swap this replaced
        // lived at all three draw sites, so a fix at one of them would have left blinking text
        // inverted on doubled rows and inside sized blocks.
        var source = string.Concat(
            System.IO.Directory.GetFiles(
                    System.IO.Path.GetDirectoryName(SourcePath("TerminalView.cs"))!, "TerminalView*.cs")
                .OrderBy(f => f)
                .Select(System.IO.File.ReadAllText));

        Assert.That(Occurrences(source, "ApplyBlinkPhase(foreground"), Is.EqualTo(3),
            "every path that shapes a cell's text must apply the blink phase; add the call, then update this count");

        // And the two places an SGR 58 underline resolves its OWN colour, which does not pass
        // through the foreground and so needs the phase applied separately -- the sized-block pass
        // draws no underline, which is why there are two of these and three above.
        Assert.That(Occurrences(source, "ApplyBlinkPhase(underlineBrush"), Is.EqualTo(1),
            "the run path's independently coloured underline must blink with its text");
        Assert.That(Occurrences(source, "ApplyBlinkPhase(dwBrush"), Is.EqualTo(1),
            "and so must a doubled row's");

        // The Skia layer expresses both conceal and the dark blink half as one snapshot flag, and
        // its ligature fast path draws blob and decorations without consulting the per-cell flags
        // -- so it must decline concealed runs up front, or blinking ligatured text stays lit
        // through the off phase. Asserted at the source like the counts above, because the layer
        // needs a Skia canvas lease this platform does not grant.
        var skiaSource = System.IO.File.ReadAllText(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(SourcePath("TerminalView.cs"))!, "Skia", "TerminalSkiaLayer.cs"));
        Assert.That(Occurrences(skiaSource, "first.Flags & SnapshotFlags.Conceal"), Is.EqualTo(1),
            "the ligature run must decline concealed cells before it draws anything");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>The library source file, found relative to the test assembly.</summary>
    private static string SourcePath(string file)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        Assert.That(dir, Is.Not.Null, "could not find the repo root from " + AppContext.BaseDirectory);
        var path = System.IO.Path.Combine(dir!.FullName, "src", "Iciclecreek.Avalonia.TerminalWindow", file);
        Assert.That(System.IO.File.Exists(path), Is.True, "not where this test expected: " + path);
        return path;
    }

    private static XTerm.Buffer.BufferCell CellAfter(TerminalView view, string write)
    {
        view.Terminal.Write(write);
        Dispatcher.UIThread.RunJobs();
        var line = view.Terminal.Buffer.GetLine(view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y);
        Assert.That(line, Is.Not.Null);
        return line![0];
    }

    [AvaloniaTest]
    public void Concealed_text_still_occupies_its_cells()
    {
        // Concealed means unreadable, not absent. The characters stay in the buffer, so the cursor
        // sits where it should and a copy still finds them -- which is why the glyph is drawn in a
        // transparent brush rather than skipped.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[8mhunter2");
            Dispatcher.UIThread.RunJobs();

            var line = view.Terminal.Buffer.GetLine(view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y);
            Assert.That(line!.TranslateToString(true), Does.StartWith("hunter2"));
            Assert.That(view.Terminal.Buffer.X, Is.EqualTo(7), "and the cursor advanced over them");
        }
        finally { window.Close(); }
    }

    // -------------------------------------------------------- pointer bounds

    [AvaloniaTest]
    public void A_pointer_above_the_control_does_not_report_a_negative_row()
    {
        // Every caller divided by the row height and used the result unchecked. A drag that leaves
        // the control at speed reports well outside it, and the capture keeps delivering those
        // events -- so a negative row reached the selection and the application as a position.
        var (view, window) = Realised();
        try
        {
            Assert.That(Row(view, -500), Is.EqualTo(0));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_pointer_below_the_last_line_does_not_report_a_row_past_the_end()
    {
        var (view, window) = Realised();
        try
        {
            Assert.That(Row(view, 100_000), Is.EqualTo(view.Terminal.Rows - 1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_pointer_at_the_right_edge_does_not_report_a_column_past_the_last()
    {
        // The control is wider than a whole number of cells, so the strip of padding at the right
        // edge resolved to a column that does not exist.
        var (view, window) = Realised();
        try
        {
            Assert.That(Column(view, 100_000), Is.EqualTo(view.Terminal.Cols - 1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_pointer_inside_the_control_still_reports_where_it_is()
    {
        // The clamp must not have flattened the ordinary case.
        var (view, window) = Realised();
        try
        {
            Assert.That(Row(view, 0), Is.EqualTo(0));
            Assert.That(Column(view, 0), Is.EqualTo(0));
            Assert.That(Row(view, 100_000), Is.Not.EqualTo(Row(view, 0)),
                "sanity: the two ends are still distinguishable");
        }
        finally { window.Close(); }
    }

    private static int Row(TerminalView view, double y) => Invoke<int>(view, "PointerRow", y);
    private static int Column(TerminalView view, double x) => Invoke<int>(view, "PointerColumn", x);

    private static T Invoke<T>(TerminalView view, string name, double arg)
    {
        var m = typeof(TerminalView).GetMethod(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(m, Is.Not.Null, $"{name} has been renamed; this test needs updating");
        return (T)m!.Invoke(view, new object[] { arg })!;
    }

    // -------------------------------------------- blink reaches the underline

    [AvaloniaTest]
    public void A_coloured_underline_blinks_with_its_text()
    {
        // SGR 58 resolves the underline's own colour, independent of the foreground -- which means
        // the blink phase has to be applied to it separately. An underline WITHOUT a colour borrows
        // the (already transparent) foreground and blinked for free; one WITH a colour stayed lit
        // through the off half, blinking the text under a steady underline -- on the classic path
        // only, while the Skia renderer suppressed both together.
        var (view, window) = Realised();
        try
        {
            SetField(view, "_cursorBlinkOn", false);
            view.Terminal.Write($"{Esc}[5;4;58:2::255:0:0merror");

            var off = FirstUnderlinedRun(view);
            Assert.That(((ISolidColorBrush)off.UnderlineBrush!).Color.A, Is.EqualTo(0),
                "the dark half of the phase hides the underline with the glyph");

            // The lit half draws it exactly as SGR 58 asked. The cache is dropped by hand because
            // the blink TICK owns that in production, and this test drives the phase directly.
            var line = view.Terminal.Buffer.GetLine(view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y);
            line!.Cache = null;
            SetField(view, "_cursorBlinkOn", true);

            var on = FirstUnderlinedRun(view);
            Assert.That(((ISolidColorBrush)on.UnderlineBrush!).Color,
                Is.EqualTo(Color.FromRgb(255, 0, 0)),
                "and the lit half draws the colour the program named");
        }
        finally { window.Close(); }
    }

    // ------------------------------------------ DECSCNM inverts the whole sheet

    [AvaloniaTest]
    public void DECSCNM_paints_the_surface_in_the_inverted_colour()
    {
        // The band-of-page-colour bug (#149), asserted on what is actually PAINTED rather than on
        // the per-cell fill rule: most of an inverted screen is rows no program ever wrote, and
        // they are covered only by the surface fill. Reverting the surface half of the fix would
        // leave the per-cell assertions green and bring the band back.
        var (view, window) = Realised();
        try
        {
            // An opaque host brush is what the emulator's pair replaces; a translucent one is a
            // host asking to be seen through and is deliberately left alone.
            view.Background = Brushes.Black;

            view.Terminal.Write($"{Esc}[?5h");
            var palette = view.Terminal.Colors.Take();

            Assert.That(SurfaceColour(view), Is.EqualTo(Rgb(palette.Foreground)),
                "under DECSCNM the sheet behind the empty rows is the inverted colour");

            view.Terminal.Write($"{Esc}[?5l");
            Assert.That(SurfaceColour(view), Is.EqualTo(Rgb(palette.Background)),
                "and clearing the mode hands the surface back");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_Skia_snapshot_carries_the_inverted_surface_too()
    {
        // The direct path clears with the snapshot's Surface, taken from the same decision the
        // classic path paints -- asserted on the snapshot the render actually built, because the
        // custom draw operation needs a Skia lease this platform does not grant.
        var (view, window) = Realised();
        try
        {
            view.Background = Brushes.Black;
            view.UseSkiaRenderer = true;
            view.Terminal.Write($"{Esc}[?5h");

            // The direct path declines while the cell metrics are unmeasured, and they are measured
            // lazily -- real layout forces them before any frame, headless layout does not. The
            // public property is the documented way to force them.
            Assert.That(view.CharWidth, Is.GreaterThan(0), "sanity: the cell metrics can be measured");

            // A bitmap context rather than a DrawingGroup: recording a DrawingGroup rejects custom
            // draw operations, and Render treats that rejection as a mid-read race and quietly
            // drops the layer this test exists to read.
            using var target = new global::Avalonia.Media.Imaging.RenderTargetBitmap(new global::Avalonia.PixelSize(800, 600));
            using (var context = target.CreateDrawingContext())
                view.Render(context);

            var layer = typeof(TerminalView)
                .GetField("_lastSkiaLayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(view);
            Assert.That(layer, Is.Not.Null, "the render built no Skia layer; the direct path did not run");

            var snapshot = (Iciclecreek.Terminal.Skia.TerminalSnapshot)layer!.GetType()
                .GetField("_snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(layer)!;

            var palette = view.Terminal.Colors.Take();
            var expected = 0xFF000000u | (uint)palette.Foreground;
            Assert.That(snapshot.Surface, Is.EqualTo(expected),
                "the snapshot's clear colour is the inverted surface, same as the classic sheet");
        }
        finally { window.Close(); }
    }

    /// <summary>The colour of the first fill of a fresh frame, which is the surface.</summary>
    private static Color SurfaceColour(TerminalView view)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
            view.Render(context);

        var fill = group.Children.OfType<GeometryDrawing>().FirstOrDefault();
        Assert.That(fill, Is.Not.Null, "the frame painted no surface at all");
        return ((ISolidColorBrush)fill!.Brush!).Color;
    }

    /// <summary>Render a frame and hand back the first underlined run the first row decided on.</summary>
    private static TerminalView.CachedTextRun FirstUnderlinedRun(TerminalView view)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
            view.Render(context);

        var line = view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY];
        var runs = line!.Cache as List<TerminalView.CachedTextRun>;
        Assert.That(runs, Is.Not.Null, "the row produced no cached runs");
        return runs!.First(r => r.UnderlineStyle != XTerm.Common.UnderlineStyle.None);
    }

    private static Color Rgb(int v)
        => Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));

    private static void SetField(TerminalView view, string name, object value)
    {
        var f = typeof(TerminalView).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(f, Is.Not.Null, $"{name} has been renamed; this test needs updating");
        f!.SetValue(view, value);
    }
}
