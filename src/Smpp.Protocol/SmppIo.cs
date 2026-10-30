using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Smpp.Protocol
{
    public static class SmppIo
    {
        public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            var header = new byte[16];
            if (!await ReadExactAsync(stream, header, 0, 16, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var length = (int)Endian.ReadUInt32(header, 0);
            if (length < 16 || length > PduCodec.MaxPduLength)
            {
                throw new InvalidDataException("Invalid SMPP command length.");
            }

            var packet = new byte[length];
            Buffer.BlockCopy(header, 0, packet, 0, 16);
            if (length > 16 && !await ReadExactAsync(stream, packet, 16, length - 16, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return packet;
        }

        public static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var delay = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
            if (completed != task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("The SMPP operation timed out.");
            }

            return await task.ConfigureAwait(false);
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = 0;
            while (read < count)
            {
                var n = await stream.ReadAsync(buffer, offset + read, count - read, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    return false;
                }

                read += n;
            }

            return true;
        }
    }

    public sealed class RateLimiter
    {
        private readonly int _perSecond;
        private readonly object _gate = new object();
        private int _count;
        private long _windowStart;

        public RateLimiter(int perSecond)
        {
            _perSecond = perSecond;
        }

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (_perSecond <= 0)
            {
                return;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int delayMs;
                lock (_gate)
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    var elapsedMs = _windowStart == 0
                        ? 1000L
                        : ((now - _windowStart) * 1000L) / System.Diagnostics.Stopwatch.Frequency;
                    if (elapsedMs >= 1000)
                    {
                        _windowStart = now;
                        _count = 0;
                    }

                    if (_count < _perSecond)
                    {
                        _count++;
                        return;
                    }

                    delayMs = (int)(1000 - elapsedMs);
                    if (delayMs < 1)
                    {
                        delayMs = 1;
                    }
                }

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
