using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// What the renderer does with the links and marks the emulator anchors.
///
/// <para>All of it is reachable from a host: methods it can bind, data it can draw from, and
/// properties it can style. Nothing here binds a key or picks a colour.</para>
/// </summary>
[TestFixture]
public class ShellIntegrationSurfaceTests
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";
    private const string Bel = "\u0007";

    private static string Link(string url, string parameters = "") => $"{Esc}]8;{parameters};{url}{St}";
    private static string Mark(string what) => $"{Esc}]133;{what}{Bel}";

    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    /// <summary>Every drawing in a captured frame, flattened out of its groups, in PAINT order.</summary>
    private static IEnumerable<Drawing> Flatten(DrawingGroup group)
    {
        foreach (var child in group.Children)
        {
            if (child is DrawingGroup inner)
            {
                foreach (var d in Flatten(inner))
                    yield return d;
            }
            else
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// The fills a render put in the gutter lane, in paint order — everything as wide as the gutter
    /// starting at x 0. The terminal's own surface fill starts there too and is excluded by width.
    /// </summary>
    private static List<GeometryDrawing> GutterFills(TerminalView view)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
            view.Render(context);

        return Flatten(group)
            .OfType<GeometryDrawing>()
            .Where(d => d.Geometry is { } g && g.Bounds.X == 0
                        && Math.Abs(g.Bounds.Width - view.GutterWidth) < 0.01)
            .ToList();
    }

    /// <summary>The IME client the view hands out, asked for the way a text input service asks.</summary>
    private static TextInputMethodClient ImeClient(TerminalView view)
    {
        var args = new TextInputMethodClientRequestedEventArgs
        {
            RoutedEvent = InputElement.TextInputMethodClientRequestedEvent,
        };
        view.RaiseEvent(args);
        return args.Client!;
    }

    // ---- OSC 8 -----------------------------------------------------------------------------

    /// <summary>
    /// The case a regular expression cannot reach: no URL appears on screen at all.
    /// </summary>
    [AvaloniaTest]
    public void A_declared_link_is_found_under_text_that_is_not_a_url()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write(Link("https://example.com/deep") + "click here" + Link(""));

            var found = view.FindUrlAtColumn(view.Terminal.Buffer.ViewportY, 3);

            Assert.That(found, Is.Not.Null, "the regex could never have found this");
            Assert.That(found!.Url, Is.EqualTo("https://example.com/deep"));
            Assert.That(found.FromSequence, Is.True);
        }
        finally { window.Close(); }
    }

    /// <summary>Where both could answer, what the program declared wins.</summary>
    [AvaloniaTest]
    public void A_declared_link_beats_the_text_it_wraps()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write(Link("https://declared.example") + "https://written.example" + Link(""));

            var found = view.FindUrlAtColumn(view.Terminal.Buffer.ViewportY, 2);

            Assert.That(found!.Url, Is.EqualTo("https://declared.example"));
            Assert.That(found.FromSequence, Is.True);
        }
        finally { window.Close(); }
    }

    /// <summary>Plain text still falls through to the regex, and says so.</summary>
    [AvaloniaTest]
    public void An_undeclared_url_still_matches_and_is_marked_as_a_guess()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("see https://example.com now");

            var found = view.FindUrlAtColumn(view.Terminal.Buffer.ViewportY, 6);

            Assert.That(found, Is.Not.Null);
            Assert.That(found!.Url, Is.EqualTo("https://example.com"));
            Assert.That(found.FromSequence, Is.False);
        }
        finally { window.Close(); }
    }

    /// <summary>An id joins the halves of a link that wrapped, so hovering either underlines both.</summary>
    [AvaloniaTest]
    public void An_id_joins_spans_on_neighbouring_lines()
    {
        var (view, window) = Realised();
        try
        {
            var url = "https://example.com";
            view.Terminal.Write(Link(url, "id=7") + "first" + Link(""));
            view.Terminal.Write("\r\n");
            view.Terminal.Write(Link(url, "id=7") + "second" + Link(""));

            var found = view.FindUrlAtColumn(view.Terminal.Buffer.ViewportY, 1);

            Assert.That(found, Is.Not.Null);
            Assert.That(found!.Segments.Count, Is.EqualTo(2), "both halves should light up");
        }
        finally { window.Close(); }
    }

    // ---- marks -----------------------------------------------------------------------------

    [AvaloniaTest]
    public void Jumping_to_the_previous_prompt_moves_the_viewport()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write(Mark("A") + "$ one\r\n");
            for (var i = 0; i < 60; i++)
                view.Terminal.Write($"output {i}\r\n");

            var before = view.Terminal.Buffer.ViewportY;

            Assert.That(view.ScrollToPreviousPrompt(), Is.True);
            Assert.That(view.Terminal.Buffer.ViewportY, Is.LessThan(before));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void With_no_prompts_the_gesture_is_left_unhandled()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("just output\r\n");

            Assert.That(view.ScrollToPreviousPrompt(), Is.False,
                        "a host needs to know so it can leave the key to something else");
            Assert.That(view.ScrollToNextPrompt(), Is.False);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Output means what the command produced — not the line it was typed on, and not the prompt
    /// that followed.
    /// </summary>
    [AvaloniaTest]
    public void Selecting_a_commands_output_takes_the_output_and_nothing_else()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write(Mark("A") + "$ build" + Mark("C") + "\r\n");
            view.Terminal.Write("compiling\r\nlinking\r\n");
            view.Terminal.Write(Mark("D;0") + Mark("A") + "$ ");

            Assert.That(view.SelectCommandOutput(view.Terminal.Buffer.ViewportY + 1), Is.True);

            var text = view.Terminal.Selection.GetSelectionText();
            Assert.That(text, Does.Contain("compiling"));
            Assert.That(text, Does.Contain("linking"));
            Assert.That(text, Does.Not.Contain("$ build"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_row_that_is_not_command_output_selects_nothing()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("plain output with no marks at all\r\n");

            Assert.That(view.SelectCommandOutput(view.Terminal.Buffer.ViewportY), Is.False);
        }
        finally { window.Close(); }
    }

    /// <summary>The data a host draws its own gutter from.</summary>
    [AvaloniaTest]
    public void Visible_marks_are_offered_with_their_row_and_status()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write(Mark("A") + "$ cmd" + Mark("C") + "\r\n");
            view.Terminal.Write(Mark("D;3"));

            var marks = view.VisibleMarks;

            Assert.That(marks.Any(m => m.Kind == XTerm.Common.ShellIntegrationMark.PromptStart));
            Assert.That(marks.Single(m => m.ExitCode is not null).ExitCode, Is.EqualTo(3));
            Assert.That(marks.All(m => m.ViewportRow >= 0));
        }
        finally { window.Close(); }
    }

    // ---- the gutter ------------------------------------------------------------------------

    /// <summary>Off unless asked for, and silent unless the host says what a mark looks like.</summary>
    [AvaloniaTest]
    public void The_gutter_is_off_by_default()
    {
        var (view, window) = Realised();
        try
        {
            Assert.That(view.GutterWidth, Is.Zero);
            Assert.That(view.GutterSuccessBrush, Is.Null);
            Assert.That(view.GutterFailureBrush, Is.Null);
            Assert.That(view.GutterPromptBrush, Is.Null);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A gutter narrows the terminal rather than pushing text off the right-hand edge.
    /// </summary>
    [AvaloniaTest]
    public void Turning_on_a_gutter_costs_columns_rather_than_content()
    {
        var (view, window) = Realised();
        try
        {
            var before = view.Terminal.Cols;

            view.GutterWidth = 40;
            window.UpdateLayout();
            view.InvalidateMeasure();
            view.InvalidateArrange();
            window.UpdateLayout();

            Assert.That(view.Terminal.Cols, Is.LessThan(before),
                        "the columns should have been taken out of the width");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Setting gutter properties on the CONTROL must reach the view. SurfaceParityTests proves the
    /// members exist; this proves the wiring — the half that a review on the search work found
    /// missing there, and that was missing here too at the window layer.
    /// </summary>
    [AvaloniaTest]
    public void Gutter_settings_on_the_control_reach_the_view()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            control.GutterWidth = 9;
            control.GutterFailureBrush = Brushes.Crimson;
            window.UpdateLayout();

            var view = control.View();
            Assert.That(view.GutterWidth, Is.EqualTo(9));
            Assert.That(view.GutterFailureBrush, Is.EqualTo(Brushes.Crimson));
        }
        finally { window.Close(); }
    }

    /// <summary>A host can style it; nothing here decides what red means.</summary>
    /// <remarks>
    /// Asserted against the recorded draw calls, not by reading the properties back. This used to
    /// render into a DrawingGroup, throw the group away, and assert that GutterFailureBrush still
    /// held the brush assigned four lines earlier — green with the whole of DrawGutter deleted.
    ///
    /// The second row carries BOTH the finish and the next prompt's start, which is exactly what a
    /// shell emits: its prompt string reports the last command's status and then opens the prompt.
    /// One bar per row, and the status is the one that shows.
    /// </remarks>
    [AvaloniaTest]
    public void The_host_supplies_the_brushes()
    {
        var (view, window) = Realised();
        try
        {
            view.GutterWidth = 6;
            view.GutterPromptBrush = Brushes.Blue;
            view.GutterSuccessBrush = Brushes.Green;
            view.GutterFailureBrush = Brushes.Red;

            view.Terminal.Write(Mark("A") + "$ x" + Mark("B") + Mark("C") + "\r\n"
                                + Mark("D;1") + Mark("A") + "$ ");

            var fills = GutterFills(view);

            Assert.That(fills.Count, Is.EqualTo(2),
                        "one bar per row: the lane was filled once per mark");
            Assert.That(fills[0].Brush, Is.EqualTo(Brushes.Blue),
                        "the first prompt's row is not the prompt colour");
            // Paint order is list order, so the LAST fill on the row is what a user sees.
            Assert.That(fills[1].Brush, Is.EqualTo(Brushes.Red),
                        "the failure bar was painted over by the prompt sharing its row");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The third place the gutter offset is needed, after the pointer maths and the column count:
    /// the IME candidate window is positioned in the same space the render shifts the grid within.
    /// </summary>
    [AvaloniaTest]
    public void The_ime_cursor_rectangle_clears_the_gutter()
    {
        var (view, window) = Realised();
        try
        {
            var client = ImeClient(view);
            Assert.That(client, Is.Not.Null, "the view handed out no IME client");

            view.GutterWidth = 0;
            var bare = client.CursorRectangle.X;

            view.GutterWidth = 12;
            var shifted = client.CursorRectangle.X;

            Assert.That(shifted - bare, Is.EqualTo(12).Within(0.01),
                        "the composition window sits GutterWidth px left of the caret");
        }
        finally { window.Close(); }
    }
}
