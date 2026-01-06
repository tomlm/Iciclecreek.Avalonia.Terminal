using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Iciclecreek.Terminal
{

    /// <summary>
    /// Synchronizes control invalidation to a target frame rate, so all terminals get invalidated together.
    /// </summary>
    public static class TerminalRenderThrottle
    {
        // Target frame rate (30 FPS = 33 ms)
        private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

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
            var now = DateTime.UtcNow;
            var elapsed = now - _lastFrame;

            // If enough time has passed, flush immediately on the UI thread
            if (elapsed >= FrameInterval)
            {
                Dispatcher.UIThread.Post(Flush);
                return;
            }

            // Otherwise schedule a delayed flush
            var delay = FrameInterval - elapsed;

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