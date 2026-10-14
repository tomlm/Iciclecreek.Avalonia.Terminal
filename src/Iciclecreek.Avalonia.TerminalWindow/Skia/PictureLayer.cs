using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Iciclecreek.Terminal.Skia
{
    /// <summary>One picture draw, planned on the UI thread: the upload, which part of it, where, and the clip.</summary>
    internal readonly record struct PictureDraw(SharedSkImage Image, SKRect Source, SKRect Destination, SKRect? Clip);

    /// <summary>
    /// The pictures in front of a frame's text -- every row's Sixel strip, then the Kitty pictures in
    /// front of it -- drawn onto the Skia canvas from GPU textures.
    /// </summary>
    /// <remarks>
    /// <para><b>Why pictures only.</b> Measured at full screen on a desktop GPU, the classic path draws
    /// text faster than a Skia text layer did (1 ms against 5), because it replays each line's shaped
    /// glyph runs; but its pictures go through <c>DrawImage</c> of an Avalonia bitmap, which cost about
    /// five times what drawing a texture costs (Sixel strips 3.7-5.5 ms against 0.7; 741 Kitty
    /// pictures 250 ms against 35), and a larger GPU cache did not close the gap. So text stays on
    /// the classic path -- the one Consolonia sees -- and only this goes to Skia.</para>
    /// <para><b>Threads.</b> Everything is planned on the UI thread, where the buffer is safe to read:
    /// which pictures, which rectangles, which clip. This only draws, on the render thread, from
    /// uploads it holds a reference on until the compositor retires the frame and disposes it.</para>
    /// <para><b>Where there is no Skia,</b> <see cref="Unsupported"/> says so and the view falls back
    /// to <c>DrawImage</c> from the next frame on.</para>
    /// </remarks>
    internal sealed class PictureLayer : ICustomDrawOperation
    {
        private readonly List<PictureDraw> _draws;
        private int _released;
        private int _unsupported;

        public PictureLayer(List<PictureDraw> draws, Rect bounds)
        {
            _draws = draws;
            Bounds = bounds;
        }

        public Rect Bounds { get; }

        /// <summary>Set when the backend would not lease a Skia canvas, so nothing was drawn.</summary>
        public bool Unsupported => Volatile.Read(ref _unsupported) != 0;

        public bool HitTest(Point p) => false;

        // Never equal: each frame's pictures are its own, and an equal operation would let the
        // compositor keep drawing one whose uploads have been released.
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (Volatile.Read(ref _released) != 0)
                return;

            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
            {
                Volatile.Write(ref _unsupported, 1);
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var gpu = lease.GrContext;

            // Textures whose pictures have gone, freed here because this is the thread whose context
            // made them.
            SharedSkImage.DisposePendingTextures();

            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            foreach (var draw in _draws)
            {
                var image = draw.Image.ForDrawing(gpu);
                if (draw.Clip is { } clip)
                {
                    canvas.Save();
                    canvas.ClipRect(clip, SKClipOperation.Intersect, antialias: false);
                    canvas.DrawImage(image, draw.Source, draw.Destination, sampling);
                    canvas.Restore();
                }
                else
                {
                    canvas.DrawImage(image, draw.Source, draw.Destination, sampling);
                }
            }
        }

        /// <summary>The frame has retired: give back the references on its uploads.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            foreach (var draw in _draws)
                draw.Image.Release();
        }
    }
}
