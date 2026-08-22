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
    public class TerminalControl : TemplatedControl
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
        /// <inheritdoc cref="TerminalView.UrlClicked"/>
        public event EventHandler<UrlClickedEventArgs>? UrlClicked;

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
            Application.Current.Styles.Add(styles);
            _stylesLoaded = true;
        }

        public TerminalControl()
        {
        }

        /// <summary>
        /// Gets the underlying <see cref="XTerm.Terminal"/> instance.
        /// </summary>
        public XTerm.Terminal Terminal => _terminalView!.Terminal;


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
            // Ensure styles are loaded (handles case where static constructor ran before Application was ready)
            LoadDefaultStyles();

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
                _terminalView.UrlClicked -= OnTerminalViewUrlClicked;
            }

            SetCurrentDirectory(null);

            // Get template parts
            _terminalView = e.NameScope.Find<TerminalView>("PART_TerminalView");
            _scrollBar = e.NameScope.Find<ScrollBar>("PART_ScrollBar");

            // Wire up scrollbar
            if (_scrollBar != null && _terminalView != null)
            {
                _scrollBar.Scroll += OnScrollBarScroll;
                _terminalView.Options = Options ?? new XTerm.Options.TerminalOptions();
                _terminalView.PropertyChanged += OnTerminalViewPropertyChanged;
                _terminalView.ProcessExited += OnTerminalViewProcessExited;
                _terminalView.ShellReady += OnTerminalViewShellReady;
                _terminalView.UrlClicked += OnTerminalViewUrlClicked;
                SetCurrentDirectory(_terminalView.CurrentDirectory);
                // (no window event hooking needed)
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
        }

        private void OnTerminalViewProcessExited(object? sender, ProcessExitedEventArgs e)
        {
            ProcessExited?.Invoke(this, e);
        }

        private void OnTerminalViewShellReady(object? sender, EventArgs e)
        {
            ShellReady?.Invoke(this, e);
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
                _scrollBar.IsVisible = false;
                _scrollBar.Value = 0;
                return;
            }

            var maxScrollback = _terminalView.MaxScrollback;
            var viewportLines = _terminalView.ViewportLines;
            var currentScroll = _terminalView.ViewportY;

            // Scrollbar range: 0 (top of buffer) to maxScrollback (bottom/current output)
            _scrollBar.Minimum = 0;
            _scrollBar.Maximum = maxScrollback;
            _scrollBar.ViewportSize = viewportLines;
            _scrollBar.IsVisible = maxScrollback > 0;

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
    }
}
