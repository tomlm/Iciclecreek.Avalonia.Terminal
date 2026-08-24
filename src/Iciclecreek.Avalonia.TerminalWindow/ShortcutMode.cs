namespace Iciclecreek.Terminal
{
    /// <summary>
    /// Which convention the terminal follows for the keys a desktop application would use for the
    /// clipboard: Ctrl+A, Ctrl+C, Ctrl+V and Ctrl+X.
    /// </summary>
    /// <remarks>
    /// These keys are contested. In a shell they are readline's <c>beginning-of-line</c>, SIGINT,
    /// <c>quoted-insert</c> and its prefix key; in every other desktop application they are select-all,
    /// copy, paste and cut. There is no answer that is right for both, so this says which one is wanted.
    /// </remarks>
    public enum ShortcutMode
    {
        /// <summary>
        /// Behave like a terminal, which is the default. Ctrl+Shift+C copies, Ctrl+C sends SIGINT unless
        /// there is a selection to copy, and Ctrl+A, Ctrl+V and Ctrl+X belong to the program.
        /// </summary>
        /// <remarks>
        /// Zero deliberately, so that <c>default(ShortcutMode)</c> is the behaviour a terminal already had
        /// rather than one of the modes that changes it.
        /// </remarks>
        Terminal = 0,

        /// <summary>
        /// Behave like every other desktop application. Ctrl+A selects all, Ctrl+V pastes and Ctrl+X cuts,
        /// with Ctrl+Shift carrying the control character each of those keys would otherwise have sent, so
        /// nothing becomes unreachable.
        /// </summary>
        /// <remarks>
        /// <para>Ctrl+X cuts only what it can actually remove — the editable input. A selection made with
        /// the mouse, or one up in the scrollback, is left alone and the chord goes to the program, rather
        /// than becoming a copy that looks like a cut.</para>
        /// <para>Suspended while the alternate screen buffer is active. A full-screen application owns its
        /// own keys — vim's Ctrl+V is blockwise-visual, not paste — so the terminal stands aside and
        /// behaves as <see cref="Terminal"/> until the application exits. Ctrl+Shift+C still copies, so
        /// text can be taken out of a full-screen application.</para>
        /// <para>On macOS this changes nothing, deliberately. The desktop clipboard lives on Cmd there, and
        /// those chords work in either mode; Ctrl+A, Ctrl+E and friends are the system-wide emacs line
        /// bindings that every macOS text field honours, so leaving them to the program IS the desktop
        /// behaviour on that platform.</para>
        /// </remarks>
        Desktop = 1,

        /// <summary>
        /// Handle nothing. Every one of these keys reaches the program untouched, including Ctrl+Shift+C
        /// and Ctrl+Shift+V — so Ctrl+C is plain SIGINT and there is no keyboard copy or paste at all.
        /// </summary>
        /// <remarks>
        /// For a host that wants to own the keyboard itself, or one embedding a program that binds these
        /// chords for its own purposes. Selecting and copying with the mouse still works; this governs the
        /// keyboard only.
        /// </remarks>
        None = 2,
    }
}
