using System;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// EventArgs for the ProcessExited event.
    /// </summary>
    public class ProcessExitedEventArgs : EventArgs
    {
        public int ExitCode { get; }

        public ProcessExitedEventArgs(int exitCode)
        {
            ExitCode = exitCode;
        }
    }
}
