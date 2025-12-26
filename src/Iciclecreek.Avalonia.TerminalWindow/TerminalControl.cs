using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Iciclecreek.Terminal
{
    public class TerminalControl : TemplatedControl
    {
        private TerminalView? _terminalView;
        private ScrollBar? _scrollBar;

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
        public event EventHandler<WindowResizedEventArgs>? WindowResized;

        /// <summary>
        /// Event raised when a window minimize command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowMinimized;

        /// <summary>
        /// Event raised when a window maximize command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowMaximized;

        /// <summary>
        /// Event raised when a window restore command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowRestored;

        /// <summary>
        /// Event raised when a window raise command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowRaised;

        /// <summary>
        /// Event raised when a window lower command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowLowered;

        /// <summary>
        /// Event raised when a window fullscreen command is received from the terminal.
        /// </summary>
        public event EventHandler? WindowFullscreened;

        /// <summary>
        /// Event raised when the terminal bell is activated.
        /// </summary>
        public event EventHandler? BellRang;

        /// <summary>
        /// Event raised when window information is requested by the terminal.
        /// The handler should set the response properties on the event args.
        /// </summary>
        public event EventHandler<WindowInfoRequestedEventArgs>? WindowInfoRequested;

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

        public static readonly StyledProperty<int> BufferSizeProperty =
            AvaloniaProperty.Register<TerminalControl, int>(
                nameof(BufferSize),
                defaultValue: 1000);

        public TextDecorationLocation? TextDecorations
        {
            get => GetValue(TextDecorationsProperty);
            set => SetValue(TextDecorationsProperty, value);
        }

        public IBrush SelectionBrush
        {
            get => GetValue(SelectionBrushProperty);
            set => SetValue(SelectionBrushProperty, value);
        }

        public string Process
        {
            get => GetValue(ProcessProperty);
            set => SetValue(ProcessProperty, value);
        }

        public IList<string> Args
        {
            get => GetValue(ArgsProperty);
            set => SetValue(ArgsProperty, value);
        }

        public int BufferSize
        {
            get => GetValue(BufferSizeProperty);
            set => SetValue(BufferSizeProperty, value);
        }

        private static bool _stylesLoaded = false;

        static TerminalControl()
        {
            // Automatically load the default theme styles
            LoadDefaultStyles();

            // TerminalControl is focusable - it will delegate to inner TerminalView
            FocusableProperty.OverrideDefaultValue<TerminalControl>(true);
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

        protected override void OnGotFocus(GotFocusEventArgs e)
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
                _terminalView.TitleChanged -= OnTerminalViewTitleChanged;
                _terminalView.WindowMoved -= OnTerminalViewWindowMoved;
                _terminalView.WindowResized -= OnTerminalViewWindowResized;
                _terminalView.WindowMinimized -= OnTerminalViewWindowMinimized;
                _terminalView.WindowMaximized -= OnTerminalViewWindowMaximized;
                _terminalView.WindowRestored -= OnTerminalViewWindowRestored;
                _terminalView.WindowRaised -= OnTerminalViewWindowRaised;
                _terminalView.WindowLowered -= OnTerminalViewWindowLowered;
                _terminalView.WindowFullscreened -= OnTerminalViewWindowFullscreened;
                _terminalView.BellRang -= OnTerminalViewBellRang;
                _terminalView.WindowInfoRequested -= OnTerminalViewWindowInfoRequested;
            }

            // Get template parts
            _terminalView = e.NameScope.Find<TerminalView>("PART_TerminalView");
            _scrollBar = e.NameScope.Find<ScrollBar>("PART_ScrollBar");

            // Wire up scrollbar
            if (_scrollBar != null && _terminalView != null)
            {
                _scrollBar.Scroll += OnScrollBarScroll;
                _terminalView.PropertyChanged += OnTerminalViewPropertyChanged;
                _terminalView.ProcessExited += OnTerminalViewProcessExited;
                _terminalView.TitleChanged += OnTerminalViewTitleChanged;
                _terminalView.WindowMoved += OnTerminalViewWindowMoved;
                _terminalView.WindowResized += OnTerminalViewWindowResized;
                _terminalView.WindowMinimized += OnTerminalViewWindowMinimized;
                _terminalView.WindowMaximized += OnTerminalViewWindowMaximized;
                _terminalView.WindowRestored += OnTerminalViewWindowRestored;
                _terminalView.WindowRaised += OnTerminalViewWindowRaised;
                _terminalView.WindowLowered += OnTerminalViewWindowLowered;
                _terminalView.WindowFullscreened += OnTerminalViewWindowFullscreened;
                _terminalView.BellRang += OnTerminalViewBellRang;
                _terminalView.WindowInfoRequested += OnTerminalViewWindowInfoRequested;
                UpdateScrollBar();
            }
        }

        private void OnTerminalViewProcessExited(object? sender, ProcessExitedEventArgs e)
        {
            // Bubble up the event from TerminalView
            ProcessExited?.Invoke(this, e);
        }

        private void OnTerminalViewTitleChanged(object? sender, TitleChangedEventArgs e)
        {
            TitleChanged?.Invoke(this, e);
        }

        private void OnTerminalViewWindowMoved(object? sender, WindowMovedEventArgs e)
        {
            WindowMoved?.Invoke(this, e);
        }

        private void OnTerminalViewWindowResized(object? sender, WindowResizedEventArgs e)
        {
            WindowResized?.Invoke(this, e);
        }

        private void OnTerminalViewWindowMinimized(object? sender, EventArgs e)
        {
            WindowMinimized?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalViewWindowMaximized(object? sender, EventArgs e)
        {
            WindowMaximized?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalViewWindowRestored(object? sender, EventArgs e)
        {
            WindowRestored?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalViewWindowRaised(object? sender, EventArgs e)
        {
            WindowRaised?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalViewWindowLowered(object? sender, EventArgs e)
        {
            WindowLowered?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalViewWindowFullscreened(object? sender, EventArgs e)
        {
            WindowFullscreened?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalViewBellRang(object? sender, EventArgs e)
        {
            BellRang?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalViewWindowInfoRequested(object? sender, WindowInfoRequestedEventArgs e)
        {
            WindowInfoRequested?.Invoke(this, e);
        }

        private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
        {
            if (_terminalView != null)
            {
                _terminalView.ViewportY = (int)e.NewValue;
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
            _scrollBar.Value = currentScroll;
            _scrollBar.IsVisible = maxScrollback > 0;
        }
    }
}
