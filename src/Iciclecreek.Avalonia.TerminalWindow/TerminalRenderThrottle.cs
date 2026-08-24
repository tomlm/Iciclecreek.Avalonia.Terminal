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

            // The set and the flag move together, under the one lock. Read outside it, the flag loses a
            // race that drops a frame: a caller can see it TRUE, decline to schedule, and only then add to
            // Pending — after Flush has already cleared the flag and drained the set. The control is then
            // queued with no frame coming, and the screen keeps whatever it last painted until something
            // else happens to request another. That is the last frame of a burst, which is the one that
            // matters: output stops, and the terminal is left showing text the buffer no longer has.
            bool schedule;
            lock (Pending)
            {
                Pending.Add(control);
                schedule = !_frameScheduled;
                if (schedule)
                    _frameScheduled = true;
            }

            if (schedule)
                ScheduleFrame();
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
            Control[] batch;

            lock (Pending)
            {
                // Cleared HERE, with the drain. Clearing it first leaves a window in which a request sees a
                // frame still scheduled, adds itself, and is then thrown away by the very drain it was
                // racing.
                _frameScheduled = false;
                _lastFrame = DateTime.UtcNow;

                if (Pending.Count == 0)
                    return;

                batch = new Control[Pending.Count];
                Pending.CopyTo(batch);
                Pending.Clear();
            }

            // Outside the lock: invalidation is UI work and a request arriving from the read task should
            // not be made to wait on it.
            foreach (var control in batch)
                control.InvalidateVisual();
        }
    }
}