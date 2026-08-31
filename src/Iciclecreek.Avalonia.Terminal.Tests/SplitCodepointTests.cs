using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// A multi-byte character split across two pty reads must still reach the buffer as one character.
///
/// <para>This is not a contrived case. A pty read returns whatever bytes are available when it returns,
/// so a boundary falls mid-character routinely on any output containing CJK, emoji or accented text —
/// and the larger the output, the more boundaries there are to land badly.</para>
///
/// <para>The view used to decode each read on its own with <c>Encoding.UTF8.GetString</c>, which cannot
/// see a sequence continuing into the next read: the trailing bytes decoded as replacement characters
/// and so did the leading bytes that followed, so one glyph became two pieces of mojibake. It now hands
/// the bytes to the parser, which holds a partial sequence until the next chunk completes it.</para>
/// </summary>
[TestFixture]
public class SplitCodepointTests
{
    private static Window Show(Control content)
    {
        var window = new Window { Width = 520, Height = 400, Content = content };
        window.Show();
        window.UpdateLayout();
        global::Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return window;
    }

    private static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out after {timeoutMs}ms waiting until {because}");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The first line's text, trailing blanks trimmed.
    ///
    /// Walks the whole line rather than a fixed number of columns: a wide glyph occupies two of them
    /// plus a zero-width spacer, so a column count is not a character count and stopping at one cuts
    /// the line short of what was written.
    /// </summary>
    private static string FirstLine(TerminalView view)
    {
        var line = view.Terminal.Buffer.Lines[view.Terminal.Buffer.YBase]!;
        var sb = new StringBuilder();
        for (var x = 0; x < line.Length; x++)
        {
            var cell = line[x];
            if (cell.Width == 0)
                continue;   // the spacer that follows a wide glyph
            sb.Append(string.IsNullOrEmpty(cell.Content) ? " " : cell.Content);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Every split point of a three-byte character, including the two that break it.</summary>
    [AvaloniaTest]
    public Task A_cjk_character_split_across_reads_still_arrives_whole() => Run(async () =>
    {
        var bytes = Encoding.UTF8.GetBytes("[世]");

        for (var split = 1; split < bytes.Length; split++)
        {
            var view = new TerminalView { Process = "" };
            var window = Show(view);

            var connection = new PushConnection();
            view.AttachConnection(connection);

            connection.Push(bytes[..split]);
            connection.Push(bytes[split..]);

            await WaitUntil(() => FirstLine(view) == "[世]", $"the glyph survived a split after byte {split}");

            Assert.That(FirstLine(view), Is.EqualTo("[世]"), $"split after byte {split} of {bytes.Length}");
            window.Close();
        }
    });

    /// <summary>The four-byte case, which is where emoji live.</summary>
    [AvaloniaTest]
    public Task An_emoji_split_across_reads_still_arrives_whole() => Run(async () =>
    {
        var bytes = Encoding.UTF8.GetBytes("[\U0001F600]");

        for (var split = 1; split < bytes.Length; split++)
        {
            var view = new TerminalView { Process = "" };
            var window = Show(view);

            var connection = new PushConnection();
            view.AttachConnection(connection);

            connection.Push(bytes[..split]);
            connection.Push(bytes[split..]);

            await WaitUntil(() => FirstLine(view) == "[\U0001F600]", $"the emoji survived a split after byte {split}");

            Assert.That(FirstLine(view), Is.EqualTo("[\U0001F600]"), $"split after byte {split} of {bytes.Length}");
            window.Close();
        }
    });

    /// <summary>
    /// A subscriber gets the raw bytes, so it can decode across chunks itself. Output on a chunk that
    /// ends mid-character cannot be right and does not claim to be — Bytes is the form that survives.
    /// </summary>
    [AvaloniaTest]
    public Task A_subscriber_can_reassemble_a_split_character_from_the_bytes() => Run(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = Show(view);

        var collected = new List<byte>();
        view.OutputReceived += (_, e) => { lock (collected) collected.AddRange(e.Bytes.ToArray()); };

        var connection = new PushConnection();
        view.AttachConnection(connection);

        var bytes = Encoding.UTF8.GetBytes("世界");
        connection.Push(bytes[..2]);
        connection.Push(bytes[2..]);

        await WaitUntil(() => { lock (collected) return collected.Count >= bytes.Length; }, "both chunks arrived");

        byte[] all;
        lock (collected) all = collected.ToArray();
        Assert.That(Encoding.UTF8.GetString(all), Is.EqualTo("世界"), "the bytes concatenate back to the original");

        window.Close();
    });

    private static Task Run(Func<Task> body) => body();
}
