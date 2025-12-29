using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Iciclecreek.Avalonia.WindowManager;

namespace Demo.Views;

public partial class CommandLineDialog : ManagedWindow
{
    public string? CommandLine { get; private set; }

    public CommandLineDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        CommandLineTextBox.Focus();
    }

    private void OnStartClicked(object? sender, RoutedEventArgs e)
    {
        CommandLine = CommandLineTextBox.Text;
        if (!string.IsNullOrWhiteSpace(CommandLine))
        {
            Close(true);
        }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
