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
        private bool _restoringFocus;

        /// <summary>
        /// Event raised when the PTY process exits.
        /// </summary>
        public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

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
                defaultValue: 1000);

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

            _terminalControl = new TerminalControl()
            {
                Options = this.Options,
                FontFamily = this.FontFamily,
                FontSize = this.FontSize,
                Foreground = this.Foreground,
                Background = this.Background,
            };

            // Process exit remains a CLR event on TerminalControl.
            _terminalControl.ProcessExited += OnTerminalControlProcessExited;

            // Subscribe to TerminalView attached events bubbling from the inner TerminalView.
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
            Content = _terminalControl;
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
            // Focus contract: for ManagedTerminalWindow we always want terminal focused.
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
            ProcessExited?.Invoke(this, e);

            if (CloseOnProcessExit)
            {
                Close();
            }
        }

        private void OnTerminalTitleChanged(object? sender, TitleChangedEventArgs e)
        {
            if (!e.Handled)
            {
                Title = e.Title;
            }
        }

        private void OnTerminalWindowMoved(object? sender, WindowMovedEventArgs e)
        {
            if (!e.Handled)
            {
                Position = new PixelPoint(e.X, e.Y);
            }
        }

        private void OnTerminalWindowResized(object? sender, Iciclecreek.Terminal.WindowResizedEventArgs e)
        {
            if (!e.Handled)
            {
                this.Width = e.Width;
                this.Height = e.Height;
            }
        }

        private void OnTerminalWindowMinimized(object? sender, RoutedEventArgs e)
        {
            this.MinimizeCommand.Execute();
        }

        private void OnTerminalWindowMaximized(object? sender, RoutedEventArgs e)
        {
            this.MaximizeCommand.Execute();
        }

        private void OnTerminalWindowRestored(object? sender, RoutedEventArgs e)
        {
            this.RestoreCommand.Execute();
        }

        private void OnTerminalWindowRaised(object? sender, RoutedEventArgs e)
        {
            Activate();
        }

        private void OnTerminalWindowLowered(object? sender, RoutedEventArgs e)
        {
            // best-effort no-op
        }

        private void OnTerminalWindowFullscreened(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.FullScreen;
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
                        e.WidthPixels = (int)Width;
                        e.HeightPixels = (int)Height;
                        e.Handled = true;
                        break;

                    case XTerm.Common.WindowInfoRequest.ScreenSizePixels:
                        var screen = Screens.ScreenFromWindow((object)this as WindowBase);
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
