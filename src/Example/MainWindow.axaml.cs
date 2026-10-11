using Avalonia.Controls;
using Iciclecreek.Terminal;
using System;
using System.IO;
using System.Linq;

namespace Example
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // While XTSHIFTESCAPE is being verified the terminal runs the MouseProbe project instead of
            // a shell: it turns on mouse tracking and CSI > 1 s and prints every report it receives, so a
            // Shift+click either shows up there (claimed by the program) or selects text (claimed by the
            // host). Falls back to the default shell when the probe project is not alongside.
            if (FindMouseProbe() is { } probe)
            {
                Terminal.Process = "dotnet";
                Terminal.ProcessArgs = new[] { probe };
            }
        }

        /// <summary>
        /// The probe's built assembly: walks up from the executable to the repository's src folder and
        /// takes the newest build under MouseProbe/bin. Launched through dotnet rather than its apphost
        /// so the same code works on every platform.
        /// </summary>
        private static string? FindMouseProbe()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var bin = Path.Combine(dir.FullName, "MouseProbe", "bin");
                if (!Directory.Exists(bin))
                    continue;

                return Directory.EnumerateFiles(bin, "MouseProbe.dll", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }

            return null;
        }
    }
}
