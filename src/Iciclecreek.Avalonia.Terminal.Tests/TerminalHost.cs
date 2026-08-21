using Avalonia.Controls;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The three things every contract test needs, and deliberately nothing else.
///
/// <para>There is no fixture base class and no [SetUp]/[TearDown] here on purpose. Under
/// <c>AvaloniaTestIsolationLevel.PerAssembly</c> each test is personally responsible for closing what it
/// opens, and hiding the Show/Close pairing behind a base class is how that responsibility gets forgotten.
/// NUnit's setup hooks are also not guaranteed to run on the headless UI thread that <c>[AvaloniaTest]</c>
/// marshals the test body onto, so state built there would be built on the wrong thread.</para>
/// </summary>
internal static class TerminalHost
{
    /// <summary>
    /// Host a control in a window and realise it. <c>Show()</c> runs the layout pass, which applies
    /// <see cref="TerminalControl"/>'s template — no explicit ApplyTemplate, no render-timer tick and no
    /// await are needed for the inner view to exist.
    /// </summary>
    /// <remarks>
    /// The size is load-bearing, not decoration. TerminalView derives Cols/Rows from its arranged size, so a
    /// zero-size window gives the emulator a degenerate grid and every dimension-sensitive assertion becomes
    /// meaningless without failing.
    /// </remarks>
    public static Window Show(Control content)
    {
        var window = new Window { Width = 800, Height = 600, Content = content };
        window.Show();
        return window;
    }

    /// <summary>
    /// Show a <see cref="TerminalWindow"/>, which builds its own inner control rather than being given one.
    /// </summary>
    /// <remarks>
    /// The explicit size is what makes this work. A window with no Width/Height gets a degenerate layout
    /// pass, the inner TerminalControl's template is never applied, and every assertion about the inner
    /// view fails with a null template part rather than anything informative.
    /// </remarks>
    public static TerminalWindow Realise(this TerminalWindow window)
    {
        window.Width = 800;
        window.Height = 600;
        window.Show();

        // Show() alone is enough for a plain Window hosting a TerminalControl, but not for TerminalWindow,
        // which assigns Content from inside OnInitialized. Forcing the pass is what applies the inner
        // control's template; without it every template part is null and the assertions are vacuous.
        window.UpdateLayout();

        return window;
    }

    /// <summary>
    /// The <see cref="TerminalView"/> inside a realised <see cref="TerminalControl"/>.
    /// </summary>
    /// <remarks>
    /// Asserts rather than returning null: an unrealised template makes every downstream assertion vacuous,
    /// and a test that passes because it asserted nothing is worse than one that fails.
    /// </remarks>
    public static TerminalView View(this TerminalControl control)
    {
        var view = control.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();

        Assert.That(view, Is.Not.Null,
            "the template did not realise PART_TerminalView, so every assertion below would be vacuous. "
            + "Was the control hosted in a window and shown?");

        return view!;
    }

    /// <summary>
    /// The <see cref="TerminalControl"/> a <see cref="TerminalWindow"/> builds for itself.
    /// </summary>
    /// <remarks>
    /// Window.Show() calls EnsureInitialized -> OnInitialized -> EnsureTerminalControl, so Content is
    /// populated by the time Show() returns. No visual-tree walk is needed for this first hop.
    /// </remarks>
    public static TerminalControl Control(this TerminalWindow window)
    {
        var control = window.Content as TerminalControl;

        Assert.That(control, Is.Not.Null,
            "TerminalWindow did not build its inner TerminalControl. Was the window shown?");

        return control!;
    }
}
