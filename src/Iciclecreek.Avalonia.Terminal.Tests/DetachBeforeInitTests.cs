using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Detaching a view that was never initialised must not throw.
///
/// <para><see cref="TerminalView"/>'s ATTACH path already guards for exactly this — "_terminal is null
/// during initial attachment (OnInitialized hasn't fired yet)" — while the detach path unsubscribes from
/// the same object with no guard. The asymmetry is the bug.</para>
/// </summary>
[TestFixture]
public class DetachBeforeInitTests
{
    /// <summary>
    /// The window is narrow but it is the one the attach guard describes: attachment is notified BEFORE
    /// OnInitialized runs, so a handler that re-parents the view on attach — a docking layout moving a pane
    /// as it arrives — detaches it while the emulator still does not exist.
    ///
    /// <para>Adding to a live tree and removing it afterwards does NOT reproduce this: the add initialises
    /// the view synchronously, so by the time the remove lands there is a real emulator to unsubscribe from.
    /// Reaching the defect needs the detach to happen inside the attach notification.</para>
    /// </summary>
    [AvaloniaTest]
    public void Detaching_during_attach_does_not_throw()
    {
        var panel = new StackPanel();
        var window = new Window { Width = 800, Height = 600, Content = panel };
        window.Show();

        var view = new TerminalView { Process = "" };
        view.AttachedToLogicalTree += (_, _) =>
        {
            Assert.That(view.IsInitialized, Is.False, "sanity: attach is notified before OnInitialized runs");
            panel.Children.Remove(view);
        };

        Assert.DoesNotThrow(() => panel.Children.Add(view),
            "a view detached before OnInitialized has no emulator to unsubscribe from");

        window.Close();
    }
}
