using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Iciclecreek.Terminal
{
    public class TerminalControl : TemplatedControl, IDisposable
    {
        private TerminalView? _terminalView;
        private ScrollBar? _scrollBar;
        private string? _currentDirectory;


        public static readonly StyledProperty<TextDecorationLocation?> TextDecorationsProperty =
            AvaloniaProperty.Register<TerminalControl, TextDecorationLocation?>(
                nameof(TextDecorations),
                defaultValue: null);

        public static readonly StyledProperty<IBrush> SelectionBrushProperty =
            AvaloniaProperty.Register<TerminalControl, IBrush>(
                nameof(SelectionBrush),
                defaultValue: new SolidColorBrush(Color.FromArgb(128, 0, 120, 215)));

        public static readonly StyledProperty<string> ProcessProperty =
            AvaloniaProperty.Register<TerminalControl, string>(
                nameof(Process),
                defaultValue: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash");

        public static readonly StyledProperty<IList<string>> ArgsProperty =
            AvaloniaProperty.Register<TerminalControl, IList<string>>(
                nameof(Args),
                defaultValue: System.Array.Empty<string>());

        // Matches TerminalView and TerminalWindow, which both default to the current directory. A null here
        // was not merely a different default: the control template binds this onto the view, so the null
        // overwrote the view's own sensible default on the way through.
        public static readonly StyledProperty<string?> StartingDirectoryProperty =
            AvaloniaProperty.Register<TerminalControl, string?>(
                nameof(StartingDirectory),
                defaultValue: Environment.CurrentDirectory);

        public static readonly DirectProperty<TerminalControl, string?> CurrentDirectoryProperty =
            AvaloniaProperty.RegisterDirect<TerminalControl, string?>(
                nameof(CurrentDirectory),
                o => o.CurrentDirectory);

        public static readonly StyledProperty<int> BufferSizeProperty =
                  AvaloniaProperty.Register<TerminalControl, int>(
                      nameof(BufferSize),
                      defaultValue: 1000);

        public static readonly StyledProperty<XTerm.Options.TerminalOptions?> OptionsProperty =
            AvaloniaProperty.Register<TerminalControl, XTerm.Options.TerminalOptions?>(
                nameof(Options),
                defaultValue: null);

        // A real StyledProperty rather than a forwarder to _terminalView. As a forwarder its setter was
        // guarded by `if (_terminalView != null)`, so any value set before the template was applied — which
        // includes every value set from XAML or an object initializer — was silently dropped and never
        // re-applied. Registered here, the value survives and reaches the view through the template.
        public static readonly StyledProperty<bool> ShowCaretOnClickProperty =
            AvaloniaProperty.Register<TerminalControl, bool>(
                nameof(ShowCaretOnClick),
                defaultValue: false);

        /// <inheritdoc cref="TerminalView.VerbatimCommandLineProperty"/>
        public static readonly StyledProperty<bool> VerbatimCommandLineProperty =
            AvaloniaProperty.Register<TerminalControl, bool>(
                nameof(VerbatimCommandLine),
                defaultValue: false);

        /// <inheritdoc cref="TerminalView.EnvironmentVariablesProperty"/>
        public static readonly StyledProperty<IDictionary<string, string>?> EnvironmentVariablesProperty =
            AvaloniaProperty.Register<TerminalControl, IDictionary<string, string>?>(
                nameof(EnvironmentVariables),
                defaultValue: null);

        /// <inheritdoc cref="TerminalView.LigaturesProperty"/>
        public static readonly StyledProperty<bool> LigaturesProperty =
            AvaloniaProperty.Register<TerminalControl, bool>(
                nameof(Ligatures),
                defaultValue: false);

        /// <inheritdoc cref="TerminalView.UseSkiaRendererProperty"/>
        public static readonly StyledProperty<bool> UseSkiaRendererProperty =
            AvaloniaProperty.Register<TerminalControl, bool>(
                nameof(UseSkiaRenderer),
                defaultValue: false);

        // Cursor appearance. Real StyledProperties with the same defaults as TerminalView's, reaching the
        // view through the template — a forwarder would drop anything set before the template applied, which
        // for appearance properties is most of the time.
        public static readonly StyledProperty<Color> CursorColorProperty =
            AvaloniaProperty.Register<TerminalControl, Color>(
                nameof(CursorColor),
                defaultValue: Colors.White);

        public static readonly StyledProperty<XTerm.Common.CursorStyle> CursorStyleProperty =
            AvaloniaProperty.Register<TerminalControl, XTerm.Common.CursorStyle>(
                nameof(CursorStyle),
                defaultValue: XTerm.Common.CursorStyle.Bar);

        public static readonly StyledProperty<bool> CursorBlinkProperty =
            AvaloniaProperty.Register<TerminalControl, bool>(
                nameof(CursorBlink),
                defaultValue: true);

        public static readonly StyledProperty<int> CursorBlinkRateProperty =
            AvaloniaProperty.Register<TerminalControl, int>(
                nameof(CursorBlinkRate),
                defaultValue: 530);

        /// <inheritdoc cref="TerminalView.ShellReady"/>
        public event EventHandler? ShellReady;
        public event EventHandler<ProcessExitedEventArgs>? ProcessExited;
        /// <inheritdoc cref="TerminalView.OutputReceived"/>
        public event EventHandler<OutputReceivedEventArgs>? OutputReceived;
        /// <inheritdoc cref="TerminalView.UrlClicked"/>
        public event EventHandler<UrlClickedEventArgs>? UrlClicked;

        /// <inheritdoc cref="TerminalView.NotificationRequested"/>
        public event EventHandler<TerminalNotificationEventArgs> NotificationRequested
        {
            add => AddHandler(TerminalView.NotificationRequestedEvent, value);
            remove => RemoveHandler(TerminalView.NotificationRequestedEvent, value);
        }

        /// <inheritdoc cref="TerminalView.AttentionRequested"/>
        public event EventHandler<TerminalAttentionEventArgs> AttentionRequested
        {
            add => AddHandler(TerminalView.AttentionRequestedEvent, value);
            remove => RemoveHandler(TerminalView.AttentionRequestedEvent, value);
        }


        /// <summary>
        /// Gets or sets the brush used to render selected terminal text.
        /// </summary>
        public IBrush SelectionBrush
        {
            get => GetValue(SelectionBrushProperty);
            set => SetValue(SelectionBrushProperty, value);
        }

        /// <summary>
        /// Gets or sets the executable or shell to launch in the terminal.
        /// </summary>
        public string Process
        {
            get => GetValue(ProcessProperty);
            set => SetValue(ProcessProperty, value);
        }

        /// <summary>
        /// Gets or sets the command-line arguments passed to <see cref="Process"/> when launching.
        /// </summary>
        public IList<string> Args
        {
            get => GetValue(ArgsProperty);
            set => SetValue(ArgsProperty, value);
        }

        /// <summary>
        /// Gets or sets the initial working directory used when the PTY process is started.
        /// </summary>
        public string? StartingDirectory
        {
            get => GetValue(StartingDirectoryProperty);
            set => SetValue(StartingDirectoryProperty, value);
        }

        /// <summary>
        /// Gets the current working directory reported by the running terminal session.
        /// </summary>
        public string? CurrentDirectory => _currentDirectory;

        /// <summary>
        /// Gets or sets the terminal scrollback buffer size in lines.
        /// </summary>
        public int BufferSize
        {
            get => GetValue(BufferSizeProperty);
            set => SetValue(BufferSizeProperty, value);
        }

        /// <summary>
        /// Gets or sets the terminal emulation options used by the inner <see cref="TerminalView"/>.
        /// </summary>
        public XTerm.Options.TerminalOptions? Options
        {
            get => GetValue(OptionsProperty);
            set => SetValue(OptionsProperty, value);
        }

        private static bool _stylesLoaded = false;

        static TerminalControl()
        {
            // Automatically load the default theme styles
            LoadDefaultStyles();

            // TerminalControl is focusable - it will delegate to inner TerminalView
            FocusableProperty.OverrideDefaultValue<TerminalControl>(true);

            // A terminal must not fall back to the proportional system UI font — see
            // TerminalView.DefaultFontFamily. This is a DEFAULT, so an inherited value or an explicit
            // style from the host still wins; it only decides what happens when nobody said anything.
            FontFamilyProperty.OverrideDefaultValue<TerminalControl>(TerminalView.DefaultFontFamily);
        }

        private static void LoadDefaultStyles()
        {
            if (_stylesLoaded || Application.Current == null)
                return;

            var uri = new Uri("avares://Iciclecreek.Avalonia.Terminal/Themes/Generic.axaml");

            // Check if styles are already loaded to avoid duplicates
            foreach (var style in Application.Current.Styles)
            {
                if (style is global::Avalonia.Markup.Xaml.Styling.StyleInclude include && include.Source == uri)
                {
                    _stylesLoaded = true;
                    return;
                }
            }

            var styles = (IStyle)new global::Avalonia.Markup.Xaml.Styling.StyleInclude(uri) { Source = uri };

            // FIRST, not last. Later styles win in Avalonia, so appending put this library's default
            // theme above everything the application had already set -- a host that styled
            // TerminalControl in App.axaml, which is where an application's styles go, was overruled
            // by the control it was styling. Inserting at the front makes it what it is meant to be:
            // a default, there when nobody said otherwise and beaten by anybody who did.
            Application.Current.Styles.Insert(0, styles);
            _stylesLoaded = true;
        }

        public TerminalControl()
        {
            // The retry for a static constructor that ran before there was an Application to add
            // styles to.
            //
            // It used to sit in OnApplyTemplate, where it could never run: this control's template
            // COMES from those styles, so if they are missing there is no template, and a hook that
            // fires when a template is applied never fires at all. The fallback was unreachable
            // exactly when it was needed, which is the only time it was needed.
            //
            // A constructor runs either way, and runs before styling, which is early enough for the
            // styles to be found.
            LoadDefaultStyles();
        }

        /// <summary>
        /// Releases the terminal behind this control, and the process with it.
        /// </summary>
        /// <remarks>
        /// Forwards to the inner view, which owns everything. Safe before the template has been
        /// applied, when there is no view yet, and safe to call twice.
        ///
        /// Explicit rather than driven by a lifecycle hook, for the reason
        /// <see cref="TerminalView.Dispose"/> gives: detaching from the logical tree is how this
        /// control is MOVED, not how it ends, so tearing down there would kill a terminal being
        /// re-parented between panels.
        /// </remarks>
        public void Dispose()
        {
            _terminalView?.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Gets the underlying <see cref="XTerm.Terminal"/> instance.
        /// </summary>
        public XTerm.Terminal Terminal => _terminalView!.Terminal;

        /// <inheritdoc cref="TerminalView.InputSent"/>
        /// <remarks>
        /// <para>The control's OWN event, forwarded from whichever view is current -- the same shape
        /// as ProcessExited, ShellReady, OutputReceived and UrlClicked beside it, and it should
        /// always have been.</para>
        /// <para>It used to add and remove straight onto <c>_terminalView</c>, which failed at both
        /// ends of that view's life. A handler added BEFORE the template was dropped by an if with no
        /// else -- silently, since += appears to have worked -- and subscribing early is the ordinary
        /// case, being what a XAML attribute does. A handler added AFTER stayed on the view it was
        /// added to, so re-applying the template left it on an orphan: leaked, and no longer firing
        /// for the control.</para>
        /// <para>Owning the list fixes both, and needs no buffering of pending handlers: there is
        /// nothing to buffer when the subscription was never on the view to begin with.</para>
        /// <para>Note the sender is the CONTROL, not the view -- again matching the four events
        /// beside it.</para>
        /// </remarks>
        public event EventHandler<string>? InputSent;

        private void OnTerminalViewInputSent(object? sender, string data) => InputSent?.Invoke(this, data);


        /// <summary>
        /// Waits for the terminal process to exit, with a timeout in milliseconds.
        /// </summary>
        /// <param name="ms">The maximum amount of time to wait, in milliseconds.</param>
        public void WaitForExit(int ms) => _terminalView!.WaitForExit(ms);

        /// <summary>
        /// Terminates the running terminal process.
        /// </summary>
        public void Kill() => _terminalView!.Kill();

        /// <inheritdoc cref="TerminalView.SendInputAsync"/>
        /// <remarks>
        /// Null-safe on the inner view deliberately. A host can hold a reference to this control before its
        /// template has been applied, and a method that exists to inject text should not be the one that
        /// throws a NullReferenceException for being called a moment early. Every forwarder below follows
        /// the same rule.
        /// </remarks>
        public Task SendInputAsync(string text, CancellationToken cancellationToken = default)
            => _terminalView?.SendInputAsync(text, cancellationToken) ?? Task.CompletedTask;

        /// <summary>
        /// Text decorations applied to terminal text.
        /// </summary>
        /// <remarks>
        /// The static property was registered from the start but had no CLR property and no template
        /// binding, so it was reachable from XAML, silently stored, and read by nothing. It compiled only
        /// because <c>nameof(TextDecorations)</c> resolved to the <see cref="Avalonia.Media.TextDecorations"/>
        /// static class rather than to a member of this type.
        /// </remarks>
        public TextDecorationLocation? TextDecorations
        {
            get => GetValue(TextDecorationsProperty);
            set => SetValue(TextDecorationsProperty, value);
        }

        /// <inheritdoc cref="TerminalView.CursorColorProperty"/>
        public Color CursorColor
        {
            get => GetValue(CursorColorProperty);
            set => SetValue(CursorColorProperty, value);
        }

        /// <inheritdoc cref="TerminalView.CursorStyleProperty"/>
        /// <inheritdoc cref="TerminalView.LigaturesProperty"/>
        public bool Ligatures
        {
            get => GetValue(LigaturesProperty);
            set => SetValue(LigaturesProperty, value);
        }

        /// <inheritdoc cref="TerminalView.UseSkiaRendererProperty"/>
        public bool UseSkiaRenderer
        {
            get => GetValue(UseSkiaRendererProperty);
            set => SetValue(UseSkiaRendererProperty, value);
        }

        public XTerm.Common.CursorStyle CursorStyle
        {
            get => GetValue(CursorStyleProperty);
            set => SetValue(CursorStyleProperty, value);
        }

        /// <inheritdoc cref="TerminalView.CursorBlinkProperty"/>
        public bool CursorBlink
        {
            get => GetValue(CursorBlinkProperty);
            set => SetValue(CursorBlinkProperty, value);
        }

        /// <inheritdoc cref="TerminalView.CursorBlinkRateProperty"/>
        public int CursorBlinkRate
        {
            get => GetValue(CursorBlinkRateProperty);
            set => SetValue(CursorBlinkRateProperty, value);
        }

        /// <summary>
        /// The absolute line index of the top of the viewport. 0 is the top of the scrollback.
        /// </summary>
        /// <remarks>
        /// Live state rather than configuration, so this forwards to the view instead of being a styled
        /// property: there is nothing meaningful to remember for a terminal that does not exist yet. Reads
        /// as 0 and ignores writes until the template has been applied.
        /// </remarks>
        public int ViewportY
        {
            get => _terminalView?.ViewportY ?? 0;
            set { if (_terminalView != null) _terminalView.ViewportY = value; }
        }

        /// <inheritdoc cref="TerminalView.MaxScrollback"/>
        public int MaxScrollback => _terminalView?.MaxScrollback ?? 0;

        /// <summary>The number of lines visible in the viewport.</summary>
        public int ViewportLines => _terminalView?.ViewportLines ?? 0;

        /// <summary>
        /// True while a full-screen application (vim, htop, less) is using the alternate screen buffer.
        /// </summary>
        public bool IsAlternateBuffer => _terminalView?.IsAlternateBuffer ?? false;

        /// <summary>Copies the current selection to the clipboard. False when there was nothing selected.</summary>
        public Task<bool> CopyAsync() => _terminalView?.CopyAsync() ?? Task.FromResult(false);

        /// <summary>Pastes text from the clipboard into the terminal.</summary>
        public Task PasteAsync() => _terminalView?.PasteAsync() ?? Task.CompletedTask;

        /// <inheritdoc cref="TerminalView.AttachConnection"/>
        /// <exception cref="InvalidOperationException">The template has not been applied yet.</exception>
        /// <remarks>
        /// This one throws rather than silently doing nothing, unlike the other forwarders. Handing over
        /// ownership of a live PTY and having it quietly ignored would leave the caller believing a process
        /// is being displayed when it is not — the failure is worth hearing about. <see cref="LaunchProcess()"/>
        /// takes the same position for the same reason.
        /// </remarks>
        public void AttachConnection(Porta.Pty.IPtyConnection connection)
        {
            if (_terminalView == null)
                ApplyTemplate();

            if (_terminalView == null)
                throw new InvalidOperationException("TerminalControl template has not been applied yet.");

            _terminalView.AttachConnection(connection);
        }

        /// <inheritdoc cref="TerminalView.DetachConnection"/>
        public Porta.Pty.IPtyConnection? DetachConnection() => _terminalView?.DetachConnection();

        /// <inheritdoc cref="TerminalView.IsLive"/>
        public bool IsLive => _terminalView?.IsLive ?? false;

        /// <summary>
        /// Call before removing this control from one visual tree and adding it to another
        /// (e.g. moving between windows). Prevents the PTY process from being killed
        /// during the detach. Pair with <see cref="EndReparent"/> after re-attaching.
        /// </summary>
        public void BeginReparent() => _terminalView?.BeginReparent();

        /// <summary>
        /// Call after the control has been re-attached to a new visual tree to restore
        /// normal cleanup behaviour.
        /// </summary>
        public void EndReparent() => _terminalView?.EndReparent();

        /// <inheritdoc cref="TerminalView.ShowCaretOnClickProperty"/>
        public bool ShowCaretOnClick
        {
            get => GetValue(ShowCaretOnClickProperty);
            set => SetValue(ShowCaretOnClickProperty, value);
        }

        /// <inheritdoc cref="TerminalView.ShortcutModeProperty"/>
        public static readonly StyledProperty<ShortcutMode> ShortcutModeProperty =
            AvaloniaProperty.Register<TerminalControl, ShortcutMode>(
                nameof(ShortcutMode),
                defaultValue: ShortcutMode.Terminal);

        /// <inheritdoc cref="ShortcutModeProperty"/>
        public ShortcutMode ShortcutMode
        {
            get => GetValue(ShortcutModeProperty);
            set => SetValue(ShortcutModeProperty, value);
        }

        /// <inheritdoc cref="TerminalView.CutAsync"/>
        public Task<bool> CutAsync() => _terminalView?.CutAsync() ?? Task.FromResult(false);

        /// <inheritdoc cref="TerminalView.SuppressCursorProperty"/>
        /// <remarks>
        /// Styled and template-bound to PART_TerminalView rather than forwarded onto the inner view: a
        /// forwarder drops any value assigned before the template runs, which is the normal timing for XAML
        /// attributes and object initialisers.
        /// </remarks>
        public static readonly StyledProperty<bool> SuppressCursorProperty =
            AvaloniaProperty.Register<TerminalControl, bool>(
                nameof(SuppressCursor),
                defaultValue: false);

        /// <inheritdoc cref="SuppressCursorProperty"/>
        public bool SuppressCursor
        {
            get => GetValue(SuppressCursorProperty);
            set => SetValue(SuppressCursorProperty, value);
        }

        /// <inheritdoc cref="TerminalView.CharWidth"/>
        public double CharWidth => _terminalView?.CharWidth ?? 0;

        /// <inheritdoc cref="TerminalView.CharHeight"/>
        public double CharHeight => _terminalView?.CharHeight ?? 0;

        /// <inheritdoc cref="TerminalView.CurrentLineText"/>
        public string CurrentLineText => _terminalView?.CurrentLineText ?? string.Empty;

        /// <inheritdoc cref="TerminalView.ClearScreen"/>
        public void ClearScreen() => _terminalView?.ClearScreen();

        // ---- shell integration, forwarded so a host never has to reach for the inner view ------

        /// <inheritdoc cref="TerminalView.GutterWidthProperty"/>
        public static readonly StyledProperty<double> GutterWidthProperty =
            AvaloniaProperty.Register<TerminalControl, double>(nameof(GutterWidth), 0.0);

        /// <inheritdoc cref="TerminalView.GutterWidth"/>
        public double GutterWidth
        {
            get => GetValue(GutterWidthProperty);
            set => SetValue(GutterWidthProperty, value);
        }

        /// <inheritdoc cref="TerminalView.GutterPromptBrushProperty"/>
        public static readonly StyledProperty<IBrush?> GutterPromptBrushProperty =
            AvaloniaProperty.Register<TerminalControl, IBrush?>(nameof(GutterPromptBrush));

        /// <inheritdoc cref="TerminalView.GutterPromptBrush"/>
        public IBrush? GutterPromptBrush
        {
            get => GetValue(GutterPromptBrushProperty);
            set => SetValue(GutterPromptBrushProperty, value);
        }

        /// <inheritdoc cref="TerminalView.GutterSuccessBrushProperty"/>
        public static readonly StyledProperty<IBrush?> GutterSuccessBrushProperty =
            AvaloniaProperty.Register<TerminalControl, IBrush?>(nameof(GutterSuccessBrush));

        /// <inheritdoc cref="TerminalView.GutterSuccessBrush"/>
        public IBrush? GutterSuccessBrush
        {
            get => GetValue(GutterSuccessBrushProperty);
            set => SetValue(GutterSuccessBrushProperty, value);
        }

        /// <inheritdoc cref="TerminalView.GutterFailureBrushProperty"/>
        public static readonly StyledProperty<IBrush?> GutterFailureBrushProperty =
            AvaloniaProperty.Register<TerminalControl, IBrush?>(nameof(GutterFailureBrush));

        /// <inheritdoc cref="TerminalView.GutterFailureBrush"/>
        public IBrush? GutterFailureBrush
        {
            get => GetValue(GutterFailureBrushProperty);
            set => SetValue(GutterFailureBrushProperty, value);
        }

        /// <inheritdoc cref="TerminalView.ScrollToPreviousPrompt"/>
        public bool ScrollToPreviousPrompt() => _terminalView?.ScrollToPreviousPrompt() ?? false;

        /// <inheritdoc cref="TerminalView.ScrollToNextPrompt"/>
        public bool ScrollToNextPrompt() => _terminalView?.ScrollToNextPrompt() ?? false;

        /// <inheritdoc cref="TerminalView.SelectCommandOutput"/>
        public bool SelectCommandOutput(int bufferRow) => _terminalView?.SelectCommandOutput(bufferRow) ?? false;

        /// <inheritdoc cref="TerminalView.VisibleMarks"/>
        public IReadOnlyList<TerminalView.VisibleMark> VisibleMarks
            => _terminalView?.VisibleMarks ?? Array.Empty<TerminalView.VisibleMark>();

        // ---- scrollback search, forwarded so a host never reaches for the inner view -----------

        /// <inheritdoc cref="TerminalView.SearchHighlightBrushProperty"/>
        public static readonly StyledProperty<IBrush> SearchHighlightBrushProperty =
            AvaloniaProperty.Register<TerminalControl, IBrush>(
                nameof(SearchHighlightBrush),
                defaultValue: new SolidColorBrush(Color.FromArgb(90, 240, 180, 41)));

        /// <inheritdoc cref="TerminalView.SearchHighlightBrush"/>
        public IBrush SearchHighlightBrush
        {
            get => GetValue(SearchHighlightBrushProperty);
            set => SetValue(SearchHighlightBrushProperty, value);
        }

        /// <inheritdoc cref="TerminalView.SearchCurrentBrushProperty"/>
        public static readonly StyledProperty<IBrush> SearchCurrentBrushProperty =
            AvaloniaProperty.Register<TerminalControl, IBrush>(
                nameof(SearchCurrentBrush),
                defaultValue: new SolidColorBrush(Color.FromArgb(160, 240, 180, 41)));

        /// <inheritdoc cref="TerminalView.SearchCurrentBrush"/>
        public IBrush SearchCurrentBrush
        {
            get => GetValue(SearchCurrentBrushProperty);
            set => SetValue(SearchCurrentBrushProperty, value);
        }

        /// <inheritdoc cref="TerminalView.FindInBuffer"/>
        public int FindInBuffer(string needle, XTerm.Search.SearchOptions options = default)
            => _terminalView?.FindInBuffer(needle, options) ?? 0;

        /// <inheritdoc cref="TerminalView.FindNext"/>
        public bool FindNext() => _terminalView?.FindNext() ?? false;

        /// <inheritdoc cref="TerminalView.FindPrevious"/>
        public bool FindPrevious() => _terminalView?.FindPrevious() ?? false;

        /// <inheritdoc cref="TerminalView.ClearSearch"/>
        public void ClearSearch() => _terminalView?.ClearSearch();

        /// <inheritdoc cref="TerminalView.SearchHitCount"/>
        public int SearchHitCount => _terminalView?.SearchHitCount ?? 0;

        /// <inheritdoc cref="TerminalView.SearchCurrentIndex"/>
        public int SearchCurrentIndex => _terminalView?.SearchCurrentIndex ?? -1;

        /// <inheritdoc cref="TerminalView.SearchTruncated"/>
        public bool SearchTruncated => _terminalView?.SearchTruncated ?? false;


        /// <inheritdoc cref="TerminalView.Refresh"/>
        public void Refresh() => _terminalView?.Refresh();

        /// <inheritdoc cref="TerminalView.OutputReceivedOnReadTaskProperty"/>
        /// <remarks>
        /// Styled and template-bound to PART_TerminalView rather than forwarded onto the inner view. A
        /// forwarder drops any value assigned before the template runs — the normal timing for XAML
        /// attributes, styles and object initialisers — which for this property would silently leave a
        /// consumer on UI-thread delivery having asked for the read task.
        /// </remarks>
        public static readonly StyledProperty<bool> OutputReceivedOnReadTaskProperty =
            AvaloniaProperty.Register<TerminalControl, bool>(
                nameof(OutputReceivedOnReadTask),
                defaultValue: false);

        /// <inheritdoc cref="OutputReceivedOnReadTaskProperty"/>
        public bool OutputReceivedOnReadTask
        {
            get => GetValue(OutputReceivedOnReadTaskProperty);
            set => SetValue(OutputReceivedOnReadTaskProperty, value);
        }

        /// <inheritdoc cref="TerminalView.AutoScrollToBottomProperty"/>
        /// <remarks>
        /// A styled property template-bound to PART_TerminalView, not a CLR forwarder onto the inner view.
        /// A forwarder drops any value assigned before the template runs — the normal timing for XAML
        /// attributes, styles and object initialisers — so <c>AutoScrollToBottom="False"</c> would silently
        /// read back as <c>true</c>, and it could not be bound or styled at all.
        /// </remarks>
        public static readonly StyledProperty<bool> AutoScrollToBottomProperty =
            AvaloniaProperty.Register<TerminalControl, bool>(
                nameof(AutoScrollToBottom),
                defaultValue: true);

        /// <inheritdoc cref="AutoScrollToBottomProperty"/>
        public bool AutoScrollToBottom
        {
            get => GetValue(AutoScrollToBottomProperty);
            set => SetValue(AutoScrollToBottomProperty, value);
        }

        /// <inheritdoc cref="TerminalView.IsFollowingTail"/>
        public bool IsFollowingTail => _terminalView?.IsFollowingTail ?? true;

        /// <inheritdoc cref="TerminalView.FollowTail"/>
        public void FollowTail() => _terminalView?.FollowTail();

        /// <inheritdoc cref="TerminalView.VerbatimCommandLineProperty"/>
        public bool VerbatimCommandLine
        {
            get => GetValue(VerbatimCommandLineProperty);
            set => SetValue(VerbatimCommandLineProperty, value);
        }

        /// <inheritdoc cref="TerminalView.EnvironmentVariablesProperty"/>
        public IDictionary<string, string>? EnvironmentVariables
        {
            get => GetValue(EnvironmentVariablesProperty);
            set => SetValue(EnvironmentVariablesProperty, value);
        }

        /// <summary>
        /// Gets the exit code of the launched process after it has terminated.
        /// </summary>
        public int ExitCode => _terminalView!.ExitCode;

        /// <summary>
        /// Gets the operating system process identifier of the launched terminal process.
        /// </summary>
        public int Pid => _terminalView!.Pid;

        /// <summary>
        /// Launch the terminal process with the current Process, Args, and StartingDirectory properties. If the process is already running, it will be
        /// terminated and replaced with a new instance using the updated properties. 
        /// </summary>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public virtual async Task LaunchProcess()
        {
            if (_terminalView == null)
            {
                ApplyTemplate();
            }

            if (_terminalView == null)
                throw new InvalidOperationException("TerminalControl template has not been applied yet.");

            await _terminalView.LaunchProcess();

            Dispatcher.UIThread.Post(() =>
            {
                if (_terminalView != null && !_terminalView.IsFocused)
                {
                    _terminalView.Focus();
                }
            }, DispatcherPriority.Input);
        }

        /// <summary>
        /// Launch the terminal process with the specified parameters, updating the Process, Args, and StartingDirectory properties. 
        /// If the process is already running, it will be terminated and replaced with a new instance using the updated properties.
        /// </summary>
        /// <param name="startingDirectory"></param>
        /// <param name="process"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public virtual async Task LaunchProcess(string? startingDirectory, string process, params string[] args)
        {
            StartingDirectory = startingDirectory;
            Process = process;
            Args = args ?? Array.Empty<string>();
            await LaunchProcess();
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);

            // Only focus the inner TerminalView if it doesn't already have focus
            if (_terminalView != null && !_terminalView.IsFocused)
            {
                // Defer until layout is ready
                Dispatcher.UIThread.Post(() =>
                {
                    if (_terminalView != null && !_terminalView.IsFocused)
                    {
                        _terminalView.Focus();
                    }
                }, DispatcherPriority.Input);
            }
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            // Unsubscribe from old controls
            if (_scrollBar != null)
            {
                _scrollBar.Scroll -= OnScrollBarScroll;
            }

            if (_terminalView != null)
            {
                _terminalView.PropertyChanged -= OnTerminalViewPropertyChanged;
                _terminalView.ProcessExited -= OnTerminalViewProcessExited;
                _terminalView.ShellReady -= OnTerminalViewShellReady;
                _terminalView.OutputReceived -= OnTerminalViewOutputReceived;
                _terminalView.UrlClicked -= OnTerminalViewUrlClicked;
                _terminalView.InputSent -= OnTerminalViewInputSent;
            }

            SetCurrentDirectory(null);

            // Get template parts
            _terminalView = e.NameScope.Find<TerminalView>("PART_TerminalView");
            _scrollBar = e.NameScope.Find<ScrollBar>("PART_ScrollBar");

            // The scrollbar, and ONLY the scrollbar. It used to gate everything below it too, so a
            // template without a PART_ScrollBar -- a host that does not want one, which is a
            // reasonable thing to want -- lost every event this control forwards, the Options bridge
            // and the current directory along with it. All of that belongs to the view; none of it
            // has anything to do with whether there is a scrollbar next to it.
            if (_scrollBar != null && _terminalView != null)
            {
                _scrollBar.Scroll += OnScrollBarScroll;
            }

            if (_terminalView != null)
            {
                _terminalView.Options = Options ?? new XTerm.Options.TerminalOptions();
                _terminalView.PropertyChanged += OnTerminalViewPropertyChanged;
                _terminalView.ProcessExited += OnTerminalViewProcessExited;
                _terminalView.ShellReady += OnTerminalViewShellReady;
                _terminalView.OutputReceived += OnTerminalViewOutputReceived;
                _terminalView.UrlClicked += OnTerminalViewUrlClicked;
                _terminalView.InputSent += OnTerminalViewInputSent;
                SetCurrentDirectory(_terminalView.CurrentDirectory);

                // Adopt whatever the view is pointing at NOW, having subscribed above. The assignment
                // two lines up may already have been answered -- the view replaces a foreign object
                // with its emulator's own once that exists -- and that answer came before there was
                // anything listening for it. Seeding here catches the case where the view was already
                // initialised; the bridge keeps the two in step from this point on.
                SetCurrentValue(OptionsProperty, _terminalView.Options);
            }
        }

        /// <summary>
        /// True while a scrollbar-driven scroll is being applied to the view, so the resulting property
        /// change does not write the scrollbar's own value back underneath the user's drag.
        /// </summary>
        private bool _applyingScrollBarValue;

        private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
        {
            if (_terminalView == null)
                return;

            // Round rather than truncate. A cast rounds toward zero, and zero is the TOP of the buffer, so
            // every drag event used to leak up to a whole line upwards — and because Avalonia's Track applies
            // a drag incrementally (Value = Value + delta, not a value captured when the drag began) that
            // leak compounded, and the thumb outran the cursor. Downward drags lost the same line against
            // their direction, so they lagged instead of raced, which is why only one direction looked wrong.
            _applyingScrollBarValue = true;
            try
            {
                _terminalView.ViewportY = (int)Math.Round(e.NewValue);
            }
            finally
            {
                _applyingScrollBarValue = false;
            }
        }

        private void OnTerminalViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TerminalView.MaxScrollbackProperty ||
                e.Property == TerminalView.ViewportLinesProperty ||
                e.Property == TerminalView.ViewportYProperty ||
                e.Property == TerminalView.IsAlternateBufferProperty)
            {
                UpdateScrollBar();
            }
            else if (e.Property == TerminalView.CurrentDirectoryProperty)
            {
                SetCurrentDirectory(_terminalView?.CurrentDirectory);
            }
            else if (e.Property == TerminalView.OptionsProperty)
            {
                // The view swaps its own Options to the emulator's snapshot once that exists, because
                // XTerm.NET no longer reads the object it was constructed with. Following it here means a
                // caller holding the CONTROL reaches the same live object rather than the one it handed
                // down at template time -- otherwise `control.Options.CursorBlink = true` after startup
                // writes into a copy nothing consults.
                //
                // Mirrored through the property-changed bridge rather than assigned after the template is
                // applied, because the order of OnApplyTemplate against the view's OnInitialized is not
                // this class's to assume. Whenever the view's value moves, this follows it.
                //
                // SetCurrentValue for the same reason as in the view: this is a redirect, not a claim
                // of ownership. It is not load-bearing -- Avalonia keeps a binding alive across a
                // plain SetValue, unlike WPF -- so the binding test below guards the behaviour rather
                // than this particular call.
                SetCurrentValue(OptionsProperty, e.NewValue as XTerm.Options.TerminalOptions);
            }
        }

        private void OnTerminalViewProcessExited(object? sender, ProcessExitedEventArgs e)
        {
            ProcessExited?.Invoke(this, e);
        }

        private void OnTerminalViewShellReady(object? sender, EventArgs e)
        {
            ShellReady?.Invoke(this, e);
        }

        private void OnTerminalViewOutputReceived(object? sender, OutputReceivedEventArgs e)
        {
            OutputReceived?.Invoke(this, e);
        }

        private void OnTerminalViewUrlClicked(object? sender, UrlClickedEventArgs e)
        {
            UrlClicked?.Invoke(this, e);
        }

        private void SetCurrentDirectory(string? currentDirectory)
        {
            SetAndRaise(CurrentDirectoryProperty, ref _currentDirectory, currentDirectory);
        }

        private void UpdateScrollBar()
        {
            if (_scrollBar == null || _terminalView == null)
                return;

            if (_terminalView.IsAlternateBuffer)
            {
                // Inert, not gone. The alternate buffer has no scrollback to offer a range over, but taking
                // the bar away would hand its column to the terminal — see ScrollBarKeepsItsColumn below.
                MakeScrollBarInert();
                return;
            }

            var maxScrollback = _terminalView.MaxScrollback;
            var viewportLines = _terminalView.ViewportLines;
            var currentScroll = _terminalView.ViewportY;

            if (maxScrollback <= 0)
            {
                // Nothing above the screen yet. Same reasoning: a bar that vanished the first time a line
                // scrolled off would narrow the terminal mid-session, under whatever is running.
                MakeScrollBarInert();
                return;
            }

            // Scrollbar range: 0 (top of buffer) to maxScrollback (bottom/current output)
            _scrollBar.Minimum = 0;
            _scrollBar.Maximum = maxScrollback;
            _scrollBar.ViewportSize = viewportLines;
            _scrollBar.IsEnabled = true;

            // Not while the user is dragging. ViewportY raises its change synchronously, so this method runs
            // inside OnScrollBarScroll — and writing Value here would replace the fractional position the
            // Track is mid-drag on with a whole-line one. Avalonia applies the next drag delta on top of
            // whatever Value currently is, so that replacement becomes the base for the rest of the gesture
            // and the error accumulates rather than cancelling out.
            if (!_applyingScrollBarValue)
            {
                _scrollBar.Value = currentScroll;
            }
        }

        /// <summary>
        /// Leave the scrollbar where it is, with nothing to scroll: it keeps its column and stops responding.
        /// </summary>
        /// <remarks>
        /// <para>The bar must never leave the layout. It lives in the template's <c>Auto</c> column, so
        /// hiding it collapses that column, the terminal grows into it, and ArrangeOverride resizes the
        /// emulator AND the pty — the process is told the terminal changed width.</para>
        ///
        /// <para>That is what made a full-screen program come up blank. Switching to the alternate buffer is
        /// its very first action, that hid the bar, and the resize landed while the program was drawing its
        /// opening frame. A program that writes one screen-width per row and lets the cursor wrap, rather
        /// than positioning it explicitly, then had every row after the first land somewhere it did not
        /// intend; it repainted the whole screen trying to recover.</para>
        ///
        /// <para>Windows Terminal and xterm both leave the bar in place for exactly this reason. Overlaying
        /// it on the terminal would keep the width fixed too, but the bar would sit over real cells and eat
        /// the mouse events belonging to them — and these programs turn mouse reporting on.</para>
        /// </remarks>
        private void MakeScrollBarInert()
        {
            if (_scrollBar == null)
                return;

            _scrollBar.Minimum = 0;
            _scrollBar.Maximum = 0;
            _scrollBar.Value = 0;
            _scrollBar.IsEnabled = false;
        }
    }
}
