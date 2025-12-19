using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Iciclecreek.Terminal
{
    public class TerminalControl : TemplatedControl
    {
        private TerminalView? _terminalView;
        private ScrollBar? _scrollBar;

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
                defaultValue: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "sh");

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
            }

            // Get template parts
            _terminalView = e.NameScope.Find<TerminalView>("PART_TerminalView");
            _scrollBar = e.NameScope.Find<ScrollBar>("PART_ScrollBar");

            // Wire up scrollbar
            if (_scrollBar != null && _terminalView != null)
            {
                _scrollBar.Scroll += OnScrollBarScroll;
                _terminalView.PropertyChanged += OnTerminalViewPropertyChanged;
                UpdateScrollBar();
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
