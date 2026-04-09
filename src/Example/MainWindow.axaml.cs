using Avalonia.Controls;

namespace Example
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private async void OnStart(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Term2.IsVisible = true;
            await Term2.LaunchProcess();
            Term2.Focus();
        }

        private void Term2_ProcessExited(object? sender, Iciclecreek.Terminal.ProcessExitedEventArgs e)
        {
            // add a message box to show the exit code
            var messageBox = new Window
            {
                Title = "Process Exited",
                Content = new TextBlock { Text = $"Process exited with code {e.ExitCode}" },
                Width = 300,
                Height = 100
            };
            messageBox.ShowDialog(this);
        }
    }
}