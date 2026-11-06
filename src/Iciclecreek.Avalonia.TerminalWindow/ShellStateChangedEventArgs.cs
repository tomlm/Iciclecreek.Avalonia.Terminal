using Avalonia.Interactivity;
using XT = global::XTerm;

namespace Iciclecreek.Terminal;

/// <summary>
/// The shell moved between prompt, input and a running command, as its OSC 133 marks reported.
/// See <see cref="TerminalView.ShellStateChanged"/>.
/// </summary>
public class ShellStateChangedEventArgs : RoutedEventArgs
{
    public ShellStateChangedEventArgs(RoutedEvent routedEvent, XT.Common.ShellState oldState, XT.Common.ShellState newState, int? exitCode)
        : base(routedEvent)
    {
        OldState = oldState;
        NewState = newState;
        ExitCode = exitCode;
    }

    /// <summary>The state before the change.</summary>
    public XT.Common.ShellState OldState { get; }

    /// <summary>The state after the change.</summary>
    public XT.Common.ShellState NewState { get; }

    /// <summary>
    /// The exit code reported with the <c>133;D</c> that ended a command, or null when something else
    /// caused this change or the shell sent none. Null means "not reported", never "succeeded".
    /// </summary>
    public int? ExitCode { get; }
}
