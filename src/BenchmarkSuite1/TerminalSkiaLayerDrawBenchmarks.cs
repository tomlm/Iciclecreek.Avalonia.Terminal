using System;
using BenchmarkDotNet.Attributes;
using SkiaSharp;
using Iciclecreek.Terminal.Skia;
using Microsoft.VSDiagnostics;

namespace Terminal.RenderBench
{
    [CPUUsageDiagnoser]
    public class TerminalSkiaLayerDrawBenchmarks
    {
        private const int Rows = 50;
        private const int Cols = 200;
        private SkiaFontCache _fonts = null!;
        private TerminalSnapshot _snapshot = null!;
        private TerminalSkiaLayer _layer = null!;
        private SKSurface _surface = null!;
        [GlobalSetup]
        public void Setup()
        {
            _fonts = new SkiaFontCache();
            _surface = SKSurface.Create(new SKImageInfo(Cols * 10, Rows * 20, SKColorType.Bgra8888, SKAlphaType.Premul));
            _snapshot = new TerminalSnapshot();
            _snapshot.EnsureCapacity(Rows, Cols);
            _snapshot.CellWidth = 10;
            _snapshot.CellHeight = 20;
            _snapshot.FontSize = 14;
            _snapshot.FontFamily = "Consolas,monospace";
            _snapshot.Ligatures = false;
            _snapshot.RenderScale = 1.0;
            _snapshot.Surface = 0xFF000000;
            _snapshot.RowCount = Rows;
            _snapshot.Cols = Cols;
            var rnd = new Random(42);
            for (var r = 0; r < Rows; r++)
            {
                var row = new SnapshotRow(Cols);
                for (var c = 0; c < Cols; c++)
                {
                    ref var cell = ref row.Cells[c];
                    cell.CodePoint = 'A' + ((r * Cols + c) % 26);
                    cell.Foreground = 0xFF000000u | (uint)rnd.Next(0x1000000);
                    cell.Background = 0xFF000000u | (uint)rnd.Next(0x1000000);
                    cell.Width = 1;
                    cell.Flags = SnapshotFlags.None;
                    cell.ClusterIndex = -1;
                    cell.ImageIndex = -1;
                    cell.UnderlineColorIndex = 0xFF;
                }

                _snapshot.Rows[r] = row;
            }

            var bounds = new Avalonia.Rect(0, 0, Cols * 10, Rows * 20);
            _layer = new TerminalSkiaLayer(_snapshot, _fonts, bounds);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _layer.Dispose();
            _surface.Dispose();
            _fonts.Dispose();
        }

        [Benchmark]
        public void DrawFullScreen()
        {
            _layer.Draw(_surface.Canvas);
        }
    }
}