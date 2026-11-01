using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Nothing the user types or pastes, and nothing the program sends, reaches the control's
/// Debug/Trace output.
/// </summary>
/// <remarks>
/// <para>A host that bridges <see cref="Trace.Listeners"/> into its own log gets every
/// <c>Debug.WriteLine</c> the control makes. OnTextInput used to write each keystroke's text there,
/// so a typed password landed in the host's log file one character per line -- and with no debugger
/// attached, the default listener on Unix also passes it to syslog.</para>
/// <para>In .NET, <c>Debug.WriteLine</c> is routed through <see cref="Trace.Listeners"/>, so a
/// listener added here sees the control's Debug output. That only holds while the control is built
/// with DEBUG defined (the test build's Debug configuration); the sanity check below proves the
/// capture worked rather than letting an empty log pass for the wrong reason.</para>
/// </remarks>
[TestFixture]
public class NoInputInDiagnosticsTests
{
    private const string Esc = "\u001b";
    private const string Bel = "\u0007";

    /// <summary>
    /// The secret. Its last character appears nowhere else in the control's messages, so a path that
    /// logs one character at a time -- the key path does -- is caught by that character alone.
    /// </summary>
    private const string Sentinel = "S3CR3T-sentinel-Ж";
    private const char Marker = 'Ж';

    private sealed class Capture : TraceListener
    {
        private readonly StringBuilder _text = new();
        public override void Write(string? message) { lock (_text) _text.Append(message); }
        public override void WriteLine(string? message) { lock (_text) _text.AppendLine(message); }
        public string Text { get { lock (_text) return _text.ToString(); } }
    }

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static void Press(TerminalView v, Key k, KeyModifiers m, string? symbol)
        => v.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = m, KeySymbol = symbol });

    private static void TypeText(TerminalView v, string text)
        => v.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });

    [AvaloniaTest]
    public async Task Typed_pasted_and_program_text_never_reaches_Debug_or_Trace()
    {
        var capture = new Capture();
        Trace.Listeners.Add(capture);

        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var pty = new RecordingConnection();
            view.AttachConnection(pty);
            view.Focus();
            Assert.That(view.IsFocused, Is.True, "sanity: OnTextInput returns early without focus");

            // A host handler that quotes what it was given in its exception: the callback drain must
            // log the failure without the message.
            view.NotificationRequested += (_, _) => throw new InvalidOperationException($"bad body: {Sentinel}");

            // Typed text, which is also how an IME commit and a dead-key result arrive.
            TypeText(view, Sentinel);
            await PtyWaits.AwaitOutput(pty);

            // The same text under Win32 input mode, where composed text is sent as records.
            view.Terminal.Write($"{Esc}[?9001h");
            TypeText(view, Sentinel);
            view.Terminal.Write($"{Esc}[?9001l");

            // Key chords, one character at a time, as a keyboard sends it.
            foreach (var c in Sentinel)
                Press(view, Key.None, KeyModifiers.None, c.ToString());
            Press(view, Key.None, KeyModifiers.Alt, Marker.ToString());

            // A paste and the host's own input.
            var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
            await clipboard.SetTextAsync(Sentinel);
            await view.PasteAsync();
            await view.SendInputAsync(Sentinel);

            // What the program sends: a title, an OSC 52 clipboard write, a notification.
            view.Terminal.Write($"{Esc}]2;{Sentinel}{Bel}");
            view.Terminal.Write($"{Esc}]52;c;{B64(Sentinel)}{Bel}");
            view.Terminal.Write($"{Esc}]9;{Sentinel}{Bel}");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();

            Assert.That(pty.Written, Does.Contain("S3CR3T"), "sanity: the input really reached the process");

            var log = capture.Text;
#if DEBUG
            Assert.That(log, Does.Contain("[TerminalView] OnTextInput"),
                "sanity: the control's Debug output reaches Trace listeners, or this test proves nothing");
            Assert.That(log, Does.Contain("host callback failed"),
                "sanity: the throwing host handler was reached and logged");
#endif
            Assert.That(log, Does.Not.Contain(Sentinel));
            Assert.That(log, Does.Not.Contain("S3CR3T"));
            Assert.That(log, Does.Not.Contain(Marker.ToString()), "a single typed character was logged");
        }
        finally
        {
            Trace.Listeners.Remove(capture);
            window.Close();
        }
    }
}
