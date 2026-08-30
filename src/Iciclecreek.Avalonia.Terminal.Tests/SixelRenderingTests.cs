using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using NUnit.Framework;
using XTerm.Graphics;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// What <see cref="TerminalView"/> does with a Sixel image sitting in the buffer.
///
/// <para>Not asserted through a captured frame, the way <see cref="BackgroundPaintTests"/> asserts fills.
/// The recording <see cref="DrawingContext"/> a <see cref="DrawingGroup"/> hands back throws
/// <see cref="NotImplementedException"/> from <c>DrawImage</c>, so a rendered frame cannot be inspected for
/// pictures on the headless platform at all. Two things are asserted instead, and between them they cover
/// every decision this renderer makes: the run list a frame produced -- which is exactly what will be drawn
/// -- and the source-to-destination arithmetic, which is the part with something to get wrong.</para>
/// </summary>
[TestFixture]
public class SixelRenderingTests
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";

    /// <summary>Four pixels wide, twelve tall. At 2x3 cells that is two columns by four rows of tiles.</summary>
    private const string TwoByFourCells = Esc + "P0;1;0q#0;2;100;0;0!4~-!4~" + St;

    private const int CellPixelWidth = 2;
    private const int CellPixelHeight = 3;

    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    /// <summary>
    /// Pin the cell size so the tile grid is predictable, then place the image. Setting it after layout is
    /// deliberate: the view publishes measured metrics during the layout pass, and leaving those in place
    /// would make the tile grid depend on whichever font the machine happens to have.
    /// </summary>
    private static void PlaceImage(TerminalView view, string sixel = TwoByFourCells)
    {
        view.Terminal.Options.CellWidthPixels = CellPixelWidth;
        view.Terminal.Options.CellHeightPixels = CellPixelHeight;
        view.Terminal.Write(sixel);
    }

    /// <summary>Render a frame, then hand back the runs the given row decided on.</summary>
    private static IReadOnlyList<TerminalView.CachedTextRun> RunsForRow(TerminalView view, int screenRow)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            view.Render(context);
        }

        var line = view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY + screenRow];
        Assert.That(line, Is.Not.Null);

        var runs = line!.Cache as List<TerminalView.CachedTextRun>;
        Assert.That(runs, Is.Not.Null,
            "the row produced no cached runs, so nothing about what it draws can be asserted");
        return runs!;
    }

    private static IReadOnlyList<TerminalView.CachedTextRun> ImageRuns(TerminalView view, int screenRow)
        => RunsForRow(view, screenRow).Where(r => r.Image is not null).ToList();

    // ---- what a frame decides to draw -------------------------------------------------------------

    [AvaloniaTest]
    public void An_image_in_the_buffer_produces_an_image_run()
    {
        var (view, window) = Realised();
        try
        {
            PlaceImage(view);

            var runs = ImageRuns(view, 0);

            Assert.That(runs.Count, Is.GreaterThan(0), "the picture would never have been drawn");
            Assert.That(runs[0].Text, Is.Null, "an image run carries no text");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Adjacent tiles of one strip are a single draw, not one per cell. There is no dirty-rect culling here,
    /// so every visible row is redrawn on every frame and the difference is per-frame cost.
    /// </summary>
    [AvaloniaTest]
    public void Adjacent_tiles_are_coalesced_into_one_run()
    {
        var (view, window) = Realised();
        try
        {
            PlaceImage(view);

            var runs = ImageRuns(view, 0);

            Assert.That(runs.Count, Is.EqualTo(1), "two adjacent tiles should be one draw, not two");
            Assert.That(runs[0].CellCount, Is.EqualTo(2));
            Assert.That(runs[0].Placement!.Value.SrcX, Is.Zero);
            Assert.That(runs[0].Placement!.Value.SrcY, Is.Zero);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Each_row_of_the_picture_gets_its_own_run()
    {
        var (view, window) = Realised();
        try
        {
            PlaceImage(view);

            for (int row = 0; row < 4; row++)
            {
                var runs = ImageRuns(view, row);
                Assert.That(runs.Count, Is.EqualTo(1), $"row {row}");
                Assert.That(runs[0].Placement!.Value.SrcY, Is.EqualTo(row * CellPixelHeight),
                            $"row {row} drew the wrong strip");
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// An image cell holds a space, so without an explicit break it would be swallowed by the text run beside
    /// it and never drawn.
    /// </summary>
    /// <summary>
    /// A Sixel replaced what was under it, so the text it covered is not drawn.
    /// </summary>
    /// <remarks>
    /// <para>The emulator does not clear the cells a Sixel covers — placing one only adds a run —
    /// so they still hold whatever was printed there. Drawing them puts that text under the
    /// picture: invisible beneath an opaque one, and showing through a Sixel drawn with background
    /// select 1, whose unset pixels are transparent precisely so the cell's own colour comes
    /// through. The cell's colour, not the previous screen's text.</para>
    /// <para>The contrast is <c>A_front_picture_leaves_no_text_over_it</c>, where a Kitty placement
    /// over the same text keeps it: that one is an overlay and the z-index decides what is seen.
    /// The two together are the whole of why the renderer has to tell the protocols apart.</para>
    /// </remarks>
    [AvaloniaTest]
    public void Text_a_sixel_covered_is_not_drawn_under_it()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("XY");
            view.Terminal.Write(Esc + "[1;1H");
            PlaceImage(view);

            var runs = RunsForRow(view, 0);

            Assert.That(runs.Any(r => r.IsImage), Is.True, "the picture should be drawn");
            Assert.That(runs.Where(r => r.Text is not null).Any(r => r.StartX < 2 && r.StartX + r.CellCount > 0),
                        Is.False,
                        "the text the picture covered should not be drawn underneath it");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Narrowing the window draws less of a picture, and widening it draws the rest back.
    /// </summary>
    /// <remarks>
    /// <para>The end-to-end half of what <c>A_clipped_run_narrows_the_source_by_the_same_proportion</c>
    /// asserts about the arithmetic. That one hands the blit a run already clipped to two columns;
    /// this one narrows an actual terminal and checks that a clipped run is what comes out, and that
    /// widening restores the picture whole.</para>
    /// <para>Worth having as its own test because it is the claim the placement model exists for, and
    /// it is the half that used to be false. When a picture was scattered across cells, narrowing the
    /// window destroyed the cells past the edge and there was nothing left to widen back into -- the
    /// picture returned with a piece missing, and that was taken to be inherent. A run keeping its
    /// NATURAL width is what makes the resize a no-op, and nothing else here would notice if that
    /// stopped being true.</para>
    /// </remarks>
    [AvaloniaTest]
    public void Narrowing_shows_less_of_a_picture_and_widening_shows_it_again()
    {
        var (view, window) = Realised();
        try
        {
            PlaceImage(view);
            Assert.That(ImageRuns(view, 0)[0].CellCount, Is.EqualTo(2), "the picture starts two columns wide");

            view.Terminal.Resize(1, view.Terminal.Rows);
            var narrowed = ImageRuns(view, 0);
            Assert.That(narrowed.Count, Is.EqualTo(1), "the picture should still be there, only narrower");
            Assert.That(narrowed[0].CellCount, Is.EqualTo(1), "only one column can be shown");
            Assert.That(narrowed[0].Placement!.Value.Cols, Is.EqualTo(2),
                "the run keeps its natural width -- that is what there is to widen back into");

            view.Terminal.Resize(20, view.Terminal.Rows);
            var widened = ImageRuns(view, 0);
            Assert.That(widened.Count, Is.EqualTo(1));
            Assert.That(widened[0].CellCount, Is.EqualTo(2), "the second column should come back");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void An_image_does_not_join_the_text_run_beside_it()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Options.CellWidthPixels = CellPixelWidth;
            view.Terminal.Options.CellHeightPixels = CellPixelHeight;
            view.Terminal.Write("ab");
            view.Terminal.Write(Esc + "[1;3H");
            view.Terminal.Write(TwoByFourCells);

            var runs = RunsForRow(view, 0);
            var images = runs.Where(r => r.Image is not null).ToList();

            Assert.That(images.Count, Is.EqualTo(1));
            Assert.That(images[0].StartX, Is.EqualTo(2), "the picture was placed after the text");
            Assert.That(runs.Any(r => r.Text is not null), Is.True, "the text beside it still draws");
        }
        finally { window.Close(); }
    }

    /// <summary>Two pictures side by side are two runs; coalescing compares the image by reference.</summary>
    [AvaloniaTest]
    public void Two_pictures_side_by_side_are_not_coalesced()
    {
        var (view, window) = Realised();
        try
        {
            PlaceImage(view);
            // A second picture on the same row, immediately right of the first.
            view.Terminal.Write(Esc + "[1;3H");
            view.Terminal.Write(TwoByFourCells);

            var runs = ImageRuns(view, 0);

            Assert.That(runs.Count, Is.EqualTo(2));
            Assert.That(runs[0].Image, Is.Not.SameAs(runs[1].Image));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Two appearances of the SAME picture, side by side, must stay two runs.
    /// </summary>
    /// <remarks>
    /// <para>This could not happen with Sixel: every decode makes a fresh image and shows it once,
    /// so comparing images was enough to keep neighbours apart. Kitty transmits a picture once and
    /// places it as often as it likes, and then two adjacent appearances share an image object.</para>
    /// <para>Coalescing on the image would run one strip across the join and blit the wrong pixels
    /// into both halves -- the second placement's tiles would be read from wherever the first one's
    /// numbering happened to continue.</para>
    /// </remarks>
    [AvaloniaTest]
    public void Two_placements_of_one_image_are_not_coalesced()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Options.CellWidthPixels = CellPixelWidth;
            view.Terminal.Options.CellHeightPixels = CellPixelHeight;

            // Transmit once under an id, then show it twice, adjacent, on the same row.
            var pixels = Convert.ToBase64String(new byte[4 * 6 * 4]);
            view.Terminal.Write($"{Esc}_Ga=t,i=1,f=32,s=4,v=6,q=2;{pixels}{St}");
            view.Terminal.Write(Esc + "[1;1H");
            view.Terminal.Write($"{Esc}_Ga=p,i=1,C=1,q=2{St}");
            view.Terminal.Write(Esc + "[1;3H");
            view.Terminal.Write($"{Esc}_Ga=p,i=1,C=1,q=2{St}");

            var runs = ImageRuns(view, 0);

            Assert.That(runs.Count, Is.EqualTo(2),
                "two appearances of one picture were merged into a single strip");
            Assert.That(runs[0].Image, Is.SameAs(runs[1].Image), "they do share the pixels");
            Assert.That(runs[0].Placement, Is.Not.SameAs(runs[1].Placement), "but not the placement");
            Assert.That(runs[1].Placement!.Value.SrcX, Is.Zero,
                        "the second appearance starts at its own first pixel");
        }
        finally { window.Close(); }
    }
    /// <summary>Typing over part of a strip takes that cell back rather than drawing across the gap.</summary>
    [AvaloniaTest]
    public void Typing_over_a_tile_takes_that_cell_back()
    {
        var (view, window) = Realised();
        try
        {
            PlaceImage(view);
            Assert.That(ImageRuns(view, 0)[0].CellCount, Is.EqualTo(2));

            view.Terminal.Write(Esc + "[1;1HX");

            var runs = ImageRuns(view, 0);
            Assert.That(runs.Count, Is.EqualTo(1));
            Assert.That(runs[0].CellCount, Is.EqualTo(1), "only the untouched tile should be left");
            Assert.That(runs[0].Placement!.Value.SrcX, Is.EqualTo(1 * CellPixelWidth));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Erasing_the_screen_removes_the_image_runs()
    {
        var (view, window) = Realised();
        try
        {
            PlaceImage(view);
            Assert.That(ImageRuns(view, 0), Is.Not.Empty);

            view.Terminal.Write(Esc + "[2J");

            Assert.That(ImageRuns(view, 0), Is.Empty);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The row cache is replayed verbatim on most frames, so an image left out of it would be drawn on the
    /// first frame and then silently vanish.
    /// </summary>
    [AvaloniaTest]
    public void An_image_run_survives_into_the_row_cache()
    {
        var (view, window) = Realised();
        try
        {
            PlaceImage(view);

            var first = ImageRuns(view, 0);
            var second = ImageRuns(view, 0); // this frame is served from the cache

            Assert.That(second.Count, Is.EqualTo(first.Count));
            Assert.That(second[0].Image, Is.SameAs(first[0].Image));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The platform under test cannot draw bitmaps at all, which is the position Consolonia is in too. The
    /// terminal has to keep rendering its text regardless.
    /// </summary>
    [AvaloniaTest]
    public void A_platform_that_cannot_draw_images_still_renders_text()
    {
        var (view, window) = Realised();
        try
        {
            PlaceImage(view);
            view.Terminal.Write(Esc + "[6;1Hstill here");

            var group = new DrawingGroup();
            Assert.DoesNotThrow(() =>
            {
                using var context = group.Open();
                view.Render(context);
            });

            Assert.That(group.Children.Count, Is.GreaterThan(0), "the frame was abandoned partway through");
        }
        finally { window.Close(); }
    }

    // ---- the blit arithmetic ----------------------------------------------------------------------

    /// <summary>
    /// A run of a picture on one line, which is what the emulator now stores.
    /// </summary>
    private static TerminalView.CachedTextRun Run(TerminalImage image, int column, int cols,
                                                  int srcX, int srcY, int srcWidth, int srcHeight,
                                                  int cellCount = -1,
                                                  short offsetX = 0, short offsetY = 0)
        => new(null, column, cellCount < 0 ? cols : cellCount, null,
               new LinePlacement(image.Id, column, cols, srcX, srcY, srcWidth, srcHeight,
                                 PlacementKind.Sixel, offsetX: offsetX, offsetY: offsetY),
               image);

    /// <summary>The image showing at a screen position, now that cells carry none.</summary>
    private static TerminalImage? ImageAt(TerminalView view, int col, int screenRow)
    {
        var line = view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY + screenRow];
        return line is not null && line.TryGetImageAt(col, out var image) ? image : null;
    }

    /// <summary>Pixels that divide evenly into cells: 8x6 over 2x3 cells is four columns by two rows.</summary>
    private static TerminalImage EvenImage()
        => new(new byte[8 * 6 * 4], 8, 6, CellPixelWidth, CellPixelHeight);

    [AvaloniaTest]
    public void A_full_strip_maps_to_whole_cells()
    {
        var image = EvenImage();

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 4, 0, 0, 8, 3), 0, 20, 10, 20, 1.0,
            out var source, out var destination), Is.True);

        Assert.That(source, Is.EqualTo(new Rect(0, 0, 8, 3)));
        Assert.That(destination, Is.EqualTo(new Rect(0, 0, 40, 20)));
    }

    /// <summary>
    /// A row further down the picture is a different run with its own slice of the source, so the
    /// strip it reads is carried rather than computed.
    /// </summary>
    [AvaloniaTest]
    public void A_strip_further_down_reads_from_further_down_the_picture()
    {
        var image = EvenImage();

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 4, 0, 3, 8, 3), 20, 20, 10, 20, 1.0,
            out var source, out var destination), Is.True);

        Assert.That(source, Is.EqualTo(new Rect(0, 3, 8, 3)));
        Assert.That(destination, Is.EqualTo(new Rect(0, 20, 40, 20)));
    }

    [AvaloniaTest]
    public void A_run_starting_partway_across_is_offset_on_screen()
    {
        var image = EvenImage();

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 5, 2, 4, 0, 4, 3), 0, 20, 10, 20, 1.0,
            out var source, out var destination), Is.True);

        Assert.That(source, Is.EqualTo(new Rect(4, 0, 4, 3)));
        Assert.That(destination, Is.EqualTo(new Rect(50, 0, 20, 20)));
    }

    /// <summary>
    /// A run clipped by a narrow line shows LESS of the picture rather than squeezing all of it into
    /// fewer cells. That is the whole point of a run keeping its natural width: nothing was
    /// destroyed by the narrowing, and widening the window shows the rest again.
    /// </summary>
    [AvaloniaTest]
    public void A_clipped_run_narrows_the_source_by_the_same_proportion()
    {
        var image = EvenImage();

        // Four columns wide, but only two of them fit on the line.
        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 4, 0, 0, 8, 3, cellCount: 2), 0, 20, 10, 20, 1.0,
            out var source, out var destination), Is.True);

        Assert.That(source, Is.EqualTo(new Rect(0, 0, 4, 3)), "half the columns is half the source");
        Assert.That(destination, Is.EqualTo(new Rect(0, 0, 20, 20)));
    }

    /// <summary>
    /// A short bottom row covers only the part of its row that it has pixels for.
    /// </summary>
    /// <remarks>
    /// <para>This assertion used to read the other way -- that the strip fills the whole row -- on
    /// the argument that a run occupies whole cells by construction, so the fraction belongs in the
    /// source rather than the destination. The first half of that is true and the conclusion does
    /// not follow: the run owns a whole cell row in the BUFFER, which says nothing about how much of
    /// it the picture should cover.</para>
    /// <para>Filling it means scaling the last strip differently from every strip above it. Two
    /// pixels stretched over a cell three pixels tall magnifies the bottom of the picture by half
    /// again, inside one image -- visible on anything with a straight edge running through it, and
    /// not what a sixel terminal does. xterm and kitty both blit at natural size and leave the
    /// remainder of the cell showing the background.</para>
    /// </remarks>
    [AvaloniaTest]
    public void A_short_bottom_row_covers_only_what_it_has_pixels_for()
    {
        // Eight pixels tall over three-pixel cells: three rows, the last holding two pixels.
        var image = new TerminalImage(new byte[8 * 8 * 4], 8, 8, CellPixelWidth, CellPixelHeight);

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 4, 0, 6, 8, 2), 40, 20, 10, 20, 1.0,
            out var source, out var destination), Is.True);

        Assert.That(source, Is.EqualTo(new Rect(0, 6, 8, 2)));

        // Two of the cell's three pixel rows, so two thirds of a twenty-pixel row.
        Assert.That(destination.Height, Is.EqualTo(13));
    }

    /// <summary>
    /// A full strip is unaffected: its pixels fill the cell exactly, so natural size IS the row.
    /// </summary>
    [AvaloniaTest]
    public void A_full_strip_still_covers_its_whole_row()
    {
        var image = new TerminalImage(new byte[8 * 8 * 4], 8, 8, CellPixelWidth, CellPixelHeight);

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 4, 0, 0, 8, 3), 0, 20, 10, 20, 1.0,
            out _, out var destination), Is.True);

        Assert.That(destination.Height, Is.EqualTo(20));
    }

    /// <summary>
    /// A picture whose width is not a whole number of cells is drawn at its width, not the box's.
    /// </summary>
    /// <remarks>
    /// The horizontal twin of the row above, and it stretched the same way: the buffer gives the
    /// picture whole cells, and drawing across all of them widened a picture that did not fill them.
    /// </remarks>
    [AvaloniaTest]
    public void A_picture_narrower_than_its_cells_is_not_stretched_across_them()
    {
        // Seven pixels wide over two-pixel cells: three and a half cells, rounded up to four.
        var image = new TerminalImage(new byte[7 * 6 * 4], 7, 6, CellPixelWidth, CellPixelHeight);

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 4, 0, 0, 7, 3), 0, 20, 10, 20, 1.0,
            out _, out var destination), Is.True);

        // Seven pixels over two-pixel cells is three and a half cells of ten pixels each.
        Assert.That(destination.Width, Is.EqualTo(35));
    }

    /// <summary>
    /// A cell with a picture BEHIND it does not paint its background over the picture.
    /// </summary>
    /// <remarks>
    /// A negative z-index means "draw this under the text", and the row pass does exactly that --
    /// then every cell carrying a background of its own painted an opaque rectangle across it,
    /// erasing the thing the z-index had asked for. The text was still drawn on top, so the picture
    /// was the only casualty.
    /// </remarks>
    [AvaloniaTest]
    public void A_backdrop_picture_is_not_erased_by_the_text_drawn_over_it()
    {
        var behind = new List<LinePlacement>
        {
            new(1, column: 2, cols: 4, 0, 0, 8, 3, PlacementKind.Sixel),
        };

        Assert.That(CoveredByBackdrop(behind, 2, 6), Is.True, "the columns the picture occupies");
        Assert.That(CoveredByBackdrop(behind, 4, 5), Is.True, "and any of them on their own");
        Assert.That(CoveredByBackdrop(behind, 0, 2), Is.False, "but not the ones before it");
        Assert.That(CoveredByBackdrop(behind, 6, 9), Is.False, "nor the ones after");
    }

    private static bool CoveredByBackdrop(List<LinePlacement> painted, int start, int end)
    {
        var m = typeof(TerminalView).GetMethod("CoveredByBackdrop",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.That(m, Is.Not.Null, "CoveredByBackdrop has been renamed; this test needs updating");
        return (bool)m!.Invoke(null, new object[] { painted, start, end })!;
    }

    /// <summary>The X and Y keys shift a picture inside its first cell without enlarging the box.</summary>
    [AvaloniaTest]
    public void A_pixel_offset_moves_the_blit_inside_the_cell()
    {
        var image = EvenImage();

        // One pixel of a two-pixel cell across, one of a three-pixel cell down: half a cell and a third.
        Assert.That(TerminalView.TryPlanImageBlit(
            Run(image, 0, 4, 0, 0, 8, 3, offsetX: 1, offsetY: 1), 0, 20, 10, 20, 1.0,
            out _, out var destination), Is.True);

        Assert.That(destination.X, Is.EqualTo(5), "half a cell across");
        Assert.That(destination.Y, Is.EqualTo(Math.Round(20.0 / 3)).Within(1.0), "a third of a row down");
    }

    [AvaloniaTest]
    public void A_positive_vertical_offset_crops_at_the_row_instead_of_stretching()
    {
        var image = EvenImage();

        Assert.That(TerminalView.TryPlanImageBlit(
            Run(image, 0, 4, 0, 0, 8, 3, offsetY: 1), 0, 20, 10, 20, 1.0,
            out var source, out var destination), Is.True);

        Assert.That(destination.Bottom, Is.LessThanOrEqualTo(20),
            "the shifted strip must not paint into the following text row");
        Assert.That(source.Height, Is.LessThan(3),
            "the source must be cropped with the destination, not squeezed into it");
    }

    [AvaloniaTest]
    public void An_empty_run_is_refused()
    {
        var image = EvenImage();

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 4, 0, 0, 8, 3, cellCount: 0), 0, 20, 10, 20, 1.0,
            out _, out _), Is.False);
    }

    [AvaloniaTest]
    public void A_run_with_no_picture_is_refused()
    {
        var run = new TerminalView.CachedTextRun(null, 0, 2, null);

        Assert.That(TerminalView.TryPlanImageBlit(run, 0, 20, 10, 20, 1.0, out _, out _), Is.False);
    }

    /// <summary>
    /// Coordinates go through the same snapping every other part of this renderer uses, or the picture shears
    /// against the text grid by a fraction of a pixel per row.
    /// </summary>
    [AvaloniaTest]
    public void Coordinates_are_snapped_to_the_device_pixel_grid()
    {
        var image = EvenImage();

        // A fractional cell width at 1.5x scaling: unsnapped, these edges land off the device grid.
        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 3, 4, 0, 0, 8, 3), 0, 20, 8.35, 20, 1.5,
            out _, out var destination), Is.True);

        Assert.That(destination.X * 1.5, Is.EqualTo(Math.Round(destination.X * 1.5)).Within(0.0001));
        Assert.That((destination.X + destination.Width) * 1.5,
            Is.EqualTo(Math.Round((destination.X + destination.Width) * 1.5)).Within(0.0001));
    }

    // ---- giving up on images, and how far ----------------------------------------------------------

    /// <summary>
    /// A backend with no raster surface says so the same way every frame, so images are abandoned for
    /// the life of the control rather than throwing out of Render thirty times a second.
    /// </summary>
    [AvaloniaTest]
    public void A_platform_that_cannot_draw_at_all_is_recognised()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TerminalView.IndicatesNoRasterBackend(new NotImplementedException()), Is.True);
            Assert.That(TerminalView.IndicatesNoRasterBackend(new PlatformNotSupportedException()), Is.True);
            Assert.That(TerminalView.IndicatesNoRasterBackend(new NotSupportedException()), Is.True);
        });
    }

    /// <summary>
    /// Everything else is about one picture, not the platform. Latching on these would let a single bad
    /// bitmap turn every image off for the life of the control, and hide whatever caused it.
    /// </summary>
    [AvaloniaTest]
    public void A_failure_in_one_picture_is_not_mistaken_for_the_platform()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TerminalView.IndicatesNoRasterBackend(new OutOfMemoryException()), Is.False);
            Assert.That(TerminalView.IndicatesNoRasterBackend(new ObjectDisposedException("bitmap")), Is.False);
            Assert.That(TerminalView.IndicatesNoRasterBackend(new ArgumentException()), Is.False);
            Assert.That(TerminalView.IndicatesNoRasterBackend(new InvalidOperationException()), Is.False);
        });
    }

    // ---- the pixel upload -------------------------------------------------------------------------

    /// <summary>A 3x2 picture with a distinct byte in every channel, so any shift or swap shows up.</summary>
    private static (TerminalImage image, byte[] pixels) DistinctPixels()
    {
        const int width = 3;
        const int height = 2;
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 4 + 0] = (byte)(10 + i);  // B
            pixels[i * 4 + 1] = (byte)(50 + i);  // G
            pixels[i * 4 + 2] = (byte)(90 + i);  // R
            pixels[i * 4 + 3] = 255;             // A
        }
        return (new TerminalImage(pixels, width, height, CellPixelWidth, CellPixelHeight), pixels);
    }

    /// <summary>
    /// Copies into a buffer of our own, because the headless platform's WriteableBitmap hands back a fresh
    /// buffer on every Lock -- nothing written through one can be read back from the bitmap itself.
    /// </summary>
    private static byte[] CopyThrough(TerminalImage image, int destinationRowBytes)
    {
        var total = destinationRowBytes * image.PixelHeight;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(total);
        try
        {
            for (int i = 0; i < total; i++)
                System.Runtime.InteropServices.Marshal.WriteByte(buffer, i, 0xCD); // poison, so gaps are visible

            TerminalView.CopyPixels(image, buffer, destinationRowBytes);

            var readBack = new byte[total];
            System.Runtime.InteropServices.Marshal.Copy(buffer, readBack, 0, total);
            return readBack;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The bytes the decoder produced have to arrive unchanged and in order. What can go wrong here fails
    /// silently rather than throwing -- as a picture with its colours swapped, or its rows sheared.
    /// </summary>
    [AvaloniaTest]
    public void The_decoded_pixels_are_copied_unchanged()
    {
        var (image, pixels) = DistinctPixels();

        var copied = CopyThrough(image, image.Stride);

        Assert.That(copied, Is.EqualTo(pixels));
    }

    /// <summary>
    /// A bitmap is free to pad its rows. Copying the picture as one contiguous block would then shear it --
    /// every row after the first offset by the padding.
    /// </summary>
    [AvaloniaTest]
    public void A_padded_destination_stride_does_not_shear_the_picture()
    {
        var (image, pixels) = DistinctPixels();
        var padded = image.Stride + 8;

        var copied = CopyThrough(image, padded);

        for (int y = 0; y < image.PixelHeight; y++)
        {
            var row = copied.Skip(y * padded).Take(image.Stride).ToArray();
            var expected = pixels.Skip(y * image.Stride).Take(image.Stride).ToArray();
            Assert.That(row, Is.EqualTo(expected), $"row {y} landed in the wrong place");

            var gap = copied.Skip(y * padded + image.Stride).Take(8).ToArray();
            Assert.That(gap, Is.All.EqualTo((byte)0xCD), $"row {y} wrote into the padding");
        }
    }

    [AvaloniaTest]
    public void An_uploaded_bitmap_has_the_pictures_dimensions()
    {
        var (image, _) = DistinctPixels();

        using var bitmap = TerminalView.CreateBitmap(image);

        Assert.That(bitmap.PixelSize.Width, Is.EqualTo(image.PixelWidth));
        Assert.That(bitmap.PixelSize.Height, Is.EqualTo(image.PixelHeight));
    }

    // ---- the numbers the emulator and applications are told ---------------------------------------

    [AvaloniaTest]
    public void The_measured_cell_size_reaches_the_emulator()
    {
        var (view, window) = Realised();
        try
        {
            var scaling = TopLevel.GetTopLevel(view)?.RenderScaling ?? 1.0;

            Assert.That(view.Terminal.Options.CellWidthPixels,
                Is.EqualTo(Math.Max(1, (int)Math.Round(view.CharWidth * scaling))),
                "the emulator cannot measure a font, so images are sized against whatever the view tells it");
            Assert.That(view.Terminal.Options.CellHeightPixels,
                Is.EqualTo(Math.Max(1, (int)Math.Round(view.CharHeight * scaling))));
            Assert.That(view.Terminal.Options.CellWidthPixels, Is.GreaterThan(0));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Ask, then give the reply time to reach the pty. Replies are written asynchronously, so reading the
    /// recorder straight after the write races the answer and usually loses.
    /// </summary>
    private static async Task<string> AskAsync(TerminalWindow window, RecordingConnection pty, string query)
    {
        window.Terminal.Write(Esc + query);
        await Task.Delay(150);
        return pty.Written;
    }

    /// <summary>
    /// The reply a Sixel-capable program sizes its images from. It used to be FontSize * 0.6 by
    /// FontSize * 1.2 -- a guess that ignored the typeface and the display scaling alike.
    /// </summary>
    [AvaloniaTest]
    public async Task A_cell_size_query_is_answered_with_the_measured_cell()
    {
        var window = new TerminalWindow { Process = "" }.Realise();
        try
        {
            window.Terminal.Options.WindowOptions.GetCellSizePixels = true;

            var pty = new RecordingConnection();
            window.Control().View().AttachConnection(pty);

            var reply = await AskAsync(window, pty, "[16t");

            var scaling = window.RenderScaling;
            var expectedWidth = Math.Max(1, (int)Math.Round(window.CharWidth * scaling));
            var expectedHeight = Math.Max(1, (int)Math.Round(window.CharHeight * scaling));

            Assert.That(reply, Is.EqualTo($"{Esc}[6;{expectedHeight};{expectedWidth}t"));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A reply that disagrees with the grid images are laid out against is worse than no reply: an
    /// application would size a picture to one and have it drawn against the other.
    /// </summary>
    [AvaloniaTest]
    public async Task The_query_reply_agrees_with_the_grid_images_use()
    {
        var window = new TerminalWindow { Process = "" }.Realise();
        try
        {
            window.Terminal.Options.WindowOptions.GetCellSizePixels = true;

            var pty = new RecordingConnection();
            window.Control().View().AttachConnection(pty);

            var reply = await AskAsync(window, pty, "[16t");
            Assert.That(reply, Does.StartWith($"{Esc}[6;"), "no reply to work with");

            var parts = reply.TrimEnd('t').Split(';');
            Assert.That(int.Parse(parts[2]), Is.EqualTo(window.Terminal.Options.CellWidthPixels));
            Assert.That(int.Parse(parts[1]), Is.EqualTo(window.Terminal.Options.CellHeightPixels));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The report has to describe the text area rather than the window: a program filling the terminal with a
    /// picture should not be handed a width that includes the scrollbar and the chrome around it.
    /// </summary>
    [AvaloniaTest]
    public async Task A_pixel_size_query_is_answered_with_the_text_area()
    {
        var window = new TerminalWindow { Process = "" }.Realise();
        try
        {
            window.Terminal.Options.WindowOptions.GetWinSizePixels = true;

            var pty = new RecordingConnection();
            window.Control().View().AttachConnection(pty);

            var reply = await AskAsync(window, pty, "[14t");
            Assert.That(reply, Does.StartWith($"{Esc}[4;"));

            var parts = reply.TrimEnd('t').Split(';');
            var height = int.Parse(parts[1]);
            var width = int.Parse(parts[2]);

            Assert.That(width, Is.GreaterThan(0));
            Assert.That(height, Is.GreaterThan(0));
            Assert.That(width, Is.LessThanOrEqualTo((int)Math.Round(window.Width * window.RenderScaling)),
                "the reported width included chrome that is not part of the text area");
        }
        finally { window.Close(); }
    }

    // ---- pixel offsets within the first cell ------------------------------------------------------

    // ---- pictures behind the text -----------------------------------------------------------------

    /// <summary>Transmits a 4x6 picture under an id and places it at the cursor with a given depth.</summary>
    private static void PlaceWithDepth(TerminalView view, int z)
    {
        view.Terminal.Options.CellWidthPixels = CellPixelWidth;
        view.Terminal.Options.CellHeightPixels = CellPixelHeight;

        var pixels = Convert.ToBase64String(new byte[4 * 6 * 4]);
        view.Terminal.Write($"{Esc}_Ga=t,i=1,f=32,s=4,v=6,q=2;{pixels}{St}");
        view.Terminal.Write(Esc + "[1;1H");
        view.Terminal.Write($"{Esc}_Ga=p,i=1,z={z},C=1,q=2{St}");
    }

    /// <summary>
    /// A picture at negative z is drawn AND its cells still carry text, so the row emits both an
    /// image run and a text run -- the image first, because runs are drawn in the order they are
    /// listed and the text belongs on top.
    /// </summary>
    [AvaloniaTest]
    public void A_background_picture_is_drawn_under_its_text()
    {
        var (view, window) = Realised();
        try
        {
            PlaceWithDepth(view, z: -1);
            view.Terminal.Write(Esc + "[1;1HXY");

            var runs = RunsForRow(view, 0).ToList();
            var imageAt = runs.FindIndex(r => r.Placement is not null);

            // The run covering column 0 specifically -- not merely "some text run", which the blanks
            // further along the row would satisfy whether or not the glyphs survived.
            var textAt = runs.FindIndex(r => r.Text is not null
                                             && r.StartX <= 0 && r.StartX + r.CellCount > 0);

            Assert.That(imageAt, Is.GreaterThanOrEqualTo(0), "the background picture was not drawn");
            Assert.That(textAt, Is.GreaterThanOrEqualTo(0), "the text over the background was not drawn");
            Assert.That(imageAt, Is.LessThan(textAt), "the text must be drawn after the picture");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A picture in front of the text replaces it, so the row has an image run and no text over it.
    /// This is what every image did before z existed.
    /// </summary>
    [AvaloniaTest]
    public void A_front_picture_leaves_no_text_over_it()
    {
        var (view, window) = Realised();
        try
        {
            PlaceWithDepth(view, z: 0);
            view.Terminal.Write(Esc + "[1;1HXY");

            var runs = RunsForRow(view, 0);

            // The character lands and the picture stays: a Kitty placement is an overlay, and the
            // z-index rather than the buffer decides which of them ends up visible.
            Assert.That(runs.Any(r => r.Text is not null), Is.True);
            Assert.That(runs.Any(r => r.IsImage), Is.True,
                        "typing over a front picture should not have removed it");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Text sitting on a background must not paint an opaque rectangle over it. A cell with no
    /// background colour of its own fills nothing, which is what lets the picture show through.
    /// </summary>
    [AvaloniaTest]
    public void Text_on_a_background_picture_paints_no_fill_over_it()
    {
        var (view, window) = Realised();
        try
        {
            PlaceWithDepth(view, z: -1);
            view.Terminal.Write(Esc + "[1;1HXY");

            // Only the runs actually sitting on the picture; blanks past its right edge prove nothing.
            var overPicture = RunsForRow(view, 0)
                .Where(r => r.Text is not null && r.StartX < 2 && r.StartX + r.CellCount > 0)
                .ToList();

            Assert.That(overPicture, Is.Not.Empty, "no text run was drawn over the picture");
            Assert.That(overPicture.All(r => r.Background is null), Is.True,
                        "a text run over a background picture painted a fill and hid it");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_backdrop_suppresses_fill_only_in_the_columns_it_covers()
    {
        // The picture covers columns 0-1. All four characters have identical attributes, so without
        // a coverage boundary they form one cached run; suppressing that run's background also drops
        // the red fill from columns 2-3, where there is no picture to preserve.
        var (view, window) = Realised();
        try
        {
            PlaceWithDepth(view, z: -1);
            view.Terminal.Write(Esc + "[1;1H" + Esc + "[41mWXYZ");

            var text = RunsForRow(view, 0).Where(r => r.Text is not null && r.StartX < 4).ToList();
            var overPicture = text.Where(r => r.StartX < 2).ToList();
            var besidePicture = text.Where(r => r.StartX >= 2 && r.StartX < 4).ToList();

            Assert.That(overPicture, Is.Not.Empty);
            Assert.That(overPicture.All(r => r.Background is null), Is.True,
                "the explicit cell background must not erase the backdrop");
            Assert.That(besidePicture, Is.Not.Empty,
                "coverage must split the same-style text run at the picture edge");
            Assert.That(besidePicture.All(r => r.Background is not null), Is.True,
                "columns beside the picture must keep their explicit background");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A row whose text sits on a background must still terminate. The run builder used to stop at
    /// any cell holding a picture, which on a background cell ends the run on its own first cell and
    /// leaves the column index where it was -- a spin, not a wrong pixel.
    /// </summary>
    [AvaloniaTest]
    public void A_row_of_text_over_a_background_completes()
    {
        var (view, window) = Realised();
        try
        {
            PlaceWithDepth(view, z: -1);
            view.Terminal.Write(Esc + "[1;1HXY");

            var covered = new HashSet<int>();
            foreach (var run in RunsForRow(view, 0).Where(r => r.Text is not null))
                for (int i = 0; i < run.CellCount; i++)
                    covered.Add(run.StartX + i);

            // Both characters were emitted, so the builder advanced past the background cells rather
            // than ending the run on the first of them and leaving the column index where it was.
            Assert.That(covered.Contains(0), Is.True, "column 0 produced no text run");
            Assert.That(covered.Contains(1), Is.True, "column 1 produced no text run");
        }
        finally { window.Close(); }
    }

    // ---- animation ---------------------------------------------------------------------------------

    /// <summary>Transmits a two frame animation under id 1 and places it at the cursor.</summary>
    private static void PlaceAnimation(TerminalView view, int gap = 50)
    {
        view.Terminal.Options.CellWidthPixels = CellPixelWidth;
        view.Terminal.Options.CellHeightPixels = CellPixelHeight;

        var red = Convert.ToBase64String(Solid(4, 6, 0, 0, 255));      // BGRA: red
        var green = Convert.ToBase64String(Solid(4, 6, 0, 255, 0));    // BGRA: green

        view.Terminal.Write($"{Esc}_Ga=t,i=1,f=32,s=4,v=6,q=2;{red}{St}");
        view.Terminal.Write($"{Esc}_Ga=f,i=1,c=1,z={gap},f=32,s=4,v=6,q=2;{green}{St}");
        view.Terminal.Write($"{Esc}_Ga=a,i=1,r=1,z={gap},q=2{St}");
        view.Terminal.Write($"{Esc}_Ga=a,i=1,s=3,q=2{St}");
        view.Terminal.Write(Esc + "[1;1H");
        view.Terminal.Write($"{Esc}_Ga=p,i=1,C=1,q=2{St}");
    }

    private static byte[] Solid(int width, int height, byte b, byte g, byte r)
    {
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            bytes[i * 4] = r;          // the protocol carries RGBA
            bytes[i * 4 + 1] = g;
            bytes[i * 4 + 2] = b;
            bytes[i * 4 + 3] = 255;
        }
        return bytes;
    }

    /// <summary>
    /// The bitmap cache is keyed on the image, and an animation's pixels move while that key stays
    /// the same. Uploading the current frame is what the serial check is for.
    /// </summary>
    [AvaloniaTest]
    public void An_advanced_animation_uploads_the_new_frame()
    {
        var (view, window) = Realised();
        try
        {
            PlaceAnimation(view);

            var image = ImageAt(view, 0, 0);
            Assert.That(image, Is.Not.Null);

            var first = TerminalView.CreateBitmap(image!);
            Assert.That(first, Is.Not.Null);

            Assert.That(view.Terminal.AdvanceAnimations(TimeSpan.FromMilliseconds(60)), Is.True);

            // The pixels a fresh upload would read must now be the second frame's. Index 1 is the
            // GREEN channel of BGRA: the two frames are red and green, and both have blue at zero,
            // so comparing index 0 would compare a channel neither of them uses.
            Assert.That(image!.CurrentPixels.Span[1], Is.Not.EqualTo(image.Pixels.Span[1]),
                        "the current frame did not move off the root");
            Assert.That(image.FrameSerial, Is.Not.Zero, "the serial did not change with the frame");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A picture that never moves must not make the renderer re-upload it. The serial stays zero for
    /// a still image, so the cached bitmap is kept.
    /// </summary>
    /// <summary>
    /// What actually reaches the bitmap has to be the CURRENT frame. Asserting on CurrentPixels
    /// alone would leave the upload free to read the root and every animation would draw frozen on
    /// its first frame -- which looks like a clock that never fires, not like the wrong buffer.
    /// </summary>
    [AvaloniaTest]
    public void The_upload_reads_the_current_frame_not_the_root()
    {
        var (view, window) = Realised();
        try
        {
            PlaceAnimation(view);

            var image = ImageAt(view, 0, 0);
            Assert.That(image, Is.Not.Null);

            var rootUpload = CopyThrough(image!, image!.Stride);
            view.Terminal.AdvanceAnimations(TimeSpan.FromMilliseconds(60));
            var frameUpload = CopyThrough(image, image.Stride);

            // Green channel: the frames are red and green, and both leave blue at zero.
            Assert.That(rootUpload[1], Is.Not.EqualTo(frameUpload[1]),
                        "the upload read the same pixels before and after the frame changed");
            Assert.That(frameUpload[1], Is.EqualTo(255), "the second frame is the green one");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The bitmap cache is keyed on the image, so for an animation the key stays put while the
    /// pixels move. It must re-upload when the frame changes -- and NOT re-upload when it has not,
    /// or every still picture on screen is uploaded afresh sixty times a second.
    /// </summary>
    [AvaloniaTest]
    public void The_cached_bitmap_is_replaced_only_when_the_frame_changes()
    {
        var (view, window) = Realised();
        try
        {
            PlaceAnimation(view);

            var image = ImageAt(view, 0, 0);
            Assert.That(image, Is.Not.Null);

            var first = view.GetOrCreateBitmap(image!);
            Assert.That(first, Is.Not.Null);
            Assert.That(view.GetOrCreateBitmap(image!), Is.SameAs(first),
                        "an unchanged frame was uploaded twice");

            view.Terminal.AdvanceAnimations(TimeSpan.FromMilliseconds(60));

            var afterFrame = view.GetOrCreateBitmap(image!);
            Assert.That(afterFrame, Is.Not.SameAs(first),
                        "the new frame was not uploaded, so the animation would draw frozen");

            // And the refresh must record which frame it uploaded. Without that the cache is stale
            // on every look, so a running animation re-uploads on each of them -- which draws
            // correctly and costs a full texture upload per frame, the kind of fault that shows up
            // as heat rather than as a wrong pixel.
            Assert.That(view.GetOrCreateBitmap(image!), Is.SameAs(afterFrame),
                        "the refreshed upload was not recorded, so it uploads again every frame");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_still_picture_keeps_a_frame_serial_of_zero()
    {
        var (view, window) = Realised();
        try
        {
            PlaceImage(view);

            var image = ImageAt(view, 0, 0);
            Assert.That(image, Is.Not.Null);
            Assert.That(image!.FrameSerial, Is.Zero);
            Assert.That(image.CurrentPixels.Length, Is.EqualTo(image.Pixels.Length));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A terminal showing text keeps no animation clock running, so the common case costs nothing.
    /// </summary>
    [AvaloniaTest]
    public void A_terminal_showing_text_reports_no_animation()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("hello");

            Assert.That(view.Terminal.HasRunningAnimations(), Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_placed_animation_reports_itself_as_running()
    {
        var (view, window) = Realised();
        try
        {
            PlaceAnimation(view);

            Assert.That(view.Terminal.HasRunningAnimations(), Is.True);
        }
        finally { window.Close(); }
    }

    // ---- overlapping pictures ---------------------------------------------------------------------

    /// <summary>
    /// Transmits two 4x6 pictures under ids 1 and 2 so they can be told apart, ready to be placed.
    /// </summary>
    private static void TransmitTwo(TerminalView view)
    {
        view.Terminal.Options.CellWidthPixels = CellPixelWidth;
        view.Terminal.Options.CellHeightPixels = CellPixelHeight;

        var pixels = Convert.ToBase64String(new byte[4 * 6 * 4]);
        view.Terminal.Write($"{Esc}_Ga=t,i=1,f=32,s=4,v=6,q=2;{pixels}{St}");
        view.Terminal.Write($"{Esc}_Ga=t,i=2,f=32,s=4,v=6,q=2;{pixels}{St}");
    }

    private static void PlaceAt(TerminalView view, int id, int col, int z)
    {
        view.Terminal.Write($"{Esc}[1;{col + 1}H");
        view.Terminal.Write($"{Esc}_Ga=p,i={id},z={z},C=1,q=2{St}");
    }

    /// <summary>
    /// Two pictures over one cell are both drawn, and the deeper one goes down first. Order is the
    /// whole of what compositing is here: Avalonia blends a translucent bitmap over whatever is
    /// already in the buffer, so getting the sequence right is what makes the blend right.
    /// </summary>
    [AvaloniaTest]
    public void Overlapping_pictures_are_drawn_back_to_front()
    {
        var (view, window) = Realised();
        try
        {
            TransmitTwo(view);
            PlaceAt(view, 1, col: 0, z: 5);
            PlaceAt(view, 2, col: 0, z: 1);

            var runs = RunsForRow(view, 0).ToList();
            var backAt = runs.FindIndex(r => r.Placement is { ZIndex: (short)1 });
            var frontAt = runs.FindIndex(r => r.Placement is { ZIndex: (short)5 });

            Assert.That(backAt, Is.GreaterThanOrEqualTo(0), "the covered picture was never drawn");
            Assert.That(frontAt, Is.GreaterThanOrEqualTo(0), "the front picture was never drawn");
            Assert.That(backAt, Is.LessThan(frontAt), "the deeper picture must be drawn first");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The cell background goes down once, under the whole stack. A nearer picture painting it again
    /// would erase the one behind it instead of blending over it — which would make overlapping
    /// placements pointless while still looking almost right, since the top picture would be correct.
    /// </summary>
    /// <remarks>
    /// The background belongs to the CELLS, and a picture no longer writes any: placing one leaves
    /// the cells exactly as they were. So the colour has to be put there by text before the pictures
    /// go over it, which is also the only way a real session produces one.
    /// </remarks>
    [AvaloniaTest]
    public void Only_the_bottom_picture_paints_the_cell_background()
    {
        var (view, window) = Realised();
        try
        {
            TransmitTwo(view);

            // Red-backed blanks for the pictures to sit on.
            view.Terminal.Write($"{Esc}[1;1H{Esc}[41m        {Esc}[0m");

            PlaceAt(view, 1, col: 0, z: 1);
            PlaceAt(view, 2, col: 0, z: 5);

            var filled = RunsForRow(view, 0)
                .Where(r => r.IsImage && r.Background is not null)
                .ToList();

            Assert.That(filled.Count, Is.EqualTo(1),
                        "the cell background belongs to the bottom picture alone");
            Assert.That(filled[0].Placement!.Value.ZIndex, Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A picture covered over part of itself is still drawn whole, and still as strips rather than
    /// one blit per cell -- the run follows one layer through cells where it is not on top.
    /// </summary>
    [AvaloniaTest]
    public void A_partly_covered_picture_is_still_drawn_in_full()
    {
        var (view, window) = Realised();
        try
        {
            TransmitTwo(view);
            PlaceAt(view, 1, col: 0, z: 1);          // columns 0-1
            PlaceAt(view, 2, col: 1, z: 5);          // columns 1-2, over its second cell

            var back = RunsForRow(view, 0).Where(r => r.Placement is { ZIndex: (short)1 }).ToList();

            // One run, not two. The lower picture is the bottom layer of both its cells -- being
            // covered does not change that -- so nothing splits the strip.
            Assert.That(back.Count, Is.EqualTo(1), "the covered picture should still be one strip");
            Assert.That(back[0].CellCount, Is.EqualTo(2),
                        "both of the lower picture's columns should still be drawn");
            Assert.That(back[0].Placement!.Value.SrcX, Is.Zero,
                        "the strip should start at the picture's own first pixel");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// An unlayered picture is still one draw per strip. The per-placement sweep replaced a
    /// per-cell walk, and coalescing is the thing most easily lost in that kind of rewrite.
    /// </summary>
    [AvaloniaTest]
    public void A_picture_with_nothing_over_it_is_still_one_run_per_strip()
    {
        var (view, window) = Realised();
        try
        {
            TransmitTwo(view);
            PlaceAt(view, 1, col: 0, z: 0);

            var runs = ImageRuns(view, 0);

            Assert.That(runs.Count, Is.EqualTo(1), "two adjacent tiles should be one draw, not two");
            Assert.That(runs[0].CellCount, Is.EqualTo(2));
        }
        finally { window.Close(); }
    }
}
