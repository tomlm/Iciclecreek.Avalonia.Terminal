using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using XTerm;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// A Window that contains a TerminalControl and automatically handles window events
    /// from the terminal (title changes, window manipulation commands, etc.).
    /// </summary>
    public class TerminalWindow : Window
    {
        private TerminalControl? _terminalControl;
        private bool _restoringFocus;

        /// <summary>
        /// Event raised when the PTY process exits.
        /// </summary>
        public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

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
        }

        public TerminalWindow()
        {
            // Set focus to terminal when window opens or is activated
            Opened += OnOpened;
            Activated += OnActivated;
            Deactivated += OnDeactivated;
        }

        protected override void OnInitialized()
        {
            base.OnInitialized();
            _terminalControl = new TerminalControl();
            _terminalControl.Options = this.Options ?? new XTerm.Options.TerminalOptions();
            _terminalControl.Options.WindowOptions.GetWinPosition = true;
            _terminalControl.Options.WindowOptions.GetWinSizePixels = true;
            _terminalControl.Options.WindowOptions.GetWinSizeChars = true;
            _terminalControl.Options.WindowOptions.GetScreenSizePixels = true;
            _terminalControl.Options.WindowOptions.GetCellSizePixels = true;
            _terminalControl.Options.WindowOptions.GetIconTitle = true;
            _terminalControl.Options.WindowOptions.GetWinTitle = true;
            _terminalControl.Options.WindowOptions.GetWinState = true;
            _terminalControl.Options.WindowOptions.SetWinPosition = true;
            _terminalControl.Options.WindowOptions.SetWinSizePixels = true;
            _terminalControl.Options.WindowOptions.SetWinSizeChars = true;
            _terminalControl.Options.WindowOptions.RaiseWin = true;
            _terminalControl.Options.WindowOptions.LowerWin = true;
            _terminalControl.Options.WindowOptions.RefreshWin = true;
            _terminalControl.Options.WindowOptions.RestoreWin = true;
            _terminalControl.Options.WindowOptions.MaximizeWin = true;
            _terminalControl.Options.WindowOptions.MinimizeWin = true;
            _terminalControl.Options.WindowOptions.FullscreenWin = true;

            // Clicking the native title bar/chrome can steal keyboard focus away from the content
            // (especially on Linux). Restore focus on any pointer press within the window.
            // Use Bubble so we don't interfere with the system caption buttons (close/maximize/minimize).
            AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Bubble);

            // Subscribe to TerminalView attached events bubbling up from the inner TerminalView.
            TerminalView.AddProcessExitedHandler(_terminalControl, OnTerminalControlProcessExited);
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
            _terminalControl.Bind(TerminalControl.SelectionBrushProperty, this.GetObservable(SelectionBrushProperty));
            _terminalControl.Bind(TerminalControl.ProcessProperty, this.GetObservable(ProcessProperty));
            _terminalControl.Bind(TerminalControl.ArgsProperty, this.GetObservable(ArgsProperty));
            _terminalControl.Bind(TerminalControl.OptionsProperty, this.GetObservable(OptionsProperty));

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

                for (var i = 0; i < 3; i++)
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
                TerminalView.RemoveProcessExitedHandler(_terminalControl, OnTerminalControlProcessExited);
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
            Title = e.Title;
        }

        private void OnTerminalWindowMoved(object? sender, WindowMovedEventArgs e)
        {
            Position = new PixelPoint(e.X, e.Y);
        }

        private void OnTerminalWindowResized(object? sender, WindowResizedEventArgs e)
        {
            Width = e.Width;
            Height = e.Height;
        }

        private void OnTerminalWindowMinimized(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnTerminalWindowMaximized(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Maximized;
        }

        private void OnTerminalWindowRestored(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Normal;
        }

        private void OnTerminalWindowRaised(object? sender, RoutedEventArgs e)
        {
            Activate();
        }

        private void OnTerminalWindowLowered(object? sender, RoutedEventArgs e)
        {
            Topmost = false;
        }

        private void OnTerminalWindowFullscreened(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.FullScreen;
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
