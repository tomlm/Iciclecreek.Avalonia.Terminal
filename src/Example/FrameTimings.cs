using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Example
{
    /// <summary>
    /// Collects Avalonia's own per-frame timings from its diagnostic meter, so the cost of a frame can
    /// be split between recording it on the UI thread and painting it on the render thread.
    /// </summary>
    /// <remarks>
    /// The instruments only report when the <c>Avalonia.Diagnostics.Diagnostic.IsEnabled</c> switch is
    /// on, which <see cref="Program"/> sets before Avalonia starts.
    /// <list type="bullet">
    /// <item><c>avalonia.ui.render.time</c>: the UI thread's render pass -- every invalidated visual's
    /// <c>Render</c> recording its draw calls.</item>
    /// <item><c>avalonia.comp.update.time</c>: the render thread applying the recorded changes.</item>
    /// <item><c>avalonia.comp.render.time</c>: the render thread replaying the draw calls into the
    /// window's surface, rasterizing and presenting.</item>
    /// <item><c>avalonia.ui.measure.time</c> and <c>avalonia.ui.arrange.time</c>: layout.</item>
    /// </list>
    /// </remarks>
    internal sealed class FrameTimings : IDisposable
    {
        public const string UiRender = "avalonia.ui.render.time";
        public const string CompUpdate = "avalonia.comp.update.time";
        public const string CompRender = "avalonia.comp.render.time";
        public const string Measure = "avalonia.ui.measure.time";
        public const string Arrange = "avalonia.ui.arrange.time";

        private readonly MeterListener _listener = new();
        private readonly object _lock = new();
        private readonly Dictionary<string, Stat> _stats = new();
        private readonly Dictionary<string, string?> _units = new();

        public FrameTimings()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name != "Avalonia.Diagnostic.Meter" || !instrument.Name.EndsWith(".time", StringComparison.Ordinal))
                    return;
                lock (_lock)
                    _units[instrument.Name] = instrument.Unit;
                listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((i, v, _, _) => Record(i.Name, v));
            _listener.SetMeasurementEventCallback<float>((i, v, _, _) => Record(i.Name, v));
            _listener.SetMeasurementEventCallback<long>((i, v, _, _) => Record(i.Name, v));
            _listener.SetMeasurementEventCallback<int>((i, v, _, _) => Record(i.Name, v));
            _listener.Start();
        }

        /// <summary>The unit each instrument declares, as published.</summary>
        public string? UnitOf(string name)
        {
            lock (_lock)
                return _units.TryGetValue(name, out var unit) ? unit : null;
        }

        private void Record(string name, double value)
        {
            // Avalonia reports these in milliseconds; a unit of seconds would be scaled here.
            if (UnitOf(name) is "s")
                value *= 1000;

            lock (_lock)
            {
                if (!_stats.TryGetValue(name, out var stat))
                    _stats[name] = stat = new Stat();
                stat.Add(value);
            }
        }

        /// <summary>The figures since the last call, and starts the next window.</summary>
        public Dictionary<string, Stat> Take()
        {
            lock (_lock)
            {
                var taken = new Dictionary<string, Stat>(_stats);
                _stats.Clear();
                return taken;
            }
        }

        public void Dispose() => _listener.Dispose();

        public sealed class Stat
        {
            public int Count;
            public double Total;
            public double Max;
            public double Average => Count == 0 ? 0 : Total / Count;

            public void Add(double value)
            {
                Count++;
                Total += value;
                Max = Math.Max(Max, value);
            }
        }
    }
}
