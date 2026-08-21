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
        /// Initializes a new instance of the <see cref="UrlClickedEventArgs"/> class.
        /// </summary>
        /// <param name="url">The url that was clicked.</param>
        public UrlClickedEventArgs(string url)
        {
            Url = url;
        }
    }
}
