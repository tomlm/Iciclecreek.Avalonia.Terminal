using System;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// Provides data for the event raised when a url in the terminal is Ctrl+Clicked.
    /// </summary>
    /// <remarks>
    /// The terminal only reports the click; it never opens the url itself. Deciding whether and how to
    /// launch it is left to the consumer, which should validate the scheme before handing it to a browser.
    /// </remarks>
    public class UrlClickedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the url that was clicked, as it appeared in the terminal buffer.
        /// </summary>
        public string Url { get; }

        /// <summary>
        /// Whether the program declared this link with OSC 8, rather than the text merely looking
        /// like a URL.
        /// </summary>
        /// <remarks>
        /// The two deserve different trust, so the host is told which it has. A declared link is a
        /// statement of intent from the program, and its target need not appear on screen at all —
        /// which is the point of OSC 8 and also the reason a host may want to confirm before
        /// following one. A matched link is a guess about characters the user can already read.
        /// </remarks>
        public bool FromSequence { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="UrlClickedEventArgs"/> class.
        /// </summary>
        /// <param name="url">The url that was clicked.</param>
        public UrlClickedEventArgs(string url, bool fromSequence = false)
        {
            Url = url;
            FromSequence = fromSequence;
        }
    }
}
