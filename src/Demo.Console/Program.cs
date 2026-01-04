using Avalonia;
using Consolonia;

namespace Demo.Console
{
    public static class Program
    {
       [STAThread]
        private static void Main(string[] args)
        {
            BuildAvaloniaApp()
                .StartWithConsoleLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UseConsolonia()
                .UseAutoDetectedConsole()
                .LogToException()
                .WithDeveloperTools(); ;
        }
    }
}