using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Iciclecreek.Terminal.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// ONE Application + Dispatcher for the whole assembly.
//
// The default is PerTest, and Avalonia's own DispatchCore documentation is explicit that a dispatch "creates a
// new application instance, setting app avalonia services" — so the default runs the entire application
// construction path once per test. That path (the compositor hooking the render loop) is racy: it intermittently
// dies with "The calling thread cannot access this object because a different thread owns it", thrown from
// inside Avalonia's own render before any test body executes. The more often it runs, the likelier it bites, so
// a suite that grows starts failing for reasons unrelated to what it tests.
//
// Building once removes those repeated trips. The trade, per Avalonia's remarks on PerAssembly: state leaks
// between tests, so a test must undo anything global it sets and close windows it opens.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The application every <c>[AvaloniaTest]</c> body runs inside. Fluent is loaded because control TEMPLATES are
/// what let a <see cref="TerminalView"/> — a TemplatedControl — realise anything at all; without a base theme
/// it has no template and nothing to measure.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .AfterSetup(builder => builder.Instance?.Styles.Add(new FluentTheme()));
}
