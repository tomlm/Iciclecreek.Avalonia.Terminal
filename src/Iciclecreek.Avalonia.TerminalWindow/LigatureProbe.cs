using System;
using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// Answers, per typeface, which ASCII characters participate in the font's ligatures — so the
    /// renderer knows which runs must reach the shaper for the <see cref="TerminalView.Ligatures"/>
    /// switch to mean anything, and which can keep the glyph-run fast path.
    /// </summary>
    /// <remarks>
    /// <para>WHY PROBING, not GSUB parsing: programming fonts implement ligatures as contextual
    /// alternates — <c>-&gt;</c> shapes to two glyphs that happen to draw as one arrow — so the
    /// only reliable oracle is the shaper itself. Shape every printable ASCII character alone,
    /// then every pair, and any character whose glyph changes in company participates. Pairs are
    /// not enough on their own: <c>ww</c> shapes to two ordinary w's in Fira Code AND Cascadia,
    /// and only <c>www</c> substitutes — so characters still clean after the pair pass get a
    /// three-of-a-kind probe. Still missed: a ligature needing three DIFFERENT characters with no
    /// two-character signal, which means parsing GSUB coverage tables — a great deal of OpenType
    /// parsing for a case yet to be seen.</para>
    /// <para>Paid once per typeface, on the first run that asks, and cached forever after. A font
    /// with no ligatures — most of them — returns null, and the caller then declines nothing.</para>
    /// </remarks>
    internal static class LigatureProbe
    {
        private const int First = ' ';
        private const int Last = '~';

        private static readonly ConcurrentDictionary<GlyphTypeface, bool[]?> _alphabets = new();

        /// <summary>
        /// The characters that participate in ligatures for this typeface, indexed by char code up
        /// to <c>'~'</c> — or null when the font has none, or shapes too strangely to trust.
        /// </summary>
        public static bool[]? Alphabet(Typeface typeface)
        {
            GlyphTypeface glyphTypeface;
            try
            {
                glyphTypeface = typeface.GlyphTypeface;
            }
            catch
            {
                // A font that cannot produce a glyph typeface takes the FormattedText path anyway;
                // there is nothing here to decide.
                return null;
            }

            return _alphabets.GetOrAdd(glyphTypeface, face =>
            {
                try
                {
                    var options = new TextShaperOptions(face, 16);
                    var shaper = TextShaper.Current;

                    var alone = new ushort[Last + 1];
                    for (var c = First; c <= Last; c++)
                    {
                        var one = shaper.ShapeText(((char)c).ToString().AsMemory(), options);
                        if (one.Length != 1)
                            return null;   // a face this odd is not one to guess about
                        alone[c] = one[0].GlyphIndex;
                    }

                    var participates = new bool[Last + 1];
                    var any = false;

                    for (var a = First; a <= Last; a++)
                    {
                        for (var b = First; b <= Last; b++)
                        {
                            var pair = shaper.ShapeText($"{(char)a}{(char)b}".AsMemory(), options);

                            if (pair.Length != 2)
                            {
                                // A real substitution rather than an alternate. Both characters
                                // are involved, even though a grid renderer will refuse to draw
                                // the N-to-1 form later.
                                participates[a] = participates[b] = any = true;
                                continue;
                            }

                            if (pair[0].GlyphIndex != alone[a]) participates[a] = any = true;
                            if (pair[1].GlyphIndex != alone[b]) participates[b] = any = true;
                        }
                    }

                    // And the three-of-a-kind forms, which a pair cannot reveal.
                    for (var c = First; c <= Last; c++)
                    {
                        if (participates[c])
                            continue;

                        var triple = shaper.ShapeText(new string((char)c, 3).AsMemory(), options);
                        if (triple.Length != 3 || triple[0].GlyphIndex != alone[c] || triple[1].GlyphIndex != alone[c])
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
        /// Whether any character of <paramref name="text"/> is named by <paramref name="alphabet"/> —
        /// i.e. whether this run could form a ligature and therefore must reach the shaper.
        /// </summary>
        public static bool ContainsCandidate(string text, bool[] alphabet)
        {
            foreach (var ch in text)
            {
                if (ch < alphabet.Length && alphabet[ch])
                    return true;
            }

            return false;
        }
    }
}
