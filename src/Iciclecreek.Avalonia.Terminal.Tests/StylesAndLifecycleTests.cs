using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Where the default theme sits, and what runs before the emulator is ready.
/// </summary>
[TestFixture]
public class StylesAndLifecycleTests
{
    // ------------------------------------------------------ the default theme

    [AvaloniaTest]
    public void The_default_theme_sits_UNDER_the_applications_own_styles()
    {
        // Later styles win in Avalonia, so appending put this library's default theme above
        // everything the application had already set -- a host that styled TerminalControl in
        // App.axaml, which is where an application's styles go, was overruled by the control it was
        // styling. A default has to be beatable or it is not a default.
        // A GUARD here rather than a regression test, and worth saying so: it passes against the
        // previous commit too. In this fixture the static constructor happens to run before the app
        // adds its own styles, so appending landed the theme first by accident -- the ordering the
        // fix now guarantees on purpose. In an application where a control is first constructed after
        // App.axaml has been applied, which is every real one, appending landed it last.
        //
        // Constructed first, because whether the theme is loaded at all depends on whether any
        // control has been made yet -- and this assembly shares one Application, so that depends on
        // what else has run. Making one here removes the order dependency.
        _ = new TerminalControl { Process = "" };

        var styles = Application.Current!.Styles;

        var ours = IndexOfTerminalTheme(styles);
        Assert.That(ours, Is.GreaterThanOrEqualTo(0),
            "sanity: the theme is loaded at all, which is what makes the position meaningful");

        // The fixture's own FluentTheme is added by TestAppBuilder.AfterSetup, which runs before any
        // control is constructed -- so it stands in for an application's styles here.
        var fluent = -1;
        for (var i = 0; i < styles.Count; i++)
        {
            if (styles[i].GetType().Name.Contains("FluentTheme", StringComparison.Ordinal))
                fluent = i;
        }

        Assert.That(fluent, Is.GreaterThanOrEqualTo(0), "sanity: the app has styles of its own");
        Assert.That(ours, Is.LessThan(fluent),
            "the library's default must come first, so anything the host adds outranks it");
    }

    private static int IndexOfTerminalTheme(IList<IStyle> styles)
    {
        var uri = new Uri("avares://Iciclecreek.Avalonia.Terminal/Themes/Generic.axaml");
        for (var i = 0; i < styles.Count; i++)
        {
            if (styles[i] is global::Avalonia.Markup.Xaml.Styling.StyleInclude include
                && include.Source == uri)
                return i;
        }

        return -1;
    }

    [AvaloniaTest]
    public void Constructing_a_control_is_what_loads_the_theme()
    {
        // The retry used to sit in OnApplyTemplate, where it could never run: the control's template
        // COMES from these styles, so if they are missing there is no template, and a hook that fires
        // when a template is applied never fires at all. Unreachable exactly when it was needed.
        //
        // A constructor runs either way, and before styling.
        // One first, so there is definitely something to take away.
        _ = new TerminalControl { Process = "" };

        var styles = Application.Current!.Styles;
        var uri = new Uri("avares://Iciclecreek.Avalonia.Terminal/Themes/Generic.axaml");

        var removed = new List<IStyle>();
        for (var i = styles.Count - 1; i >= 0; i--)
        {
            if (styles[i] is global::Avalonia.Markup.Xaml.Styling.StyleInclude include
                && include.Source == uri)
            {
                removed.Add(styles[i]);
                styles.RemoveAt(i);
            }
        }

        try
        {
            ResetStylesLoadedFlag();
            Assert.That(removed, Is.Not.Empty, "sanity: there was a copy to take away");
            Assert.That(IndexOfTerminalTheme(styles), Is.LessThan(0), "sanity: it is gone");

            _ = new TerminalControl { Process = "" };

            Assert.That(IndexOfTerminalTheme(styles), Is.GreaterThanOrEqualTo(0),
                "constructing a control must put the theme back, with no template needed to get there");
        }
        finally
        {
            // Put the fixture back the way it was: this assembly shares one Application.
            for (var i = styles.Count - 1; i >= 0; i--)
            {
                if (styles[i] is global::Avalonia.Markup.Xaml.Styling.StyleInclude include
                    && include.Source == uri)
                    styles.RemoveAt(i);
            }

            foreach (var style in removed)
                styles.Insert(0, style);

            ResetStylesLoadedFlag();
        }
    }

    private static void ResetStylesLoadedFlag()
    {
        var f = typeof(TerminalControl).GetField("_stylesLoaded",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.That(f, Is.Not.Null, "_stylesLoaded has been renamed; this test needs updating");
        f!.SetValue(null, false);
    }

    // ------------------------------------------------------------- the reader

    [AvaloniaTest]
    public void A_reader_started_just_before_a_teardown_still_gets_its_token()
    {
        // The token used to be read INSIDE the reader lambda, so it was a field access happening on
        // the new thread whenever it got scheduled -- and CleanupProcess nulls _processCts. A
        // relaunch or a close landing in that gap killed the reader with an unobserved
        // NullReferenceException before it had read a byte, and nothing reported it: the task is
        // discarded, so the exception went nowhere and the terminal simply never showed output.
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();

        try
        {
            var pty = new RecordingConnection();
            view.AttachConnection(pty);

            // Straight into a teardown, which is the race: the reader thread may not have been
            // scheduled yet.
            view.DetachConnection();
            Thread.Sleep(150);

            // Still usable afterwards, which it would not be if the reader had died mid-start.
            var second = new RecordingConnection();
            Assert.DoesNotThrow(() => view.AttachConnection(second));

            view.SendInputAsync("still alive").GetAwaiter().GetResult();
            Thread.Sleep(150);

            Assert.That(second.Written, Does.Contain("still alive"));
        }
        finally { window.Close(); }
    }

    // ---------------------------------------------------------- the resize lock

    [AvaloniaTest]
    public void A_resize_while_output_is_arriving_does_not_corrupt_the_buffer()
    {
        // A resize reflows the whole buffer, and the pty reader can be inside Terminal.Write at the
        // same moment -- one thread rewriting the lines another is appending to. Layout runs on the
        // UI thread and output arrives on the reader thread, so the two meet whenever a window is
        // resized while anything is printing.
        //
        // A race rather than a state, so this cannot prove the lock is there. What it does is drive
        // both sides hard enough that an unlocked version has a real chance of throwing -- and
        // assert the terminal is still coherent afterwards.
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();

        try
        {
            var stop = false;
            var writer = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                    view.Terminal.Write("some output line\r\n");
            }) { IsBackground = true };

            writer.Start();

            Assert.DoesNotThrow(() =>
            {
                for (var i = 0; i < 40; i++)
                {
                    window.Width = 600 + (i % 7) * 30;
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }
            });

            Volatile.Write(ref stop, true);
            writer.Join(TimeSpan.FromSeconds(5));

            Assert.That(view.Terminal.Cols, Is.GreaterThan(0));
            Assert.That(view.Terminal.Buffer.Length, Is.GreaterThan(0));
        }
        finally { window.Close(); }
    }
}
