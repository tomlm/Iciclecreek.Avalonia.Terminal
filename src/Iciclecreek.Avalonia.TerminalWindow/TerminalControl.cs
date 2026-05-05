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

        public static readonly StyledProperty<string?> StartingDirectoryProperty =
            AvaloniaProperty.Register<TerminalControl, string?>(
                nameof(StartingDirectory),
                defaultValue: null);

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

        public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

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
            get => _terminalView?.ShowCaretOnClick ?? false;
            set { if (_terminalView != null) _terminalView.ShowCaretOnClick = value; }
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
                SetCurrentDirectory(_terminalView.CurrentDirectory);
                // (no window event hooking needed)
            }
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
            else if (e.Property == TerminalView.CurrentDirectoryProperty)
            {
                SetCurrentDirectory(_terminalView?.CurrentDirectory);
            }
        }

        private void OnTerminalViewProcessExited(object? sender, ProcessExitedEventArgs e)
        {
            ProcessExited?.Invoke(this, e);
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
            _scrollBar.Value = currentScroll;
            _scrollBar.IsVisible = maxScrollback > 0;
        }
    }
}
