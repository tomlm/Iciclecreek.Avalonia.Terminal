using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Iciclecreek.Terminal
{

    /// <summary>
    /// Synchronizes control invalidation to a target frame rate, so all terminals get invalidated together.
    /// </summary>
    public static class TerminalRenderThrottle
    {
        // Held as a rate rather than an interval because a rate is the unit the tradeoff is actually reasoned
        // about in: below roughly 30 FPS a busy `tail -f` visibly stutters, and above it the extra frames cost
        // UI-thread time to buy detail nobody reading text can perceive. 30 is the default for that reason.
        private const int DefaultFrameRate = 30;

        // 1000 FPS is a one millisecond interval, which is "effectively unthrottled" rather than a rate anyone
        // renders at. It exists as a ceiling so the setter has something to reject other than zero.
        private const int MaxFrameRate = 1000;

        private static int _targetFrameRate = DefaultFrameRate;

        /// <summary>
        /// Gets or sets the target frame rate, in frames per second, at which pending terminals are invalidated.
        /// Defaults to 30.
        /// </summary>
        /// <remarks>
        /// <para>Global rather than per-terminal, which is the whole point of this class: every terminal repaints
        /// on the SAME frame, so a window hosting four of them does one invalidation pass and not four staggered
        /// ones. A per-control rate would need a deadline per control and would give up exactly that.</para>
        /// <para>Raise it for a smoother repaint on a high-refresh display; lower it to hand UI-thread time back
        /// to the rest of the app, since a terminal streaming build output at 10 FPS is still perfectly readable
        /// and costs a third of the invalidations.</para>
        /// <para>Takes effect from the next frame scheduled. A frame already in flight completes at the rate that
        /// was in force when it was scheduled, so a change can take up to one old interval to be visible.</para>
        /// <para>Safe to set from any thread — the PTY read thread included, which is where
        /// <see cref="RequestInvalidate"/> is called from.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 or greater than 1000.</exception>
        public static int TargetFrameRate
        {
            get => Volatile.Read(ref _targetFrameRate);
            set
            {
                if (value < 1 || value > MaxFrameRate)
                    throw new ArgumentOutOfRangeException(nameof(value), value,
                        $"Frame rate must be between 1 and {MaxFrameRate} frames per second.");

                Volatile.Write(ref _targetFrameRate, value);
            }
        }

        // Derived per use rather than cached beside the rate: an int write is atomic where a TimeSpan write is
        // not, so storing only the rate is what lets the setter above skip a lock.
        private static TimeSpan FrameInterval => TimeSpan.FromSeconds(1.0 / Volatile.Read(ref _targetFrameRate));

        // Controls waiting to be invalidated
        private static readonly HashSet<Control> Pending = new();

        // State
        private static bool _frameScheduled;
        private static DateTime _lastFrame = DateTime.MinValue;

        /// <summary>
        /// Request that a control be invalidated on the next coordinated frame.
        /// </summary>
        public static void RequestInvalidate(this Control control)
        {
            if (control == null)
                return;

            lock (Pending)
                Pending.Add(control);

            if (!_frameScheduled)
            {
                _frameScheduled = true;
                ScheduleFrame();
            }
        }

        private static void ScheduleFrame()
        {
            // Sampled once. Read twice, a rate change landing between the comparison and the subtraction below
            // yields a NEGATIVE delay, which Task.Delay throws on and which would kill the scheduling loop.
            var interval = FrameInterval;

            var now = DateTime.UtcNow;
            var elapsed = now - _lastFrame;

            // If enough time has passed, flush immediately on the UI thread
            if (elapsed >= interval)
            {
                Dispatcher.UIThread.Post(Flush);
                return;
            }

            // Otherwise schedule a delayed flush
            var delay = interval - elapsed;

            Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(delay);
                Flush();
            });
        }

        private static void Flush()
        {
            _frameScheduled = false;
            _lastFrame = DateTime.UtcNow;

            lock (Pending)
            {
                if (Pending.Count == 0)
                    return;

                foreach (var control in Pending)
                    control.InvalidateVisual();

                Pending.Clear();
            }
        }
    }
}