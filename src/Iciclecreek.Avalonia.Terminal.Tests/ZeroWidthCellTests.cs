using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// A width-0 cell is not always the placeholder behind a wide glyph, and rendering must survive the other
/// kind.
///
/// <para>A combining character printed with nothing in front of it to combine with — a line beginning with
/// U+0301, a stray variation selector, a keycap with no digit — is stored in a cell of its own with width 0
/// AND content, because <c>TryAppendToPreviousCell</c> found no base to attach it to. The renderer skips it,
/// which is correct: a combining mark with nothing to combine with has nothing to draw.</para>
///
/// <para>The render path used to assert that a width-0 cell had empty content, which is true of placeholders
/// and false of these. It fired on ordinary output.</para>
/// </summary>
[TestFixture]
public class ZeroWidthCellTests
{
    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    /// <summary>Each of these lands in a width-0 cell carrying content when it has no base.</summary>
    [TestCase("́", "combining acute")]
    [TestCase("️", "variation selector 16")]
    [TestCase("⃣", "combining keycap")]
    [TestCase("‍", "zero width joiner")]
    [AvaloniaTest]
    public void A_combining_character_with_no_base_renders_without_complaint(string text, string what)
    {
        var (view, window) = Realised();

        view.Terminal.Write(text);
        window.UpdateLayout();

        var cell = view.Terminal.Buffer.Lines[0]![0];
        Assert.That(cell.Width, Is.EqualTo(0), $"precondition: {what} occupies no column");
        Assert.That(cell.Content, Is.Not.Empty, $"precondition: and unlike a placeholder, {what} has content");

        // The assertion that matters is that rendering this does not blow up. Debug.Assert on a width-0
        // cell with content used to fail here, which is what a debugger stopped on.
        Assert.DoesNotThrow(() => view.InvalidateVisual());

        window.Close();
    }

    /// <summary>
    /// And the placeholder kind still behaves: a wide glyph is drawn once, over two columns, with the cell
    /// behind it skipped rather than drawn as a second glyph.
    /// </summary>
    [AvaloniaTest]
    public void A_wide_glyph_still_occupies_two_columns_and_draws_once()
    {
        var (view, window) = Realised();

        view.Terminal.Write("世X");        // a CJK character, then something narrow
        window.UpdateLayout();

        var line = view.Terminal.Buffer.Lines[0]!;
        Assert.That(line[0].Width, Is.EqualTo(2));
        Assert.That(line[1].Width, Is.EqualTo(0), "the placeholder behind it");
        Assert.That(line[1].Content, Is.Empty, "which carries nothing, unlike a stranded combining mark");
        Assert.That(line[2].Content, Is.EqualTo("X"), "and the next character starts past both columns");

        window.Close();
    }
}
