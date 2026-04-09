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
    }
}