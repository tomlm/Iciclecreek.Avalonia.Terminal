using System;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// EventArgs for the TitleChanged event.
    /// </summary>
    public class TitleChangedEventArgs : EventArgs
    {
        public string Title { get; }
        public bool Handled { get; set; }

        public TitleChangedEventArgs(string title)
        {
            Title = title;
        }
    }
}
