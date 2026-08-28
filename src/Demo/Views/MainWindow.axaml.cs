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


    // ---- ManagedTerminalWindow: hosted inside the WindowsPanel ----------------------------------

    private void OnNewManagedClicked(object? sender, RoutedEventArgs e)
    {
        var maxWidth = (int)this.Bounds.Width / 2;
        var maxHeight = (int)this.Bounds.Height / 2;

        var terminalWindow = new ManagedTerminalWindow
        {
            Width = 80 * FontSize,
            Height = 25 * FontSize,
            Background = Avalonia.Media.Brushes.Black,
            Foreground = Avalonia.Media.Brushes.LightGray,
            CloseOnProcessExit = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(Random.Shared.Next(0, (int)this.Bounds.Width - maxWidth),
                                          Random.Shared.Next(0, (int)this.Bounds.Height - maxHeight))
        };
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
            Process = command.Value.Process,
            Args = command.Value.Args,
            Title = command.Value.Process,
            Background = Avalonia.Media.Brushes.Black,
            Foreground = Avalonia.Media.Brushes.LightGray,
            Width = 80 * FontSize,
            Height = 25 * FontSize,
            CloseOnProcessExit = true
        };
        Demo.PtyTrace.Attach(terminalWindow.Terminal, terminalWindow.Title ?? "managed");
        terminalWindow.Show(Windows);
    }

    // ---- TerminalWindow: a real top-level OS window ------------------------------------------------
    //
    // Kept alongside the managed one deliberately. TerminalWindow had no consumer anywhere in this repo
    // — the demo only ever used ManagedTerminalWindow — which is how it came to ship in a state where it
    // realised no terminal at all. Driving it from the demo means that class of defect is visible the
    // next time someone runs the app.

    private void OnNewTerminalWindowClicked(object? sender, RoutedEventArgs e)
    {
        var terminalWindow = new TerminalWindow
        {
            Title = "TerminalWindow",
            Width = 80 * FontSize,
            Height = 25 * FontSize,
            Background = Avalonia.Media.Brushes.Black,
            Foreground = Avalonia.Media.Brushes.LightGray,
            CloseOnProcessExit = true
        };
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
            Process = command.Value.Process,
            Args = command.Value.Args,
            Title = command.Value.Process,
            Background = Avalonia.Media.Brushes.Black,
            Foreground = Avalonia.Media.Brushes.LightGray,
            Width = 80 * FontSize,
            Height = 25 * FontSize,
            CloseOnProcessExit = true
        };

        // Recording what the process writes is on by default here; set PTY_TRACE=0 to turn it off.
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
            Width = 80 * FontSize,
            Height = 25 * FontSize,
            Background = Avalonia.Media.Brushes.Black,
            Foreground = Avalonia.Media.Brushes.LightGray,
            CloseOnProcessExit = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(Random.Shared.Next(0, (int)this.Bounds.Width - maxWidth),
                                          Random.Shared.Next(0, (int)this.Bounds.Height - maxHeight))
        };
        Demo.PtyTrace.Attach(terminalWindow.Terminal, terminalWindow.Title ?? "managed");
        terminalWindow.Show(Windows);
    }
}
