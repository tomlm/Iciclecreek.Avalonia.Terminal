using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The Ligatures switch, and the probe that decides which runs it applies to.
/// </summary>
/// <remarks>
/// <para>Asserted on the machinery rather than on pixels, for the same reason FontFallbackTests is:
/// whether an arrow actually joins depends on the installed font, which a test cannot rely on. What
/// CAN be pinned headless is the shape of the decisions — who participates, what a null alphabet
/// declines, what flipping the switch invalidates — and that is where the defects would live.</para>
/// </remarks>
[TestFixture]
public class LigatureTests
{
    // ---- the candidate test, on a hand-made alphabet --------------------------------------------

    private static bool[] AlphabetOf(params char[] chars)
    {
        var alphabet = new bool['~' + 1];
        foreach (var c in chars)
            alphabet[c] = true;
        return alphabet;
    }

    [Test]
    public void A_run_containing_a_participating_character_is_a_candidate()
    {
        var alphabet = AlphabetOf('-', '>', '=');

        Assert.That(LigatureProbe.ContainsCandidate("a -> b", alphabet), Is.True);
        Assert.That(LigatureProbe.ContainsCandidate("x == y", alphabet), Is.True);
    }

    [Test]
    public void A_run_of_uninvolved_characters_is_not()
    {
        var alphabet = AlphabetOf('-', '>', '=');

        Assert.That(LigatureProbe.ContainsCandidate("plain words only", alphabet), Is.False);
    }

    [Test]
    public void Characters_beyond_ascii_never_index_out_of_the_alphabet()
    {
        var alphabet = AlphabetOf('-');

        // A CJK or emoji character is far past the table. It must be treated as uninvolved,
        // not throw.
        Assert.That(LigatureProbe.ContainsCandidate("世界 \U0001F600", alphabet), Is.False);
    }

    // ---- the probe under the headless shaper ----------------------------------------------------

    /// <summary>
    /// The headless platform shapes one glyph per character with no substitutions, which is
    /// exactly what a real font without ligatures looks like — so the probe must resolve to null,
    /// and the renderer then declines nothing and keeps the fast path everywhere. This is also the
    /// safety property: a face the probe cannot read must never slow the terminal down. The first
    /// ask reports not-known (the probe runs in the background, never on the asking thread) and
    /// later asks converge on the answer.
    /// </summary>
    [AvaloniaTest]
    public void A_face_without_ligatures_resolves_to_no_alphabet()
    {
        var typeface = new Typeface(TerminalView.DefaultFontFamily);

        bool[]? alphabet = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!LigatureProbe.TryGetAlphabet(typeface, _ => { }, out alphabet))
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("the probe never resolved");
            Thread.Sleep(10);
        }

        Assert.That(alphabet, Is.Null);
    }

    /// <summary>
    /// Every caller waiting on a probe hears the answer, not just the one whose ask started it.
    /// Two views sharing a face both cache unshaped runs while the probe runs; if only the winner
    /// were invalidated, the other would keep displaying unligated text until its lines happened
    /// to change. The probe is held still with the test override so both registrations
    /// deterministically land mid-flight.
    /// </summary>
    [AvaloniaTest]
    public void Every_waiter_hears_the_alphabet_not_just_the_first()
    {
        var gate = new ManualResetEventSlim(false);
        var alphabet = new bool['~' + 1];
        alphabet['-'] = alphabet['>'] = true;

        LigatureProbe.ProbeOverride = _ => { gate.Wait(TimeSpan.FromSeconds(10)); return alphabet; };
        LigatureProbe.ResetForTests();
        try
        {
            // Distinct faces per test run are not available headless, so distinct CALLBACKS carry
            // the point: register two while the probe is held open.
            var typeface = new Typeface(new global::Avalonia.Media.FontFamily("every-waiter-test-face"));

            var first = new ManualResetEventSlim(false);
            var second = new ManualResetEventSlim(false);

            Assert.That(LigatureProbe.TryGetAlphabet(typeface, _ => first.Set(), out _), Is.False,
                "the first ask starts the probe and reports not-known");
            Assert.That(LigatureProbe.TryGetAlphabet(typeface, _ => second.Set(), out _), Is.False,
                "a second caller lands while the probe is in flight");

            gate.Set();

            Assert.That(first.Wait(TimeSpan.FromSeconds(10)), Is.True, "the starter was told");
            Assert.That(second.Wait(TimeSpan.FromSeconds(10)), Is.True,
                "the caller that did NOT start the probe was told too");

            Assert.That(LigatureProbe.TryGetAlphabet(typeface, _ => { }, out var resolved), Is.True);
            Assert.That(resolved, Is.SameAs(alphabet));
        }
        finally
        {
            LigatureProbe.ProbeOverride = null;
            gate.Set();
            LigatureProbe.ResetForTests();
        }
    }

    // ---- the switch -----------------------------------------------------------------------------

    [AvaloniaTest]
    public void Ligatures_default_off()
    {
        var view = new TerminalView { Process = "" };

        Assert.That(view.Ligatures, Is.False);
    }

    /// <summary>
    /// Flipping the switch must reach lines that never change again. The cached runs are replayed,
    /// not rebuilt, so without the purge the new setting would apply only to lines that happen to
    /// be rewritten afterwards — which looks like a switch that half worked.
    /// </summary>
    [AvaloniaTest]
    public void Flipping_the_switch_purges_every_cached_run()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        try
        {
            view.Terminal.Write("one line\r\nand another");

            var line = view.Terminal.Buffer.Lines[0]!;
            line.Cache = new object();

            view.Ligatures = true;

            Assert.That(line.Cache, Is.Null, "the cached runs were built under the old setting");
        }
        finally
        {
            window.Close();
        }
    }
}
