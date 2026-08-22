using System.Reflection;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// <see cref="TerminalControl"/> and <see cref="TerminalWindow"/> must expose what
/// <see cref="TerminalView"/> exposes.
///
/// <para>Written by reflection rather than as a hand-written list on purpose. A list is a snapshot: it
/// passes forever while the thing it describes rots, because nobody remembers to add to it. This walks the
/// actual surface, so a member added to the view tomorrow and not forwarded fails this test tomorrow —
/// which is the only version of a parity test worth having.</para>
///
/// <para>The gap it was written to close was real and had shipped: `AttachConnection`, `DetachConnection`,
/// `IsLive`, the four cursor properties, the four viewport members and `CopyAsync`/`PasteAsync` were public
/// on the view and unreachable from either wrapper. A host using `TerminalControl` — which is most of them —
/// could not style the cursor, drive a scrollbar, copy a selection, or take ownership of a PTY. #19's
/// reporter hit the same shape of problem and resorted to reflection.</para>
/// </summary>
[TestFixture]
public class SurfaceParityTests
{
    /// <summary>
    /// Members that are genuinely the view's own business and are not part of the wrapper contract.
    /// Each is excluded for a stated reason, so the exclusions can be argued with rather than trusted.
    /// </summary>
    private static readonly HashSet<string> NotPartOfTheContract = new()
    {
        // ITextInputMethodClient — IME plumbing Avalonia calls on the view directly. A host never does.
        "SetPreeditText", "ClearPreeditText", "PreeditText", "SupportsPreedit",
        "SupportsSurroundingText", "SurroundingText", "TextViewVisual", "CursorRectangle",
        "SelectionChanged", "InputMethodClient", "RaiseInputMethodClientRequested",

        // ICustomHitTest and rendering — the framework's entry points, not API.
        "HitTest", "Render",

        // The emulator's own object. Both wrappers expose Terminal, which is the way through to it.
        "Selection",

        // Routed events reach a wrapper by bubbling: TerminalView.AddXxxHandler(control, ...) already
        // works on a TerminalControl. Re-declaring them as CLR events on each wrapper would be a second,
        // conflicting mechanism.
        "TitleChanged", "BellRang", "WindowMoved", "WindowResized", "WindowMinimized",
        "WindowMaximized", "WindowRestored", "WindowRaised", "WindowLowered",
        "WindowFullscreened", "WindowInfoRequested",
    };

    /// <summary>Public instance members declared on a type, ignoring anything inherited from Avalonia.</summary>
    private static HashSet<string> DeclaredSurface(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var names = type.GetMethods(flags).Where(m => !m.IsSpecialName).Select(m => m.Name)
            .Concat(type.GetProperties(flags).Select(p => p.Name))
            .Concat(type.GetEvents(flags).Select(e => e.Name));

        return names.ToHashSet();
    }

    /// <summary>
    /// Anything on the view that a wrapper does not expose, and that is not deliberately excluded. Also
    /// filtered against the wrapper's inherited surface, so members it gets from Control or Window for free
    /// (FontFamily, Background, and so on) do not read as gaps.
    /// </summary>
    private static List<string> MissingFrom(Type wrapper)
    {
        var view = DeclaredSurface(typeof(TerminalView));
        var declared = DeclaredSurface(wrapper);

        var inherited = wrapper
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();

        return view
            .Where(n => !NotPartOfTheContract.Contains(n))
            .Where(n => !declared.Contains(n) && !inherited.Contains(n))
            .OrderBy(n => n)
            .ToList();
    }

    [AvaloniaTest]
    public void TerminalControl_exposes_everything_TerminalView_does()
    {
        var missing = MissingFrom(typeof(TerminalControl));

        Assert.That(missing, Is.Empty,
            "TerminalControl is the type most hosts actually use; anything reachable only on the inner view "
            + "is effectively unreachable, and the workaround is reflection. Missing: " + string.Join(", ", missing));
    }

    [AvaloniaTest]
    public void TerminalWindow_exposes_everything_TerminalView_does()
    {
        var missing = MissingFrom(typeof(TerminalWindow));

        Assert.That(missing, Is.Empty,
            "Missing: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The window must also keep up with the control, which is the parity #37 established. Cheap to state
    /// and it fails loudly if someone adds to one and forgets the other.
    /// </summary>
    [AvaloniaTest]
    public void TerminalWindow_exposes_everything_TerminalControl_does()
    {
        var control = DeclaredSurface(typeof(TerminalControl));
        var window = DeclaredSurface(typeof(TerminalWindow));
        var inherited = typeof(TerminalWindow)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();

        var missing = control
            .Where(n => !NotPartOfTheContract.Contains(n))
            .Where(n => !window.Contains(n) && !inherited.Contains(n))
            .OrderBy(n => n)
            .ToList();

        Assert.That(missing, Is.Empty, "Missing: " + string.Join(", ", missing));
    }
}
