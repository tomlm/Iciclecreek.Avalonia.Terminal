using Avalonia.Input;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// The key gestures <see cref="TerminalView"/> claims for its own operations, as Avalonia
    /// <see cref="KeyGesture"/>s, so a host can show the same chords in its menus
    /// (<c>MenuItem.InputGesture</c>) and test against the same table the view matches.
    /// </summary>
    /// <remarks>
    /// <para>These follow the conventions of the platform's own terminals rather than of a text field. The
    /// clipboard lives on Ctrl+Shift on Windows and Linux because plain Ctrl+C is SIGINT and plain Ctrl+V is
    /// readline's quoted-insert; on macOS it lives on Cmd, which no shell binds. Zoom is the command modifier
    /// with Plus, Minus and 0, the way every browser and terminal does it.</para>
    /// <para>Scrolling the scrollback is Shift with Page Up/Down and Ctrl+Shift with Home/End on Windows and
    /// Linux, and Cmd with the same keys on macOS. Plain Shift+Home and Shift+End are deliberately not here:
    /// they extend a keyboard selection to the line edge, as in any text field, and a selection gesture
    /// and a scroll gesture cannot share a chord.</para>
    /// <para>Decided by the operating system, not by <c>PlatformHotkeyConfiguration</c>, because the
    /// contested chords (Ctrl+C, Ctrl+V, Ctrl+A) differ from a text field's and the headless platform
    /// reports the same configuration on every OS. Every list is read-only and fixed for the life of the
    /// process.</para>
    /// <para>Honoured in every <see cref="ShortcutMode"/> except <see cref="ShortcutMode.None"/>, which hands
    /// the whole keyboard to the program.</para>
    /// </remarks>
    public static class TerminalKeyGestures
    {
        private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>Ctrl on Windows and Linux, Cmd (Meta) on macOS.</summary>
        public static KeyModifiers CommandModifiers { get; } = IsMacOS ? KeyModifiers.Meta : KeyModifiers.Control;

        /// <summary>Copy the selection: Ctrl+Shift+C and Ctrl+Insert, or Cmd+C on macOS.</summary>
        /// <remarks>
        /// <para>Ctrl+C with a selection also copies, in every mode but <see cref="ShortcutMode.None"/>, and is
        /// not listed because without a selection it is SIGINT — a menu cannot promise it.</para>
        /// <para>The Ctrl+Shift chords stay on macOS too, second to Cmd: a Mac keyboard has a Ctrl key, no
        /// shell binds Ctrl+Shift+letter, and they worked there before the table existed.</para>
        /// </remarks>
        public static IReadOnlyList<KeyGesture> Copy { get; } = IsMacOS
            ? new[] { new KeyGesture(Key.C, KeyModifiers.Meta), new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift) }
            : new[] { new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift), new KeyGesture(Key.Insert, KeyModifiers.Control) };

        /// <summary>Paste the clipboard: Ctrl+Shift+V and Shift+Insert, or Cmd+V on macOS.</summary>
        public static IReadOnlyList<KeyGesture> Paste { get; } = IsMacOS
            ? new[] { new KeyGesture(Key.V, KeyModifiers.Meta), new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift) }
            : new[] { new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift), new KeyGesture(Key.Insert, KeyModifiers.Shift) };

        /// <summary>
        /// Select the shell's editable input (<see cref="TerminalView.SelectInputAsync"/>): Ctrl+Shift+A, or
        /// Cmd+A on macOS. <see cref="ShortcutMode.Desktop"/> adds plain Ctrl+A on Windows and Linux.
        /// </summary>
        public static IReadOnlyList<KeyGesture> SelectAll { get; } = IsMacOS
            ? new[] { new KeyGesture(Key.A, KeyModifiers.Meta), new KeyGesture(Key.A, KeyModifiers.Control | KeyModifiers.Shift) }
            : new[] { new KeyGesture(Key.A, KeyModifiers.Control | KeyModifiers.Shift) };

        /// <summary>
        /// Zoom in: the command modifier with the =/+ key, shifted or not, so Ctrl+= and Ctrl++ both work on
        /// a US layout. The numeric keypad's + matches too — Avalonia resolves it to the same key.
        /// </summary>
        public static IReadOnlyList<KeyGesture> ZoomIn { get; } = new[]
        {
            new KeyGesture(Key.OemPlus, CommandModifiers),
            new KeyGesture(Key.OemPlus, CommandModifiers | KeyModifiers.Shift),
        };

        /// <summary>Zoom out: the command modifier with the -/_ key or the keypad's -.</summary>
        public static IReadOnlyList<KeyGesture> ZoomOut { get; } = new[]
        {
            new KeyGesture(Key.OemMinus, CommandModifiers),
        };

        /// <summary>Back to the configured font size: the command modifier with 0, on either row of keys.</summary>
        public static IReadOnlyList<KeyGesture> ResetZoom { get; } = new[]
        {
            new KeyGesture(Key.D0, CommandModifiers),
            new KeyGesture(Key.NumPad0, CommandModifiers),
        };

        /// <summary>Scroll the scrollback up by a screenful: Shift+Page Up, or Cmd+Page Up on macOS.</summary>
        public static IReadOnlyList<KeyGesture> ScrollPageUp { get; } = new[]
        {
            new KeyGesture(Key.PageUp, IsMacOS ? KeyModifiers.Meta : KeyModifiers.Shift),
        };

        /// <summary>Scroll the scrollback down by a screenful: Shift+Page Down, or Cmd+Page Down on macOS.</summary>
        public static IReadOnlyList<KeyGesture> ScrollPageDown { get; } = new[]
        {
            new KeyGesture(Key.PageDown, IsMacOS ? KeyModifiers.Meta : KeyModifiers.Shift),
        };

        /// <summary>Scroll to the top of the scrollback: Ctrl+Shift+Home, or Cmd+Home on macOS.</summary>
        public static IReadOnlyList<KeyGesture> ScrollToTop { get; } = new[]
        {
            new KeyGesture(Key.Home, IsMacOS ? KeyModifiers.Meta : KeyModifiers.Control | KeyModifiers.Shift),
        };

        /// <summary>Scroll back to the live output: Ctrl+Shift+End, or Cmd+End on macOS.</summary>
        public static IReadOnlyList<KeyGesture> ScrollToBottom { get; } = new[]
        {
            new KeyGesture(Key.End, IsMacOS ? KeyModifiers.Meta : KeyModifiers.Control | KeyModifiers.Shift),
        };

        /// <summary>True when <paramref name="e"/> is one of <paramref name="gestures"/>.</summary>
        public static bool Matches(this IReadOnlyList<KeyGesture> gestures, KeyEventArgs e)
        {
            for (int i = 0; i < gestures.Count; i++)
            {
                if (gestures[i].Matches(e))
                    return true;
            }

            return false;
        }
    }
}
