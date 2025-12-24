using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Demo.Views;

public partial class CommandLineDialog : Window
{
    public string? CommandLine { get; private set; }

    public CommandLineDialog()
    {
        InitializeComponent();
        this.CommandLineTextBox.Focus();
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
