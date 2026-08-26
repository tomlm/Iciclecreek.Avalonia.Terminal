using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The window reports have to describe the grid, exactly.
///
/// <para>An image viewer works out the cell size for itself by dividing the pixel size from
/// <c>CSI 14 t</c> by the row and column counts it already has -- that is what <c>six</c> does, and it
/// is the ordinary way to do it. So the reply is not merely informative: divided by the grid, it has to
/// give back the cell size images are laid out against, or every picture comes out the wrong size.</para>
///
/// <para>Reporting the control's own bounds broke that twice over. It includes the scrollbar, and it
/// includes the strip below the last row -- the grid is a truncated division, so up to a row of the
/// control's height belongs to no row at all. At 549px with a 15.26px row pitch reported as 15, an
/// application dividing 549 by 35 rows is told the terminal is taller than it is, sizes a picture to
/// fill it, and the surplus scrolls whatever was above it off the top.</para>
/// </summary>
[TestFixture]
public class WindowGeometryReportTests
{
    private const string Esc = "\u001b";

    private static TerminalWindow Sized(int width, int height)
    {
        var window = new TerminalWindow { Process = "" };
        window.Width = width;
        window.Height = height;
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static async Task<string> AskAsync(TerminalWindow window, RecordingConnection pty, string query)
    {
        window.Terminal.Write(Esc + query);
        await Task.Delay(150);
        return pty.Written;
    }

    /// <summary>
    /// Sizes chosen so the text area is NOT a whole number of rows -- the ordinary case, and the one the
    /// old report got wrong.
    /// </summary>
    [TestCase(800, 600)]
    [TestCase(1188, 549)]
    [TestCase(1024, 733)]
    [AvaloniaTest]
    public async Task The_reported_pixel_size_divides_into_the_grid_exactly(int width, int height)
    {
        var window = Sized(width, height);
        try
        {
            window.Terminal.Options.WindowOptions.GetWinSizePixels = true;

            var pty = new RecordingConnection();
            window.Control().View().AttachConnection(pty);

            var reply = await AskAsync(window, pty, "[14t");

            var parts = reply.TrimEnd('t').Split(';');
            Assert.That(parts.Length, Is.EqualTo(3), $"malformed reply: '{reply}'");

            var reportedHeight = int.Parse(parts[1]);
            var reportedWidth = int.Parse(parts[2]);

            var rows = window.Terminal.Rows;
            var cols = window.Terminal.Cols;
            var cellHeight = window.Terminal.Options.CellHeightPixels;
            var cellWidth = window.Terminal.Options.CellWidthPixels;

            // The division an image viewer performs, integer maths and all.
            Assert.That(reportedHeight / rows, Is.EqualTo(cellHeight),
                $"reported {reportedHeight}px over {rows} rows implies a {reportedHeight / rows}px cell, "
                + $"but images are laid out against {cellHeight}px");
            Assert.That(reportedWidth / cols, Is.EqualTo(cellWidth),
                $"reported {reportedWidth}px over {cols} cols implies a {reportedWidth / cols}px cell, "
                + $"but images are laid out against {cellWidth}px");

            // And no remainder, so there is no invisible strip a picture could be sized into.
            Assert.That(reportedHeight, Is.EqualTo(rows * cellHeight));
            Assert.That(reportedWidth, Is.EqualTo(cols * cellWidth));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The end of the story that division begins: a picture sized to fill the reported area, the way a
    /// viewer does it, has to fit the screen rather than scroll it.
    /// </summary>
    [TestCase(1188, 549)]
    [TestCase(800, 600)]
    [TestCase(1024, 733)]
    [AvaloniaTest]
    public async Task An_image_sized_to_the_report_fits_without_scrolling(int width, int height)
    {
        var window = Sized(width, height);
        try
        {
            window.Terminal.Options.WindowOptions.GetWinSizePixels = true;

            var pty = new RecordingConnection();
            window.Control().View().AttachConnection(pty);

            var reply = await AskAsync(window, pty, "[14t");
            var reportedHeight = int.Parse(reply.TrimEnd('t').Split(';')[1]);
            var rows = window.Terminal.Rows;

            // Exactly what six does: derive the cell height, reserve a header and two border rows, round
            // down to a whole sixel band, and fill what is left.
            var cellHeight = reportedHeight / rows;
            var available = reportedHeight - 3 * cellHeight;
            available = available / 6 * 6;
            var imageRows = (available + cellHeight - 1) / cellHeight;

            Assert.That(imageRows + 3, Is.LessThanOrEqualTo(rows),
                $"a picture filling the reported {reportedHeight}px needs {imageRows} rows, and with the "
                + $"viewer's own 3 rows of chrome that is {imageRows + 3} in a {rows} row terminal — the "
                + "surplus scrolls the screen");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The cell size reported has to be the one images are actually laid out against, not a second
    /// measurement that might round differently.
    /// </summary>
    [TestCase(1188, 549)]
    [AvaloniaTest]
    public async Task The_cell_size_report_matches_the_layout_grid(int width, int height)
    {
        var window = Sized(width, height);
        try
        {
            window.Terminal.Options.WindowOptions.GetCellSizePixels = true;

            var pty = new RecordingConnection();
            window.Control().View().AttachConnection(pty);

            var reply = await AskAsync(window, pty, "[16t");
            var parts = reply.TrimEnd('t').Split(';');

            Assert.That(int.Parse(parts[1]), Is.EqualTo(window.Terminal.Options.CellHeightPixels));
            Assert.That(int.Parse(parts[2]), Is.EqualTo(window.Terminal.Options.CellWidthPixels));
        }
        finally { window.Close(); }
    }
}
