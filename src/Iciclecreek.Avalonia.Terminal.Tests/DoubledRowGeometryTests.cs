using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Overlays drawn over rows and cells that are wider than one column.
/// </summary>
/// <remarks>
/// <para>The row pass draws a DECDWL/DECDHL row inside a 2x transform, so the cells come out right.
/// Everything drawn as an OVERLAY afterwards -- the caret, the selection, the hovered-link
/// underline -- is outside that transform and has to double its own geometry. None of it did, so on
/// a doubled row the selection covered the left half of what was selected, the underline stopped
/// halfway along the link, and the caret sat at half the column it marked.</para>
/// <para>Asserted against the geometry helpers rather than pixels, which headless Avalonia has no
/// backend to produce. They are the whole content of the fix: every one of these overlays multiplies
/// by what they return.</para>
/// </remarks>
[TestFixture]
public class DoubledRowGeometryTests
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

    private static int Row(TerminalView view) => view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y;

    private static double Scale(TerminalView view, int row) => Invoke<double>(view, "RowCellScale", row);

    private static T Invoke<T>(TerminalView view, string name, params object[] args)
    {
        var m = typeof(TerminalView).GetMethod(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(m, Is.Not.Null, $"{name} has been renamed; this test needs updating");
        return (T)m!.Invoke(view, args)!;
    }

    [AvaloniaTest]
    public void An_ordinary_row_is_one_cell_wide_per_column()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("plain");
            Dispatcher.UIThread.RunJobs();

            Assert.That(Scale(view, Row(view)), Is.EqualTo(1.0));
        }
        finally { window.Close(); }
    }

    // One method per attribute rather than [TestCase], because a plain NUnit case does not get the
    // Avalonia application this fixture needs -- it runs the view's static constructor with no
    // platform and takes the rest of the assembly down with it. [AvaloniaTest] does not combine with
    // TestCase here, so three bodies it is.

    [AvaloniaTest]
    public void A_DECDWL_row_is_two_cells_wide_per_column() => AssertDoubled("#6");

    [AvaloniaTest]
    public void A_DECDHL_top_row_is_two_cells_wide_per_column() => AssertDoubled("#3");

    [AvaloniaTest]
    public void A_DECDHL_bottom_row_is_two_cells_wide_per_column() => AssertDoubled("#4");

    private static void AssertDoubled(string sequence)
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}{sequence}doubled");
            Dispatcher.UIThread.RunJobs();

            Assert.That(Scale(view, Row(view)), Is.EqualTo(2.0));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_caret_on_a_narrow_cell_covers_one_column()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("abc");
            Dispatcher.UIThread.RunJobs();

            Assert.That(Invoke<int>(view, "CellWidthAt", Row(view), 0), Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_caret_on_a_wide_cell_covers_both_of_its_columns()
    {
        // A block caret one column wide over a two-column glyph repainted the whole glyph in the
        // background colour and then filled only its left half -- so the right half of the character
        // was erased for as long as the caret sat on it.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("你");   // CJK, two columns
            Dispatcher.UIThread.RunJobs();

            Assert.That(Invoke<int>(view, "CellWidthAt", Row(view), 0), Is.EqualTo(2),
                "sanity: the emulator laid it out as a wide cell");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_row_that_is_not_there_is_treated_as_ordinary()
    {
        // Both helpers are asked about rows the caller believes are on screen, and a buffer that
        // shrinks underneath makes that briefly untrue. Answering 1 keeps the frame drawing.
        var (view, window) = Realised();
        try
        {
            Assert.That(Scale(view, 100_000), Is.EqualTo(1.0));
            Assert.That(Invoke<int>(view, "CellWidthAt", 100_000, 0), Is.EqualTo(1));
            Assert.That(Invoke<int>(view, "CellWidthAt", Row(view), 100_000), Is.EqualTo(1));
        }
        finally { window.Close(); }
    }
}
