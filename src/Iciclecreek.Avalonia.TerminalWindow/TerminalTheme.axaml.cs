using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Iciclecreek.Terminal
{
    internal class TerminalTheme : Styles
    {
        public TerminalTheme() => AvaloniaXamlLoader.Load(this);
    }
}
