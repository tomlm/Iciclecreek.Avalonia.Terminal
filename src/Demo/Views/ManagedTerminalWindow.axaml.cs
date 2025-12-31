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

        /// <summary>
        /// Event raised when the terminal title changes.
        /// </summary>
        public event EventHandler<TitleChangedEventArgs>? TitleChanged;

        /// <summary>
        /// Event raised when a window move command is received from the terminal.
        /// </summary>
        public event EventHandler<WindowMovedEventArgs>? WindowMoved;

        /// <summary>
        /// Event raised when a window resize command is received from the terminal.
        /// </summary>
        public event EventHandler<Iciclecreek.Terminal.WindowResizedEventArgs>? WindowResized;

        /// <summary>
        /// Event raised when the terminal bell is activated.
        /// </summary>
        public event EventHandler? BellRang;

        /// <summary>
        /// Event raised when window information is requested by the terminal.
        /// </summary>
        public event EventHandler<WindowInfoRequestedEventArgs>? WindowInfoRequested;

        public static readonly StyledProperty<TextDecorationLocation?> TextDecorationsProperty =
            AvaloniaProperty.Register<ManagedTerminalWindow, TextDecorationLocation?>(
                nameof(TextDecorations),
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

        public static readonly StyledProperty<bool> UpdateTitleFromTerminalProperty =
            AvaloniaProperty.Register<ManagedTerminalWindow, bool>(
                nameof(UpdateTitleFromTerminal),
                defaultValue: true);

        public static readonly StyledProperty<bool> HandleWindowCommandsProperty =
            AvaloniaProperty.Register<ManagedTerminalWindow, bool>(
                nameof(HandleWindowCommands),
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
        /// Gets or sets whether the window title should be updated from terminal escape sequences.
        /// </summary>
        public bool UpdateTitleFromTerminal
        {
            get => GetValue(UpdateTitleFromTerminalProperty);
            set => SetValue(UpdateTitleFromTerminalProperty, value);
        }

        /// <summary>
        /// Gets or sets whether window manipulation commands from the terminal should be handled.
        /// </summary>
        public bool HandleWindowCommands
        {
            get => GetValue(HandleWindowCommandsProperty);
            set => SetValue(HandleWindowCommandsProperty, value);
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

        

        private void OnOpened(object? sender, EventArgs e)
        {
            // Create the terminal control as content
            _terminalControl = new TerminalControl()
            {
                FontFamily = this.FontFamily,
                FontSize = this.FontSize,
                Foreground = this.Foreground,
                Background = this.Background,
            };
            Content = _terminalControl;

            // Wire up events
            _terminalControl.ProcessExited += OnTerminalControlProcessExited;
            _terminalControl.TitleChanged += OnTerminalControlTitleChanged;
            _terminalControl.WindowMoved += OnTerminalControlWindowMoved;
            _terminalControl.WindowResized += OnTerminalControlWindowResized;
            _terminalControl.WindowMinimized += OnTerminalControlWindowMinimized;
            _terminalControl.WindowMaximized += OnTerminalControlWindowMaximized;
            _terminalControl.WindowRestored += OnTerminalControlWindowRestored;
            _terminalControl.WindowRaised += OnTerminalControlWindowRaised;
            _terminalControl.WindowLowered += OnTerminalControlWindowLowered;
            _terminalControl.WindowFullscreened += OnTerminalControlWindowFullscreened;
            _terminalControl.BellRang += OnTerminalControlBellRang;
            _terminalControl.WindowInfoRequested += OnTerminalControlWindowInfoRequested;

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
                _terminalControl.TitleChanged -= OnTerminalControlTitleChanged;
                _terminalControl.WindowMoved -= OnTerminalControlWindowMoved;
                _terminalControl.WindowResized -= OnTerminalControlWindowResized;
                _terminalControl.WindowMinimized -= OnTerminalControlWindowMinimized;
                _terminalControl.WindowMaximized -= OnTerminalControlWindowMaximized;
                _terminalControl.WindowRestored -= OnTerminalControlWindowRestored;
                _terminalControl.WindowRaised -= OnTerminalControlWindowRaised;
                _terminalControl.WindowLowered -= OnTerminalControlWindowLowered;
                _terminalControl.WindowFullscreened -= OnTerminalControlWindowFullscreened;
                _terminalControl.BellRang -= OnTerminalControlBellRang;
                _terminalControl.WindowInfoRequested -= OnTerminalControlWindowInfoRequested;
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

        private void OnTerminalControlTitleChanged(object? sender, TitleChangedEventArgs e)
        {
            TitleChanged?.Invoke(this, e);

            if (UpdateTitleFromTerminal)
            {
                Title = e.Title;
            }
        }

        private void OnTerminalControlWindowMoved(object? sender, WindowMovedEventArgs e)
        {
            WindowMoved?.Invoke(this, e);

            if (HandleWindowCommands)
            {
                Position = new PixelPoint(e.X, e.Y);
            }
        }

        private void OnTerminalControlWindowResized(object? sender, Iciclecreek.Terminal.WindowResizedEventArgs e)
        {
            WindowResized?.Invoke(this, e);

            if (HandleWindowCommands)
            {
                Width = e.Width;
                Height = e.Height;
            }
        }

        private void OnTerminalControlWindowMinimized(object? sender, EventArgs e)
        {
            if (HandleWindowCommands)
            {
                WindowState = WindowState.Minimized;
            }
        }

        private void OnTerminalControlWindowMaximized(object? sender, EventArgs e)
        {
            if (HandleWindowCommands)
            {
                WindowState = WindowState.Maximized;
            }
        }

        private void OnTerminalControlWindowRestored(object? sender, EventArgs e)
        {
            if (HandleWindowCommands)
            {
                WindowState = WindowState.Normal;
            }
        }

        private void OnTerminalControlWindowRaised(object? sender, EventArgs e)
        {
            if (HandleWindowCommands && !IsActive)
            {
                Activate();
            }
        }

        private void OnTerminalControlWindowLowered(object? sender, EventArgs e)
        {
            if (HandleWindowCommands)
            {
                // Avalonia doesn't have a direct "lower window" API, but we can try to deactivate
                // This is a best-effort implementation
                Topmost = false;
            }
        }

        private void OnTerminalControlWindowFullscreened(object? sender, EventArgs e)
        {
            if (HandleWindowCommands)
            {
                WindowState = WindowState.FullScreen;
            }
        }

        private void OnTerminalControlBellRang(object? sender, EventArgs e)
        {
            BellRang?.Invoke(this, EventArgs.Empty);
            
            // Default bell behavior - could flash the window or play a sound
            // Note: We intentionally do NOT activate the window here as it would
            // steal focus from other terminal windows and cause input routing issues.
            // Applications can subscribe to BellRang event for custom behavior.
        }

        private void OnTerminalControlWindowInfoRequested(object? sender, WindowInfoRequestedEventArgs e)
        {
            // Raise the event so external handlers can respond
            WindowInfoRequested?.Invoke(this, e);
            
            // If not handled externally, provide default responses from the window
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
                        // Get screen size from the current screen
                        var screen = Screens.ScreenFromWindow((object)this as WindowBase);
                        if (screen != null)
                        {
                            e.WidthPixels = (int)screen.Bounds.Width;
                            e.HeightPixels = (int)screen.Bounds.Height;
                            e.Handled = true;
                        }
                        break;
                        
                    case XTerm.Common.WindowInfoRequest.CellSizePixels:
                        // Cell size is derived from font metrics in TerminalView
                        // We need to expose this from the terminal control
                        // For now, use reasonable defaults based on typical terminal fonts
                        e.CellWidth = (int)(FontSize * 0.6);  // Approximate monospace width
                        e.CellHeight = (int)(FontSize * 1.2); // Approximate line height
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
