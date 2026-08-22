using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The behaviour <see cref="TerminalWindow"/> promises in response to terminal escape sequences, driven by
/// raising the routed events the emulator would raise.
///
/// <para>This is deliberately not done by writing escape sequences into the terminal. That would test
/// XTerm.NET's parser, which is upstream's job and already covered there. Raising the routed event directly
/// tests exactly the boundary this library owns — what the window does once told — and does it without a
/// process, in microseconds, deterministically.</para>
///
/// <para>Every args type here is public with a public constructor and a settable RoutedEvent, so no
/// reflection is needed.</para>
/// </summary>
[TestFixture]
public class TerminalWindowBehaviourTests
{
    /// <summary>Raise a routed event on the inner view, exactly as the emulator would.</summary>
    private static void Raise(TerminalWindow window, RoutedEventArgs args) =>
        window.Control().View().RaiseEvent(args);

    /// <summary>README: "Dynamic title updates from terminal escape sequences".</summary>
    [AvaloniaTest]
    public void A_title_change_updates_the_window_title()
    {
        var window = new TerminalWindow { Process = "", Title = "before" }.Realise();

        try
        {
            Raise(window, new TitleChangedEventArgs("after") { RoutedEvent = TerminalView.TitleChangedEvent });

            Assert.That(window.Title, Is.EqualTo("after"), $"observed '{window.Title}'");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// README: UpdateTitleFromTerminal — "Update window title from terminal escape sequences", default true.
    ///
    /// <para>A documented opt-out that does nothing is worse than no opt-out at all: a host that turns it off
    /// to protect its own title bar still has the title rewritten from under it, and nothing reports that the
    /// setting was ignored. The property is registered but its value is never read.</para>
    /// </summary>
    [AvaloniaTest]
    public void A_title_change_is_ignored_when_UpdateTitleFromTerminal_is_false()
    {
        var window = new TerminalWindow { Process = "", Title = "mine", UpdateTitleFromTerminal = false }.Realise();

        try
        {
            Raise(window, new TitleChangedEventArgs("hijacked") { RoutedEvent = TerminalView.TitleChangedEvent });

            Assert.That(window.Title, Is.EqualTo("mine"),
                $"the host opted out of terminal title updates and was overruled. Observed '{window.Title}'");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A maximize command from the terminal maximizes the window.</summary>
    [AvaloniaTest]
    public void A_maximize_command_maximizes_the_window()
    {
        var window = new TerminalWindow { Process = "" }.Realise();

        try
        {
            Raise(window, new RoutedEventArgs { RoutedEvent = TerminalView.WindowMaximizedEvent });

            Assert.That(window.WindowState, Is.EqualTo(WindowState.Maximized),
                $"observed {window.WindowState}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A restore command returns the window to normal.</summary>
    [AvaloniaTest]
    public void A_restore_command_returns_the_window_to_normal()
    {
        var window = new TerminalWindow { Process = "", WindowState = WindowState.Maximized }.Realise();

        try
        {
            Raise(window, new RoutedEventArgs { RoutedEvent = TerminalView.WindowRestoredEvent });

            Assert.That(window.WindowState, Is.EqualTo(WindowState.Normal),
                $"observed {window.WindowState}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A move command repositions the window.</summary>
    [AvaloniaTest]
    public void A_move_command_repositions_the_window()
    {
        var window = new TerminalWindow { Process = "" }.Realise();

        try
        {
            Raise(window, new WindowMovedEventArgs(120, 240) { RoutedEvent = TerminalView.WindowMovedEvent });

            Assert.Multiple(() =>
            {
                Assert.That(window.Position.X, Is.EqualTo(120), $"observed X={window.Position.X}");
                Assert.That(window.Position.Y, Is.EqualTo(240), $"observed Y={window.Position.Y}");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A resize command resizes the window.</summary>
    [AvaloniaTest]
    public void A_resize_command_resizes_the_window()
    {
        var window = new TerminalWindow { Process = "" }.Realise();

        try
        {
            Raise(window, new WindowResizedEventArgs(640, 480) { RoutedEvent = TerminalView.WindowResizedEvent });

            Assert.Multiple(() =>
            {
                Assert.That(window.Width, Is.EqualTo(640), $"observed {window.Width}");
                Assert.That(window.Height, Is.EqualTo(480), $"observed {window.Height}");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A host that handles the routed event first must suppress the window's default behaviour. Every
    /// handler in TerminalWindow follows the <c>if (!e.Handled)</c> contract, which is what makes the
    /// bubbling events genuinely overridable rather than advisory.
    /// </summary>
    [AvaloniaTest]
    public void A_host_that_handles_the_event_first_suppresses_the_default()
    {
        var window = new TerminalWindow { Process = "", Title = "mine" }.Realise();

        try
        {
            // Attached to the VIEW, not the control. The event bubbles view -> control -> window, and the
            // window's own handler is already on the control, so a handler added to the control afterwards
            // would run second and never get the chance to pre-empt it.
            TerminalView.AddTitleChangedHandler(window.Control().View(), (_, e) => e.Handled = true);

            Raise(window, new TitleChangedEventArgs("hijacked") { RoutedEvent = TerminalView.TitleChangedEvent });

            Assert.That(window.Title, Is.EqualTo("mine"),
                $"a host that marked the event handled still had its title overwritten. Observed '{window.Title}'");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The window answers title queries with its own title. This is the observable half of the
    /// window-command support — the terminal asks, the window replies.
    /// </summary>
    [AvaloniaTest]
    public void The_window_answers_a_title_query()
    {
        var window = new TerminalWindow { Process = "", Title = "answer me" }.Realise();

        try
        {
            var args = new WindowInfoRequestedEventArgs(XTerm.Common.WindowInfoRequest.Title)
            {
                RoutedEvent = TerminalView.WindowInfoRequestedEvent
            };

            Raise(window, args);

            Assert.Multiple(() =>
            {
                Assert.That(args.Handled, Is.True, "the window should have answered the query");
                Assert.That(args.Title, Is.EqualTo("answer me"), $"observed '{args.Title ?? "null"}'");
            });
        }
        finally
        {
            window.Close();
        }
    }
}
