using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Terminal.AotSmoke;

public class HostInclude : Styles
{
    public HostInclude() => AvaloniaXamlLoader.Load(this);
}
