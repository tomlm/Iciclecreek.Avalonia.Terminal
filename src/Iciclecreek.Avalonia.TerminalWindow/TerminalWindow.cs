using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using XTerm;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// A "native" Window that contains a TerminalControl and automatically handles window events
    /// from the terminal (title changes, window manipulation commands, etc.).
    /// </summary>
    public class TerminalWindow : Window
    {
        private TerminalControl? _terminalControl;
        private bool _restoringFocus;

        public static readonly StyledProperty<IBrush> SelectionBrushProperty =
            AvaloniaProperty.Register<TerminalWindow, IBrush>(
                nameof(SelectionBrush),
                defaultValue: new SolidColorBrush(Color.FromArgb(128, 0, 120, 215)));

        public static readonly StyledProperty<string> ProcessProperty =
            AvaloniaProperty.Register<TerminalWindow, string>(
                nameof(Process),
                defaultValue: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash");

        public static readonly StyledProperty<IList<string>> ArgsProperty =
            AvaloniaProperty.Register<TerminalWindow, IList<string>>(
                nameof(Args),
                defaultValue: Array.Empty<string>());

        public static readonly StyledProperty<string?> StartingDirectoryProperty =
            AvaloniaProperty.Register<TerminalWindow, string?>(
                nameof(StartingDirectory),
                defaultValue: Environment.CurrentDirectory);

        public static readonly DirectProperty<TerminalWindow, string?> CurrentDirectoryProperty =
            AvaloniaProperty.RegisterDirect<TerminalWindow, string?>(
                nameof(CurrentDirectory),
                o => o.CurrentDirectory);

        public static readonly StyledProperty<bool> CloseOnProcessExitProperty =
            AvaloniaProperty.Register<TerminalWindow, bool>(
                nameof(CloseOnProcessExit),
                defaultValue: true);

        public static readonly StyledProperty<bool> UpdateTitleFromTerminalProperty =
            AvaloniaProperty.Register<TerminalWindow, bool>(
                nameof(UpdateTitleFromTerminal),
                defaultValue: true);


        public static readonly StyledProperty<XTerm.Options.TerminalOptions?> OptionsProperty =
            AvaloniaProperty.Register<TerminalWindow, XTerm.Options.TerminalOptions?>(
                nameof(Options),
                defaultValue: null);

        public static readonly StyledProperty<int> BufferSizeProperty =
            AvaloniaProperty.Register<TerminalWindow, int>(
                nameof(BufferSize),
                defaultValue: 1000);

        public static readonly StyledProperty<bool> ShowCaretOnClickProperty =
            AvaloniaProperty.Register<TerminalWindow, bool>(
                nameof(ShowCaretOnClick),
                defaultValue: false);

        /// <inheritdoc cref="TerminalView.VerbatimCommandLineProperty"/>
        public static readonly StyledProperty<bool> VerbatimCommandLineProperty =
            AvaloniaProperty.Register<TerminalWindow, bool>(
                nameof(VerbatimCommandLine),
                defaultValue: false);

        /// <inheritdoc cref="TerminalView.EnvironmentVariablesProperty"/>
        public static readonly StyledProperty<IDictionary<string, string>?> EnvironmentVariablesProperty =
            AvaloniaProperty.Register<TerminalWindow, IDictionary<string, string>?>(
                nameof(EnvironmentVariables),
                defaultValue: null);

        public static readonly StyledProperty<TextDecorationLocation?> TextDecorationsProperty =
            AvaloniaProperty.Register<TerminalWindow, TextDecorationLocation?>(
                nameof(TextDecorations),
                defaultValue: null);

        public static readonly StyledProperty<Color> CursorColorProperty =
            AvaloniaProperty.Register<TerminalWindow, Color>(
                nameof(CursorColor),
                defaultValue: Colors.White);

        public static readonly StyledProperty<XTerm.Common.CursorStyle> CursorStyleProperty =
            AvaloniaProperty.Register<TerminalWindow, XTerm.Common.CursorStyle>(
                nameof(CursorStyle),
                defaultValue: XTerm.Common.CursorStyle.Bar);

        public static readonly StyledProperty<bool> CursorBlinkProperty =
            AvaloniaProperty.Register<TerminalWindow, bool>(
                nameof(CursorBlink),
                defaultValue: true);

        public static readonly StyledProperty<int> CursorBlinkRateProperty =
            AvaloniaProperty.Register<TerminalWindow, int>(
                nameof(CursorBlinkRate),
                defaultValue: 530);

        /// <inheritdoc cref="TerminalView.ShellReady"/>
        public event EventHandler? ShellReady;

        public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

        /// <inheritdoc cref="TerminalView.UrlClicked"/>
        public event EventHandler<UrlClickedEventArgs>? UrlClicked;


        /// <summary>
        /// Gets or sets the selection brush for the terminal.
        /// </summary>
        public IBrush SelectionBrush
        {
            get => GetValue(SelectionBrushProperty);
            set => SetValue(SelectionBrushProperty, value);
        }

        /// <summary>
        /// Gets or sets the process to launch in the terminal.
        /// </summary>
        public string Process
        {
            get => GetValue(ProcessProperty);
            set => SetValue(ProcessProperty, value);
        }

        /// <summary>
        /// Gets or sets the arguments for the process.
        /// </summary>
        public IList<string> Args
        {
            get => GetValue(ArgsProperty);
            set => SetValue(ArgsProperty, value);
        }

        /// <summary>
        /// Gets or sets the initial working directory for the terminal process.
        /// </summary>
        public string? StartingDirectory
        {
            get => GetValue(StartingDirectoryProperty);
            set => SetValue(StartingDirectoryProperty, value);
        }

        /// <summary>
        /// Gets the current working directory reported by the terminal session.
        /// </summary>
        public string? CurrentDirectory => _terminalControl?.CurrentDirectory;

        /// <summary>
        /// Gets or sets the terminal scrollback buffer size in lines.
        /// </summary>
        public int BufferSize
        {
            get => GetValue(BufferSizeProperty);
            set => SetValue(BufferSizeProperty, value);
        }

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
        /// Gets the underlying <see cref="XTerm.Terminal"/> instance.
        /// </summary>
        public XTerm.Terminal Terminal => _terminalControl!.Terminal;

        /// <summary>
        /// Waits for the terminal process to exit, with a timeout in milliseconds.
        /// </summary>
        /// <param name="ms">The maximum amount of time to wait, in milliseconds.</param>
        public void WaitForExit(int ms) => _terminalControl!.WaitForExit(ms);

        /// <summary>
        /// Terminates the running terminal process.
        /// </summary>
        public void Kill() => _terminalControl!.Kill();

        /// <inheritdoc cref="TerminalView.SendInputAsync"/>
        public Task SendInputAsync(string text, CancellationToken cancellationToken = default)
            => _terminalControl?.SendInputAsync(text, cancellationToken) ?? Task.CompletedTask;

        /// <inheritdoc cref="TerminalControl.TextDecorations"/>
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

        /// <inheritdoc cref="TerminalControl.ViewportY"/>
        public int ViewportY
        {
            get => _terminalControl?.ViewportY ?? 0;
            set { if (_terminalControl != null) _terminalControl.ViewportY = value; }
        }

        /// <inheritdoc cref="TerminalView.MaxScrollback"/>
        public int MaxScrollback => _terminalControl?.MaxScrollback ?? 0;

        /// <inheritdoc cref="TerminalControl.ViewportLines"/>
        public int ViewportLines => _terminalControl?.ViewportLines ?? 0;

        /// <inheritdoc cref="TerminalControl.IsAlternateBuffer"/>
        public bool IsAlternateBuffer => _terminalControl?.IsAlternateBuffer ?? false;

        /// <inheritdoc cref="TerminalControl.CopyAsync"/>
        public Task<bool> CopyAsync() => _terminalControl?.CopyAsync() ?? Task.FromResult(false);

        /// <inheritdoc cref="TerminalControl.PasteAsync"/>
        public Task PasteAsync() => _terminalControl?.PasteAsync() ?? Task.CompletedTask;

        /// <inheritdoc cref="TerminalView.AttachConnection"/>
        public void AttachConnection(Porta.Pty.IPtyConnection connection)
        {
            EnsureTerminalControl();
            _terminalControl!.AttachConnection(connection);
        }

        /// <inheritdoc cref="TerminalView.DetachConnection"/>
        public Porta.Pty.IPtyConnection? DetachConnection() => _terminalControl?.DetachConnection();

        /// <inheritdoc cref="TerminalView.IsLive"/>
        public bool IsLive => _terminalControl?.IsLive ?? false;

        /// <inheritdoc cref="TerminalControl.BeginReparent"/>
        public void BeginReparent() => _terminalControl?.BeginReparent();

        /// <inheritdoc cref="TerminalControl.EndReparent"/>
        public void EndReparent() => _terminalControl?.EndReparent();

        /// <summary>
        /// Gets the exit code of the launched process after it has terminated.
        /// </summary>
        public int ExitCode => _terminalControl!.ExitCode;

        /// <summary>
        /// Gets the operating system process identifier of the launched terminal process.
        /// </summary>
        public int Pid => _terminalControl!.Pid;


        /// <summary>
        /// Gets or sets whether the window should close when the process exits.
        /// </summary>
        public bool CloseOnProcessExit
        {
            get => GetValue(CloseOnProcessExitProperty);
            set => SetValue(CloseOnProcessExitProperty, value);
        }

        /// <summary>
        /// Gets or sets whether the window title should be updated from terminal escape sequences.
        /// </summary>
        public bool UpdateTitleFromTerminal
        {
            get => GetValue(UpdateTitleFromTerminalProperty);
            set => SetValue(UpdateTitleFromTerminalProperty, value);
        }


        /// <summary>
        /// Gets or sets the terminal options.
        /// </summary>
        public XTerm.Options.TerminalOptions? Options
        {
            get => GetValue(OptionsProperty);
            set => SetValue(OptionsProperty, value);
        }

        static TerminalWindow()
        {
            BackgroundProperty.OverrideDefaultValue<TerminalWindow>(Brushes.Black);
            ForegroundProperty.OverrideDefaultValue<TerminalWindow>(Brushes.White);

            // Without this a bare TerminalWindow renders the shell in the proportional system UI font. The
            // demo only looked right because its App.axaml styles ManagedTerminalWindow specifically —
            // TerminalWindow matched no selector and fell through to the UI font.
            FontFamilyProperty.OverrideDefaultValue<TerminalWindow>(TerminalView.DefaultFontFamily);
        }

        public TerminalWindow()
        {
            // Content is built here rather than in OnInitialized. A control assigned to Content during
            // OnInitialized never gets styled: its Template stays null, so TerminalControl's template is
            // never applied, PART_TerminalView is never created, and the window shows nothing at all.
            // Building it in the constructor puts it in place before initialisation, which is the same
            // position it would occupy had a caller assigned Content themselves.
            EnsureTerminalControl();

            // Set focus to terminal when window opens or is activated
            Opened += OnOpened;
            Activated += OnActivated;
            Deactivated += OnDeactivated;
        }

        /// <summary>
        /// Turn on the terminal capabilities whose commands this window handles.
        /// </summary>
        /// <remarks>
        /// Without these flags the emulator never emits the sequences at all, so the eleven handlers wired in
        /// <see cref="EnsureTerminalControl"/> are unreachable and every window-manipulation command silently
        /// does nothing.
        ///
        /// <para>Applied to whatever <see cref="Options"/> currently is, every time it changes, rather than
        /// once during construction. A caller's object initializer runs after this constructor, so a
        /// one-shot configuration would be applied to an object the caller then replaces — which is precisely
        /// how the flags came to be lost before.</para>
        /// </remarks>
        private static void EnableWindowCommands(XTerm.Options.TerminalOptions options)
        {
            var windowOptions = options.WindowOptions;
            windowOptions.GetWinPosition = true;
            windowOptions.GetWinSizePixels = true;
            windowOptions.GetWinSizeChars = true;
            windowOptions.GetScreenSizePixels = true;
            windowOptions.GetCellSizePixels = true;
            windowOptions.GetIconTitle = true;
            windowOptions.GetWinTitle = true;
            windowOptions.GetWinState = true;
            windowOptions.SetWinPosition = true;
            windowOptions.SetWinSizePixels = true;
            windowOptions.SetWinSizeChars = true;
            windowOptions.RaiseWin = true;
            windowOptions.LowerWin = true;
            windowOptions.RefreshWin = true;
            windowOptions.RestoreWin = true;
            windowOptions.MaximizeWin = true;
            windowOptions.MinimizeWin = true;
            windowOptions.FullscreenWin = true;
        }

        private void EnsureTerminalControl()
        {
            if (_terminalControl != null)
                return;

            _terminalControl = new TerminalControl();

            // Set as a LOCAL value, not a default. A default loses to inheritance, and the application theme
            // puts a proportional font (FluentTheme uses Inter) on every window — which then flows down into
            // the terminal and breaks the cell grid. A local value outranks that. A host assigning FontFamily
            // themselves still wins, because their assignment happens after this constructor. The trade is
            // that a Style targeting TerminalWindow will NOT win, since a local value outranks a style
            // setter — set the property directly to choose a different font.
            FontFamily = TerminalView.DefaultFontFamily;

            // Keep the window-command flags on whatever Options ends up being. Setting Content below attaches
            // this window and triggers initialisation DURING construction, which is earlier than a caller's
            // object initializer — so configuring once here would decorate an object they are about to
            // replace. Observing the property instead means the flags follow the value.
            Options ??= new XTerm.Options.TerminalOptions();
            this.GetObservable(OptionsProperty).Subscribe(
                new global::Avalonia.Reactive.AnonymousObserver<XTerm.Options.TerminalOptions?>(options =>
                {
                    if (options != null)
                        EnableWindowCommands(options);
                }));

            // Clicking the native title bar/chrome can steal keyboard focus away from the content
            // (especially on Linux). Restore focus on any pointer press within the window.
            // Use Bubble so we don't interfere with the system caption buttons (close/maximize/minimize).
            AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Bubble);

            // Subscribe to terminal events.
            _terminalControl.ProcessExited += OnTerminalControlProcessExited;
            _terminalControl.ShellReady += OnTerminalControlShellReady;
            _terminalControl.UrlClicked += OnTerminalControlUrlClicked;
            TerminalView.AddTitleChangedHandler(_terminalControl, OnTerminalTitleChanged);
            TerminalView.AddWindowMovedHandler(_terminalControl, OnTerminalWindowMoved);
            TerminalView.AddWindowResizedHandler(_terminalControl, OnTerminalWindowResized);
            TerminalView.AddWindowMinimizedHandler(_terminalControl, OnTerminalWindowMinimized);
            TerminalView.AddWindowMaximizedHandler(_terminalControl, OnTerminalWindowMaximized);
            TerminalView.AddWindowRestoredHandler(_terminalControl, OnTerminalWindowRestored);
            TerminalView.AddWindowRaisedHandler(_terminalControl, OnTerminalWindowRaised);
            TerminalView.AddWindowLoweredHandler(_terminalControl, OnTerminalWindowLowered);
            TerminalView.AddWindowFullscreenedHandler(_terminalControl, OnTerminalWindowFullscreened);
            TerminalView.AddBellRangHandler(_terminalControl, OnTerminalBellRang);
            TerminalView.AddWindowInfoRequestedHandler(_terminalControl, OnTerminalWindowInfoRequested);
            _terminalControl.PropertyChanged += OnTerminalControlPropertyChanged;

            // Bind properties from Window to TerminalControl
            _terminalControl.Bind(TerminalControl.FontFamilyProperty, this.GetObservable(FontFamilyProperty));
            _terminalControl.Bind(TerminalControl.FontSizeProperty, this.GetObservable(FontSizeProperty));
            _terminalControl.Bind(TerminalControl.FontStyleProperty, this.GetObservable(FontStyleProperty));
            _terminalControl.Bind(TerminalControl.FontWeightProperty, this.GetObservable(FontWeightProperty));
            _terminalControl.Bind(TemplatedControl.ForegroundProperty, this.GetObservable(ForegroundProperty));
            _terminalControl.Bind(TemplatedControl.BackgroundProperty, this.GetObservable(BackgroundProperty));
            _terminalControl.Bind(TerminalControl.SelectionBrushProperty, this.GetObservable(SelectionBrushProperty));
            _terminalControl.Bind(TerminalControl.ProcessProperty, this.GetObservable(ProcessProperty));
            _terminalControl.Bind(TerminalControl.StartingDirectoryProperty, this.GetObservable(StartingDirectoryProperty));
            _terminalControl.Bind(TerminalControl.ArgsProperty, this.GetObservable(ArgsProperty));
            _terminalControl.Bind(TerminalControl.OptionsProperty, this.GetObservable(OptionsProperty));
            _terminalControl.Bind(TerminalControl.BufferSizeProperty, this.GetObservable(BufferSizeProperty));
            _terminalControl.Bind(TerminalControl.ShowCaretOnClickProperty, this.GetObservable(ShowCaretOnClickProperty));
            _terminalControl.Bind(TerminalControl.VerbatimCommandLineProperty, this.GetObservable(VerbatimCommandLineProperty));
            _terminalControl.Bind(TerminalControl.EnvironmentVariablesProperty, this.GetObservable(EnvironmentVariablesProperty));
            _terminalControl.Bind(TerminalControl.TextDecorationsProperty, this.GetObservable(TextDecorationsProperty));
            _terminalControl.Bind(TerminalControl.CursorColorProperty, this.GetObservable(CursorColorProperty));
            _terminalControl.Bind(TerminalControl.CursorStyleProperty, this.GetObservable(CursorStyleProperty));
            _terminalControl.Bind(TerminalControl.CursorBlinkProperty, this.GetObservable(CursorBlinkProperty));
            _terminalControl.Bind(TerminalControl.CursorBlinkRateProperty, this.GetObservable(CursorBlinkRateProperty));

            Content = _terminalControl;
        }

        /// <summary>
        /// Launch the terminal process with the current Process, Args, and StartingDirectory properties. If the process is already running, it will be
        /// terminated and replaced with a new instance using the updated properties. 
        /// </summary>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public virtual async Task LaunchProcess()
        {
            EnsureTerminalControl();
            await _terminalControl.LaunchProcess();

            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible)
                {
                    Activate();
                }

                RestoreTerminalFocus();
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


        private void OnOpened(object? sender, EventArgs e)
        {
            RestoreTerminalFocus();
        }

        private void OnActivated(object? sender, EventArgs e)
        {
            RestoreTerminalFocus();
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            // Focus contract: for TerminalWindow we always want terminal focused.
            // We don't need to "remember" any other element.
        }

        private void RestoreTerminalFocus()
        {
            if (_terminalControl == null)
                return;

            if (_restoringFocus)
                return;

            _restoringFocus = true;
            try
            {
                if (!IsActive)
                    return;

                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsActive || _terminalControl == null)
                        return;

                    if (!_terminalControl.IsKeyboardFocusWithin)
                    {
                        _terminalControl.Focus();
                    }
                }, DispatcherPriority.Input);
            }
            finally
            {
                _restoringFocus = false;
            }
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);

            Opened -= OnOpened;
            Activated -= OnActivated;
            Deactivated -= OnDeactivated;

            RemoveHandler(PointerPressedEvent, OnAnyPointerPressed);

            if (_terminalControl != null)
            {
                _terminalControl.PropertyChanged -= OnTerminalControlPropertyChanged;
                _terminalControl.ProcessExited -= OnTerminalControlProcessExited;
                _terminalControl.ShellReady -= OnTerminalControlShellReady;
                _terminalControl.UrlClicked -= OnTerminalControlUrlClicked;
                TerminalView.RemoveTitleChangedHandler(_terminalControl, OnTerminalTitleChanged);
                TerminalView.RemoveWindowMovedHandler(_terminalControl, OnTerminalWindowMoved);
                TerminalView.RemoveWindowResizedHandler(_terminalControl, OnTerminalWindowResized);
                TerminalView.RemoveWindowMinimizedHandler(_terminalControl, OnTerminalWindowMinimized);
                TerminalView.RemoveWindowMaximizedHandler(_terminalControl, OnTerminalWindowMaximized);
                TerminalView.RemoveWindowRestoredHandler(_terminalControl, OnTerminalWindowRestored);
                TerminalView.RemoveWindowRaisedHandler(_terminalControl, OnTerminalWindowRaised);
                TerminalView.RemoveWindowLoweredHandler(_terminalControl, OnTerminalWindowLowered);
                TerminalView.RemoveWindowFullscreenedHandler(_terminalControl, OnTerminalWindowFullscreened);
                TerminalView.RemoveBellRangHandler(_terminalControl, OnTerminalBellRang);
                TerminalView.RemoveWindowInfoRequestedHandler(_terminalControl, OnTerminalWindowInfoRequested);
            }
        }

        private void OnTerminalControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TerminalControl.CurrentDirectoryProperty)
            {
                RaisePropertyChanged(CurrentDirectoryProperty, e.OldValue as string, e.NewValue as string);
            }
        }

        private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Capture focus *after* the click is processed by the target.
            // This avoids breaking the window chrome buttons while still reliably restoring
            // focus after clicking the title bar/background.
            Dispatcher.UIThread.Post(RestoreTerminalFocus, DispatcherPriority.Input);
        }

        private void OnTerminalControlShellReady(object? sender, EventArgs e)
        {
            ShellReady?.Invoke(this, e);
        }

        private void OnTerminalControlUrlClicked(object? sender, UrlClickedEventArgs e)
        {
            UrlClicked?.Invoke(this, e);
        }

        private void OnTerminalControlProcessExited(object? sender, ProcessExitedEventArgs e)
        {
            ProcessExited?.Invoke(this, e);

            if (CloseOnProcessExit)
            {
                Close();
            }
        }

        private void OnTerminalTitleChanged(object? sender, TitleChangedEventArgs e)
        {
            // UpdateTitleFromTerminal was registered but never read, so a host that turned it off to protect
            // its own title bar had the title rewritten anyway, with nothing to indicate the setting was
            // ignored. The event is left unhandled when opted out, so a host handler can still act on it.
            if (!UpdateTitleFromTerminal)
                return;

            if (!e.Handled)
            {
                Title = e.Title;
                e.Handled = true;
            }
        }

        private void OnTerminalWindowMoved(object? sender, WindowMovedEventArgs e)
        {
            if (!e.Handled)
            {
                Position = new PixelPoint(e.X, e.Y);
                e.Handled = true;
            }
        }

        private void OnTerminalWindowResized(object? sender, WindowResizedEventArgs e)
        {
            if (!e.Handled)
            {
                Width = e.Width;
                Height = e.Height;
                e.Handled = true;
            }
        }

        private void OnTerminalWindowMinimized(object? sender, RoutedEventArgs e)
        {
            if (!e.Handled)
            {
                WindowState = WindowState.Minimized;
                e.Handled = true;
            }
        }

        private void OnTerminalWindowMaximized(object? sender, RoutedEventArgs e)
        {
            if (!e.Handled)
            {
                WindowState = WindowState.Maximized;
                e.Handled = true;
            }
        }

        private void OnTerminalWindowRestored(object? sender, RoutedEventArgs e)
        {
            if (!e.Handled)
            {
                WindowState = WindowState.Normal;
                e.Handled = true;
            }
        }

        private void OnTerminalWindowRaised(object? sender, RoutedEventArgs e)
        {
            if (!e.Handled)
            {
                this.Activate();
                e.Handled = true;
            }
        }

        private void OnTerminalWindowLowered(object? sender, RoutedEventArgs e)
        {
            if (!e.Handled)
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                if (lifetime != null)
                {
                    lifetime.Windows.FirstOrDefault(win => win != this)?.Activate();
                    e.Handled = true;
                }
            }

        }

        private void OnTerminalWindowFullscreened(object? sender, RoutedEventArgs e)
        {
            if (!e.Handled)
            {
                WindowState = WindowState.FullScreen;
                e.Handled = true;
            }
        }

        private void OnTerminalBellRang(object? sender, RoutedEventArgs e)
        {
            // default bell behavior: no-op
        }

        private void OnTerminalWindowInfoRequested(object? sender, WindowInfoRequestedEventArgs e)
        {
            if (!e.Handled)
            {
                switch (e.Request)
                {
                    case XTerm.Common.WindowInfoRequest.State:
                        e.IsIconified = WindowState == WindowState.Minimized;
                        e.Handled = true;
                        break;

                    case XTerm.Common.WindowInfoRequest.Position:
                        e.X = Position.X;
                        e.Y = Position.Y;
                        e.Handled = true;
                        break;

                    case XTerm.Common.WindowInfoRequest.SizePixels:
                        e.WidthPixels = (int)Width;
                        e.HeightPixels = (int)Height;
                        e.Handled = true;
                        break;

                    case XTerm.Common.WindowInfoRequest.ScreenSizePixels:
                        var screen = Screens.ScreenFromWindow(this);
                        if (screen != null)
                        {
                            e.WidthPixels = (int)screen.Bounds.Width;
                            e.HeightPixels = (int)screen.Bounds.Height;
                            e.Handled = true;
                        }
                        break;

                    case XTerm.Common.WindowInfoRequest.CellSizePixels:
                        e.CellWidth = (int)(FontSize * 0.6);
                        e.CellHeight = (int)(FontSize * 1.2);
                        e.Handled = true;
                        break;

                    case XTerm.Common.WindowInfoRequest.Title:
                    case XTerm.Common.WindowInfoRequest.IconTitle:
                        e.Title = Title;
                        e.Handled = true;
                        break;
                }
            }
        }
    }
}
