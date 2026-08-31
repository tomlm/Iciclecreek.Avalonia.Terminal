using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    /// <para>Probed once per typeface, OFF the UI thread: the full scan is ~9,400 shapes, which
    /// is far too much to pay inside a paint. The first ask kicks the probe off in the background
    /// and reports "not known yet"; the caller keeps the fast path — drawing exactly as it would
    /// with ligatures off — and is told when the answer arrives so it can invalidate. A font with
    /// no ligatures — most of them — resolves to null and nothing needed invalidating. Shaping off
    /// the UI thread is safe for the same reason Avalonia's own render thread may shape while the
    /// UI thread measures: HarfBuzz objects are immutable once created, and each ShapeText call
    /// builds its own buffer.</para>
    /// </remarks>
    internal static class LigatureProbe
    {
        private const int First = ' ';
        private const int Last = '~';

        private static readonly ConcurrentDictionary<GlyphTypeface, bool[]?> _alphabets = new();
        private static readonly ConcurrentDictionary<GlyphTypeface, List<Action<bool[]?>>> _waiters = new();

        /// <summary>Test seam: replaces the real shaping probe, which tests cannot hold still.</summary>
        internal static Func<GlyphTypeface, bool[]?>? ProbeOverride;

        /// <summary>
        /// Test seam: forgets every cached answer. The headless font manager can hand different
        /// FontFamily names the same glyph typeface, so without this a test inherits what an
        /// earlier one cached.
        /// </summary>
        internal static void ResetForTests()
        {
            _alphabets.Clear();
            _waiters.Clear();
        }

        /// <summary>
        /// The characters that participate in ligatures for this typeface, indexed by char code up
        /// to <c>'~'</c> — null when the font has none, or shapes too strangely to trust.
        /// </summary>
        /// <returns>
        /// True when the answer is known and in <paramref name="alphabet"/>. False while the probe
        /// is still running in the background — the caller should behave as if the font had no
        /// ligatures, and <paramref name="whenKnown"/> fires (once, on an arbitrary thread) when
        /// the real answer lands, but only if that answer is one the caller would act on.
        /// </returns>
        public static bool TryGetAlphabet(Typeface typeface, Action<bool[]?> whenKnown, out bool[]? alphabet)
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
                alphabet = null;
                return true;
            }

            if (_alphabets.TryGetValue(glyphTypeface, out alphabet))
                return true;

            // EVERY waiter is remembered, not just the one whose ask started the probe: two views
            // sharing a face both cache unshaped runs while the probe runs, so both must hear the
            // answer or the loser keeps displaying unligated text until its lines happen to
            // change. Deduplicated by delegate equality, because a view asks once per run it
            // builds and its callback is a stable instance -- see the caller.
            var waiters = _waiters.GetOrAdd(glyphTypeface, _ => new List<Action<bool[]?>>());
            var startProbe = false;
            lock (waiters)
            {
                // Re-checked under the lock: the probe can complete between the read above and
                // here, and a callback registered after completion would never fire.
                if (_alphabets.TryGetValue(glyphTypeface, out alphabet))
                    return true;

                startProbe = waiters.Count == 0;
                if (!waiters.Contains(whenKnown))
                    waiters.Add(whenKnown);
            }

            if (startProbe)
            {
                var face = glyphTypeface;
                Task.Run(() =>
                {
                    var result = ProbeOverride?.Invoke(face) ?? Probe(face);

                    Action<bool[]?>[] toCall;
                    lock (waiters)
                    {
                        // The answer is published INSIDE the lock, so a late registrant either
                        // sees it in its own locked re-check or made it into this snapshot.
                        _alphabets[face] = result;
                        _waiters.TryRemove(face, out _);
                        toCall = waiters.ToArray();
                        waiters.Clear();
                    }

                    // Only a real alphabet is announced: a null one changes nothing anyone drew.
                    if (result is not null)
                    {
                        foreach (var callback in toCall)
                            callback(result);
                    }
                });
            }

            alphabet = null;
            return false;
        }

        private static bool[]? Probe(GlyphTypeface face)
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
                        if (triple.Length != 3 || triple[0].GlyphIndex != alone[c] || triple[1].GlyphIndex != alone[c]
                            || triple[2].GlyphIndex != alone[c])
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
