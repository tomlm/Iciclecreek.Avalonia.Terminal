using System;

namespace Iciclecreek.Terminal
{
    public class UrlClickedEventArgs : EventArgs
    {
        public string Url { get; }

        public UrlClickedEventArgs(string url)
        {
            Url = url;
        }
    }
}
