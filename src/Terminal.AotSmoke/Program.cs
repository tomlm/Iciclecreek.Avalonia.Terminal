using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Media;
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
            var text = new StringBuilder();
            var buffer = control!.Terminal.Buffer;
            for (var i = 0; i < buffer.Length; i++)
                text.AppendLine(buffer.Lines[i]?.TranslateToString(true));
            if (!text.ToString().Contains(marker))
                throw new Exception("marker is not in the buffer: " + text.ToString().Trim());
            return "marker found";
        });

        await Step("VT parsing and rendering", async () =>
        {
            control!.Terminal.Write("\x1b[31mred\x1b[0m \x1b]0;title\x07 \x1b[?1049h alt \x1b[?1049l wide:\U0001F600 é\r\n");
            await Task.Delay(100);
            if (desktop)
                return "written; rendering is judged by looking at the window";

            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window!.CaptureRenderedFrame();
            if (frame is null)
                throw new Exception("no frame");
            return $"frame {frame.PixelSize}";
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

                var text = new StringBuilder();
                var buffer = shell.Terminal.Buffer;
                for (var i = 0; i < buffer.Length; i++)
                    text.AppendLine(buffer.Lines[i]?.TranslateToString(true));
                if (!text.ToString().Contains($"typed-{marker}"))
                    throw new Exception("input did not come back: " + text.ToString().Trim());
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
