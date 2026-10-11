using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseProbe;

/// <summary>
/// A program that asks its terminal for mouse reports and prints every one it gets, with the
/// modifier bits decoded. It exists to check XTSHIFTESCAPE end to end: with <c>CSI &gt; 1 s</c> on, a
/// Shift+click must arrive here with the shift bit set instead of starting a text selection in the
/// host. Press <c>s</c> to toggle that mode and watch the same click go the other way.
/// </summary>
/// <remarks>
/// Run it inside a terminal — the Example app launches it — not with redirected input. Reports are
/// SGR encoded (DECSET 1006): <c>CSI &lt; b ; x ; y M</c> for a press, <c>m</c> for a release, where
/// <c>b</c> carries the button in bits 0–1 and Shift, Meta and Ctrl in bits 2, 3 and 4.
/// </remarks>
internal static class Program
{
    private const string Esc = "\u001b";

    private static bool _shiftEscape = true;

    private static int Main()
    {
        using var console = RawConsole.Enter();
        var output = Console.Out;

        output.Write($"{Esc}[?1000h{Esc}[?1006h");
        SetShiftEscape(output, _shiftEscape);
        output.WriteLine("MouseProbe: every mouse report the terminal sends is printed below.");
        output.WriteLine("Click, Shift+click and Ctrl+click anywhere.  s toggles XTSHIFTESCAPE, q quits.");
        output.WriteLine();
        output.Flush();

        var input = Console.OpenStandardInput();
        var pending = new StringBuilder();
        var buffer = new byte[256];

        while (true)
        {
            int read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;

            pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (!Drain(pending, output))
                break;
        }

        output.Write($"{Esc}[>0s{Esc}[?1006l{Esc}[?1000l");
        output.Flush();
        return 0;
    }

    /// <summary>Consume complete reports and keystrokes from the front of the buffer. False to quit.</summary>
    private static bool Drain(StringBuilder pending, TextWriter output)
    {
        while (pending.Length > 0)
        {
            var text = pending.ToString();

            if (text[0] == '\u001b')
            {
                // An SGR mouse report, or the start of one that has not all arrived yet.
                if (text.Length >= 3 && text[1] == '[' && text[2] == '<')
                {
                    int end = text.IndexOfAny(new[] { 'M', 'm' }, 3);
                    if (end < 0)
                        return true;    // wait for the rest

                    Report(text.AsSpan(3, end - 3), release: text[end] == 'm', output);
                    pending.Remove(0, end + 1);
                    continue;
                }

                if (text.Length < 3)
                    return true;

                // Some other escape sequence (a modified key, a focus report). Not what this is
                // for; drop it rather than echo it.
                pending.Remove(0, 1);
                continue;
            }

            switch (text[0])
            {
                case 'q':
                case '\u0003':
                    return false;

                case 's':
                    _shiftEscape = !_shiftEscape;
                    SetShiftEscape(output, _shiftEscape);
                    break;
            }

            pending.Remove(0, 1);
        }

        return true;
    }

    private static void SetShiftEscape(TextWriter output, bool on)
    {
        output.Write(on ? $"{Esc}[>1s" : $"{Esc}[>0s");
        output.WriteLine(on
            ? "XTSHIFTESCAPE on  (CSI > 1 s): Shift+click should be reported here, not select text."
            : "XTSHIFTESCAPE off (CSI > 0 s): Shift+click should select text in the host and not appear here.");
        output.Flush();
    }

    private static void Report(ReadOnlySpan<char> parameters, bool release, TextWriter output)
    {
        var parts = parameters.ToString().Split(';');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var b)
            || !int.TryParse(parts[1], out var col)
            || !int.TryParse(parts[2], out var row))
        {
            output.WriteLine($"  unparsed report: CSI < {parameters} {(release ? 'm' : 'M')}");
            output.Flush();
            return;
        }

        bool wheel = (b & 64) != 0;
        bool motion = (b & 32) != 0;
        string button = (b & 3) switch
        {
            0 => wheel ? "wheel-up" : "left",
            1 => wheel ? "wheel-down" : "middle",
            2 => "right",
            _ => "none",
        };
        string kind = release ? "release" : motion ? "motion " : "press  ";

        var mods = new List<string>();
        if ((b & 4) != 0) mods.Add("Shift");
        if ((b & 8) != 0) mods.Add("Meta");
        if ((b & 16) != 0) mods.Add("Ctrl");

        var shift = (b & 4) != 0 ? $"{Esc}[1;32mSHIFT{Esc}[0m " : "";
        output.WriteLine($"  {kind} {shift}{button,-10} col={col,-3} row={row,-3} modifiers={(mods.Count == 0 ? "none" : string.Join("+", mods))}  (b={b})");
        output.Flush();
    }
}

/// <summary>
/// Puts the console into the mode a raw-input program needs and restores it on dispose. On Windows that
/// means ENABLE_VIRTUAL_TERMINAL_INPUT so mouse reports arrive as bytes rather than being swallowed,
/// and no line buffering or echo. On Unix it is <c>stty raw -echo</c>.
/// </summary>
internal sealed class RawConsole : IDisposable
{
    private readonly Action _restore;

    private RawConsole(Action restore) => _restore = restore;

    public static RawConsole Enter()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return EnterWindows();

        var saved = Stty("-g")?.Trim();
        Stty("raw -echo");
        return new RawConsole(() => { if (!string.IsNullOrEmpty(saved)) Stty(saved); });
    }

    public void Dispose() => _restore();

    private static string? Stty(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/sh", $"-c \"stty {args} < /dev/tty\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            var text = p?.StandardOutput.ReadToEnd();
            p?.WaitForExit();
            return text;
        }
        catch
        {
            return null;
        }
    }

    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static RawConsole EnterWindows()
    {
        var stdin = GetStdHandle(StdInputHandle);
        var stdout = GetStdHandle(StdOutputHandle);

        bool haveIn = GetConsoleMode(stdin, out var inMode);
        bool haveOut = GetConsoleMode(stdout, out var outMode);

        if (haveIn)
        {
            var raw = (inMode & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput)) | EnableVirtualTerminalInput;
            SetConsoleMode(stdin, raw);
        }

        if (haveOut)
            SetConsoleMode(stdout, outMode | EnableVirtualTerminalProcessing);

        return new RawConsole(() =>
        {
            if (haveIn) SetConsoleMode(stdin, inMode);
            if (haveOut) SetConsoleMode(stdout, outMode);
        });
    }
}
