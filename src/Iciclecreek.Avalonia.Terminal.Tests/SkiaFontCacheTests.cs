using System;
using Iciclecreek.Terminal.Skia;
using NUnit.Framework;
using SkiaSharp;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The font cache behind the direct Skia renderer.
///
/// <para>This is the part of that renderer with the least margin for error, because it re-implements
/// what DrawingContext was doing invisibly: resolving a face, finding another one for a glyph the
/// first lacks, and keeping both alive. Every failure here is quiet — a glyph that does not appear, a
/// face disposed under another terminal, an allocation per glyph that only shows as a frame rate.</para>
///
/// <para>Plain NUnit, no Avalonia: the cache talks to SkiaSharp directly and needs no UI thread.</para>
/// </summary>
[TestFixture]
public class SkiaFontCacheTests
{
    /// <summary>
    /// A monospace face the machine actually has. Hardcoding one name here made the suite a test
    /// of the runner's font inventory: Menlo exists on macOS and nowhere else, so the Linux CI leg
    /// failed five tests that had nothing to do with the cache. FromFamilyName never returns null
    /// -- it hands back the default face under a different family name -- so a real hit is the one
    /// whose FamilyName round-trips.
    /// </summary>
    private static readonly string Mono = ResolveMono();

    private static string ResolveMono()
    {
        foreach (var candidate in new[] { "Menlo", "DejaVu Sans Mono", "Consolas", "Liberation Mono" })
        {
            using var face = SKTypeface.FromFamilyName(candidate);
            if (face is not null && face.FamilyName == candidate)
                return candidate;
        }

        return SKTypeface.Default.FamilyName;
    }

    /// <summary>
    /// A PROPORTIONAL face the machine actually has, resolved the same way and for the same reason.
    /// Needed because one rule the resolver has to keep is that a host naming a proportional face as
    /// its primary has chosen one and is owed it, and that cannot be asserted without one to name.
    /// </summary>
    private static readonly string Proportional = ResolveProportional();

    private static string ResolveProportional()
    {
        foreach (var candidate in new[] { "Segoe UI", "Helvetica", "DejaVu Sans", "Liberation Sans", "Arial" })
        {
            using var face = SKTypeface.FromFamilyName(candidate);
            if (face is not null && face.FamilyName == candidate && !face.IsFixedPitch)
                return candidate;
        }

        return string.Empty;
    }

    /// <summary>
    /// The chain's last name is the net under every other one being absent, and it has to hold.
    /// </summary>
    /// <remarks>
    /// "monospace" is a GENERIC ALIAS rather than a family: fontconfig resolves it to whatever the
    /// machine configured, so the name does not round-trip -- on this Linux it comes back as Cousine --
    /// and the exact-match pass skipped it like any other miss. The net was unreachable, and a chain
    /// naming nothing installed fell through to the platform's proportional default instead.
    /// </remarks>
    [Test]
    public void The_generic_monospace_alias_is_the_net_under_a_chain_of_misses()
    {
        using var probe = SKTypeface.FromFamilyName("monospace");
        Assume.That(probe?.IsFixedPitch, Is.True,
            "this platform does not resolve the generic alias to a fixed-pitch face, so there is nothing to catch");

        using var cache = new SkiaFontCache();

        var font = cache.For("No Such Face,Also Not Installed,monospace", 12, SnapshotFlags.None);

        Assert.That(font.Typeface.IsFixedPitch, Is.True,
            "a chain ending in the generic alias must not come out proportional");
    }

    /// <summary>
    /// An emoji family in the chain must never become the face the grid is drawn with.
    /// </summary>
    /// <remarks>
    /// They are in the chain on purpose and they come last on purpose — TerminalView.DefaultFontFamily
    /// says so — but nothing enforced it here. The exact-match pass took the first name that EXISTS, and
    /// on a machine with no Cascadia installed that was Noto Color Emoji: a real family, an exact
    /// round-trip, no Latin. Every Latin cell then missed the primary face and went through per-codepoint
    /// fallback to the platform's proportional default, putting proportional glyphs on a monospace grid.
    /// <para>Every emoji family the default chain names, not one of them, because they are not built
    /// alike and a single hardcoded name tests only the platform that has it. Noto Color Emoji has no
    /// Latin at all; Segoe UI Emoji carries Segoe UI's entire proportional Latin set and so is the
    /// SAME bug reached through the opposite property. Pinned to one name, this test skipped on Windows
    /// and macOS -- exactly the machines where the second form of it lives.</para>
    /// </remarks>
    [TestCase("Noto Color Emoji")]
    [TestCase("Segoe UI Emoji")]
    [TestCase("Apple Color Emoji")]
    public void An_emoji_family_is_never_chosen_as_the_cell_face(string emoji)
    {
        using var probe = SKTypeface.FromFamilyName(emoji);
        Assume.That(probe?.FamilyName, Is.EqualTo(emoji), $"this machine has no {emoji} to be fooled by");

        using var monoProbe = SKTypeface.FromFamilyName(Mono);
        Assume.That(monoProbe?.IsFixedPitch, Is.True,
            $"this machine offered no fixed-pitch face ({Mono}), so there is nothing for the chain to land on");

        using var cache = new SkiaFontCache();

        // Named SECOND on purpose: the first name in a chain is the host's explicit choice and is
        // honoured whatever it measures like. The defect is an emoji family reached as a FALLBACK.
        var font = cache.For($"No Such Face,{emoji},{Mono}", 12, SnapshotFlags.None);

        // Asserted on the ADVANCE and not on the family name, because the advance is the property the
        // cell grid is laid out from -- this catches any proportional face reaching the cell face, not
        // only the one this case happens to name.
        var advance = font.MeasureText("i");
        Assert.Multiple(() =>
        {
            Assert.That(font.MeasureText("M"), Is.EqualTo(advance),
                $"the cell face ({font.Typeface.FamilyName}) is proportional, so the glyphs will not sit on the grid");
            Assert.That(font.MeasureText("."), Is.EqualTo(advance),
                $"the cell face ({font.Typeface.FamilyName}) is proportional, so the glyphs will not sit on the grid");
        });
    }

    /// <summary>
    /// The rule the uniform-advance guard must not break: the FIRST name is the host's own choice.
    /// </summary>
    /// <remarks>
    /// The guard rejects a candidate whose Latin advances differ, which is how an emoji family named as a
    /// fallback is kept off the cell grid. Applied to the first name too it would overreach: a host that
    /// writes a proportional face at the head of the chain has chosen one deliberately, and second-guessing
    /// that would silently draw something it did not ask for. Only the names AFTER the first are fallbacks.
    /// </remarks>
    [Test]
    public void A_proportional_face_named_first_is_still_honoured()
    {
        Assume.That(Proportional, Is.Not.Empty, "this machine has no proportional face to ask for");

        using var cache = new SkiaFontCache();

        var font = cache.For($"{Proportional},{Mono}", 12, SnapshotFlags.None);

        Assert.That(font.Typeface.FamilyName, Is.EqualTo(Proportional),
            "the host named this face first, so it is the one it asked for");
    }

    [Test]
    public void A_face_it_has_is_drawn_with_the_requested_font()
    {
        using var cache = new SkiaFontCache();

        var font = cache.For(Mono, 12, SnapshotFlags.None);

        Assert.That(font.Typeface.FamilyName, Is.EqualTo(Mono), "the requested family should be the one resolved");
        Assert.That(font.ContainsGlyph('a'), Is.True);
    }

    /// <summary>
    /// Fallback is not an edge case. The terminal font usually has no CJK and no emoji — Menlo has
    /// neither — so this is the path most non-Latin text takes, and it has to actually find something.
    /// </summary>
    [TestCase(0x4E16, TestName = "cjk")]
    [TestCase(0x3053, TestName = "kana")]
    [TestCase(0xD55C, TestName = "hangul")]
    [TestCase(0x1F600, TestName = "emoji")]
    public void A_glyph_the_terminal_font_lacks_resolves_to_a_face_that_has_it(int codePoint)
    {
        using var cache = new SkiaFontCache();

        var primary = cache.For(Mono, 12, SnapshotFlags.None);
        Assume.That(primary.ContainsGlyph(codePoint), Is.False,
            $"U+{codePoint:X4} would not exercise fallback if {Mono} already had it");

        // The system's own answer is the ENVIRONMENT check only: a runner without CJK fonts has
        // nothing to offer, which is a fact about the machine rather than a defect. The macOS leg
        // always has them, so the assertions below run on every CI run.
        Assume.That(SKFontManager.Default.MatchCharacter(codePoint), Is.Not.Null,
                    "the system offers no face for this codepoint; nothing to verify");

        // What is under test is the CACHE's resolver -- ResolveFallback and the family search
        // behind it -- not SKFontManager. Asserting on the system's answer left every regression
        // in our own resolution passing.
        var fallback = cache.FallbackFace(codePoint);

        Assert.That(fallback, Is.Not.Null, "the cache should resolve a face for this codepoint");
        using var sized = new SKFont(fallback!, 12);
        Assert.That(sized.ContainsGlyph(codePoint), Is.True, "the face the cache chose should have the glyph");
    }

    /// <summary>
    /// Repeated lookups must return the SAME font, not an equivalent one.
    ///
    /// A screen of CJK asks for a fallback font per glyph per frame. Building one each time was
    /// thousands of allocations a frame, and nothing about the rendered output would have shown it.
    /// </summary>
    [Test]
    public void Fonts_are_cached_rather_than_rebuilt()
    {
        using var cache = new SkiaFontCache();

        var first = cache.For(Mono, 12, SnapshotFlags.None);
        var second = cache.For(Mono, 12, SnapshotFlags.None);

        Assert.That(second, Is.SameAs(first), "a repeated request should not build another font");
    }

    [Test]
    public void Style_and_size_are_part_of_the_identity()
    {
        using var cache = new SkiaFontCache();

        var plain = cache.For(Mono, 12, SnapshotFlags.None);
        var bold = cache.For(Mono, 12, SnapshotFlags.Bold);
        var bigger = cache.For(Mono, 14, SnapshotFlags.None);

        Assert.That(bold, Is.Not.SameAs(plain), "bold must not be served the regular font");
        Assert.That(bigger, Is.Not.SameAs(plain), "a different size must not be served the same font");
        Assert.That(bigger.Size, Is.EqualTo(14f));
    }

    /// <summary>The baseline must come from the face being drawn with, or glyphs sit at the wrong height.</summary>
    [Test]
    public void Baseline_comes_from_the_requested_face()
    {
        using var cache = new SkiaFontCache();

        var baseline = cache.Baseline(Mono, 12, SnapshotFlags.None);

        using var expected = new SKFont(SKTypeface.FromFamilyName(Mono), 12);
        Assert.That(baseline, Is.EqualTo(-expected.Metrics.Ascent).Within(0.001),
            "the baseline should be the requested face's ascent, not some default face's");
        Assert.That(baseline, Is.GreaterThan(0));
    }

    [Test]
    public void Baseline_scales_with_size()
    {
        using var cache = new SkiaFontCache();

        Assert.That(cache.Baseline(Mono, 24, SnapshotFlags.None),
            Is.GreaterThan(cache.Baseline(Mono, 12, SnapshotFlags.None)));
    }

    /// <summary>
    /// Disposing one cache must not break another.
    ///
    /// SkiaSharp's typeface factories hand back SHARED instances — FromFamilyName, CreateDefault and
    /// MatchCharacter all return the same object for the same request, and Avalonia's own Skia backend
    /// asks for the same faces. A cache that disposed what it merely borrowed would take the face out
    /// from under a second terminal, or under Avalonia, and the failure would surface far from here.
    /// </summary>
    [Test]
    public void Disposing_a_cache_leaves_shared_faces_usable()
    {
        var first = new SkiaFontCache();
        _ = first.For(Mono, 12, SnapshotFlags.None);
        _ = first.Baseline(Mono, 12, SnapshotFlags.None);
        first.Dispose();

        using var second = new SkiaFontCache();
        var font = second.For(Mono, 12, SnapshotFlags.None);

        Assert.That(font.Typeface.FamilyName, Is.EqualTo(Mono));
        Assert.That(font.ContainsGlyph('a'), Is.True, "the shared face should have survived the other cache's disposal");

        // And the face SkiaSharp hands out directly is still the same live object.
        var direct = SKTypeface.FromFamilyName(Mono);
        Assert.That(direct, Is.Not.Null);
        using var sized = new SKFont(direct!, 12);
        Assert.That(sized.ContainsGlyph('a'), Is.True);
    }

    [Test]
    public void An_unknown_family_falls_back_rather_than_throwing()
    {
        using var cache = new SkiaFontCache();

        var font = cache.For("This Font Does Not Exist At All", 12, SnapshotFlags.None);

        Assert.That(font, Is.Not.Null);
        Assert.That(font.ContainsGlyph('a'), Is.True, "some usable face should still be drawn with");
    }

    /// <summary>
    /// The pixels one DrawCodepoint call produces, against black, so two draws can be compared
    /// byte for byte.
    ///
    /// A raster bitmap rather than a surface: the cache talks to SkiaSharp directly, and a GPU
    /// context would make these tests a check on the runner's graphics stack.
    /// </summary>
    private static byte[] Render(Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(48, 48, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.Black);
        draw(canvas);
        canvas.Flush();

        return bitmap.GetPixelSpan().ToArray();
    }

    private static SKPaint White() => new() { Color = SKColors.White, IsAntialias = true };

    /// <summary>
    /// The cached glyph has to draw what the uncached one drew.
    ///
    /// DrawCodepoint builds a positioned text blob on first sight and draws that blob thereafter,
    /// which is a different Skia call than DrawText(string, ...) with its own notion of where the
    /// origin is. Getting the position wrong shifts every glyph by a fraction of a cell and getting
    /// the glyph id wrong draws the wrong character -- neither of which any throughput measurement,
    /// or any other test here, would see.
    /// </summary>
    [Test]
    public void A_cached_glyph_draws_what_the_uncached_path_drew()
    {
        using var cache = new SkiaFontCache();
        using var paint = White();

        var font = cache.For(Mono, 24, SnapshotFlags.None);

        var reference = Render(c => c.DrawText("A", 6, 34, font, paint));
        var built = Render(c => cache.DrawCodepoint(c, 'A', 6, 34, font, paint));
        var hit = Render(c => cache.DrawCodepoint(c, 'A', 6, 34, font, paint));

        Assert.That(built, Is.Not.EqualTo(Render(_ => { })), "the glyph should have drawn something");
        Assert.That(built, Is.EqualTo(reference), "the blob it built should draw the same glyph in the same place");
        Assert.That(hit, Is.EqualTo(built), "the cached blob should draw the same as the one that built it");
        Assert.That(cache.HasCachedGlyph('A', font), Is.True, "the glyph should have been cached");
    }

    /// <summary>
    /// Fallback glyphs must be cached too, and cached against the font that was ASKED for.
    ///
    /// An earlier version resolved nothing and cached a null whenever the primary face lacked the
    /// codepoint, so every CJK and emoji cell still re-ran ContainsGlyph, the fallback lookup and
    /// the string DrawText every frame -- with a dictionary probe now added on top. That is the
    /// path most non-Latin text takes, so the optimisation missed the cells that needed it most.
    /// </summary>
    [TestCase(0x4E16, TestName = "cjk")]
    [TestCase(0x1F600, TestName = "emoji")]
    public void A_glyph_drawn_from_a_fallback_face_is_cached_too(int codePoint)
    {
        using var cache = new SkiaFontCache();
        using var paint = White();

        var font = cache.For(Mono, 24, SnapshotFlags.None);
        Assume.That(font.ContainsGlyph(codePoint), Is.False,
            $"U+{codePoint:X4} would not exercise fallback if {Mono} already had it");

        // A runner with no face for this codepoint is a fact about the machine, not a defect.
        var face = cache.FallbackFace(codePoint);
        Assume.That(face, Is.Not.Null, "the system offers no face for this codepoint; nothing to verify");

        var built = Render(c => cache.DrawCodepoint(c, codePoint, 6, 34, font, paint));
        var hit = Render(c => cache.DrawCodepoint(c, codePoint, 6, 34, font, paint));

        Assert.That(cache.HasCachedGlyph(codePoint, font), Is.True,
            "a codepoint that needs fallback should be cached against the font it was asked for, not skipped");
        Assert.That(built, Is.Not.EqualTo(Render(_ => { })), "the fallback face's glyph should have drawn something");
        Assert.That(hit, Is.EqualTo(built), "the cached fallback blob should draw the same glyph");

        // And it is the FALLBACK face's glyph, not the primary's missing-glyph box: building the
        // blob from one face while keying it on another is the whole trick here, and drawing the
        // box instead would still pass every assertion above.
        using var sized = new SKFont(face!, 24) { Subpixel = true };
        var reference = Render(c => c.DrawText(char.ConvertFromUtf32(codePoint), 6, 34, sized, paint));
        Assert.That(built, Is.EqualTo(reference), "the cache should draw with the face that has the glyph");
    }

    /// <summary>
    /// A supplementary-plane codepoint arrives as a surrogate PAIR, which is two chars for one
    /// glyph. A blob builder that mapped chars to glyphs one for one would build the wrong run.
    /// </summary>
    [Test]
    public void A_supplementary_plane_codepoint_caches_one_glyph_not_two()
    {
        const int codePoint = 0x1D400;   // MATHEMATICAL BOLD CAPITAL A

        using var cache = new SkiaFontCache();
        using var paint = White();

        var font = cache.For(Mono, 24, SnapshotFlags.None);
        var face = font.ContainsGlyph(codePoint) ? font.Typeface : cache.FallbackFace(codePoint);
        Assume.That(face, Is.Not.Null, "the system offers no face for this codepoint; nothing to verify");

        using var sized = new SKFont(face!, 24) { Subpixel = true };
        Assume.That(sized.ContainsGlyph(codePoint), Is.True, "the face chosen for this codepoint does not have it");

        var built = Render(c => cache.DrawCodepoint(c, codePoint, 6, 34, font, paint));
        var hit = Render(c => cache.DrawCodepoint(c, codePoint, 6, 34, font, paint));

        Assert.That(built, Is.Not.EqualTo(Render(_ => { })), "the astral glyph should have drawn something");
        Assert.That(hit, Is.EqualTo(built), "the cached blob should draw the same glyph");
        Assert.That(built, Is.EqualTo(Render(c => c.DrawText(char.ConvertFromUtf32(codePoint), 6, 34, sized, paint))),
            "one glyph for the pair, not one per surrogate");
    }

    /// <summary>
    /// Disposing a cache frees the blobs it built without taking the shared face down with them.
    ///
    /// The blobs are built ON a face this cache only borrowed -- FromFamilyName and MatchCharacter
    /// hand back shared instances -- so disposing them is exactly the kind of thing that could
    /// leave a second terminal, or Avalonia, drawing with a dead typeface. Nothing about the first
    /// cache's own output would show it.
    /// </summary>
    [Test]
    public void Disposing_a_cache_that_built_glyphs_leaves_the_shared_face_usable()
    {
        using var paint = White();

        var first = new SkiaFontCache();
        var font = first.For(Mono, 24, SnapshotFlags.None);
        _ = Render(c => first.DrawCodepoint(c, 'A', 6, 34, font, paint));
        Assume.That(first.HasCachedGlyph('A', font), Is.True, "the glyph should have been cached before disposal");
        first.Dispose();

        using var second = new SkiaFontCache();
        var again = second.For(Mono, 24, SnapshotFlags.None);

        Assert.That(Render(c => second.DrawCodepoint(c, 'A', 6, 34, again, paint)),
            Is.EqualTo(Render(c => c.DrawText("A", 6, 34, again, paint))),
            "a fresh cache should still draw the glyph after another one disposed its blobs");
    }
}
