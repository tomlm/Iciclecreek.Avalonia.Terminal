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
/// <para>On by default here; set <c>PTY_TRACE=0</c> to turn it off. It writes every byte a program emits, which
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
                AttachSends(control, gate, txt);
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
        AttachSends(control, gate, txt);

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
    private static void AttachSends(TerminalControl control, object gate, string txt)
    {
        // Everything the terminal sends the process, which is the half that matters here. Focus and mouse
        // reports never pass through the emulator's DataReceived, so watching only that misses them — and
        // a focus report landing mid-startup is exactly the kind of thing a TUI repaints for.
        control.InputSent += (_, data) =>
        {
            lock (gate)
                File.AppendAllText(txt, $"\n    <<< WE SENT: {Annotate(data)}{Explain(data)}\n");
        };

        // The size we tell the process is the other thing it decides on. An app that writes exactly one
        // screen-width and lets the cursor wrap is trusting our column count; if that count moves under it
        // mid-startup, every row after the first lands somewhere it did not intend.
        try
        {
            var terminal = control.Terminal;

            lock (gate)
                File.AppendAllText(txt, $"\n    ### SIZE AT ATTACH: {terminal.Cols}x{terminal.Rows}\n");

            terminal.Resized += (_, e) =>
            {
                lock (gate)
                    File.AppendAllText(txt, $"\n    ### RESIZED to {e.Cols}x{e.Rows} <<< the process is told this\n");
            };
        }
        catch (Exception ex)
        {
            // Logged rather than swallowed. A silently skipped hook reads as "nothing happened", which is
            // the opposite of what it means, and that already cost one round of this investigation.
            lock (gate)
                File.AppendAllText(txt, $"\n    ### SIZE HOOK FAILED: {ex.GetType().Name} {ex.Message}\n");
        }
    }

    /// <summary>Name the things we send that a program is likely to act on.</summary>
    private static string Explain(string data) => data switch
    {
        "\u001b[I" => "        ^^^ FOCUS GAINED — a TUI repaints for this",
        "\u001b[O" => "        ^^^ FOCUS LOST — a TUI repaints for this",
        _ => "",
    };

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
