using System;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// EventArgs for the WindowResized event.
    /// </summary>
    public class WindowResizedEventArgs : EventArgs
    {
        public int Width { get; }
        public int Height { get; }

        public WindowResizedEventArgs(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }
}
