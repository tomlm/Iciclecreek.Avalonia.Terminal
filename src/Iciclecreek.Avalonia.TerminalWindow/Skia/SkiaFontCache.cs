using System;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Iciclecreek.Terminal.Skia
{
    /// <summary>
    /// Faces, fonts and glyph lookups for the Skia layer.
    /// </summary>
    /// <remarks>
    /// <para>Everything here exists because the direct path gave up what DrawingContext supplied.
    /// Avalonia resolved a typeface, found a fallback face for a glyph the primary one lacked, and
    /// shaped clusters; drawing onto the canvas means doing all three by hand.</para>
    ///
    /// <para>Accessed from the render thread, and rebuilt from the UI thread when the font changes, so
    /// the maps are concurrent. The values are native handles: an SKFont is cheap to keep and
    /// expensive to build, which is the whole reason for caching them rather than the drawing.</para>
    /// </remarks>
    internal sealed class SkiaFontCache : IDisposable
    {
        private readonly ConcurrentDictionary<(string Family, float Size, SnapshotFlags Style), SKFont> _fonts = new();

        /// <summary>
        /// Which face actually has a given codepoint. Resolved once and remembered, because asking
        /// the system font manager is expensive and the answer never changes for a codepoint.
        /// </summary>
        private readonly ConcurrentDictionary<int, SKTypeface?> _fallback = new();

        /// <summary>
        /// Sized fonts for fallback faces.
        ///
        /// Not a micro-optimisation: the terminal font typically has NO cjk and no emoji — Menlo has
        /// neither — so fallback is the path every CJK glyph takes, not a rare one. Building an SKFont
        /// per glyph meant thousands of them per frame on a screen of Japanese.
        /// </summary>
        private readonly ConcurrentDictionary<(IntPtr Face, float Size), SKFont> _fallbackFonts = new();

        private readonly ConcurrentDictionary<(string Family, SnapshotFlags Style), SKTypeface> _faces = new();
        private readonly ConcurrentDictionary<(string Family, float Size), float> _baselines = new();

        /// <summary>
        /// One glyph, pre-shaped, per (face, size, codepoint). A terminal draws from a small
        /// alphabet against a screen of thousands of cells, so this settles at a few hundred
        /// entries — and every one of those cells previously paid DrawText's own codepoint-to-glyph
        /// lookup again, on top of the ContainsGlyph lookup that had just made the same one. Built
        /// once, a cell now draws a blob whose glyph is already known: DrawText(blob, ...) positions
        /// and rasterizes, nothing more.
        /// </summary>
        private readonly ConcurrentDictionary<(IntPtr Face, float Size, int CodePoint), SKTextBlob?> _glyphBlobs = new();

        /// <summary>One shaper per face. Building one parses the font's tables; clusters repeat.</summary>
        private readonly ConcurrentDictionary<SKTypeface, SKShaper> _shapers = new();

        /// <summary>
        /// Which characters a face changes the glyph of when something sits beside them, probed once
        /// per face. Indexed by codepoint over printable ASCII; null where the face ligates nothing.
        /// </summary>
        private readonly ConcurrentDictionary<SKTypeface, bool[]?> _ligatureAlphabet = new();

        /// <summary>Shaped runs, keyed on the text and the face that shaped it.</summary>
        private readonly ConcurrentDictionary<(string Text, IntPtr Face, float Size), ushort[]?> _shapedRuns = new();

        /// <summary>Scratch for turning a codepoint into the chars Skia wants, avoiding a string per glyph.</summary>
        [ThreadStatic] private static char[]? _scratch;

        public SKFont For(string family, double size, SnapshotFlags flags)
        {
            // Dim is part of the FACE, not only the alpha: the classic path maps SGR 2 to
            // FontWeight.Thin, so a dim run drawn at normal weight here came out heavier than the
            // same text on the other path.
            var style = flags & (SnapshotFlags.Bold | SnapshotFlags.Italic | SnapshotFlags.Dim);
            return _fonts.GetOrAdd((family, (float)size, style),
                key => new SKFont(Face(key.Family, key.Style), key.Size) { Subpixel = true });
        }

        private SKTypeface Face(string family, SnapshotFlags style)
        {
            return _faces.GetOrAdd((family, style), key =>
            {
                var weight = (key.Style & SnapshotFlags.Bold) != 0 ? SKFontStyleWeight.Bold
                           : (key.Style & SnapshotFlags.Dim) != 0 ? SKFontStyleWeight.Thin
                           : SKFontStyleWeight.Normal;
                var slant = (key.Style & SnapshotFlags.Italic) != 0 ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;

                // A FAMILY CHAIN, not one name. Avalonia's FontFamily is comma-separated -- the
                // library's own default is such a chain, and a host that names a ligature face
                // usually appends fallbacks after it -- and FromFamilyName knows nothing about
                // that syntax: handed the whole string it matched nothing and quietly returned the
                // platform default, which is how a chain naming Cascadia Code drew in a
                // proportional system face with no ligatures at all.
                //
                // A real hit is the name that comes back: FromFamilyName never returns null, it
                // returns the default face under whatever name the default has.
                //
                // Only the FIRST name is the host's own choice; every name after it is one the host
                // named in case the one before it is absent, and the grid has to survive whichever of
                // them ends up answering. So the primary is taken on its word and the rest have to
                // measure like a terminal font as well as exist -- see HasUniformLatin.
                var isPrimary = true;
                foreach (var candidate in key.Family.Split(','))
                {
                    var name = candidate.Trim();
                    if (name.Length == 0)
                        continue;

                    var face = SKTypeface.FromFamilyName(name, weight, SKFontStyleWidth.Normal, slant);
                    if (face is not null && string.Equals(face.FamilyName, name, StringComparison.OrdinalIgnoreCase)
                        && CanDrawText(face) && (isPrimary || HasUniformLatin(face)))
                        return face;

                    isPrimary = false;
                }

                // "monospace" is a GENERIC ALIAS rather than a family, and the chain ends in one on
                // purpose -- it is the net under every named face being absent. fontconfig resolves it
                // to whatever the machine configured, so the name never round-trips (here: Cousine) and
                // the exact-match pass above always skips it, leaving the net unreachable. Accept the
                // substitute, but only when it really is fixed pitch, since a grid is the whole point.
                foreach (var candidate in key.Family.Split(','))
                {
                    var name = candidate.Trim();
                    if (!string.Equals(name, "monospace", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var generic = SKTypeface.FromFamilyName(name, weight, SKFontStyleWidth.Normal, slant);
                    if (generic is not null && generic.IsFixedPitch && CanDrawText(generic))
                        return generic;
                }

                // Nothing in the chain is installed: take the first name's answer, which is the
                // platform substitute, exactly as before.
                var first = key.Family.Split(',')[0].Trim();
                return SKTypeface.FromFamilyName(first.Length > 0 ? first : key.Family, weight, SKFontStyleWidth.Normal, slant)
                       ?? SKTypeface.CreateDefault();
            });
        }

        /// <summary>
        /// Whether a face can carry the terminal's TEXT, as opposed to merely existing.
        /// </summary>
        /// <remarks>
        /// <para>The chain ends in emoji families on purpose — see TerminalView.DefaultFontFamily, which
        /// states the rule this enforces: they come last and never in front, because the cell grid comes
        /// from the face chosen here and they would break it rather than fix it. Nothing was enforcing it.
        /// The exact-match pass took the first name that EXISTS, and on a stock Linux with no Cascadia
        /// installed that is Noto Color Emoji: a real family, an exact round-trip, and no Latin at all.</para>
        /// <para>What that looks like is not blank text, which would have been obvious. Every Latin cell
        /// missed the primary face and went through the per-codepoint fallback, which answers 'a' with the
        /// platform's proportional default — so the glyphs came out proportional on a monospace grid,
        /// columns landing correctly and the characters inside them not.</para>
        /// <para>This on its OWN is not enough, and the two emoji families are built oppositely enough
        /// to prove it: Noto Color Emoji has no Latin and reports fixed pitch, while Segoe UI Emoji is
        /// the exact reverse. Either test alone lets one of the two through, so both are asked -- see
        /// HasUniformLatin, which is the half that catches Segoe UI Emoji.</para>
        /// </remarks>
        private static bool CanDrawText(SKTypeface face)
            => face.GetGlyph('M') != 0 && face.GetGlyph('a') != 0;

        /// <summary>
        /// Whether the face measures every Latin character the same, which is what a cell grid needs.
        /// </summary>
        /// <remarks>
        /// <para>CanDrawText's companion, and the half that keeps Segoe UI Emoji out. It is a real family
        /// that round-trips its own name, and it carries the whole of Segoe UI's PROPORTIONAL Latin set --
        /// measured on Windows 11, GetGlyph('M')=48, GetGlyph('a')=68, IsFixedPitch=False, advances i/M/.
        /// of 3.875/14.367/3.469 at 16px against Cascadia Mono's uniform 9.375 -- so "has Latin" passes it
        /// outright and it was taken as the cell face.</para>
        /// <para>Not a hypothetical: this repo's own Demo.Desktop chain hits it on any Windows machine
        /// where Cascadia Code is not SYSTEM-installed, which is the normal case, because
        /// "fonts:CascadiaCode#Cascadia Code" is an EMBEDDED avares:// font that Avalonia's text stack
        /// loads and DirectWrite never sees. The first name misses, Segoe UI Emoji is next, and the cells
        /// came out proportional on a grid measured from the embedded monospace face.</para>
        /// <para>Asked as the measured ADVANCE rather than IsFixedPitch, which would also have caught this
        /// one: the flag is a claim in the font's tables, the advance is the thing the grid is actually
        /// laid out from. Compared exactly rather than within a tolerance because a monospace face's
        /// characters share one advance scaled by one factor -- verified equal to the bit across sizes and
        /// subpixel settings on every installed fixed-pitch family here.</para>
        /// <para>Off the hot path: Face() is memoized per (chain, style), so this costs three measurements
        /// per distinct chain, not per glyph.</para>
        /// </remarks>
        private static bool HasUniformLatin(SKTypeface face)
        {
            using var probe = new SKFont(face, 16f);
            var advance = probe.MeasureText("i");
            return advance == probe.MeasureText("M") && advance == probe.MeasureText(".");
        }

        /// <summary>
        /// Distance from the top of a cell to the text baseline, for the face actually being drawn with.
        ///
        /// Taken from the REQUESTED family. An earlier version measured SKTypeface.CreateDefault()
        /// whatever was being drawn, so every glyph sat on the system font's ascent rather than its
        /// own — which reads as the text being a different size, and which no throughput measurement
        /// can see.
        /// </summary>
        public float Baseline(string family, double fontSize, SnapshotFlags flags)
        {
            var style = flags & (SnapshotFlags.Bold | SnapshotFlags.Italic);

            return _baselines.GetOrAdd((family, (float)fontSize), key =>
            {
                using var probe = new SKFont(Face(key.Family, style), key.Size);
                return -probe.Metrics.Ascent;
            });
        }

        /// <summary>
        /// Draws one codepoint, falling back to another face when the requested one has no glyph for it.
        /// </summary>
        /// <remarks>
        /// Without this, a glyph outside the terminal font — an emoji, a box-drawing character in a
        /// face that lacks them — draws as nothing at all, where DrawingContext would silently have
        /// found a face that had it. Drawing nothing is a far worse failure than drawing it in a
        /// substitute face, because it is invisible rather than merely ugly.
        /// </remarks>
        public void DrawCodepoint(SKCanvas canvas, int codePoint, float x, float y, SKFont font, SKPaint paint)
        {
            var scratch = _scratch ??= new char[2];
            int length;

            if (codePoint <= 0xFFFF)
            {
                scratch[0] = (char)codePoint;
                length = 1;
            }
            else
            {
                var value = codePoint - 0x10000;
                scratch[0] = (char)(0xD800 + (value >> 10));
                scratch[1] = (char)(0xDC00 + (value & 0x3FF));
                length = 2;
            }

            var span = new ReadOnlySpan<char>(scratch, 0, length);
            var text = TextFor(codePoint, span);

            var blob = _glyphBlobs.GetOrAdd((font.Typeface.Handle, font.Size, codePoint), key =>
            {
                // Resolved ONCE per (face, size, codepoint) rather than on every cell that draws
                // it: ContainsGlyph, the fallback search and DrawText(string, ...) each mapped the
                // codepoint on their own, so a screen of the same handful of characters paid that
                // work thousands of times a frame for an answer that never changes.
                //
                // The key is the face that was ASKED for; the blob is built with the face that
                // actually DRAWS, which is not the same one whenever fallback is involved. Keying
                // on the drawing face instead would force every cell to resolve fallback before it
                // could find its own entry -- and fallback is not an edge case here, since the
                // terminal font typically has no CJK and no emoji, so that is the common path for
                // all non-Latin text.
                var drawFont = font;

                if (!font.ContainsGlyph(codePoint))
                {
                    var fallback = _fallback.GetOrAdd(codePoint, ResolveFallback);

                    // A null face means nothing on the system claims this codepoint, so the primary
                    // font stays and draws the missing-glyph box: a visible box says "this cell
                    // holds something I cannot draw", where drawing nothing says "this cell is
                    // empty" -- and the cell is NOT empty, as selecting and copying it would show.
                    if (fallback is not null)
                        drawFont = FallbackFont(fallback, font.Size);
                }

                var glyphs = drawFont.GetGlyphs(text);
                if (glyphs.Length != 1)
                    return null;   // an odd shape; the per-frame path below still handles it

                using var builder = new SKTextBlobBuilder();
                var run = builder.AllocatePositionedRun(drawFont, 1);
                run.Glyphs[0] = glyphs[0];
                run.Positions[0] = new SKPoint(0, 0);
                return builder.Build();
            });

            if (blob is not null)
            {
                canvas.DrawText(blob, x, y, paint);
                return;
            }

            // Only a codepoint the builder could not reduce to a single glyph reaches here, so the
            // whole resolution is walked again -- rarely, and correctness comes first.
            if (font.ContainsGlyph(codePoint))
            {
                // The string for this codepoint, remembered rather than rebuilt. This runs once per
                // visible cell per frame, so span.ToString() was thousands of short-lived
                // allocations a frame; a terminal draws from a small alphabet, so the cache settles
                // at a few hundred entries and never allocates again.
                canvas.DrawText(text, x, y, font, paint);
                return;
            }

            var face = _fallback.GetOrAdd(codePoint, ResolveFallback);

            if (face is null)
            {
                // Nothing on the system claims it. Draw with the primary face anyway, which produces
                // the missing-glyph box: a visible box says "this cell holds something I cannot draw",
                // where drawing nothing says "this cell is empty" — and the cell is NOT empty, as
                // selecting and copying it would show.
                canvas.DrawText(text, x, y, font, paint);
                return;
            }

            canvas.DrawText(text, x, y, FallbackFont(face, font.Size), paint);
        }

        /// <summary>
        /// Draws text that is more than one codepoint — combining marks, a ZWJ emoji sequence —
        /// through HarfBuzz, so the components combine into the glyph they are supposed to be.
        /// </summary>
        /// <remarks>
        /// <para>Without shaping, SKCanvas.DrawText maps characters to glyphs one for one: a family
        /// emoji drew as several boxes followed by a stray child, which the side-by-side against
        /// DrawingContext caught and no throughput number could.</para>
        ///
        /// <para>Shapers are cached per face. Building one parses the font's tables, which is far too
        /// expensive to repeat per glyph, and clusters repeat heavily — a prompt that ends in the same
        /// emoji every line asks for the identical shape thousands of times.</para>
        /// </remarks>
        public void DrawShaped(SKCanvas canvas, string text, float x, float y,
                               string family, double fontSize, SnapshotFlags flags, SKPaint paint)
        {
            var style = flags & (SnapshotFlags.Bold | SnapshotFlags.Italic);
            var face = Face(family, style);

            // A cluster the primary face cannot render at all goes to whatever face has its first
            // codepoint, the same way a single glyph does.
            if (!face.ContainsGlyph(char.ConvertToUtf32(text, 0)))
            {
                var fallback = _fallback.GetOrAdd(char.ConvertToUtf32(text, 0), ResolveFallback);

                if (fallback is not null)
                    face = fallback;
            }

            var shaper = _shapers.GetOrAdd(face, f => new SKShaper(f));
            canvas.DrawShapedText(shaper, text, x, y, FallbackFont(face, (float)fontSize), paint);
        }

        /// <summary>
        /// The characters this face draws differently depending on their neighbours — which is what a
        /// programming ligature is. Null when it has none.
        /// </summary>
        /// <remarks>
        /// <para>Not what "ligature" usually means, and the difference decides the whole design.
        /// Cascadia Code, Fira Code and the rest do NOT substitute two characters for one glyph; they
        /// use <c>calt</c> to swap each character for a piece of the picture, so <c>=&gt;</c> stays
        /// two glyphs and becomes the left and right halves of an arrow. That is deliberate — it
        /// keeps every advance exactly one cell, which is the only way a ligature can live in a
        /// grid.</para>
        /// <para>So the probe compares GLYPH IDS shaped together against shaped alone. Counting
        /// glyphs, which is the obvious test and the one written first, detects nothing at all: the
        /// count never changes.</para>
        /// <para>Swept over every printable-ASCII pair rather than assumed. A hardcoded set of
        /// "characters that look like they ligate" is a table that rots, and it is wrong: measured,
        /// Fira Code's participating set includes <c>i</c>, <c>j</c>, <c>l</c> and <c>w</c>, and
        /// nobody was going to guess those. The sweep costs <b>9,025 shapes in about 12ms</b>, once
        /// per face, and gives that face's exact answer.</para>
        /// <para>Same-character triples are swept after the pairs, and that sweep is not a nicety —
        /// it is the only thing that finds <c>w</c>. <c>ww</c> shapes to two ordinary w's; only
        /// <c>www</c> substitutes, in Fira Code AND in Cascadia. A pairwise probe reports both fonts
        /// as having no alphabetic ligatures at all, which is how the first version of this went
        /// wrong.</para>
        /// <para>Still missed: a ligature needing three DIFFERENT characters with no two-character
        /// signal. Catching those means parsing GSUB coverage tables, which is a great deal of
        /// OpenType parsing for a case yet to be seen.</para>
        /// <para>Paid on the first frame that uses a face, so one frame, once. Everything downstream
        /// is gated on it, and a font with no ligatures — most of them — returns null immediately
        /// after.</para>
        /// </remarks>
        public bool[]? LigatureAlphabet(string family, SnapshotFlags flags)
        {
            var face = Face(family, flags & (SnapshotFlags.Bold | SnapshotFlags.Italic));

            return _ligatureAlphabet.GetOrAdd(face, f =>
            {
                try
                {
                    using var probe = new SKFont(f, 16f);
                    var shaper = _shapers.GetOrAdd(f, x => new SKShaper(x));

                    const int first = ' ', last = '~';
                    var alone = new uint[last + 1];

                    for (var c = first; c <= last; c++)
                    {
                        var one = shaper.Shape(((char)c).ToString(), probe).Codepoints;
                        if (one.Length != 1)
                            return null;   // a face this odd is not one to guess about
                        alone[c] = (uint)one[0];
                    }

                    var participates = new bool[last + 1];
                    var any = false;

                    for (var a = first; a <= last; a++)
                    {
                        for (var b = first; b <= last; b++)
                        {
                            var pair = shaper.Shape($"{(char)a}{(char)b}", probe).Codepoints;

                            if (pair.Length != 2)
                            {
                                // A real substitution rather than an alternate. Both characters are
                                // involved, even though the run drawer will refuse this one later.
                                participates[a] = participates[b] = any = true;
                                continue;
                            }

                            if (pair[0] != alone[a]) participates[a] = any = true;
                            if (pair[1] != alone[b]) participates[b] = any = true;
                        }
                    }

                    // And the three-of-a-kind forms, which a pair cannot reveal.
                    for (var c = first; c <= last; c++)
                    {
                        if (participates[c])
                            continue;

                        var triple = shaper.Shape(new string((char)c, 3), probe).Codepoints;
                        if (triple.Length != 3 || triple[0] != alone[c] || triple[1] != alone[c])
                            participates[c] = any = true;
                    }

                    return any ? participates : null;
                }
                catch
                {
                    // A face that will not shape is a face with no ligatures as far as this goes.
                    // Drawing per cell is both the safe answer and the existing one.
                    return null;
                }
            });
        }

        /// <summary>
        /// The glyphs a run of text shapes to, one per character, or null when that does not hold.
        /// </summary>
        /// <remarks>
        /// <para>One glyph per character is the assumption the caller draws on: it puts glyph
        /// <c>i</c> in cell <c>i</c>, which is what keeps the grid exact and costs no arithmetic.
        /// True of the contextual-alternate form every programming font uses.</para>
        /// <para>Null when it is not true — a face using real N-to-1 ligature substitution, or a run
        /// the shaper decomposed. The caller then falls back to drawing that run per cell, which
        /// loses the ligature and keeps the grid. Losing the grid is much the worse failure: every
        /// column after the run would sit somewhere the application did not put it.</para>
        /// <para>Cached on the text and the face, because terminal output repeats enormously — a
        /// prompt, a diff, a log present the same short runs thousands of times — and shaping is far
        /// too expensive to repeat per frame.</para>
        /// </remarks>
        public ushort[]? ShapeRun(string text, string family, double fontSize, SnapshotFlags flags)
        {
            var face = Face(family, flags & (SnapshotFlags.Bold | SnapshotFlags.Italic));

            return _shapedRuns.GetOrAdd((text, face.Handle, (float)fontSize), key =>
            {
                try
                {
                    var font = For(family, fontSize, flags);
                    var shaper = _shapers.GetOrAdd(face, f => new SKShaper(f));
                    var shaped = shaper.Shape(text, font).Codepoints;

                    if (shaped.Length != text.Length)
                        return null;

                    var glyphs = new ushort[shaped.Length];
                    for (var i = 0; i < shaped.Length; i++)
                        glyphs[i] = (ushort)shaped[i];

                    return glyphs;
                }
                catch
                {
                    return null;
                }
            });
        }

        /// <summary>The face resolved for a codepoint, for tests that assert the resolution itself.</summary>
        internal SKTypeface? FallbackFace(int codePoint) => _fallback.GetOrAdd(codePoint, ResolveFallback);

        /// <summary>
        /// Whether a drawable glyph is held for this codepoint against this font, for tests that
        /// assert the cache is actually being populated. Nothing about the rendered output shows
        /// the difference between a cache that works and one that misses every time.
        /// </summary>
        internal bool HasCachedGlyph(int codePoint, SKFont font) =>
            _glyphBlobs.TryGetValue((font.Typeface.Handle, font.Size, codePoint), out var blob) && blob is not null;

        /// <summary>
        /// Which installed face can actually draw a codepoint the terminal font lacks.
        /// </summary>
        /// <remarks>
        /// <para><c>MatchCharacter</c> alone is not enough, and the case it fails is the one people
        /// notice. A Nerd Font symbol lives in the private use area, which by definition carries no
        /// Unicode meaning — so the system matcher has nothing to reason from and answers
        /// <c>.LastResort</c>, Apple's placeholder face. Measured on this machine: every Nerd Font
        /// codepoint probed came back as <c>.LastResort</c> while <c>Cascadia Code NF</c>, installed
        /// and holding all of them, sat right there. A powerline prompt drew a row of boxes with the
        /// font that could draw it one lookup away.</para>
        /// <para>And <c>.LastResort</c> reports <c>ContainsGlyph</c> as TRUE for anything asked of
        /// it — that is what it is for — so it cannot be detected by asking whether it has the glyph.
        /// It has to be recognised by name.</para>
        /// <para>Then the installed families are searched. Nerd Font builds first, because that is
        /// what this exists for and it usually ends the search immediately; everything else after,
        /// since a private-use codepoint could come from anywhere. Around 17ms with an early hit and
        /// 51ms exhaustive across 183 families here — paid once per distinct codepoint and then
        /// cached forever, so a prompt costs a handful of frames once and never again.</para>
        /// </remarks>
        private static SKTypeface? ResolveFallback(int codePoint)
        {
            var matched = SKFontManager.Default.MatchCharacter(codePoint);

            if (matched is not null && !IsLastResort(matched))
                return matched;

            return SearchFamilies(codePoint) ?? matched;
        }

        /// <summary>
        /// The one-character string for a codepoint, built once and kept. Shared across caches and
        /// threads because it is immutable and tiny, and because a terminal's alphabet is small.
        /// </summary>
        private static readonly ConcurrentDictionary<int, string> _texts = new();

        private static string TextFor(int codePoint, ReadOnlySpan<char> span)
            => _texts.TryGetValue(codePoint, out var text) ? text : _texts[codePoint] = span.ToString();

        /// <summary>
        /// The placeholder face a matcher returns when it has nothing: it draws a box for every
        /// codepoint and claims to contain them all, so only its name gives it away.
        /// </summary>
        private static bool IsLastResort(SKTypeface face) =>
            face.FamilyName is null
            || face.FamilyName.StartsWith('.')
            || face.FamilyName.Contains("LastResort", StringComparison.OrdinalIgnoreCase);

        /// <summary>Every installed family, Nerd Font builds first, until one has the glyph.</summary>
        private static SKTypeface? SearchFamilies(int codePoint)
        {
            try
            {
                var families = SKFontManager.Default.FontFamilies.ToArray();

                foreach (var family in families)
                {
                    if (LooksLikeNerdFont(family))
                    {
                        var face = SKTypeface.FromFamilyName(family);
                        if (face is not null && !IsLastResort(face) && face.ContainsGlyph(codePoint))
                            return face;

                        // Every rejected candidate is dropped here rather than left for a
                        // finaliser: this walks every installed family, so a miss on a machine with
                        // hundreds of fonts held hundreds of native faces alive for nothing.
                        face?.Dispose();
                    }
                }

                foreach (var family in families)
                {
                    if (LooksLikeNerdFont(family))
                        continue;   // already tried

                    var face = SKTypeface.FromFamilyName(family);
                    if (face is not null && !IsLastResort(face) && face.ContainsGlyph(codePoint))
                        return face;

                    // Same as the Nerd Font sweep above: a rejected candidate is dropped now.
                    // This loop walks EVERY remaining family, so it is the bigger leak of the two.
                    face?.Dispose();
                }
            }
            catch
            {
                // Enumerating the system's fonts is not something to fail a frame over.
            }

            return null;
        }

        /// <summary>
        /// Whether a family name suggests a Nerd Font patch.
        /// </summary>
        /// <remarks>
        /// A hint for search ORDER, never a filter: a name that fools this only changes which face is
        /// asked first, and a Nerd Font named unusually is still found by the sweep that follows.
        /// </remarks>
        private static bool LooksLikeNerdFont(string family) =>
            family.Contains("Nerd", StringComparison.OrdinalIgnoreCase)
            || family.EndsWith(" NF", StringComparison.OrdinalIgnoreCase)
            || family.EndsWith("NerdFont", StringComparison.OrdinalIgnoreCase);

        /// <summary>A sized font for a face this cache did not create, kept rather than rebuilt.</summary>
        private SKFont FallbackFont(SKTypeface face, float size) =>
            _fallbackFonts.GetOrAdd((face.Handle, size), key => new SKFont(face, key.Size) { Subpixel = true });

        /// <summary>
        /// One <see cref="SKImage"/> per terminal image, uploaded on first sight and reused.
        ///
        /// Keyed weakly on the image, the same way the DrawingContext path keys its bitmaps: the
        /// upload dies with the picture, with no eviction list to keep in step with a scrolling
        /// buffer. The SKImage's native memory is released by its finalizer — acceptable for
        /// pictures, which are few, where it would not be for glyphs.
        /// </summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<XTerm.Graphics.TerminalImage, SKImage> _images = new();

        /// <summary>
        /// The uploaded form of <paramref name="image"/>. The decoder writes BGRA — see
        /// SixelDecoder, which stores B, G, R in that order — so the copy says so.
        /// </summary>
        public SKImage? Upload(XTerm.Graphics.TerminalImage image)
        {
            if (_images.TryGetValue(image, out var existing))
                return existing;

            var info = new SKImageInfo(image.PixelWidth, image.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            // The span overload copies before returning, so no pin has to outlive this call --
            // and no unsafe block, which the fork's version needed only because it predated the
            // Span overloads.
            var uploaded = SKImage.FromPixelCopy(info, image.Pixels.Span, image.Stride);

            if (uploaded is not null)
                _images.Add(image, uploaded);

            return uploaded;
        }

        /// <summary>
        /// Draw operations currently inside this cache, and whether disposal is owed once they
        /// leave. The cache is disposed from the UI thread while the render thread may be halfway
        /// through a composite that holds it: freeing an SKFont there is a native access violation,
        /// not a managed exception, so the last one out does the freeing.
        /// </summary>
        private int _inFlight;
        private int _disposeRequested;

        /// <summary>
        /// Marks a draw as using this cache. Returns false when the cache is already being disposed,
        /// in which case the caller must draw nothing.
        /// </summary>
        internal bool TryEnter()
        {
            Interlocked.Increment(ref _inFlight);

            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                Leave();
                return false;
            }

            return true;
        }

        /// <summary>Ends what <see cref="TryEnter"/> began, freeing the cache if it is owed.</summary>
        internal void Leave()
        {
            if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _disposeRequested) != 0)
                Free();
        }

        public void Dispose()
        {
            // Announce first, so a draw that has not started yet declines, then free only if no
            // draw is inside. One that IS inside frees on its way out.
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
                return;

            if (Volatile.Read(ref _inFlight) == 0)
                Free();
        }

        private void Free()
        {
            // Only what this cache CREATED is disposed.
            //
            // SKTypeface factories hand back shared instances — FromFamilyName, CreateDefault and
            // MatchCharacter all return the same object for the same request, and Avalonia's own Skia
            // backend asks for the same faces. Disposing one here would pull it out from under a
            // second terminal, or under Avalonia. The font manager owns them; this cache only
            // remembers them.
            foreach (var shaper in _shapers.Values) shaper.Dispose();
            foreach (var font in _fonts.Values) font.Dispose();
            foreach (var font in _fallbackFonts.Values) font.Dispose();
            foreach (var blob in _glyphBlobs.Values) blob?.Dispose();

            _shapers.Clear();
            _fonts.Clear();
            _fallbackFonts.Clear();
            _faces.Clear();
            _fallback.Clear();
            _baselines.Clear();
            _shapedRuns.Clear();
            _ligatureAlphabet.Clear();
            _glyphBlobs.Clear();
            _images.Clear();
        }
    }
}
