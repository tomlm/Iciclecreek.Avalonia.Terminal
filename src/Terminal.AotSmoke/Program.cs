using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using Terminal.AotSmoke;

// Usage: Terminal.AotSmoke [--host-include] [--desktop] [--hold <seconds>]
//
// Headless by default, so CI can run it. --desktop opens a real window on the real platform, and --hold keeps
// it up long enough to look at. Exits non-zero if any step failed; each step runs even if an earlier one did,
// so one failure does not hide the next.
internal static class Program
{
    // Avalonia's clipboard and drag-and-drop on Windows are COM, which needs this on the UI thread.
    [STAThread]
    public static int Main(string[] args)
    {
        var hostInclude = args.Contains("--host-include");
        var desktop = args.Contains("--desktop");
        var hold = Array.IndexOf(args, "--hold") is >= 0 and var at ? int.Parse(args[at + 1]) : 0;

        var builder = AppBuilder.Configure<SmokeApp>().UseSkia();
        builder = desktop
            ? builder.UsePlatformDetect()
            : builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        builder.SetupWithoutStarting();

        var finished = new CancellationTokenSource();
        var failures = 0;
        Dispatcher.UIThread.Post(async () =>
        {
            failures = await RunAsync(hostInclude, desktop, hold);
            finished.Cancel();
        });
        Dispatcher.UIThread.MainLoop(finished.Token);

        Console.WriteLine(failures == 0 ? "RESULT all passed" : $"RESULT {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private static string BufferText(TerminalControl control)
    {
        var text = new StringBuilder();
        var buffer = control.Terminal.Buffer;
        for (var i = 0; i < buffer.Length; i++)
            text.AppendLine(buffer.Lines[i]?.TranslateToString(true));
        return text.ToString();
    }

    // Red rather than merely "not black": the scrollbar and the cursor are in the frame whatever the text does.
    private static int CountRedPixels(WriteableBitmap frame)
    {
        using var pixels = frame.Lock();
        var (redOffset, blueOffset) = pixels.Format == PixelFormats.Rgba8888 ? (0, 2) : (2, 0);
        var count = 0;
        for (var y = 0; y < pixels.Size.Height; y++)
        {
            for (var x = 0; x < pixels.Size.Width; x++)
            {
                var at = (y * pixels.RowBytes) + (x * 4);
                int r = Marshal.ReadByte(pixels.Address, at + redOffset);
                int g = Marshal.ReadByte(pixels.Address, at + 1);
                int b = Marshal.ReadByte(pixels.Address, at + blueOffset);
                if (r >= 128 && r > g + 60 && r > b + 60)
                    count++;
            }
        }
        return count;
    }

    static async Task<int> RunAsync(bool hostInclude, bool desktop, int hold)
    {
        var app = Application.Current!;
        app.Styles.Add(new FluentTheme());
        if (hostInclude)
            app.Styles.Add(new HostInclude());

        var failures = 0;
        async Task Step(string name, Func<Task<string>> body)
        {
            try { Console.WriteLine($"PASS {name}: {await body()}"); }
            catch (Exception e) { failures++; Console.WriteLine($"FAIL {name}: {e}"); }
        }

        TerminalControl? control = null;
        Window? window = null;
        var exited = false;

        const string marker = "AOT_MARKER";
        await Step("construct TerminalControl", () =>
        {
            control = OperatingSystem.IsWindows()
                ? new TerminalControl { Process = "cmd.exe", ProcessArgs = ["/c", $"echo {marker}"] }
                : new TerminalControl { Process = "/bin/sh", ProcessArgs = ["-c", $"echo {marker}"] };
            control.ProcessExited += (_, _) => exited = true;
            return Task.FromResult("ok");
        });

        await Step("template applies", async () =>
        {
            window = new Window { Title = "Terminal.AotSmoke", Width = 800, Height = 500, Content = control };
            window.Show();
            await Task.Delay(100);
            window.UpdateLayout();
            var view = control!.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
            var bar = control!.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault();
            if (view is null || bar is null)
                throw new Exception($"template is missing parts: view={view is not null} bar={bar is not null}");
            return "view and scrollbar present";
        });

        await Step("process runs and its output reaches the buffer", async () =>
        {
            var clock = Stopwatch.StartNew();
            while (!exited && clock.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(20);
            if (!exited)
                throw new Exception("the process never reported exiting");

            await Task.Delay(100);
            var text = BufferText(control!);
            if (!text.Contains(marker))
                throw new Exception("marker is not in the buffer: " + text.Trim());
            return "marker found";
        });

        await Step("VT parsing and rendering", async () =>
        {
            // No emoji yet: a colour glyph has red in it, and this frame has to show that the SGR worked.
            control!.Terminal.Write("\x1b[31mred\x1b[0m \x1b]0;title\x07 \x1b[?1049h alt \x1b[?1049l\r\n");
            await Task.Delay(100);

            var text = BufferText(control);
            if (!text.Contains("red"))
                throw new Exception("written text is not in the buffer: " + text.Trim());
            if (text.Contains("[31m") || text.Contains("[?1049") || text.Contains("title"))
                throw new Exception("control sequences were printed instead of parsed: " + text.Trim());
            if (text.Contains(" alt "))
                throw new Exception("the alternate screen's text reached the normal buffer: " + text.Trim());

            var red = -1;
            if (!desktop)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window!.CaptureRenderedFrame();
                if (frame is null)
                    throw new Exception("no frame");
                red = CountRedPixels(frame);
                if (red == 0)
                    throw new Exception($"the {frame.PixelSize} frame has no red text in it");
            }

            control.Terminal.Write("wide:\U0001F600 e\u0301\r\n");
            await Task.Delay(100);
            if (!BufferText(control).Contains("wide:"))
                throw new Exception("text after the wide characters' line is not in the buffer");

            return desktop ? "parsed; rendering is judged by looking at the window" : $"{red} red pixels";
        });

        await Step("TerminalWindow can be created", () =>
            Task.FromResult(new TerminalWindow { Process = "" }.GetType().Name));

        if (desktop)
        {
            await Step("clipboard round trip", async () =>
            {
                var clipboard = window!.Clipboard ?? throw new Exception("no clipboard");
                await clipboard.SetTextAsync("aot-clipboard");
                // Several tries, because other programs open the clipboard the moment it changes, and a read that
                // lands in that window comes back null. Measured at about 6% straight after a write, none 25ms later.
                string? back = null;
                for (var attempt = 1; attempt <= 10; attempt++)
                {
                    back = await clipboard.TryGetTextAsync();
                    if (back == "aot-clipboard")
                        return $"ok on read {attempt}";
                    await Task.Delay(100);
                }
                throw new Exception($"read back '{back}'");
            });

            await Step("resolved font", () =>
                Task.FromResult(control!.GetVisualDescendants().OfType<TerminalView>().First().FontFamily.Name));

            await Step("a shell stays up and takes input", async () =>
            {
                var shell = OperatingSystem.IsWindows()
                    ? new TerminalControl { Process = "cmd.exe" }
                    : new TerminalControl { Process = "/bin/sh" };
                window!.Content = shell;
                await Task.Delay(1500);
                await shell.SendInputAsync($"echo typed-{marker}\r");
                await Task.Delay(1000);

                var text = BufferText(shell);
                if (!text.Contains($"typed-{marker}"))
                    throw new Exception("input did not come back: " + text.Trim());
                return "echoed";
            });

            if (hold > 0)
                await Task.Delay(TimeSpan.FromSeconds(hold));
        }

        Console.WriteLine("app styles: " + string.Join(",", app.Styles.Select(s => s.GetType().Name)));
        window?.Close();
        return failures;
    }
}

public class SmokeApp : Application;
