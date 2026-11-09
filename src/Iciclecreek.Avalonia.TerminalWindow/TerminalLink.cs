using System;
using System.Collections.Generic;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// A link in the terminal buffer, resolved to the cells it occupies.
    /// </summary>
    /// <remarks>
    /// Reported by <see cref="TerminalView.HoveredLink"/>. The terminal never opens it; what to do with
    /// it — show it, copy it, launch it — is the host's decision.
    /// </remarks>
    public sealed class TerminalLink
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TerminalLink"/> class.
        /// </summary>
        public TerminalLink(string url, bool fromSequence, IReadOnlyList<TerminalLinkSegment> segments)
        {
            Url = url ?? throw new ArgumentNullException(nameof(url));
            FromSequence = fromSequence;
            Segments = segments ?? throw new ArgumentNullException(nameof(segments));
        }

        /// <summary>
        /// The link's target: the declared URI for an OSC 8 link, or the matched text otherwise.
        /// </summary>
        public string Url { get; }

        /// <summary>
        /// Whether the program declared this link with OSC 8, rather than the text merely looking
        /// like a URL.
        /// </summary>
        /// <remarks>
        /// The same distinction <see cref="UrlClickedEventArgs.FromSequence"/> makes. A declared link's
        /// target need not appear on screen, which is why a host may want to preview it.
        /// </remarks>
        public bool FromSequence { get; }

        /// <summary>
        /// The cells the link covers, one inclusive range per buffer line. A link that wrapped
        /// across the right edge has more than one.
        /// </summary>
        public IReadOnlyList<TerminalLinkSegment> Segments { get; }
    }

    /// <summary>
    /// One line's worth of a <see cref="TerminalLink"/>.
    /// </summary>
    /// <param name="Line">
    /// The absolute buffer line, scrollback included. Subtract <see cref="TerminalView.ViewportY"/> for
    /// the row on screen.
    /// </param>
    /// <param name="StartColumn">The first column covered, inclusive.</param>
    /// <param name="EndColumn">The last column covered, inclusive.</param>
    public readonly record struct TerminalLinkSegment(int Line, int StartColumn, int EndColumn);

    /// <summary>
    /// Provides data for <see cref="TerminalView.HoveredLinkChanged"/>.
    /// </summary>
    public class HoveredLinkChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="HoveredLinkChangedEventArgs"/> class.
        /// </summary>
        public HoveredLinkChangedEventArgs(TerminalLink? previousLink, TerminalLink? link)
        {
            PreviousLink = previousLink;
            Link = link;
        }

        /// <summary>The link that was hovered before, or <see langword="null"/> if none was.</summary>
        public TerminalLink? PreviousLink { get; }

        /// <summary>The link now hovered, or <see langword="null"/> when the pointer left it.</summary>
        public TerminalLink? Link { get; }
    }
}
