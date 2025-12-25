using System;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// EventArgs for the WindowMoved event.
    /// </summary>
    public class WindowMovedEventArgs : EventArgs
    {
        public int X { get; }
        public int Y { get; }

        public WindowMovedEventArgs(int x, int y)
        {
            X = x;
            Y = y;
        }
    }
}
