using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Whether a bare line feed also returns the carriage, and who gets to decide.
/// </summary>
/// <remarks>
/// <para>This host used to force the translation on for every platform but Windows. The emulator
/// asks <c>Options.ConvertEol || LineFeedMode</c>, so forcing the first half left LNM half-wired: a
/// program could SET mode 20 and never RESET it, and <c>CSI 20 l</c> did nothing at all on macOS and
/// Linux. A program that resets LNM to move down a line WITHOUT returning the carriage got the
/// carriage return anyway and wrote its whole line into column one -- which is how vttest's
/// cursor-control screen collapsed to a single character.</para>
/// <para>It is off by default now, where every other terminal leaves it, and settable for a
/// transport that really does deliver bare line feeds.</para>
/// </remarks>
[TestFixture]
public class ConvertEolTests
{
    private const string Esc = "";

    private static (TerminalView view, Window window) Realised()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    /// <summary>The row the second half of "AB[LF]CD" landed on, trailing blanks trimmed.</summary>
    private static string SecondRow(TerminalView view)
    {
        var top = view.Terminal.Buffer.ViewportY;
        return (view.Terminal.Buffer.GetLine(top + 1)?.TranslateToString(true) ?? "").TrimEnd();
    }

    [AvaloniaTest]
    public void A_bare_line_feed_keeps_its_column_by_default()
    {
        var (view, window) = Realised();
        try
        {
            Assert.That(view.ConvertEol, Is.False,
                "the tty's line discipline owns this, as it does on every other terminal");

            view.Terminal.Write("AB\nCD");
            Dispatcher.UIThread.RunJobs();

            Assert.That(SecondRow(view), Is.EqualTo("  CD"),
                "a line feed moves DOWN; the carriage stays where the program left it");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Turning_it_on_returns_the_carriage_too()
    {
        var (view, window) = Realised();
        try
        {
            view.ConvertEol = true;

            view.Terminal.Write("AB\nCD");
            Dispatcher.UIThread.RunJobs();

            Assert.That(SecondRow(view), Is.EqualTo("CD"),
                "which is the whole point of the option: a bare feed behaves as CRLF");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_property_reaches_the_emulator_that_is_already_running()
    {
        // Live, not just read once while the emulator was being built. A host binding this to a
        // setting toggles it long after the first byte has been written.
        var (view, window) = Realised();
        try
        {
            Assert.That(view.Terminal.Options.ConvertEol, Is.False, "sanity: the default got through");

            view.ConvertEol = true;
            Assert.That(view.Terminal.Options.ConvertEol, Is.True,
                "the running emulator has to see the change, not only a freshly built one");

            view.ConvertEol = false;
            Assert.That(view.Terminal.Options.ConvertEol, Is.False, "and it has to move back");

            // And it is not merely stored -- the next line feed behaves differently.
            view.Terminal.Write("AB\nCD");
            Dispatcher.UIThread.RunJobs();
            Assert.That(SecondRow(view), Is.EqualTo("  CD"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void A_program_can_now_reset_LNM_and_be_obeyed()
    {
        // The defect this whole change exists for. With the flag forced on, the emulator's
        // `ConvertEol || LineFeedMode` could never be false, so CSI 20 l was unreachable.
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write($"{Esc}[20h");          // LNM on: a feed returns the carriage
            view.Terminal.Write("AB\nCD");
            Dispatcher.UIThread.RunJobs();
            Assert.That(SecondRow(view), Is.EqualTo("CD"), "sanity: setting LNM still works");

            var (view2, window2) = Realised();
            try
            {
                view2.Terminal.Write($"{Esc}[20h{Esc}[20l");   // set, then RESET
                view2.Terminal.Write("AB\nCD");
                Dispatcher.UIThread.RunJobs();
                Assert.That(SecondRow(view2), Is.EqualTo("  CD"),
                    "resetting LNM has to be obeyed -- this is what silently did nothing before");
            }
            finally { window2.Close(); }
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void The_host_option_still_wins_over_a_program_that_reset_LNM()
    {
        // Deliberate, and worth pinning: a host that turns this on is describing its TRANSPORT, and
        // a transport that sends bare line feeds does so whatever the program believes about LNM.
        var (view, window) = Realised();
        try
        {
            view.ConvertEol = true;

            view.Terminal.Write($"{Esc}[20l");
            view.Terminal.Write("AB\nCD");
            Dispatcher.UIThread.RunJobs();

            Assert.That(SecondRow(view), Is.EqualTo("CD"),
                "the host's answer about its own wire is not the program's to overrule");
        }
        finally { window.Close(); }
    }
}
