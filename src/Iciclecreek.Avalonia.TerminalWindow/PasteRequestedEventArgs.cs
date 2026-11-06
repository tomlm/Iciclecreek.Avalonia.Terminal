using System;
using System.Threading.Tasks;

namespace Iciclecreek.Terminal
{
    /// <summary>Where a paste came from.</summary>
    public enum PasteSource
    {
        /// <summary>A paste key: Ctrl+Shift+V, Shift+Insert, Cmd+V, or Ctrl+V under <see cref="ShortcutMode.Desktop"/>.</summary>
        Keyboard,

        /// <summary>The host called <see cref="TerminalView.PasteAsync"/>.</summary>
        Api,

        /// <summary>A right-click with nothing selected.</summary>
        RightClick,
    }

    /// <summary>
    /// Provides data for <see cref="TerminalView.PasteRequested"/>: the text about to be pasted, which a
    /// handler may change or cancel.
    /// </summary>
    /// <remarks>
    /// <para>A handler that needs to ask the user first calls <see cref="GetDeferral"/>, awaits its prompt,
    /// sets <see cref="Text"/> or <see cref="Cancel"/>, and disposes the deferral. Nothing is sent until every
    /// deferral is disposed.</para>
    /// <para>The terminal does its own framing after the event, with the program's modes as they are then:
    /// bracketing (<c>ESC[200~</c>…<c>ESC[201~</c>), the paste announcement (mode 5522), replacing a keyboard
    /// selection, and declining while the keyboard is locked. So <see cref="Text"/> is the bare text, and a
    /// handler never adds bracketing itself.</para>
    /// </remarks>
    public class PasteRequestedEventArgs : EventArgs
    {
        private readonly object _gate = new();
        private string _text;
        private int _deferrals;
        private TaskCompletionSource? _completed;

        /// <summary>
        /// Initializes a new instance of the <see cref="PasteRequestedEventArgs"/> class.
        /// </summary>
        public PasteRequestedEventArgs(string text, PasteSource source, bool bracketed, bool announced)
        {
            _text = text ?? "";
            Source = source;
            Bracketed = bracketed;
            Announced = announced;
        }

        /// <summary>
        /// The text to paste: the clipboard's text, as read. Set it to paste something else; set it empty to
        /// paste nothing. For an announced paste this is the <c>text/plain</c> the program can fetch.
        /// </summary>
        public string Text
        {
            get => _text;
            set => _text = value ?? "";
        }

        /// <summary>Set to <see langword="true"/> to send nothing.</summary>
        public bool Cancel { get; set; }

        /// <summary>Whether the program has bracketed paste (mode 2004) on as the event is raised.</summary>
        public bool Bracketed { get; }

        /// <summary>
        /// Whether the program has asked for pastes to be announced (mode 5522) as the event is raised. It
        /// then fetches the clipboard's formats itself, <see cref="Text"/> among them.
        /// </summary>
        public bool Announced { get; }

        /// <summary>What started the paste.</summary>
        public PasteSource Source { get; }

        /// <summary>
        /// Holds the paste until the returned object is disposed, for a handler that awaits something. Each
        /// call needs its own dispose; disposing one twice counts once.
        /// </summary>
        public IDisposable GetDeferral()
        {
            lock (_gate)
            {
                _deferrals++;
                return new Deferral(this);
            }
        }

        /// <summary>Completes once every deferral handed out has been disposed.</summary>
        internal Task WhenDeferralsCompleteAsync()
        {
            lock (_gate)
            {
                if (_deferrals == 0)
                    return Task.CompletedTask;
                _completed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _completed.Task;
            }
        }

        private void Release()
        {
            TaskCompletionSource? done = null;
            lock (_gate)
            {
                if (--_deferrals == 0)
                    done = _completed;
            }
            done?.TrySetResult();
        }

        private sealed class Deferral(PasteRequestedEventArgs owner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
                    owner.Release();
            }
        }
    }
}
