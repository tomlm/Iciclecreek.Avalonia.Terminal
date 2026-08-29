using Avalonia.Interactivity;
using XT = global::XTerm;

namespace Iciclecreek.Terminal;

/// <summary>
/// A desktop notification the running program requested (OSC 9 or Kitty OSC 99), surfaced as a
/// routed event because the terminal control cannot post OS notifications itself — the
/// application decides how, and whether, to show it.
/// </summary>
public class TerminalNotificationEventArgs : RoutedEventArgs
{
    public TerminalNotificationEventArgs(RoutedEvent routedEvent, XT.Events.TerminalEvents.NotificationEventArgs notification)
        : base(routedEvent)
    {
        Notification = notification;
    }

    /// <summary>
    /// The emulator's notification: <c>Text</c> always; identifier, title, body, urgency and
    /// icon when the program used the structured OSC 99 form.
    /// </summary>
    public XT.Events.TerminalEvents.NotificationEventArgs Notification { get; }
}

/// <summary>
/// The running program asked for the user's attention (iTerm2 OSC 1337 RequestAttention).
/// </summary>
public class TerminalAttentionEventArgs : RoutedEventArgs
{
    public TerminalAttentionEventArgs(RoutedEvent routedEvent, string action)
        : base(routedEvent)
    {
        Action = action;
    }

    /// <summary>
    /// The request verbatim: "yes" (bounce until focused), "once", "fireworks", or "no"
    /// (cancel a pending request). The application owns the dock or taskbar, so the
    /// application interprets it.
    /// </summary>
    public string Action { get; }
}
