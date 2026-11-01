using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Porta.Pty;

namespace Iciclecreek.Terminal
{
    /// <summary>
    /// What a connection's readers share across a handover: the right to read it, and the bytes a
    /// reader took that it could no longer deliver.
    /// </summary>
    /// <remarks>
    /// <para>A blocking-mode reader cannot be stopped without closing the stream (issue #123), so a
    /// detached view's read stays parked until the process next speaks -- and that read takes the
    /// chunk. The next owner's reader used to park its own read on the same descriptor beside it, and
    /// whichever woke first took the chunk. When the stale one did after the new owner attached, the
    /// chunk could not be handed over without risking reordering, and was dropped.</para>
    ///
    /// <para>The <see cref="Handover.Gate"/> takes the race away. A reader holds it across its read AND
    /// across delivering or parking what it read, so only one read is ever outstanding on a connection
    /// and the order readers pass through the gate IS stream order. A new owner's reader waits its turn
    /// behind a stale one instead of racing it, and on getting the gate replays whatever was parked
    /// before reading on. Parked bytes are therefore always the earliest undelivered output, whenever
    /// the new owner attached, and delivering them first is exactly in order.</para>
    ///
    /// <para>Keyed weakly: a connection nobody re-attached dies with its parked bytes, which is the
    /// right outcome -- there is no one to deliver them to. The same goes for a host that detaches and
    /// then reads the connection itself rather than through a view: a stale blocking reader still takes
    /// one chunk, which waits here for a view that never comes. A cancellable connection
    /// (<see cref="IPtyConnection.SupportsCancellableRead"/>) never leaves a reader parked, but it can
    /// still leave a chunk here: one already read when the detach landed, refused by the session check
    /// on its way to the emulator.</para>
    ///
    /// <para>Deliberately not exposed to hosts. Taking the bytes is only safe once no reader can park
    /// more behind them -- for a blocking connection that is not until the process next speaks -- so a
    /// "take what is pending" call would hand a host an answer that can already be incomplete.</para>
    /// </remarks>
    internal static class PendingHandoverBytes
    {
        internal sealed class Handover
        {
            /// <summary>Held by whichever reader is reading this connection, from before its read until
            /// what it read has been delivered or parked.</summary>
            public readonly SemaphoreSlim Gate = new(1, 1);

            private byte[]? _bytes;

            /// <summary>Appends a chunk its reader could not deliver. Call holding <see cref="Gate"/>.</summary>
            public void Park(ReadOnlySpan<byte> chunk)
            {
                lock (this)
                {
                    _bytes = Join(_bytes, chunk);
                }
            }

            /// <summary>Puts back bytes a <see cref="Claim"/> took but could not deliver, ahead of
            /// anything parked since -- they were read first.</summary>
            public void Unclaim(byte[] bytes)
            {
                lock (this)
                {
                    _bytes = _bytes is null ? bytes : Join(bytes, _bytes);
                }
            }

            /// <summary>Takes everything parked. The caller must deliver it before reading on.</summary>
            public byte[]? Claim()
            {
                // The common case -- nothing parked -- is a field read, not a lock, because the read
                // loop asks before every read.
                if (Volatile.Read(ref _bytes) is null)
                    return null;

                lock (this)
                {
                    var bytes = _bytes;
                    _bytes = null;
                    return bytes;
                }
            }

            private static byte[] Join(byte[]? first, ReadOnlySpan<byte> second)
            {
                if (first is null)
                    return second.ToArray();

                var joined = new byte[first.Length + second.Length];
                first.CopyTo(joined, 0);
                second.CopyTo(joined.AsSpan(first.Length));
                return joined;
            }
        }

        private static readonly ConditionalWeakTable<IPtyConnection, Handover> Handovers = new();

        public static Handover For(IPtyConnection connection) => Handovers.GetOrCreateValue(connection);
    }
}
