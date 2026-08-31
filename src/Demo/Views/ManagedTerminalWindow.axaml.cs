using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Avalonia.WindowManager;
using Iciclecreek.Terminal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Demo.Views
{
    /// <summary>
    /// A Window that contains a TerminalControl and automatically handles window events
    /// from the terminal (title changes, window manipulation commands, etc.).
    /// </summary>
    public partial class ManagedTerminalWindow : ManagedWindow
    {
        private TerminalControl? _terminalControl;

        /// <summary>Forwarded to the hosted control at construction; the demo turns them on.</summary>
        public bool Ligatures { get; set; }
        private bool _restoringFocus;

        // Window-related events are now exposed by `TerminalView` as bubbling attached events.

        public static readonly StyledProperty<TextDecorationLocation?> TextDecorationsProperty =
            AvaloniaProperty.Register<ManagedTerminalWindow, TextDecorationLocation?>(
                nameof(TextDecorations),
                defaultValue: null);

        public static readonly StyledProperty<XTerm.Options.TerminalOptions?> OptionsProperty =
            AvaloniaProperty.Register<TerminalWindow, XTerm.Options.TerminalOptions?>(
                nameof(Options),
                defaultValue: null);


        public static readonly StyledProperty<IBrush> SelectionBrushProperty =
            AvaloniaProperty.Register<ManagedTerminalWindow, IBrush>(
                nameof(SelectionBrush),
                defaultValue: new SolidColorBrush(Color.FromArgb(128, 0, 120, 215)));

        public static readonly StyledProperty<string> ProcessProperty =
            AvaloniaProperty.Register<ManagedTerminalWindow, string>(
                nameof(Process),
                defaultValue: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash");

        public static readonly StyledProperty<IList<string>> ArgsProperty =
            AvaloniaProperty.Register<ManagedTerminalWindow, IList<string>>(
                nameof(Args),
                defaultValue: Array.Empty<string>());

        public static readonly StyledProperty<int> BufferSizeProperty =
            AvaloniaProperty.Register<ManagedTerminalWindow, int>(
                nameof(BufferSize),
                // Deep enough that Ctrl+F has something real to search -- 1,000 lines of scrollback
                // makes every search demo trivially fast and proves nothing.
                defaultValue: 50_000);

        public static readonly StyledProperty<bool> CloseOnProcessExitProperty =
            AvaloniaProperty.Register<ManagedTerminalWindow, bool>(
                nameof(CloseOnProcessExit),
                defaultValue: true);

        /// <summary>
        /// Gets or sets the text decorations for the terminal.
        /// </summary>
        public TextDecorationLocation? TextDecorations
        {
            get => GetValue(TextDecorationsProperty);
            set => SetValue(TextDecorationsProperty, value);
        }

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
        /// Gets or sets the scrollback buffer size.
        /// </summary>
        public int BufferSize
        {
            get => GetValue(BufferSizeProperty);
            set => SetValue(BufferSizeProperty, value);
        }

        /// <summary>
        /// Gets or sets whether the window should close when the process exits.
        /// </summary>
        public bool CloseOnProcessExit
        {
            get => GetValue(CloseOnProcessExitProperty);
            set => SetValue(CloseOnProcessExitProperty, value);
        }

        /// <summary>
        /// Gets or sets the terminal options.
        /// </summary>
        public XTerm.Options.TerminalOptions? Options
        {
            get => GetValue(OptionsProperty);
            set => SetValue(OptionsProperty, value);
        }

        static ManagedTerminalWindow()
        {
            BackgroundProperty.OverrideDefaultValue<ManagedTerminalWindow>(Brushes.Black);
            ForegroundProperty.OverrideDefaultValue<ManagedTerminalWindow>(Brushes.White);
        }

        public ManagedTerminalWindow()
        {
            this.Position = new PixelPoint(0,0);

            // Set focus to terminal when window opens or is activated
            Opened += OnOpened;
            Activated += OnActivated;
            Deactivated += OnDeactivated;

            // Clicking the native title bar/chrome can steal keyboard focus to a non-content element
            // (especially on Linux/Wayland). Proactively restore focus on any pointer press.
            // This runs only when we're active and doesn't try to fight activation.
            // Use Bubble so we don't interfere with the system caption buttons (close/maximize/minimize).
            AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Bubble);

        }

        protected override void OnInitialized()
        {
            // Create the terminal control as content
            Options = this.Options ?? new XTerm.Options.TerminalOptions();
            Options.WindowOptions.GetWinPosition = true;
            Options.WindowOptions.GetWinSizePixels = true;
            Options.WindowOptions.GetWinSizeChars = true;
            Options.WindowOptions.GetScreenSizePixels = true;
            Options.WindowOptions.GetCellSizePixels = true;
            Options.WindowOptions.GetIconTitle = true;
            Options.WindowOptions.GetWinTitle = true;
            Options.WindowOptions.GetWinState = true;
            Options.WindowOptions.SetWinPosition = true;
            Options.WindowOptions.SetWinSizePixels = true;
            Options.WindowOptions.SetWinSizeChars = true;
            Options.WindowOptions.RaiseWin = true;
            Options.WindowOptions.LowerWin = true;
            Options.WindowOptions.RefreshWin = true;
            Options.WindowOptions.RestoreWin = true;
            Options.WindowOptions.MaximizeWin = true;
            Options.WindowOptions.MinimizeWin = true;
            Options.WindowOptions.FullscreenWin = true;

            // Foreground and Background are deliberately NOT seeded here. OnInitialized runs during
            // construction — before a caller's object initialiser — so reading them now captures the
            // defaults and not what the caller asked for. The bindings below are what carry them.
            _terminalControl = new TerminalControl()
            {
                Options = this.Options,
                FontFamily = this.FontFamily,
                FontSize = this.FontSize,
                Ligatures = this.Ligatures,
            };

            // Subscribe to terminal events.
            _terminalControl.ProcessExited += OnTerminalControlProcessExited;
            _terminalControl.UrlClicked += OnTerminalUrlClicked;
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

            // Bind properties from Window to TerminalControl
            _terminalControl.Bind(TerminalControl.FontFamilyProperty, this.GetObservable(FontFamilyProperty));
            _terminalControl.Bind(TerminalControl.FontSizeProperty, this.GetObservable(FontSizeProperty));
            _terminalControl.Bind(TerminalControl.FontStyleProperty, this.GetObservable(FontStyleProperty));
            _terminalControl.Bind(TerminalControl.FontWeightProperty, this.GetObservable(FontWeightProperty));
            _terminalControl.Bind(TemplatedControl.ForegroundProperty, this.GetObservable(ForegroundProperty));
            _terminalControl.Bind(TemplatedControl.BackgroundProperty, this.GetObservable(BackgroundProperty));
            _terminalControl.Bind(TerminalControl.TextDecorationsProperty, this.GetObservable(TextDecorationsProperty));
            _terminalControl.Bind(TerminalControl.SelectionBrushProperty, this.GetObservable(SelectionBrushProperty));
            _terminalControl.Bind(TerminalControl.ProcessProperty, this.GetObservable(ProcessProperty));
            _terminalControl.Bind(TerminalControl.ArgsProperty, this.GetObservable(ArgsProperty));
            _terminalControl.Bind(TerminalControl.BufferSizeProperty, this.GetObservable(BufferSizeProperty));

            // The find bar, hidden until Ctrl+F. It drives nothing but the public search API on
            // TerminalControl -- which is the point of having it in the demo: if the bar needs to
            // reach past that API, the API is wrong.
            _findBar = BuildFindBar();
            var layout = new DockPanel();
            DockPanel.SetDock(_findBar, Dock.Top);
            layout.Children.Add(_findBar);
            layout.Children.Add(_terminalControl);
            Content = layout;

            // Tunnel, so the shortcut wins over the terminal, which otherwise eats every key.
            AddHandler(KeyDownEvent, OnFindKeyDown, RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// The terminal this window hosts. Exposed so the demo can attach a trace to it — unlike
        /// TerminalWindow, this class does not re-raise the terminal's own events.
        /// </summary>
        public TerminalControl? Terminal => _terminalControl;

        /// <inheritdoc cref="TerminalView.SendInputAsync"/>
        public System.Threading.Tasks.Task SendInputAsync(string text, System.Threading.CancellationToken cancellationToken = default)
            => _terminalControl?.SendInputAsync(text, cancellationToken) ?? System.Threading.Tasks.Task.CompletedTask;

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
            // Focus contract: for ManagedTerminalWindow we always want terminal focused.
            // We don't need to "remember" any other element.
        }

        // ---- the find bar -----------------------------------------------------------------

        private Border? _findBar;
        private TextBox? _findBox;
        private TextBlock? _findCount;

        private Border BuildFindBar()
        {
            _findBox = new TextBox
            {
                Watermark = "Find",
                MinWidth = 220,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            };
            _findBox.TextChanged += (_, _) => RunFind();
            _findBox.KeyDown += OnFindBoxKeyDown;

            _findCount = new TextBlock
            {
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                Opacity = 0.7,
                MinWidth = 90,
            };

            var previous = new Button { Content = "\u25B2" };
            previous.Click += (_, _) => Step(next: false);
            var next = new Button { Content = "\u25BC" };
            next.Click += (_, _) => Step(next: true);
            var close = new Button { Content = "\u2715" };
            close.Click += (_, _) => CloseFindBar();

            var row = new StackPanel
            {
                Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(8, 6),
            };
            row.Children.Add(_findBox);
            row.Children.Add(_findCount);
            row.Children.Add(previous);
            row.Children.Add(next);
            row.Children.Add(close);

            return new Border
            {
                Child = row,
                IsVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(240, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 70, 70, 70)),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
        }

        private void OnFindKeyDown(object? sender, KeyEventArgs e)
        {
            // Ctrl+F, or Cmd+F where Cmd is the convention.
            var accel = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (e.Key == Key.F && accel)
            {
                if (_findBar is { } bar)
                {
                    bar.IsVisible = true;
                    _findBox?.Focus();
                    _findBox?.SelectAll();
                }
                e.Handled = true;
            }
        }

        private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    // Enter walks UP through history, Shift+Enter back down -- the terminal
                    // direction, not the browser one, because stepping starts from the most
                    // recent match and older is the only way a search usually goes from there.
                    Step(next: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    e.Handled = true;
                    break;

                case Key.Escape:
                    CloseFindBar();
                    e.Handled = true;
                    break;
            }
        }

        private void RunFind()
        {
            if (_terminalControl is null || _findBox is null)
                return;

            var needle = _findBox.Text ?? string.Empty;
            if (needle.Length == 0)
            {
                _terminalControl.ClearSearch();
                if (_findCount is not null)
                    _findCount.Text = string.Empty;
                return;
            }

            var count = _terminalControl.FindInBuffer(needle);

            // Land on the MOST RECENT match as the user types. A terminal is read from the bottom --
            // whatever is being looked for almost certainly just scrolled past -- so "the first
            // result" is the one nearest the prompt, not the oldest line in the scrollback.
            // FindPrevious from a fresh search wraps to exactly that.
            if (count > 0)
                _terminalControl.FindPrevious();

            UpdateFindCount(count);
        }

        private void Step(bool next)
        {
            if (_terminalControl is null)
                return;

            _ = next ? _terminalControl.FindNext() : _terminalControl.FindPrevious();
            UpdateFindCount(_terminalControl.SearchHitCount);
        }

        private void UpdateFindCount(int count)
        {
            if (_findCount is null || _terminalControl is null)
                return;

            // Truncated says the cap bit, so the label admits it rather than stating a number that
            // has quietly stopped being true.
            var total = _terminalControl.SearchTruncated ? $"{count:N0}+" : count.ToString("N0");
            var index = _terminalControl.SearchCurrentIndex;
            _findCount.Text = count == 0 ? "no matches"
                : index >= 0 ? $"{index + 1:N0} of {total}"
                : $"{total} matches";
        }

        private void CloseFindBar()
        {
            if (_findBar is { } bar)
                bar.IsVisible = false;

            _terminalControl?.ClearSearch();
            RestoreTerminalFocus();
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
                // Don't fight window activation. We'll be called again on Activated.
                if (!IsActive)
                    return;

                // Post a few times: on Linux/Wayland/X11 focus/activation and layout settle
                // across multiple ticks (especially after closing another window).
                for (var i = 0; i < 1; i++)
                {
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
            }
            finally
            {
                // Allow subsequent activations to restore.
                Dispatcher.UIThread.Post(() => _restoringFocus = false, DispatcherPriority.Background);
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
                _terminalControl.ProcessExited -= OnTerminalControlProcessExited;
                _terminalControl.UrlClicked -= OnTerminalUrlClicked;
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

        private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Capture focus *after* the click is processed by the target.
            // This avoids breaking the window chrome buttons while still reliably restoring
            // focus after clicking the title bar/background.
            Dispatcher.UIThread.Post(RestoreTerminalFocus, DispatcherPriority.Background);
        }

        private void OnTerminalControlProcessExited(object? sender, ProcessExitedEventArgs e)
        {
            if (CloseOnProcessExit)
            {
                Close();
            }
        }

        private async void OnTerminalUrlClicked(object? sender, UrlClickedEventArgs e)
        {
            // Only launch well-formed http(s) urls, never anything else the terminal may have matched.
            if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            try
            {
                var launcher = TopLevel.GetTopLevel(this)?.Launcher;
                if (launcher != null)
                {
                    await launcher.LaunchUriAsync(uri);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to launch {uri}: {ex.Message}");
            }
        }

        private void OnTerminalTitleChanged(object? sender, TitleChangedEventArgs e)
        {
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

        private void OnTerminalWindowResized(object? sender, Iciclecreek.Terminal.WindowResizedEventArgs e)
        {
            if (!e.Handled)
            {
                this.Width = e.Width;
                this.Height = e.Height;
                e.Handled = true;
            }
        }

        private void OnTerminalWindowMinimized(object? sender, RoutedEventArgs e)
        {
            if (!e.Handled)
            {
                this.WindowState = WindowState.Minimized;
                e.Handled = true;
            }
        }

        private void OnTerminalWindowMaximized(object? sender, RoutedEventArgs e)
        {
            if (!e.Handled)
            {
                this.WindowState = WindowState.Maximized;
                e.Handled = true;
            }
        }

        private void OnTerminalWindowRestored(object? sender, RoutedEventArgs e)
        {
            if (!e.Handled)
            {
                this.WindowState = WindowState.Normal;
                e.Handled = true;
            }
        }

        private void OnTerminalWindowRaised(object? sender, RoutedEventArgs e)
        {
            if (!e.Handled)
            {
                this.BringToTop();
                e.Handled = true;
            }
        }

        private void OnTerminalWindowLowered(object? sender, RoutedEventArgs e)
        {
            if (!e.Handled)
            {
                this.NextWindow();
                e.Handled = true;
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
            // no-op by default
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
                        // The text area, and specifically the GRID: columns times the cell width by rows times
                        // the cell height. That is what xterm reports, and the only answer consistent with the
                        // cell size reported below.
                        //
                        // Width and Height are this window's, chrome and all -- a title bar and a border that
                        // no character ever occupies. An image viewer divides this figure by the row count to
                        // work out a cell height, then fills what it believes is left, so every pixel of chrome
                        // in the number comes back as picture that does not fit. It runs off the bottom and
                        // scrolls the screen.
                        if (_terminalControl?.Terminal is { } sizeTerminal)
                        {
                            e.WidthPixels = sizeTerminal.Cols * sizeTerminal.Options.CellWidthPixels;
                            e.HeightPixels = sizeTerminal.Rows * sizeTerminal.Options.CellHeightPixels;
                            e.Handled = true;
                        }
                        break;

                    case XTerm.Common.WindowInfoRequest.ScreenSizePixels:
                        var screen = Screens.ScreenFromWindow((WindowBase)(object)this as WindowBase);
                        if (screen != null)
                        {
                            e.WidthPixels = (int)screen.Bounds.Width;
                            e.HeightPixels = (int)screen.Bounds.Height;
                            e.Handled = true;
                        }
                        break;

                    case XTerm.Common.WindowInfoRequest.CellSizePixels:
                        // Taken from the emulator, so it is the same number images are laid out against rather
                        // than a guess from the font size.
                        if (_terminalControl?.Terminal is { } cellTerminal)
                        {
                            e.CellWidth = cellTerminal.Options.CellWidthPixels;
                            e.CellHeight = cellTerminal.Options.CellHeightPixels;
                            e.Handled = true;
                        }
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
