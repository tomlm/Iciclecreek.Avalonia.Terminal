using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;

namespace Example
{
    public partial class MainWindow : Window
    {
        private const int RepaintTestFrames = 120;

        private readonly FrameTimings _timings = new();
        private readonly DispatcherTimer _timingTimer;
        private bool _testRunning;

        public MainWindow()
        {
            InitializeComponent();

            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.OemPlus, KeyModifiers.Control), Command = new Relay(() => SetFontSize(TermControl.FontSize + 2)) });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.OemMinus, KeyModifiers.Control), Command = new Relay(() => SetFontSize(TermControl.FontSize - 2)) });

            // Show the grid the terminal currently has, so cell count and pixel size can be told apart
            TermControl.SizeChanged += (_, _) => UpdateTitle();
            TermControl.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.TemplatedControl.FontSizeProperty)
                    Dispatcher.UIThread.Post(UpdateTitle, DispatcherPriority.Background);
            };
            Opened += (_, _) => UpdateTitle();

            // Live figures for whatever is running, once a second
            _timingTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ShowLiveTimings());
            _timingTimer.Start();
            Closed += (_, _) =>
            {
                _timingTimer.Stop();
                _timings.Dispose();
            };
        }

        private void OnFontSizeClick(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: string tag } && double.TryParse(tag, out double size))
                SetFontSize(size);
        }

        private void OnFontLargerClick(object? sender, RoutedEventArgs e) => SetFontSize(TermControl.FontSize + 2);

        private void OnFontSmallerClick(object? sender, RoutedEventArgs e) => SetFontSize(TermControl.FontSize - 2);

        private void SetFontSize(double size)
        {
            TermControl.FontSize = Math.Clamp(size, 4, 72);
        }

        private void OnSkiaClick(object? sender, RoutedEventArgs e) => TermControl.UseSkiaRenderer = SkiaItem.IsChecked;

        private void OnGraphClick(object? sender, RoutedEventArgs e)
        {
            RendererDiagnostics.DebugOverlays = GraphItem.IsChecked
                ? RendererDebugOverlays.Fps | RendererDebugOverlays.RenderTimeGraph | RendererDebugOverlays.LayoutTimeGraph
                : RendererDebugOverlays.None;
        }

        /// <summary>
        /// Repaints the whole terminal every frame for a fixed number of frames, with nothing else
        /// changing, and reports what each frame cost on each thread.
        /// </summary>
        private void OnRepaintTestClick(object? sender, RoutedEventArgs e)
        {
            var view = TermControl.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
            if (view is null || _testRunning)
                return;

            _testRunning = true;
            TestText.Text = $"Repaint test: {RepaintTestFrames} frames...";
            _timings.Take(); // drop whatever came before

            int frames = 0;
            var clock = Stopwatch.StartNew();

            void Frame(TimeSpan _)
            {
                if (frames++ < RepaintTestFrames)
                {
                    view.InvalidateVisual();
                    RequestAnimationFrame(Frame);
                    return;
                }

                clock.Stop();
                var stats = _timings.Take();
                _testRunning = false;
                TestText.Text = $"Repaint test, {Describe()}: {RepaintTestFrames} frames in {clock.Elapsed.TotalMilliseconds:0} ms " +
                                  $"= {clock.Elapsed.TotalMilliseconds / RepaintTestFrames:0.0} ms/frame wall clock\n" + Format(stats);
            }

            RequestAnimationFrame(Frame);
        }

        private void ShowLiveTimings()
        {
            if (_testRunning)
                return;

            var stats = _timings.Take();
            var input = FormatInput();
            if (stats.Count == 0 && input is null)
                return; // idle: keep the last figures on screen

            // Updating this text is itself a frame, so an idle terminal shows about one a second.
            TimingText.Text = $"Last second, {Describe()}:\n" + Format(stats) + (input is null ? "" : "\n" + input);
        }

        private TerminalView? _countedView;
        private long _lastBytes, _lastChunks, _lastWriteTicks, _lastLockTicks, _lastSample;

        /// <summary>
        /// What the read loop did since the last call: bytes and chunks off the pty, and the time
        /// spent waiting for the terminal lock and inside Terminal.Write. Null when nothing arrived.
        /// </summary>
        private string? FormatInput()
        {
            var view = TermControl.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
            if (view is null)
                return null;

            var c = view.Counters;
            long bytes = Interlocked.Read(ref c.Bytes), chunks = Interlocked.Read(ref c.Chunks);
            long write = Interlocked.Read(ref c.WriteTicks), lockWait = Interlocked.Read(ref c.LockWaitTicks);
            long now = Stopwatch.GetTimestamp();

            bool first = !ReferenceEquals(view, _countedView);
            long dBytes = bytes - _lastBytes, dChunks = chunks - _lastChunks;
            double dWriteMs = (write - _lastWriteTicks) * 1000.0 / Stopwatch.Frequency;
            double dLockMs = (lockWait - _lastLockTicks) * 1000.0 / Stopwatch.Frequency;
            double seconds = (now - _lastSample) / (double)Stopwatch.Frequency;

            _countedView = view;
            (_lastBytes, _lastChunks, _lastWriteTicks, _lastLockTicks, _lastSample) = (bytes, chunks, write, lockWait, now);

            if (first || dChunks == 0 || seconds <= 0)
                return null;

            double mbPerSec = dBytes / seconds / (1024 * 1024);
            double writeShare = dWriteMs / (seconds * 1000) * 100;
            return $"Input: {mbPerSec:0.00} MB/s in {dChunks / seconds:0} chunks/s (avg {dBytes / (double)dChunks / 1024:0.0} KB)   " +
                   $"Terminal.Write: {dWriteMs / seconds:0} ms/s = {writeShare:0}% of the read thread, " +
                   $"{(dBytes > 0 ? dWriteMs / (dBytes / (1024.0 * 1024)) : 0):0.0} ms per MB   " +
                   $"lock wait: {dLockMs / seconds:0.0} ms/s";
        }

        private string Describe()
        {
            var grid = TermControl.Terminal is { } t ? $"{t.Cols}x{t.Rows} cells, " : "";
            var renderer = (TermControl.UseSkiaRenderer ? "Skia" : "classic") +
                           (Program.SoftwareRendering ? " software" : "");
            return $"{renderer}, {CountImages()}{grid}{(int)(TermControl.Bounds.Width * RenderScaling)}x{(int)(TermControl.Bounds.Height * RenderScaling)} px";
        }

        /// <summary>
        /// How many pictures the visible rows hold: the distinct images, and the per-row pieces of
        /// them the renderer draws one at a time. Read without the terminal's lock, so a figure taken
        /// mid-write can be slightly off.
        /// </summary>
        private string CountImages()
        {
            try
            {
                if (TermControl.Terminal is not { } t)
                    return "";

                var images = new HashSet<XTerm.Graphics.TerminalImage>();
                int pieces = 0, strips = 0;
                long stripBytes = 0;
                var buffer = t.Buffer;
                for (int r = 0; r < t.Rows; r++)
                {
                    var line = buffer.GetLine(buffer.ViewportY + r);
                    if (line is null || !line.HasImages)
                        continue;
                    pieces += line.Placements.Count;
                    if (line.Sixel is { } strip)
                    {
                        strips++;
                        stripBytes += strip.ByteCount;
                    }
                    foreach (var image in line.Images)
                        images.Add(image);
                }

                long bytes = 0;
                foreach (var image in images)
                    bytes += (long)image.PixelWidth * image.PixelHeight * 4;

                var kitty = images.Count == 0 ? "" : $"{images.Count} images ({bytes / (1024.0 * 1024):0} MB of pixels) in {pieces} row pieces, ";
                var sixel = strips == 0 ? "" : $"{strips} Sixel strips ({stripBytes / (1024.0 * 1024):0.0} MB), ";
                return kitty + sixel;
            }
            catch (Exception)
            {
                return ""; // the buffer moved underneath; the next second will have it
            }
        }

        private static string Format(Dictionary<string, FrameTimings.Stat> stats)
        {
            var sb = new StringBuilder();
            void Line(string label, params string[] names)
            {
                var parts = names.Where(stats.ContainsKey).Select(n => stats[n]).ToList();
                if (parts.Count == 0)
                {
                    sb.Append($"{label}: -   ");
                    return;
                }

                int count = parts.Max(p => p.Count);
                double total = parts.Sum(p => p.Total);
                double max = parts.Max(p => p.Max);
                sb.Append($"{label}: {count}x avg {total / Math.Max(1, count):0.00} max {max:0.00} ms   ");
            }

            Line("UI render (record)", FrameTimings.UiRender);
            Line("measure", FrameTimings.Measure);
            Line("arrange", FrameTimings.Arrange);
            Line("compositor update", FrameTimings.CompUpdate);
            Line("compositor render (paint+present)", FrameTimings.CompRender);
            return sb.ToString().TrimEnd();
        }

        private void UpdateTitle()
        {
            string grid = "";
            try
            {
                var t = TermControl.Terminal;
                grid = $" {t.Cols}x{t.Rows} cells";
            }
            catch (NullReferenceException)
            {
                // Template not applied yet
            }
            string px = $" {(int)TermControl.Bounds.Width}x{(int)TermControl.Bounds.Height}px";
            Title = $"Example - font {TermControl.FontSize}{grid}{px}";
        }

        private sealed class Relay(Action action) : System.Windows.Input.ICommand
        {
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => action();
        }
    }
}
