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
            Assert.That(runs[0].TileCol, Is.Zero);
            Assert.That(runs[0].TileRow, Is.Zero);
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
                Assert.That(runs[0].TileRow, Is.EqualTo(row), $"row {row} drew the wrong strip");
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// An image cell holds a space, so without an explicit break it would be swallowed by the text run beside
    /// it and never drawn.
    /// </summary>
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
            Assert.That(runs[0].TileCol, Is.EqualTo(1));
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

    private static TerminalView.CachedTextRun Run(TerminalImage image, int startX, int cellCount, int tileCol, int tileRow)
        => new(null, startX, cellCount, null, image, tileCol, tileRow);

    /// <summary>Pixels that divide evenly into cells: 8x6 over 2x3 cells is four by two tiles.</summary>
    private static TerminalImage EvenImage()
        => new(new byte[8 * 6 * 4], 8, 6, CellPixelWidth, CellPixelHeight);

    [AvaloniaTest]
    public void A_full_strip_maps_to_whole_cells()
    {
        var image = EvenImage();

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 4, 0, 0), 0, 0, 10, 20, 1.0,
            out var source, out var destination), Is.True);

        Assert.That(source, Is.EqualTo(new Rect(0, 0, 8, 3)));
        Assert.That(destination, Is.EqualTo(new Rect(0, 0, 40, 20)));
    }

    [AvaloniaTest]
    public void A_strip_further_down_reads_from_further_down_the_picture()
    {
        var image = EvenImage();

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 4, 0, 1), 1, 20, 10, 20, 1.0,
            out var source, out var destination), Is.True);

        Assert.That(source, Is.EqualTo(new Rect(0, 3, 8, 3)));
        Assert.That(destination, Is.EqualTo(new Rect(0, 20, 40, 20)));
    }

    [AvaloniaTest]
    public void A_run_starting_partway_across_is_offset_in_both_rectangles()
    {
        var image = EvenImage();

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 5, 2, 2, 0), 0, 0, 10, 20, 1.0,
            out var source, out var destination), Is.True);

        Assert.That(source, Is.EqualTo(new Rect(4, 0, 4, 3)));
        Assert.That(destination, Is.EqualTo(new Rect(50, 0, 20, 20)));
    }

    /// <summary>
    /// The case that separates a picture from a smeared one: a tile holding half a cell's worth of pixels has
    /// to cover half a cell, not be stretched across a whole one.
    /// </summary>
    [AvaloniaTest]
    public void A_clipped_edge_tile_covers_only_the_fraction_of_a_cell_it_holds()
    {
        // Seven pixels wide over two-pixel cells: four columns, the last holding a single pixel.
        var image = new TerminalImage(new byte[7 * 6 * 4], 7, 6, CellPixelWidth, CellPixelHeight);

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 4, 0, 0), 0, 0, 10, 20, 1.0,
            out var source, out var destination), Is.True);

        Assert.That(source.Width, Is.EqualTo(7), "the source cannot extend past the picture");
        Assert.That(destination.Width, Is.EqualTo(35),
            "three and a half cells of pixels should cover three and a half cells of screen");
    }

    [AvaloniaTest]
    public void A_clipped_bottom_tile_covers_only_the_fraction_of_a_row_it_holds()
    {
        // Eight pixels tall over three-pixel cells: three rows, the last holding two pixels.
        var image = new TerminalImage(new byte[8 * 8 * 4], 8, 8, CellPixelWidth, CellPixelHeight);

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 4, 0, 2), 2, 40, 10, 20, 1.0,
            out var source, out var destination), Is.True);

        Assert.That(source, Is.EqualTo(new Rect(0, 6, 8, 2)));

        // Two thirds of a 20-unit row, snapped to the device grid.
        Assert.That(destination.Height, Is.EqualTo(Math.Round(20.0 * 2 / 3)).Within(0.0001));
    }

    [AvaloniaTest]
    public void A_tile_outside_the_picture_is_refused()
    {
        var image = EvenImage();

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 1, 9, 0), 0, 0, 10, 20, 1.0, out _, out _),
            Is.False);
        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 1, 0, 9), 0, 0, 10, 20, 1.0, out _, out _),
            Is.False);
    }

    [AvaloniaTest]
    public void An_empty_run_is_refused()
    {
        var image = EvenImage();

        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 0, 0, 0, 0), 0, 0, 10, 20, 1.0, out _, out _),
            Is.False);
    }

    [AvaloniaTest]
    public void A_run_with_no_picture_is_refused()
    {
        var run = new TerminalView.CachedTextRun(null, 0, 2, null);

        Assert.That(TerminalView.TryPlanImageBlit(run, 0, 0, 10, 20, 1.0, out _, out _), Is.False);
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
        Assert.That(TerminalView.TryPlanImageBlit(Run(image, 3, 4, 0, 0), 0, 0, 8.35, 20, 1.5,
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
}
