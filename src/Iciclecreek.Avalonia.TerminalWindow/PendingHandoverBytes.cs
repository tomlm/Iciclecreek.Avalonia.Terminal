using System;
using System.Runtime.CompilerServices;
using Porta.Pty;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// Bytes a detached view's reader stole off a connection before its new owner attached.
    /// </summary>
    /// <remarks>
    /// <para>A blocking-mode reader cannot be stopped without closing the stream (issue #123), so a
    /// detached view's read stays parked until the process next speaks — and that read takes the
    /// chunk. If the connection's new owner has not attached yet, the chunk can be handed over
    /// losslessly: it is by construction the earliest unread output, so the new owner consuming it
    /// FIRST preserves stream order exactly.</para>
    ///
    /// <para>Once a new owner IS attached, its own reader races the stale one on the same
    /// descriptor, and a stolen chunk delivered late would interleave out of order — splicing bytes
    /// into the middle of whatever the new reader already consumed, which can land inside an escape
    /// sequence. Reordered output corrupts; lost output merely gaps. So a chunk stolen after attach
    /// is dropped, and this table narrows the loss window rather than pretending to close it.
    /// Closing it fully is what <see cref="IPtyConnection.SupportsCancellableRead"/> is for.</para>
    ///
    /// <para>Keyed weakly: a connection nobody re-attached dies with its parked bytes, which is the
    /// right outcome — there is no one to deliver them to.</para>
    /// </remarks>
    internal static class PendingHandoverBytes
    {
        private sealed class Box
        {
            public byte[]? Bytes;
            public bool OwnerAttached = true;   // the spawning view is the first owner
        }

        private static readonly ConditionalWeakTable<IPtyConnection, Box> Boxes = new();

        /// <summary>Marks the connection as ownerless; parked bytes become acceptable.</summary>
        public static void NoteDetached(IPtyConnection connection)
        {
            var box = Boxes.GetOrCreateValue(connection);
            lock (box)
            {
                box.OwnerAttached = false;
            }
        }

        /// <summary>
        /// Parks a stolen chunk, but only while no new owner is attached.
        /// </summary>
        /// <returns>False when an owner already attached — the caller must drop the chunk, because
        /// delivering it late risks reordering, which is worse than the gap.</returns>
        public static bool TryPark(IPtyConnection connection, ReadOnlySpan<byte> chunk)
        {
            var box = Boxes.GetOrCreateValue(connection);
            lock (box)
            {
                if (box.OwnerAttached)
                    return false;

                if (box.Bytes is null)
                {
                    box.Bytes = chunk.ToArray();
                }
                else
                {
                    var joined = new byte[box.Bytes.Length + chunk.Length];
                    box.Bytes.CopyTo(joined, 0);
                    chunk.CopyTo(joined.AsSpan(box.Bytes.Length));
                    box.Bytes = joined;
                }

                return true;
            }
        }

        /// <summary>
        /// Claims parked bytes and marks the connection owned. The caller must consume what it gets
        /// BEFORE starting its own reader, or the order guarantee that makes parking safe is gone.
        /// </summary>
        public static byte[]? ClaimOnAttach(IPtyConnection connection)
        {
            var box = Boxes.GetOrCreateValue(connection);
            lock (box)
            {
                box.OwnerAttached = true;
                var bytes = box.Bytes;
                box.Bytes = null;
                return bytes;
            }
        }
    }
}
