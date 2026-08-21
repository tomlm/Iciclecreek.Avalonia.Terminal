using System;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// Provides data for the <see cref="TerminalView.UrlClicked"/> event,
    /// raised when the user Ctrl+Clicks a URL in the terminal.
    /// </summary>
    public class UrlClickedEventArgs : EventArgs
    {
        /// <summary>Gets the URL that was clicked.</summary>
        public string Url { get; }

        /// <summary>Initializes a new instance with the given URL.</summary>
        public UrlClickedEventArgs(string url)
        {
            Url = url;
        }
    }
}
