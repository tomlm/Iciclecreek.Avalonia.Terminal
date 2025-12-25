using System;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// EventArgs for the TitleChanged event.
    /// </summary>
    public class TitleChangedEventArgs : EventArgs
    {
        public string Title { get; }

        public TitleChangedEventArgs(string title)
        {
            Title = title;
        }
    }
}
