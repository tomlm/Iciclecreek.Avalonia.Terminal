using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Iciclecreek.Terminal;
using System;
using System.Collections.Generic;

namespace Demo.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The options that opt a demo terminal into every host-serviced feature. Reads are off by
    /// default for good reason; the demo turns them on so the tour can show the round trip.
    /// </summary>
    private static XTerm.Options.TerminalOptions DemoOptions() => new()
    {
        KittyNotificationsEnabled = true,
        PointerShapesEnabled = true,
        ClipboardReadEnabled = true,
        WindowOptions =
        {
            GetCellSizePixels = true,
            RaiseWin = true,
            RequestAttention = true,
        },
    };

    /// <summary>
    /// Makes the invisible features audible: notifications and attention requests have no
    /// on-screen form of their own, so they print to the console the demo was launched from.
    /// Run tools/demo-tour.sh inside any terminal window to exercise the lot.
    /// </summary>
    private static void WireHostSeams(global::Avalonia.Interactivity.Interactive terminalWindow)
    {
        terminalWindow.AddHandler(TerminalView.NotificationRequestedEvent, (_, e) =>
        {
            var n = e.Notification;
            Console.WriteLine($"[demo] NOTIFICATION  title={n.Title ?? "-"}  body={n.Body ?? "-"}  " +
                              $"text={n.Text}  id={n.Identifier ?? "-"}  urgency={n.Urgency?.ToString() ?? "-"}");
        });

        terminalWindow.AddHandler(TerminalView.AttentionRequestedEvent, (_, e) =>
            Console.WriteLine($"[demo] ATTENTION  action={e.Action}"));
    }


    // ---- ManagedTerminalWindow: hosted inside the WindowsPanel ----------------------------------

    private void OnNewManagedClicked(object? sender, RoutedEventArgs e)
    {
        var maxWidth = (int)this.Bounds.Width / 2;
        var maxHeight = (int)this.Bounds.Height / 2;

        var terminalWindow = new ManagedTerminalWindow
        {
            // A ligature face first in the chain, and the switch on -- this demo is where the
            // feature gets eyeballed. Cascadia MONO (the library default) has no ligatures by design.
            FontFamily = new Avalonia.Media.FontFamily("Cascadia Code,FiraCode Nerd Font,Cascadia Mono,Menlo,monospace"),
            Ligatures = true,
            Width = 80 * FontSize,
            Height = 25 * FontSize,
            Background = Avalonia.Media.Brushes.Black,
            Foreground = Avalonia.Media.Brushes.LightGray,
            CloseOnProcessExit = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(Random.Shared.Next(0, (int)this.Bounds.Width - maxWidth),
                                          Random.Shared.Next(0, (int)this.Bounds.Height - maxHeight))
        };
        terminalWindow.Options = DemoOptions();
        WireHostSeams(terminalWindow);
        Demo.PtyTrace.Attach(terminalWindow.Terminal, terminalWindow.Title ?? "managed");
        terminalWindow.Show(Windows);
    }

    private async void OnRunManagedClicked(object? sender, RoutedEventArgs e)
    {
        var command = await PromptForCommandLine();
        if (command == null)
            return;

        var terminalWindow = new ManagedTerminalWindow
        {
            // A ligature face first in the chain, and the switch on -- this demo is where the
            // feature gets eyeballed. Cascadia MONO (the library default) has no ligatures by design.
            FontFamily = new Avalonia.Media.FontFamily("Cascadia Code,FiraCode Nerd Font,Cascadia Mono,Menlo,monospace"),
            Ligatures = true,
            Process = command.Value.Process,
            ProcessArgs = command.Value.Args,
            Title = command.Value.Process,
            Background = Avalonia.Media.Brushes.Black,
            Foreground = Avalonia.Media.Brushes.LightGray,
            Width = 80 * FontSize,
            Height = 25 * FontSize,
            CloseOnProcessExit = true
        };
        terminalWindow.Options = DemoOptions();
        WireHostSeams(terminalWindow);
        Demo.PtyTrace.Attach(terminalWindow.Terminal, terminalWindow.Title ?? "managed");
        terminalWindow.Show(Windows);
    }

    // ---- TerminalWindow: a real top-level OS window ------------------------------------------------
    //
    // Kept alongside the managed one deliberately. TerminalWindow had no consumer anywhere in this repo
    // — the demo only ever used ManagedTerminalWindow — which is how it came to ship in a state where it
    // realised no terminal at all. Driving it from the demo means that class of defect is visible the
    // next time someone runs the app.

    /// <summary>
    /// The direct renderer with Avalonia controls composited over it. The terminal's cell grid is
    /// drawn by a Custom() Skia operation; the translucent panel, the live button and the border
    /// are ordinary retained-mode controls layered above it in the same display list. If the
    /// custom operation misbehaved -- drew over its bounds, or out of order -- this window is
    /// where it would show.
    /// </summary>
    private void OnNewSkiaCompositedClicked(object? sender, RoutedEventArgs e)
    {
        var terminal = new TerminalControl
        {
            FontFamily = new Avalonia.Media.FontFamily("Cascadia Code,FiraCode Nerd Font,Cascadia Mono,Menlo,monospace"),
            Ligatures = true,
            UseSkiaRenderer = true,
            Background = Avalonia.Media.Brushes.Black,
            Foreground = Avalonia.Media.Brushes.LightGray,
        };
        terminal.Options = DemoOptions();

        var clicks = 0;
        var button = new Button { Content = "Composited button — 0 clicks" };
        button.Click += (_, _) => button.Content = $"Composited button — {++clicks} clicks";

        var overlay = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0xAA, 0x20, 0x30, 0x40)),
            BorderBrush = Avalonia.Media.Brushes.CadetBlue,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(16),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Avalonia controls over UseSkiaRenderer.\nThe grid below is the Skia layer;\nthis panel is retained-mode, translucent,\nand the button is live.",
                        Foreground = Avalonia.Media.Brushes.White,
                    },
                    button,
                },
            },
        };

        var window = new Window
        {
            Title = "Skia renderer, composited",
            Width = 900,
            Height = 560,
            Content = new Grid { Children = { terminal, overlay } },
        };
        window.Show(this);
    }

    private void OnNewTerminalWindowClicked(object? sender, RoutedEventArgs e)
    {
        var terminalWindow = new TerminalWindow
        {
            // A ligature face first in the chain, and the switch on -- this demo is where the
            // feature gets eyeballed. Cascadia MONO (the library default) has no ligatures by design.
            FontFamily = new Avalonia.Media.FontFamily("Cascadia Code,FiraCode Nerd Font,Cascadia Mono,Menlo,monospace"),
            Ligatures = true,
            Title = "TerminalWindow",
            Width = 80 * FontSize,
            Height = 25 * FontSize,
            Background = Avalonia.Media.Brushes.Black,
            Foreground = Avalonia.Media.Brushes.LightGray,
            CloseOnProcessExit = true
        };
        terminalWindow.Options = DemoOptions();
        WireHostSeams(terminalWindow);
        Demo.PtyTrace.Attach(terminalWindow, terminalWindow.Title ?? "terminal");
        terminalWindow.Show();
    }

    private async void OnRunTerminalWindowClicked(object? sender, RoutedEventArgs e)
    {
        var command = await PromptForCommandLine();
        if (command == null)
            return;

        var terminalWindow = new TerminalWindow
        {
            // A ligature face first in the chain, and the switch on -- this demo is where the
            // feature gets eyeballed. Cascadia MONO (the library default) has no ligatures by design.
            FontFamily = new Avalonia.Media.FontFamily("Cascadia Code,FiraCode Nerd Font,Cascadia Mono,Menlo,monospace"),
            Ligatures = true,
            Process = command.Value.Process,
            ProcessArgs = command.Value.Args,
            Title = command.Value.Process,
            Background = Avalonia.Media.Brushes.Black,
            Foreground = Avalonia.Media.Brushes.LightGray,
            Width = 80 * FontSize,
            Height = 25 * FontSize,
            CloseOnProcessExit = true
        };

        // Recording what the process writes is on by default here; set PTY_TRACE=0 to turn it off.
        terminalWindow.Options = DemoOptions();
        WireHostSeams(terminalWindow);
        var traceDir = Demo.PtyTrace.Attach(terminalWindow, command.Value.Process);
        if (traceDir != null)
            terminalWindow.Title += $"  [tracing → {traceDir}]";

        terminalWindow.Show();
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Ask for a command line and split it, or null if the user cancelled or typed nothing.
    /// </summary>
    private async System.Threading.Tasks.Task<(string Process, List<string> Args)?> PromptForCommandLine()
    {
        var dialog = new CommandLineDialog();
        var result = await dialog.ShowDialog<bool?>(this);

        if (result != true || string.IsNullOrWhiteSpace(dialog.CommandLine))
            return null;

        var commandLine = dialog.CommandLine.Trim();
        var parts = ParseCommandLine(commandLine);
        var process = parts.Count > 0 ? parts[0] : commandLine;
        var args = parts.Count > 1 ? parts.GetRange(1, parts.Count - 1) : [];

        return (process, args);
    }

    private static List<string> ParseCommandLine(string commandLine)
    {
        var args = new List<string>();
        var current = "";
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (!string.IsNullOrEmpty(current))
                {
                    args.Add(current);
                    current = "";
                }
            }
            else
            {
                current += c;
            }
        }

        if (!string.IsNullOrEmpty(current))
        {
            args.Add(current);
        }

        return args;
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        var maxWidth = (int)this.Bounds.Width / 2;
        var maxHeight = (int)this.Bounds.Height / 2;

        var terminalWindow = new ManagedTerminalWindow
        {
            // A ligature face first in the chain, and the switch on -- this demo is where the
            // feature gets eyeballed. Cascadia MONO (the library default) has no ligatures by design.
            FontFamily = new Avalonia.Media.FontFamily("Cascadia Code,FiraCode Nerd Font,Cascadia Mono,Menlo,monospace"),
            Ligatures = true,
            Width = 80 * FontSize,
            Height = 25 * FontSize,
            Background = Avalonia.Media.Brushes.Black,
            Foreground = Avalonia.Media.Brushes.LightGray,
            CloseOnProcessExit = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(Random.Shared.Next(0, (int)this.Bounds.Width - maxWidth),
                                          Random.Shared.Next(0, (int)this.Bounds.Height - maxHeight))
        };
        terminalWindow.Options = DemoOptions();
        WireHostSeams(terminalWindow);
        Demo.PtyTrace.Attach(terminalWindow.Terminal, terminalWindow.Title ?? "managed");
        terminalWindow.Show(Windows);
    }
}
