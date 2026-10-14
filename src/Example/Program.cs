using Avalonia;
using System;

namespace Example
{
    internal class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // Turns on Avalonia's per-frame timing instruments, which the Render menu's timings read.
            AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", true);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        /// <summary>EXAMPLE_SOFTWARE=1 renders on the CPU instead of the GPU, for comparison.</summary>
        internal static bool SoftwareRendering => Environment.GetEnvironmentVariable("EXAMPLE_SOFTWARE") == "1";

        /// <summary>
        /// EXAMPLE_GPU_CACHE_MB sets the Skia GPU resource cache, the texture memory pictures are kept
        /// in between frames. Pictures that do not fit are evicted and uploaded again every frame.
        /// </summary>
        private static SkiaOptions SkiaOptionsFromEnvironment()
        {
            var options = new SkiaOptions();
            if (long.TryParse(Environment.GetEnvironmentVariable("EXAMPLE_GPU_CACHE_MB"), out long mb) && mb > 0)
                options.MaxGpuResourceSizeBytes = mb * 1024 * 1024;
            return options;
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(SkiaOptionsFromEnvironment())
                .With(SoftwareRendering
                    ? new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } }
                    : new Win32PlatformOptions())
                .WithInterFont()
                .LogToTrace();
    }
}
