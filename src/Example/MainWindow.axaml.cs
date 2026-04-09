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
            Term1.IsVisible = true;
            await Term1.LaunchProcess();
            Term1.Focus();
        }
    }
}