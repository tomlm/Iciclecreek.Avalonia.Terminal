using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Iciclecreek.Terminal;

namespace Demo;

/// <summary>
/// Records everything a process writes to the terminal, annotated, so an intermittent rendering fault can be
/// read after the fact instead of guessed at from a screenshot.
///
/// <para>Opt in with <c>set PTY_TRACE=1</c>. Off by default — this writes every byte a program emits, which
/// for a full-screen application is a lot of them.</para>
///
/// <para>Two files per terminal: a <c>.bin</c> holding the stream verbatim, and a <c>.txt</c> with escape
/// sequences named. The binary is the one that matters if we end up disagreeing about what arrived; the text
/// is for reading.</para>
/// </summary>
internal static class PtyTrace
{
    /// <summary>Sequences worth calling out by name, because each one can whiten a screen on its own.</summary>
    private static readonly (string Pattern, string Meaning)[] Notable =
    {
        ("[?5h",    "DECSCNM ON  — reverse video, WHOLE SCREEN inverted"),
        ("[?5l",    "DECSCNM off — reverse video cleared"),
        ("[7m",     "SGR 7 — inverse attribute ON"),
        ("[27m",    "SGR 27 — inverse attribute off"),
        ("[?1049h", "alternate buffer ON"),
        ("[?1049l", "alternate buffer off"),
        ("[2J",     "erase display (fills with the CURRENT attributes)"),
    };

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("PTY_TRACE") is { Length: > 0 } v && v != "0";

    /// <summary>
    /// Attach to a terminal. Returns the trace directory, or null when tracing is off.
    /// </summary>
    public static string? Attach(TerminalWindow window, string name)
    {
        if (!Enabled)
            return null;

        var dir = Path.Combine(Path.GetTempPath(), "pty-trace");
        Directory.CreateDirectory(dir);

        var stamp = DateTime.Now.ToString("HHmmss");
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        var bin = Path.Combine(dir, $"{safe}-{stamp}.bin");
        var txt = Path.Combine(dir, $"{safe}-{stamp}.txt");

        // On the read task, so what is recorded is what arrived and in the order it arrived — marshalling to
        // the UI thread first would reorder relative to the writes that provoked it.
        window.OutputReceivedOnReadTask = true;

        var gate = new object();
        window.OutputReceived += (_, e) =>
        {
            lock (gate)
            {
                File.AppendAllText(bin, e.Output);
                File.AppendAllText(txt, Annotate(e.Output));
            }
        };

        return dir;
    }

    /// <summary>Rewrite a chunk so escape sequences are visible, and flag the ones that can invert a screen.</summary>
    private static string Annotate(string chunk)
    {
        var sb = new StringBuilder();
        var flagged = new List<string>();

        foreach (var (pattern, meaning) in Notable)
        {
            int at = -1;
            while ((at = chunk.IndexOf(pattern, at + 1, StringComparison.Ordinal)) >= 0)
                flagged.Add($"    >>> {meaning}");
        }

        foreach (var ch in chunk)
        {
            sb.Append(ch switch
            {
                '' => "<ESC>",
                '' => "<BEL>",
                '\r' => "<CR>",
                '\n' => "<LF>\n",
                '\t' => "<TAB>",
                < ' ' => $"<{(int)ch:X2}>",
                _ => ch.ToString(),
            });
        }

        if (flagged.Count > 0)
            sb.Append('\n').Append(string.Join("\n", flagged)).Append('\n');

        return sb.ToString();
    }
}
