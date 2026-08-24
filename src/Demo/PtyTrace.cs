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
        ("\u001b[6n",     "DSR — asking WHERE THE CURSOR IS; our answer decides what it draws next"),
        ("\u001b[48;2;255;255;255m", "background set to RGB WHITE"),
        ("\u200d",    "ZWJ in the output — a joined emoji, almost certainly a width probe"),
    };

    /// <summary>
    /// On by default in the demo. An opt-in variable is one more thing to get wrong — it has to reach the
    /// process, which it does not when the app is launched from an IDE rather than the shell that set it,
    /// and the cost of finding that out is a lost reproduction of an intermittent fault. Set PTY_TRACE=0
    /// to turn it off.
    /// </summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("PTY_TRACE") is not "0";

    /// <summary>Attach to a TerminalWindow. Returns the trace directory, or null when tracing is off.</summary>
    public static string? Attach(TerminalWindow window, string name)
    {
        if (!Enabled)
            return null;

        var (dir, bin, txt) = Files(name);

        // On the read task, so what is recorded is what arrived and in the order it arrived — marshalling to
        // the UI thread first would reorder relative to the writes that provoked it.
        window.OutputReceivedOnReadTask = true;

        var gate = new object();
        window.OutputReceived += (_, e) => Record(gate, bin, txt, e.Output);

        // The window builds its control during initialisation, so by the time a caller can attach a trace
        // it exists — but it is reached through the visual tree, and that is only realised once shown.
        window.Opened += (_, _) =>
        {
            if (window.Content is TerminalControl control)
                AttachReplies(control, gate, txt);
        };

        return dir;
    }

    /// <summary>Attach to a TerminalControl, for hosts that do not re-raise the event themselves.</summary>
    public static string? Attach(TerminalControl? control, string name)
    {
        if (!Enabled || control == null)
            return null;

        var (dir, bin, txt) = Files(name);

        control.OutputReceivedOnReadTask = true;

        var gate = new object();
        control.OutputReceived += (_, e) => Record(gate, bin, txt, e.Output);
        AttachReplies(control, gate, txt);

        return dir;
    }

    /// <summary>
    /// Also record what the TERMINAL answers — cursor position reports, device attributes, colour queries.
    /// </summary>
    /// <remarks>
    /// One direction of a conversation is not enough to diagnose one. A program that probes the terminal and
    /// then behaves differently is deciding on OUR answer, and a trace that only holds its side shows the
    /// decision without the thing that caused it.
    /// </remarks>
    private static void AttachReplies(TerminalControl control, object gate, string txt)
    {
        try
        {
            control.Terminal.DataReceived += (_, e) =>
            {
                lock (gate)
                    File.AppendAllText(txt, $"\n    <<< WE REPLIED: {Annotate(e.Data)}\n");
            };
        }
        catch
        {
            // The emulator is built during initialisation; if it is not there yet this is simply skipped.
            // A missing reply log is worth less than a demo that will not start.
        }
    }

    /// <summary>Where this run's two files go. Stamped to the second so repeated launches do not merge.</summary>
    private static (string Dir, string Bin, string Txt) Files(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pty-trace");
        Directory.CreateDirectory(dir);

        var stamp = DateTime.Now.ToString("HHmmss");
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return (dir, Path.Combine(dir, $"{safe}-{stamp}.bin"), Path.Combine(dir, $"{safe}-{stamp}.txt"));
    }

    private static void Record(object gate, string bin, string txt, string chunk)
    {
        lock (gate)
        {
            File.AppendAllText(bin, chunk);
            File.AppendAllText(txt, Annotate(chunk));
        }
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
