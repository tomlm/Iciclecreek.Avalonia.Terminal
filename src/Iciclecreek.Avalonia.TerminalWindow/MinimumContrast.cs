using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// Enforces <c>MinimumContrastRatio</c>: when a program picks a foreground the user cannot read
    /// against the background it lands on, move the foreground toward black or white until they can.
    /// </summary>
    /// <remarks>
    /// <para>The option has existed on <c>TerminalOptions</c> since the port and nothing ever read
    /// it — like <c>DrawBoldTextInBrightColors</c> before it, the emulator carries the setting and
    /// has no renderer to apply it. The pain it exists for: a program that picks colours without
    /// knowing the theme — dark grey <c>SGR 38;5;236</c> on a dark background — produces text the
    /// user cannot read, and the terminal has no answer.</para>
    ///
    /// <para>The model is WCAG relative luminance and contrast ratio, matching xterm.js, from which
    /// the option takes its name and semantics: <c>1</c> means off (the default, and byte-identical
    /// to the behaviour before this existed), up to <c>21</c> forcing black-or-white. Only the
    /// FOREGROUND moves — the background is the theme's and stays. It moves toward whichever of
    /// black or white can actually reach the ratio, found by binary search on the blend.</para>
    ///
    /// <para>The adjustment runs on the per-cell render path, so it is cached by the resolved
    /// (foreground, background) pair. Colours arrive here AFTER bold-bright selection and the
    /// inverse swaps — the pair being tested is the pair being painted — and before dim, which is
    /// brush opacity and composites on top.</para>
    /// </remarks>
    internal sealed class MinimumContrast
    {
        /// <summary>
        /// Adjusted foreground per (fg, bg) pair. Bounded and cleared wholesale rather than evicted:
        /// a session uses a handful of pairs, and a palette animation that churns past the cap is
        /// better served by a cheap reset than by bookkeeping on the render path.
        /// </summary>
        private readonly Dictionary<ulong, uint> _cache = new();
        private double _ratio = 1;
        private const int MaxEntries = 1024;

        /// <summary>Points the enforcer at this frame's ratio, clearing the cache when it changed.</summary>
        /// <remarks>
        /// Sanitised here, at the one door a ratio comes in through: the option is a bare settable
        /// double, so a host can write anything into it. NaN means off — and it must be caught
        /// explicitly, because every comparison with it answers false, including the "did it
        /// change" one, which would otherwise leave a stale ratio in force forever. Everything
        /// else clamps to the 1..21 the model defines, 21 being black-on-white.
        /// </remarks>
        public void SnapshotRatio(double ratio)
        {
            ratio = double.IsNaN(ratio) ? 1 : Math.Clamp(ratio, 1, 21);

            if (Math.Abs(ratio - _ratio) > double.Epsilon)
            {
                _ratio = ratio;
                _cache.Clear();
            }
        }

        /// <summary>Whether the current ratio asks for anything at all.</summary>
        public bool Active => _ratio > 1;

        /// <summary>
        /// The foreground to paint <paramref name="fg"/> as, over <paramref name="bg"/>.
        /// </summary>
        /// <remarks>
        /// The foreground's ALPHA rides through untouched, and is part of the cache key. A
        /// translucent solid can reach this path — a translucent host background swapped into the
        /// foreground by inverse — and the contrast model has nothing to say about alpha: it moves
        /// a colour's channels, and how much of that colour the host wants shown is a separate,
        /// already-made decision. Keying on it too keeps two foregrounds that share RGB but not
        /// alpha from answering for each other.
        /// </remarks>
        public Color Apply(Color fg, Color bg)
        {
            if (!Active)
                return fg;

            var key = ((ulong)PackArgb(fg) << 24) | Pack(bg);
            if (_cache.TryGetValue(key, out var cached))
                return UnpackArgb(cached);

            var adjusted = Adjust(fg, bg, _ratio);
            adjusted = Color.FromArgb(fg.A, adjusted.R, adjusted.G, adjusted.B);

            if (_cache.Count >= MaxEntries)
                _cache.Clear();
            _cache[key] = PackArgb(adjusted);

            return adjusted;
        }

        private static Color Adjust(Color fg, Color bg, double minRatio)
        {
            var bgLum = Luminance(bg);
            if (ContrastRatio(Luminance(fg), bgLum) >= minRatio)
                return fg;

            // Move toward whichever endpoint can actually reach the ratio. White wins ties on dark
            // backgrounds and black on light ones, because that direction has the headroom.
            var towardWhite = ContrastRatio(1.0, bgLum);
            var towardBlack = ContrastRatio(0.0, bgLum);
            var target = towardWhite >= towardBlack ? Colors.White : Colors.Black;
            var best = towardWhite >= towardBlack ? towardWhite : towardBlack;

            // Even the endpoint cannot reach the ratio (a mid-grey background at 21): take the
            // endpoint, which is the closest the model allows.
            if (best < minRatio)
                return target;

            // Binary search the smallest blend that reaches the ratio, so the adjusted colour keeps
            // as much of the programme's hue as legibility permits.
            double lo = 0, hi = 1;
            for (var i = 0; i < 8; i++)
            {
                var mid = (lo + hi) / 2;
                var candidate = Blend(fg, target, mid);
                if (ContrastRatio(Luminance(candidate), bgLum) >= minRatio)
                    hi = mid;
                else
                    lo = mid;
            }

            return Blend(fg, target, hi);
        }

        /// <summary>WCAG relative luminance, over linearised sRGB.</summary>
        internal static double Luminance(Color c)
        {
            static double Lin(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        /// <summary>WCAG contrast ratio between two luminances, 1..21.</summary>
        internal static double ContrastRatio(double l1, double l2)
        {
            var (hi, lo) = l1 >= l2 ? (l1, l2) : (l2, l1);
            return (hi + 0.05) / (lo + 0.05);
        }

        private static Color Blend(Color from, Color to, double t) => Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));

        private static uint Pack(Color c) => ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        private static uint PackArgb(Color c) => ((uint)c.A << 24) | Pack(c);
        private static Color UnpackArgb(uint v)
            => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

        /// <summary>
        /// Codepoints exempt from adjustment: box drawing, block elements, and the Powerline glyphs.
        /// </summary>
        /// <remarks>
        /// The same carve-out xterm.js makes, for the same reason: these characters form visually
        /// CONNECTED shapes with their neighbours — a Powerline arrow whose fill is meant to equal
        /// the segment beside it, a block gradient — and "fixing" their contrast breaks the joint
        /// where two of them meet. Their legibility is not textual, so the rule that protects text
        /// vandalises them.
        /// </remarks>
        internal static bool IsExemptCodepoint(int cp) =>
            (cp >= 0x2500 && cp <= 0x259F)      // box drawing + block elements
            || (cp >= 0x25A0 && cp <= 0x25FF)   // geometric shapes, which Powerline fonts lean on
            || (cp >= 0xE0B0 && cp <= 0xE0BF);  // Powerline private-use arrows

        /// <summary>True when every character of a run is exempt, so the run stays untouched.</summary>
        /// <remarks>
        /// Per RUN rather than per cell, because a run shares one brush by construction. A run that
        /// MIXES text and box characters is adjusted: the text's legibility is the point of the
        /// feature, and a mixed run's box characters were already sharing the text's colour.
        /// </remarks>
        internal static bool IsExemptRun(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            for (var i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);

                if (!IsExemptCodepoint(cp) && !char.IsWhiteSpace((char)Math.Min(cp, 0xFFFF)))
                    return false;
            }

            return true;
        }
    }
}
