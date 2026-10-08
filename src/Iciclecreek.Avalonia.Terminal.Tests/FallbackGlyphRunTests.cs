using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Platform;
using NUnit.Framework;
using SkiaSharp;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Characters the font lacks get runs of their own, so a fallback glyph cannot push its neighbours
/// off the grid.
/// </summary>
/// <remarks>
/// <para>Shaped together with the rest of a run, a fallback glyph keeps its own font's advance. IBM
/// Plex Mono has no ▲ and no braille; the faces macOS falls back to are wider than a cell, so every
/// character after one landed a little right of its column, and the next run's background, drawn
/// after it, covered the overhang. btop lost the last digit of "100%" on every core row, the "B" of
/// "MiB", and the descenders under its arrows. The pixel result was checked against btop with
/// screenshots; what is asserted here is the run split and the pinned advances behind it.</para>
/// </remarks>
[TestFixture]
public class FallbackGlyphRunTests
{
    /// <summary>
    /// A private-use codepoint: no stock font has it, so it always needs a fallback, whatever the
    /// machine has installed.
    /// </summary>
    private const char Missing = '\uE000';

    /// <summary>
    /// A real font's glyph typeface, one that has <paramref name="codePoint"/>.
    /// </summary>
    /// <remarks>
    /// The headless platform's own font maps no character at all -- not even "a" -- so every cell
    /// would look like it needs a fallback, and it will not read a font file either. Skia's font
    /// manager will, but Avalonia keeps it and its reader internal, so both are reached by name; if
    /// a later Avalonia moves them, these tests skip rather than fail. Any installed face with an
    /// "a" in it does.
    /// </remarks>
    private static GlyphTypeface RealFont(int codePoint = 'a')
    {
        using var typeface = SKFontManager.Default.MatchCharacter(codePoint);
        Assume.That(typeface, Is.Not.Null, $"this machine has no font with U+{codePoint:X4} in it");
        using var data = SKData.Create(typeface!.OpenStream());

        var skiaType = Type.GetType("Avalonia.Skia.FontManagerImpl, Avalonia.Skia");
        var read = typeof(IFontManagerImpl).GetMethod("TryCreateGlyphTypeface",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new[] { typeof(Stream), typeof(FontSimulations), typeof(IPlatformTypeface).MakeByRefType() }, null);
        Assume.That(skiaType is not null && read is not null, "Avalonia moved its Skia font reader");

        var args = new object?[] { new MemoryStream(data.ToArray()), FontSimulations.None, null };
        Assert.That(read!.Invoke(Activator.CreateInstance(skiaType!, nonPublic: true), args), Is.True,
                    "Skia could not read the font");
        return new GlyphTypeface((IPlatformTypeface)args[2]!);
    }

    /// <summary>Puts <paramref name="font"/> where the view looks up its regular face.</summary>
    private static void UseFont(TerminalView view, GlyphTypeface font) =>
        Field<Dictionary<(FontStyle Style, FontWeight Weight), GlyphTypeface?>>(view, "_glyphTypefaces")
            [(FontStyle.Normal, FontWeight.Normal)] = font;

    /// <summary>Puts <paramref name="font"/> where the view looks up the fallback for a character.</summary>
    private static void UseFallback(TerminalView view, int codePoint, GlyphTypeface font) =>
        Field<Dictionary<(int CodePoint, FontStyle Style, FontWeight Weight), GlyphTypeface?>>(view, "_fallbackGlyphTypefaces")
            [(codePoint, FontStyle.Normal, FontWeight.Normal)] = font;

    private static T Field<T>(TerminalView view, string name) =>
        (T)typeof(TerminalView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static List<TerminalView.CachedTextRun> RunsFor(TerminalView view, string text)
    {
        view.Terminal.Write(text);

        var group = new DrawingGroup();
        using (var context = group.Open())
            view.Render(context);

        return (List<TerminalView.CachedTextRun>)view.Terminal.Buffer.Lines[view.Terminal.Buffer.ViewportY]!.Cache!;
    }

    private static bool FontHas(GlyphTypeface font, char c) =>
        font.CharacterToGlyphMap.TryGetGlyph(c, out var glyph) && glyph != 0;

    [AvaloniaTest]
    public void A_character_the_font_lacks_gets_a_run_of_its_own()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        try
        {
            var view = control.View();
            var font = RealFont();
            Assert.That(FontHas(font, 'a'), Is.True, "sanity: the font has plain letters");
            Assume.That(FontHas(font, Missing), Is.False, "this font has the private-use codepoint");
            UseFont(view, font);

            var runs = RunsFor(view, $"ab{Missing}{Missing}cd");
            var drawn = runs.Where(r => r.Text is not null || r.Glyphs is not null).ToList();

            Assert.That(drawn.Select(r => r.StartX), Is.EqualTo(new[] { 0, 2, 4 }),
                        "the letters after the fallback characters start a run at their own column");
            Assert.That(drawn[1].CellCount, Is.EqualTo(2), "the fallback run holds only the two it lacks");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_run_the_font_covers_is_not_split()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        try
        {
            var view = control.View();
            UseFont(view, RealFont());
            var runs = RunsFor(view, "abc def");

            Assert.That(runs.Count(r => r.Text is not null || r.Glyphs is not null), Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The fallback glyphs sit one cell apart and are drawn small enough to fit one.
    /// </summary>
    /// <remarks>
    /// Braille, because btop draws its graphs in it and few monospace faces have it. Whatever face
    /// the machine falls back to for it is the one asserted against, in a cell made a third of the
    /// font size wide so that the glyph is wider than it on any machine.
    /// </remarks>
    [AvaloniaTest]
    public void A_fallback_glyph_is_pinned_to_its_cell_and_fits_it()
    {
        const char Braille = '\u28FF';
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        try
        {
            var view = control.View();
            var font = RealFont();
            var fallback = RealFont(Braille);
            Assume.That(FontHas(font, Braille), Is.False, "this machine's plain face has braille");
            UseFont(view, font);
            UseFallback(view, Braille, fallback);
            var cell = view.FontSize / 3;
            typeof(TerminalView).GetField("_charWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, cell);

            Assert.That(fallback.TryGetHorizontalGlyphAdvance(fallback.CharacterToGlyphMap[Braille], out var advance), Is.True);
            var naturalWidth = advance * view.FontSize / fallback.Metrics.DesignEmHeight;
            Assume.That(naturalWidth, Is.GreaterThan(cell), "this machine's braille is narrower than a third of an em");

            var runs = RunsFor(view, $"a{Braille}{Braille}b");
            var glyphs = runs.Single(r => r.StartX == 1).Glyphs;

            Assert.That(glyphs, Is.Not.Null, "the fallback run was not drawn as a glyph run");
            Assert.That(glyphs!.GlyphInfos.Select(g => g.GlyphAdvance), Is.All.EqualTo(cell),
                        "each fallback glyph is one cell from the next");

            var drawnWidth = advance * glyphs.FontRenderingEmSize / fallback.Metrics.DesignEmHeight;
            Assert.That(drawnWidth, Is.EqualTo(cell).Within(1e-6), "shrunk until it fits the cell, and no further");
        }
        finally { window.Close(); }
    }
}
