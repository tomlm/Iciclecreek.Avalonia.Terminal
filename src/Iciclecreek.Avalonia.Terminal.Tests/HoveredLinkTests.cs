using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The link under the pointer as a host sees it: <see cref="TerminalView.HoveredLink"/>, its change
/// event, the click modifier, and the two switches that decide when a link is one.
/// </summary>
/// <remarks>
/// Driven through real pointer and key events rather than by calling the hover logic directly, because
/// what a host relies on is the whole path: the cell the pointer resolves to, the modifier the event
/// carries, and the order the notifications arrive in.
/// </remarks>
[TestFixture]
public class HoveredLinkTests
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";

    private static string Link(string url) => $"{Esc}]8;;{url}{St}";

    // Columns 4..22 are the url; the rest is plain text.
    private const string OneUrl = "see https://example.com now";

    private static (TerminalView view, Window window) LiveView(string text)
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();

        view.AttachConnection(new RecordingConnection());
        view.Focus();
        view.Terminal.Write(text);
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    private static List<HoveredLinkChangedEventArgs> Record(TerminalView view)
    {
        var changes = new List<HoveredLinkChangedEventArgs>();
        view.HoveredLinkChanged += (_, e) => changes.Add(e);
        return changes;
    }

    // ---- enter, change, leave ---------------------------------------------------------------

    [AvaloniaTest]
    public void Moving_onto_a_link_reports_it_with_its_cells()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            var changes = Record(view);

            MoveTo(view, col: 10);

            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].PreviousLink, Is.Null);
            var link = changes[0].Link!;
            Assert.That(link.Url, Is.EqualTo("https://example.com"));
            Assert.That(link.FromSequence, Is.False);
            Assert.That(link.Segments, Is.EqualTo(new[]
            {
                new TerminalLinkSegment(view.Terminal.Buffer.ViewportY, 4, 22),
            }));
            Assert.That(view.HoveredLink, Is.SameAs(link), "the property and the event agree");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Moving_within_one_link_does_not_report_it_again()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            var changes = Record(view);

            MoveTo(view, col: 5);
            MoveTo(view, col: 12);
            MoveTo(view, col: 22);

            Assert.That(changes, Has.Count.EqualTo(1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Moving_from_one_link_to_the_next_is_one_change()
    {
        // "a https://one.example b https://two.example": one at 2..18, two at 22..38.
        var (view, window) = LiveView("a https://one.example b https://two.example");
        try
        {
            MoveTo(view, col: 5);
            var changes = Record(view);

            MoveTo(view, col: 30);

            Assert.That(changes, Has.Count.EqualTo(1), "not a leave followed by an enter");
            Assert.That(changes[0].PreviousLink!.Url, Is.EqualTo("https://one.example"));
            Assert.That(changes[0].Link!.Url, Is.EqualTo("https://two.example"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Moving_off_a_link_reports_that_there_is_none()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            MoveTo(view, col: 10);
            var entered = view.HoveredLink;
            var changes = Record(view);

            MoveTo(view, col: 25);

            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].PreviousLink, Is.SameAs(entered));
            Assert.That(changes[0].Link, Is.Null);
            Assert.That(view.HoveredLink, Is.Null);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Leaving_the_control_reports_that_there_is_none()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            MoveTo(view, col: 10);
            var changes = Record(view);

            view.RaiseEvent(new PointerEventArgs(InputElement.PointerExitedEvent, view, NewPointer(), view,
                default, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
                KeyModifiers.None));
            Dispatcher.UIThread.RunJobs();

            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Link, Is.Null);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_declared_link_is_reported_as_one()
    {
        var (view, window) = LiveView(Link("https://example.com/deep") + "click here" + Link(""));
        try
        {
            MoveTo(view, col: 3);

            Assert.That(view.HoveredLink!.Url, Is.EqualTo("https://example.com/deep"));
            Assert.That(view.HoveredLink.FromSequence, Is.True);
        }
        finally { window.Close(); }
    }

    // ---- the click modifier -----------------------------------------------------------------

    [AvaloniaTest]
    public void Ctrl_click_opens_a_link_by_default()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            Assert.That(view.LinkClickModifiers, Is.EqualTo(KeyModifiers.Control), "the default is unchanged");
            var clicked = RecordClicks(view);

            Click(view, col: 10, KeyModifiers.Control);

            Assert.That(clicked, Is.EqualTo(new[] { "https://example.com" }));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_configured_modifier_replaces_ctrl()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            view.LinkClickModifiers = KeyModifiers.Meta;
            var clicked = RecordClicks(view);

            Click(view, col: 10, KeyModifiers.Control);
            Assert.That(clicked, Is.Empty, "Ctrl no longer opens it");

            Click(view, col: 10, KeyModifiers.Meta);
            Assert.That(clicked, Is.EqualTo(new[] { "https://example.com" }));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Every_configured_modifier_has_to_be_held()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            view.LinkClickModifiers = KeyModifiers.Control | KeyModifiers.Shift;
            var clicked = RecordClicks(view);

            Click(view, col: 10, KeyModifiers.Control);
            Assert.That(clicked, Is.Empty, "Shift is missing");

            Click(view, col: 10, KeyModifiers.Control | KeyModifiers.Shift);
            Assert.That(clicked, Has.Count.EqualTo(1));
        }
        finally { window.Close(); }
    }

    // ---- highlight only while the modifier is held ------------------------------------------

    [AvaloniaTest]
    public void By_default_a_link_highlights_with_no_modifier()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            MoveTo(view, col: 10);

            Assert.That(view.HoveredLink, Is.Not.Null);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Requiring_the_modifier_holds_the_highlight_back_until_it_is_held()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            view.LinkHighlightRequiresModifier = true;
            var changes = Record(view);

            MoveTo(view, col: 10);
            Assert.That(view.HoveredLink, Is.Null);
            Assert.That(view.Cursor, Is.Null, "no hand cursor either");

            MoveTo(view, col: 11, KeyModifiers.Control);
            Assert.That(view.HoveredLink!.Url, Is.EqualTo("https://example.com"));
            Assert.That(view.Cursor, Is.Not.Null, "the hand cursor comes with it");

            MoveTo(view, col: 12);
            Assert.That(view.HoveredLink, Is.Null, "and goes away when it is let go");
            Assert.That(changes, Has.Count.EqualTo(2));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Pressing_and_releasing_the_modifier_updates_a_pointer_that_has_not_moved()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            view.LinkHighlightRequiresModifier = true;
            view.LinkClickModifiers = KeyModifiers.Meta;
            MoveTo(view, col: 10);
            var changes = Record(view);

            SendKey(view, InputElement.KeyDownEvent, Key.LWin, KeyModifiers.None);
            Assert.That(view.HoveredLink?.Url, Is.EqualTo("https://example.com"), "armed by the press");

            SendKey(view, InputElement.KeyUpEvent, Key.LWin, KeyModifiers.Meta);
            Assert.That(view.HoveredLink, Is.Null, "disarmed by the release");

            Assert.That(changes.Select(c => c.Link?.Url), Is.EqualTo(new[] { "https://example.com", null }));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_modifier_other_than_the_link_one_does_not_arm_it()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            view.LinkHighlightRequiresModifier = true;
            MoveTo(view, col: 10);

            SendKey(view, InputElement.KeyDownEvent, Key.LeftShift, KeyModifiers.None);

            Assert.That(view.HoveredLink, Is.Null);
        }
        finally { window.Close(); }
    }

    // ---- detection --------------------------------------------------------------------------

    [AvaloniaTest]
    public void Turning_detection_off_stops_matched_links()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            view.DetectUrls = false;
            var clicked = RecordClicks(view);

            MoveTo(view, col: 10);
            Click(view, col: 10, KeyModifiers.Control);

            Assert.That(view.HoveredLink, Is.Null);
            Assert.That(clicked, Is.Empty);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// As Ghostty's <c>link-url = false</c>: only the pattern match goes. A link the program declared is
    /// still a link.
    /// </summary>
    [AvaloniaTest]
    public void Turning_detection_off_leaves_declared_links_alone()
    {
        var (view, window) = LiveView(Link("https://example.com/deep") + "click here" + Link(""));
        try
        {
            view.DetectUrls = false;
            var clicked = RecordClicks(view);

            MoveTo(view, col: 3);
            Click(view, col: 3, KeyModifiers.Control);

            Assert.That(view.HoveredLink?.Url, Is.EqualTo("https://example.com/deep"));
            Assert.That(clicked, Is.EqualTo(new[] { "https://example.com/deep" }));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Turning_detection_off_drops_a_current_hover()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            MoveTo(view, col: 10);
            var changes = Record(view);

            view.DetectUrls = false;

            Assert.That(view.HoveredLink, Is.Null);
            Assert.That(changes, Has.Count.EqualTo(1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_handler_that_throws_does_not_break_the_hover()
    {
        var (view, window) = LiveView(OneUrl);
        try
        {
            view.HoveredLinkChanged += (_, _) => throw new InvalidOperationException("host bug");

            MoveTo(view, col: 10);
            Assert.That(view.HoveredLink, Is.Not.Null);

            // Clearing the hover raises the event from inside the setter, so a throw would land here.
            Assert.DoesNotThrow(() => view.DetectUrls = false);
            Assert.That(view.HoveredLink, Is.Null);
        }
        finally { window.Close(); }
    }

    // ---- the wrappers -----------------------------------------------------------------------

    [AvaloniaTest]
    public void The_link_settings_reach_the_view_from_a_control_and_a_window()
    {
        var control = new TerminalControl
        {
            Process = "",
            LinkClickModifiers = KeyModifiers.Meta,
            LinkHighlightRequiresModifier = true,
            DetectUrls = false,
        };
        var host = TerminalHost.Show(control);
        var terminalWindow = new TerminalWindow
        {
            Process = "",
            LinkClickModifiers = KeyModifiers.Alt,
            LinkHighlightRequiresModifier = true,
            DetectUrls = false,
        }.Realise();
        try
        {
            foreach (var (view, modifiers) in new[]
                     {
                         (control.View(), KeyModifiers.Meta),
                         (terminalWindow.Control().View(), KeyModifiers.Alt),
                     })
            {
                Assert.That(view.LinkClickModifiers, Is.EqualTo(modifiers));
                Assert.That(view.LinkHighlightRequiresModifier, Is.True);
                Assert.That(view.DetectUrls, Is.False);
            }
        }
        finally
        {
            host.Close();
            terminalWindow.Close();
        }
    }

    [AvaloniaTest]
    public void The_hover_is_forwarded_by_a_control_and_a_window()
    {
        var terminalWindow = new TerminalWindow { Process = "" }.Realise();
        try
        {
            var control = terminalWindow.Control();
            var view = control.View();
            view.AttachConnection(new RecordingConnection());
            view.Terminal.Write(OneUrl);
            Dispatcher.UIThread.RunJobs();

            object? fromControl = null, fromWindow = null;
            control.HoveredLinkChanged += (s, _) => fromControl = s;
            terminalWindow.HoveredLinkChanged += (s, _) => fromWindow = s;

            MoveTo(view, col: 10);

            Assert.That(fromControl, Is.SameAs(control));
            Assert.That(fromWindow, Is.SameAs(terminalWindow));
            Assert.That(control.HoveredLink, Is.SameAs(view.HoveredLink));
            Assert.That(terminalWindow.HoveredLink, Is.SameAs(view.HoveredLink));
            Assert.That(view.HoveredLink, Is.Not.Null);
        }
        finally { terminalWindow.Close(); }
    }

    // ---- input ------------------------------------------------------------------------------

    private static List<string> RecordClicks(TerminalView view)
    {
        var clicked = new List<string>();
        view.UrlClicked += (_, e) => clicked.Add(e.Url);
        return clicked;
    }

    private static double Field(TerminalView view, string name)
    {
        var f = typeof(TerminalView).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(f, Is.Not.Null, $"{name} has been renamed; this test needs updating");
        var value = (double)f!.GetValue(view)!;
        Assert.That(value, Is.GreaterThan(0), "the view has not measured its font yet");
        return value;
    }

    /// <summary>The centre of a cell on the first row.</summary>
    private static global::Avalonia.Point CellCentre(TerminalView view, int col)
        => new(Math.Max(0, view.GutterWidth) + (col + 0.5) * Field(view, "_charWidth"),
               0.5 * Field(view, "_charHeight"));

    private static Pointer NewPointer() => new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

    private static void MoveTo(TerminalView view, int col, KeyModifiers modifiers = KeyModifiers.None)
    {
        view.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, view, NewPointer(), view,
            CellCentre(view, col), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), modifiers));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(TerminalView view, int col, KeyModifiers modifiers)
    {
        var pointer = NewPointer();
        var at = CellCentre(view, col);
        view.RaiseEvent(new PointerPressedEventArgs(view, pointer, view, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            modifiers)
        {
            RoutedEvent = InputElement.PointerPressedEvent,
        });
        view.RaiseEvent(new PointerReleasedEventArgs(view, pointer, view, at, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            modifiers, MouseButton.Left)
        {
            RoutedEvent = InputElement.PointerReleasedEvent,
        });
        Dispatcher.UIThread.RunJobs();
    }

    private static void SendKey(TerminalView view, RoutedEvent<KeyEventArgs> routedEvent, Key key, KeyModifiers modifiers)
    {
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = routedEvent, Key = key, KeyModifiers = modifiers });
        Dispatcher.UIThread.RunJobs();
    }
}
