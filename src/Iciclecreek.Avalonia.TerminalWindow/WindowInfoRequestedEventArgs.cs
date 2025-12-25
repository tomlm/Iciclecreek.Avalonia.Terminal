using System;
using XT = global::XTerm;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// EventArgs for the WindowInfoRequested event.
    /// The handler should set the appropriate response properties based on the Request type.
    /// </summary>
    public class WindowInfoRequestedEventArgs : EventArgs
    {
        /// <summary>
        /// The type of window information being requested.
        /// </summary>
        public XT.Common.WindowInfoRequest Request { get; }

        /// <summary>
        /// Set to true if the request was handled and a response should be sent.
        /// </summary>
        public bool Handled { get; set; }

        /// <summary>
        /// For State request: true if window is iconified (minimized), false otherwise.
        /// </summary>
        public bool IsIconified { get; set; }

        /// <summary>
        /// For Position request: X coordinate of window position in pixels.
        /// </summary>
        public int X { get; set; }

        /// <summary>
        /// For Position request: Y coordinate of window position in pixels.
        /// </summary>
        public int Y { get; set; }

        /// <summary>
        /// For SizePixels/ScreenSizePixels request: Width in pixels.
        /// </summary>
        public int WidthPixels { get; set; }

        /// <summary>
        /// For SizePixels/ScreenSizePixels request: Height in pixels.
        /// </summary>
        public int HeightPixels { get; set; }

        /// <summary>
        /// For CellSizePixels request: Cell width in pixels.
        /// </summary>
        public int CellWidth { get; set; }

        /// <summary>
        /// For CellSizePixels request: Cell height in pixels.
        /// </summary>
        public int CellHeight { get; set; }

        /// <summary>
        /// For Title/IconTitle request: The title string.
        /// </summary>
        public string? Title { get; set; }

        public WindowInfoRequestedEventArgs(XT.Common.WindowInfoRequest request)
        {
            Request = request;
        }
    }
}
