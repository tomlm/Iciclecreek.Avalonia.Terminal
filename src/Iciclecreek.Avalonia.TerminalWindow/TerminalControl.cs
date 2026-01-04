using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Porta.Pty;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using XTerm;
using XTerm.Buffer;

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

        public static readonly StyledProperty<XTerm.Options.TerminalOptions?> OptionsProperty =
            AvaloniaProperty.Register<TerminalControl, XTerm.Options.TerminalOptions?>(
                nameof(Options),
                defaultValue: null);

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

        public XTerm.Terminal Terminal => _terminalView!.Terminal;


        public void WaitForExit(int ms) => _terminalView!.WaitForExit(ms);

        public void Kill() => _terminalView!.Kill();

        public int ExitCode => _terminalView!.ExitCode;

        public int Pid => _terminalView!.Pid;

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
                // (no window event unhooking needed)
            }

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
                // (no window event hooking needed)
            }
        }

        private void OnTerminalViewProcessExited(object? sender, ProcessExitedEventArgs e)
        {
            // Bubble up the event from TerminalView
            ProcessExited?.Invoke(this, e);
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
