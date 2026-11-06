using System.Text.RegularExpressions;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// <see cref="TerminalView.UrlRegex"/> is source-generated; it must match exactly what the runtime-compiled
/// regex it replaced matched.
/// </summary>
/// <remarks>
/// <para>The reference is a literal copy of the 4.1.0 declaration, NOT built from the view's own pattern --
/// comparing the view against itself could never fail.</para>
/// <para>An Avalonia test only because reading the property runs TerminalView's static constructor, which
/// builds a cursor and needs a platform.</para>
/// </remarks>
[TestFixture]
public class UrlRegexEquivalenceTests
{
    private static readonly Regex Reference = new(@"https?://[^\s<>""'`]+", RegexOptions.Compiled);

    private static readonly string[] Corpus =
    {
        // the lines the link tests write
        "see https://example.com now",
        "https://written.example",
        "https://example.com/deep",
        // trailing punctuation TrimUrlEnd decides about, which the regex must still hand it
        "see https://example.com.",
        "(see https://example.com)",
        "https://en.wikipedia.org/wiki/Foo_(bar)",
        "a, https://x.example/a,b; then",
        "done: https://x.example/?q=1&r=2#frag!",
        // what stops a match
        "<https://angle.example>",
        "\"https://quoted.example\" and 'https://single.example'",
        "`https://tick.example`",
        "https://tab.example\tafter",
        // several per line, both schemes, ports, userinfo, IPv6, non-ASCII
        "http://a.example https://b.example:8443/p http://user:pw@c.example/",
        "http://[::1]:8080/x https://exämple.example/päth",
        // near misses
        "ftp://nope.example https:/one-slash http:// HTTPS://UPPER.example",
        "",
    };

    [AvaloniaTest]
    public void The_generated_regex_is_declared_the_same()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TerminalView.UrlRegex.ToString(), Is.EqualTo(Reference.ToString()), "pattern");
            Assert.That(TerminalView.UrlRegex.Options, Is.EqualTo(Reference.Options), "options");
        });
    }

    [AvaloniaTest]
    public void The_generated_regex_matches_what_the_compiled_one_did()
    {
        Assert.Multiple(() =>
        {
            foreach (var line in Corpus)
            {
                var expected = Reference.Matches(line).Select(m => (m.Index, m.Value)).ToArray();
                var actual = TerminalView.UrlRegex.Matches(line).Select(m => (m.Index, m.Value)).ToArray();
                Assert.That(actual, Is.EqualTo(expected), $"on \"{line}\"");
            }
        });

        Assert.That(Corpus.Sum(line => Reference.Matches(line).Count), Is.GreaterThan(10),
            "sanity: the corpus actually exercises matches");
    }
}
