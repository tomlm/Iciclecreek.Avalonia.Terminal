using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using SkiaSharp;

namespace Iciclecreek.Terminal.Skia
{
    /// <summary>
    /// An uploaded picture, shared between the cache and every frame in flight that draws it.
    /// </summary>
    /// <remarks>
    /// Counted rather than left to the finalizer. A SixelBuffer app makes a new picture for every
    /// changed run of cells, several a frame, and their native pixels are invisible to the GC: freed
    /// only when a finalizer happens to run, they piled up. The cache holds one reference while the
    /// picture is on screen and each frame that draws it holds one until the compositor retires it,
    /// so the last of them out frees it -- on whichever thread that is.
    /// </remarks>
    internal sealed class SharedSkImage
    {
        private int _refs = 1;

        public SharedSkImage(SKImage image) => Image = image;

        public SKImage Image { get; }

        /// <summary>
        /// The picture as a texture on <paramref name="gpu"/>, made the first time it is drawn there
        /// and kept for as long as the picture lives; the CPU image when there is no GPU.
        /// </summary>
        /// <remarks>
        /// <para>Render thread only. Skia can cache a CPU image's upload on its own, but measured on a
        /// desktop GPU the cost of a frame of pictures tracked their total size -- about 5 ms per MB,
        /// with only a hundred of them -- which is what uploading every one again every frame looks
        /// like. A texture made here is ours, not the cache's, so it cannot be evicted between frames.</para>
        /// <para>Made per context: a texture belongs to the context that made it, and a lost device or
        /// a moved window can hand the next frame a different one.</para>
        /// </remarks>
        public SKImage ForDrawing(GRContext? gpu)
        {
            if (gpu is null || Volatile.Read(ref _refs) <= 0)
                return Image;

            if (_texture is not null && ReferenceEquals(_textureContext, gpu))
                return _texture;

            if (_texture is not null)
                PendingTextureDisposal.Enqueue(_texture);

            _texture = Image.ToTextureImage(gpu);
            _textureContext = gpu;
            return _texture ?? Image;
        }

        private SKImage? _texture;
        private GRContext? _textureContext;

        /// <summary>
        /// Textures whose pictures are gone, waiting for the render thread. A GPU resource is freed on
        /// the thread whose context made it, and the last reference to a picture is as often given up
        /// on the UI thread -- by the cache, as the picture leaves the screen.
        /// </summary>
        internal static readonly ConcurrentQueue<SKImage> PendingTextureDisposal = new();

        /// <summary>Frees the textures left behind by pictures that are gone. Render thread, context current.</summary>
        internal static void DisposePendingTextures()
        {
            while (PendingTextureDisposal.TryDequeue(out var texture))
                texture.Dispose();
        }

        public SharedSkImage AddRef()
        {
            Interlocked.Increment(ref _refs);
            return this;
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) != 0)
                return;

            Image.Dispose();
            if (_texture is not null)
                PendingTextureDisposal.Enqueue(_texture);
            _texture = null;
        }
    }

    /// <summary>
    /// The pictures on screen, uploaded once each and kept while they stay visible. UI thread only.
    /// </summary>
    /// <remarks>
    /// <para>An immutable <see cref="SKImage"/> per picture: Skia uploads it to the GPU the first time
    /// it is drawn and reuses the texture after that. An animation re-uploads when its frame serial
    /// moves, the same rule the classic path's bitmap cache follows.</para>
    /// <para>Kept to what the last frame showed. Anything not drawn by the frame just built is let go,
    /// which keeps the cache the size of one screen however much scrolls past; a picture scrolled back
    /// into view is uploaded again, which costs one copy.</para>
    /// </remarks>
    internal sealed class SkiaPictureCache
    {
        private sealed class Entry
        {
            public required SharedSkImage Image;
            public int FrameSerial;
            public long LastFrame;
        }

        private readonly Dictionary<XTerm.Graphics.TerminalImage, Entry> _entries = new(ReferenceEqualityComparer.Instance);
        private readonly List<XTerm.Graphics.TerminalImage> _unused = new();

        private sealed class StripEntry
        {
            public required SharedSkImage Image;
            public long Version;
            public long LastFrame;
        }

        private readonly Dictionary<XTerm.Graphics.SixelStrip, StripEntry> _strips = new(ReferenceEqualityComparer.Instance);
        private readonly List<XTerm.Graphics.SixelStrip> _unusedStrips = new();

        /// <summary>
        /// The uploaded form of a line's Sixel pixels for frame <paramref name="frame"/>, uploaded again
        /// whenever the strip's version moves.
        /// </summary>
        /// <remarks>
        /// The pty thread can paint the strip while this copies it, so the version is read before the
        /// copy and again after. A copy that raced a write is drawn this frame but remembered under a
        /// version no strip has, so the next frame uploads it again -- the write that raced it has
        /// already asked for that frame.
        /// </remarks>
        public SharedSkImage? GetStrip(XTerm.Graphics.SixelStrip strip, long frame)
        {
            var version = strip.Version;
            if (_strips.TryGetValue(strip, out var entry))
            {
                if (entry.Version == version)
                {
                    entry.LastFrame = frame;
                    return entry.Image;
                }

                entry.Image.Release();
                _strips.Remove(strip);
            }

            var info = new SKImageInfo(strip.PixelWidth, strip.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var pixels = strip.Pixels.Span;
            var length = Math.Min(pixels.Length, strip.Stride * strip.PixelHeight);
            var uploaded = SKImage.FromPixelCopy(info, pixels.Slice(0, length), strip.Stride);
            if (uploaded is null)
                return null;

            var shared = new SharedSkImage(uploaded);
            _strips[strip] = new StripEntry
            {
                Image = shared,
                Version = strip.Version == version ? version : -1,
                LastFrame = frame,
            };
            return shared;
        }

        /// <summary>The uploaded form of <paramref name="image"/> for frame <paramref name="frame"/>, or null if it cannot be uploaded.</summary>
        public SharedSkImage? Get(XTerm.Graphics.TerminalImage image, long frame)
        {
            if (_entries.TryGetValue(image, out var entry))
            {
                if (entry.FrameSerial == image.FrameSerial)
                {
                    entry.LastFrame = frame;
                    return entry.Image;
                }

                entry.Image.Release();
                _entries.Remove(image);
            }

            var uploaded = Upload(image);
            if (uploaded is null)
                return null;

            _entries[image] = new Entry { Image = uploaded, FrameSerial = image.FrameSerial, LastFrame = frame };
            return uploaded;
        }

        /// <summary>Lets go of every picture the frame <paramref name="frame"/> did not draw.</summary>
        public void Sweep(long frame)
        {
            foreach (var (image, entry) in _entries)
            {
                if (entry.LastFrame != frame)
                    _unused.Add(image);
            }

            foreach (var image in _unused)
            {
                _entries[image].Image.Release();
                _entries.Remove(image);
            }

            _unused.Clear();

            foreach (var (strip, entry) in _strips)
            {
                if (entry.LastFrame != frame)
                    _unusedStrips.Add(strip);
            }

            foreach (var strip in _unusedStrips)
            {
                _strips[strip].Image.Release();
                _strips.Remove(strip);
            }

            _unusedStrips.Clear();
        }

        /// <summary>
        /// CurrentPixels, not Pixels: for an animation that is the frame being shown. The decoder writes
        /// BGRA, unpremultiplied -- the same format the classic path's bitmaps are made in.
        /// </summary>
        private static SharedSkImage? Upload(XTerm.Graphics.TerminalImage image)
        {
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
                return null;

            var info = new SKImageInfo(image.PixelWidth, image.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var uploaded = SKImage.FromPixelCopy(info, image.CurrentPixels.Span, image.Stride);
            return uploaded is null ? null : new SharedSkImage(uploaded);
        }
    }
}
