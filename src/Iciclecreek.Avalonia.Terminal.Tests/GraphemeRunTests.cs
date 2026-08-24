using Iciclecreek.Avalonia.Terminal;
using NUnit.Framework;
using XTerm;
using XTerm.Buffer;
using XTerm.Options;

namespace Iciclecreek.Avalonia.Terminal.Tests;

/// <summary>
/// A ZWJ emoji sequence must reach the shaper as ONE run, or the ligature cannot form and a family emoji
/// draws as separate people.
///
/// <para>These run against a real <see cref="XTerm.Terminal"/> rather than a hand-built line, because the whole
/// point is how the emulator actually lays a sequence out — a fabricated fixture would encode the assumption
/// under test. No Avalonia here: run building is pure text, so it needs no headless UI thread.</para>
/// </summary>
[TestFixture]
public class GraphemeRunTests
{
    private const string Zwj = "\u200D";
    private const string Family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";   // family: man-woman-girl
    private const string HeartOnFire = "\u2764\uFE0F\u200D\U0001F525";       // heart on fire: narrow-looking base, wide tail

    private static bool EndsWithJoiner(string? content) =>
        !string.IsNullOrEmpty(content) && content.EndsWith(Zwj, StringComparison.Ordinal);

    private static BufferLine LineOf(string text, out XTerm.Terminal terminal)
    {
        terminal = new XTerm.Terminal(new TerminalOptions());
        terminal.Write(text);
        return terminal.Buffer.Lines[0];
    }

    /// <summary>The emulator's layout, asserted directly — every other test here depends on this shape.</summary>
    [Test]
    public void Emulator_Splits_A_Zwj_Sequence_Across_Cells_Keeping_The_Joiner()
    {
        var line = LineOf(Family, out _);

        // ORDINAL, deliberately. string.EndsWith(string) defaults to a CULTURE-SENSITIVE comparison, and ICU
        // treats U+200D as an ignorable character — so "👧".EndsWith("\u200D") is TRUE under the default and
        // every assertion here would hold no matter what the emulator did.
        Assert.That(EndsWithJoiner(line[0].Content), Is.True, "the joiner rides on the first component");
        Assert.That(line[0].Width, Is.EqualTo(2));
        Assert.That(line[1].Width, Is.EqualTo(0), "wide cells are followed by a placeholder");
        Assert.That(EndsWithJoiner(line[2].Content), Is.True);
        Assert.That(EndsWithJoiner(line[4].Content), Is.False, "the last component carries no joiner");
    }

    [Test]
    public void Absorbs_A_Family_Sequence_Into_One_Run()
    {
        var line = LineOf(Family, out var terminal);

        var x = 2;              // as the width-2 branch leaves it: past the first cell and its placeholder
        var cellCount = 2;
        var text = GraphemeRuns.AbsorbJoinedCells(line, terminal.Cols, line[0], line[0].Content, ref x, ref cellCount);

        Assert.That(text, Is.EqualTo(Family), "the shaper must receive the whole cluster");
        Assert.That(cellCount, Is.EqualTo(6), "the run still spans every column the emulator advanced over");
        Assert.That(x, Is.EqualTo(6), "and the cursor lands past the whole sequence");
    }

    [Test]
    public void Absorbs_A_Sequence_That_Starts_Narrow_And_Continues_Wide()
    {
        // ❤️‍🔥 — the base is a narrow cell, the tail is a wide one, so this is the case that the width-1
        // collection loop would otherwise stop short of.
        var line = LineOf(HeartOnFire, out var terminal);
        Assert.That(EndsWithJoiner(line[0].Content), Is.True, "precondition: the base carries the joiner");

        // Start where the render loop would: past the first cell and whatever placeholder follows it.
        var x = line[0].Width;
        var cellCount = line[0].Width;
        var text = GraphemeRuns.AbsorbJoinedCells(line, terminal.Cols, line[0], line[0].Content, ref x, ref cellCount);

        Assert.That(text, Is.EqualTo(HeartOnFire), "the whole cluster, across the width change");
        Assert.That(cellCount, Is.GreaterThan(line[0].Width), "the tail cell is included in the span");
    }

    [Test]
    public void Leaves_An_Unjoined_Run_Untouched()
    {
        var line = LineOf("\U0001F600ab", out var terminal);   // 😀 then plain text

        var x = 2;
        var cellCount = 2;
        var text = GraphemeRuns.AbsorbJoinedCells(line, terminal.Cols, line[0], line[0].Content, ref x, ref cellCount);

        Assert.That(text, Is.EqualTo("\U0001F600"), "no joiner means no continuation");
        Assert.That(cellCount, Is.EqualTo(2));
        Assert.That(x, Is.EqualTo(2), "and nothing is consumed");
    }

    /// <summary>
    /// A dangling joiner — U+200D with no component after it — must absorb nothing.
    ///
    /// <para>This is the case the render loop really reaches. The emulator blanks a line with spaces, so the
    /// cell after the joiner is a width-1 space with the same attributes: absorbing it would pass the
    /// attribute check, stretch <c>cellCount</c> a column past the glyph actually drawn, and leave that
    /// column out of the rest of the line's run building.</para>
    /// </summary>
    [Test]
    public void A_Dangling_Joiner_On_A_Wide_Cell_Absorbs_Nothing()
    {
        var line = LineOf("\U0001F468" + Zwj, out var terminal);   // a lone man + joiner, nothing to join to
        Assert.That(EndsWithJoiner(line[0].Content), Is.True, "precondition: the joiner is dangling");
        Assert.That(line[2].Content, Is.EqualTo(" "), "precondition: the emulator blanks with spaces");

        // Exactly what the width-2 branch of the render loop leaves behind.
        var x = line[0].Width;
        var cellCount = line[0].Width;
        var text = GraphemeRuns.AbsorbJoinedCells(line, terminal.Cols, line[0], line[0].Content, ref x, ref cellCount);

        Assert.That(text, Is.EqualTo(line[0].Content), "nothing joins to a blank");
        Assert.That(cellCount, Is.EqualTo(2), "the run stays the width of the glyph it draws");
        Assert.That(x, Is.EqualTo(2), "and the rest of the line is still there to be drawn");
    }

    /// <summary>Same for a joiner dangling off a narrow cell, using that cell's real width rather than a guess.</summary>
    [Test]
    public void A_Dangling_Joiner_On_A_Narrow_Cell_Absorbs_Nothing()
    {
        var line = LineOf("a" + Zwj, out var terminal);
        Assert.That(EndsWithJoiner(line[0].Content), Is.True, "precondition: the joiner rides on the 'a'");

        var x = line[0].Width;
        var cellCount = line[0].Width;
        var text = GraphemeRuns.AbsorbJoinedCells(line, terminal.Cols, line[0], line[0].Content, ref x, ref cellCount);

        Assert.That(text, Is.EqualTo("a" + Zwj));
        Assert.That(cellCount, Is.EqualTo(line[0].Width), "no column beyond the 'a' belongs to this run");
        Assert.That(x, Is.EqualTo(line[0].Width));
    }

    /// <summary>
    /// A cell that contributes no text ends the walk, rather than leaving the trailing joiner in place and
    /// consuming the rest of the line one column at a time.
    ///
    /// <para>The line here is hand-built, unlike every other test in this fixture, and deliberately so: the
    /// emulator fills blank cells with spaces, so this state cannot be reached by writing to a real
    /// <see cref="XTerm.Terminal"/> today. That is exactly why the guard is worth pinning — if a future
    /// emulator version blanks with empty content instead, the failure would not be a missing ligature but
    /// one run stretched over a whole line, and this test is what would catch it.</para>
    /// </summary>
    [Test]
    public void A_Cell_That_Contributes_Nothing_Ends_The_Walk()
    {
        var attrs = new AttributeData();
        var line = new BufferLine(20, new BufferCell(string.Empty, 1, attrs));
        line[0] = new BufferCell("\U0001F468" + Zwj, 2, attrs);
        line[1] = new BufferCell(string.Empty, 0, attrs);   // the placeholder behind the wide cell

        var x = 2;
        var cellCount = 2;
        GraphemeRuns.AbsorbJoinedCells(line, 20, line[0], line[0].Content, ref x, ref cellCount);

        Assert.That(x, Is.EqualTo(2), "an empty cell cannot continue the cluster, so the walk stops");
        Assert.That(cellCount, Is.EqualTo(2), "and the run does not swallow the line");
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("a", false)]
    [TestCase("a\u200D", true)]
    public void ContinuesIntoNextCell_Reads_The_Trailing_Joiner(string? text, bool expected) =>
        Assert.That(GraphemeRuns.ContinuesIntoNextCell(text), Is.EqualTo(expected));
}
