using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Smpp.Protocol
{
    public sealed class SmppSessionOptions
    {
        public SmppSessionOptions()
        {
            WindowSize = 10;
            ResponseTimeout = TimeSpan.FromSeconds(10);
            EnquireLinkInterval = TimeSpan.FromSeconds(30);
        }

        public int WindowSize { get; set; }

        public TimeSpan ResponseTimeout { get; set; }

        public TimeSpan EnquireLinkInterval { get; set; }
    }

    public sealed class SmppSession : IDisposable
    {
        private readonly Stream _stream;
        private readonly SmppSessionOptions _options;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _window;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<uint, PendingRequest> _pending = new ConcurrentDictionary<uint, PendingRequest>();
        private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _sequence;
        private int _closed;
        private Timer _timer;

        public SmppSession(Stream stream, SmppSessionOptions options, ILogger logger)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

            _stream = stream;
            _options = options ?? new SmppSessionOptions();
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            var window = _options.WindowSize <= 0 ? 10 : _options.WindowSize;
            _window = new SemaphoreSlim(window, window);
        }

        public Func<SmppPdu, CancellationToken, Task<SmppPdu>> OnRequest { get; set; }

        public Task Completion
        {
            get { return _completion.Task; }
        }

        public async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token))
            {
                try
                {
                    while (!linked.IsCancellationRequested)
                    {
                        var frame = await SmppIo.ReadFrameAsync(_stream, linked.Token).ConfigureAwait(false);
                        if (frame == null)
                        {
                            break;
                        }

                        SmppPdu pdu;
                        try
                        {
                            pdu = PduCodec.Decode(frame);
                        }
                        catch (InvalidDataException ex)
                        {
                            _logger.LogWarning(ex, "Closing SMPP session after a malformed PDU.");
                            break;
                        }

                        if (SmppCommandInfo.IsResponse(pdu.Command))
                        {
                            PendingRequest pending;
                            if (_pending.TryRemove(pdu.SequenceNumber, out pending))
                            {
                                pending.ReleaseWindow();
                                pending.Completion.TrySetResult(pdu);
                            }

                            continue;
                        }

                        if (pdu.Command == SmppCommand.EnquireLink)
                        {
                            var response = new EmptyBodyPdu(SmppCommand.EnquireLinkResp);
                            response.SequenceNumber = pdu.SequenceNumber;
                            await SendAsync(response, linked.Token).ConfigureAwait(false);
                            continue;
                        }

                        await DispatchRequestAsync(pdu, linked.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "SMPP session ended.");
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SMPP receive loop stopped.");
                }
                finally
                {
                    FaultPending();
                    _completion.TrySetResult(true);
                }
            }
        }

        public async Task<SmppPdu> RequestAsync(SmppPdu pdu, CancellationToken cancellationToken)
        {
            if (pdu == null)
            {
                throw new ArgumentNullException("pdu");
            }

            await _window.WaitAsync(cancellationToken).ConfigureAwait(false);
            var sequence = NextSequence();
            pdu.SequenceNumber = sequence;
            var pending = new PendingRequest(_window);
            _pending[sequence] = pending;
            try
            {
                await SendAsync(pdu, cancellationToken).ConfigureAwait(false);
                var timeout = Task.Delay(_options.ResponseTimeout, cancellationToken);
                await Task.WhenAny(pending.Completion.Task, timeout).ConfigureAwait(false);
                if (pending.Completion.Task.IsCompleted)
                {
                    return await pending.Completion.Task.ConfigureAwait(false);
                }

                PendingRequest removed;
                if (_pending.TryRemove(sequence, out removed))
                {
                    removed.ReleaseWindow();
                }

                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("SMPP response timed out.");
            }
            catch
            {
                if (!pending.Completion.Task.IsCompleted)
                {
                    PendingRequest removed;
                    if (_pending.TryRemove(sequence, out removed))
                    {
                        removed.ReleaseWindow();
                    }
                }

                throw;
            }
        }

        public async Task SendAsync(SmppPdu pdu, CancellationToken cancellationToken)
        {
            var bytes = PduCodec.Encode(pdu);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void StartEnquireLink()
        {
            var interval = _options.EnquireLinkInterval;
            if (interval <= TimeSpan.Zero)
            {
                return;
            }

            _timer = new Timer(_ =>
            {
                var ignored = SendEnquireAsync();
            }, null, interval, interval);
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                }
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _stream.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            Close();
            _cts.Dispose();
            _window.Dispose();
            _writeLock.Dispose();
        }

        private async Task SendEnquireAsync()
        {
            try
            {
                if (Volatile.Read(ref _closed) != 0)
                {
                    return;
                }

                await RequestAsync(new EmptyBodyPdu(SmppCommand.EnquireLink), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enquire link failed.");
                Close();
            }
        }

        private async Task DispatchRequestAsync(SmppPdu pdu, CancellationToken cancellationToken)
        {
            SmppPdu response = null;
            try
            {
                var handler = OnRequest;
                if (handler != null)
                {
                    response = await handler(pdu, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SMPP request handler failed.");
                response = new EmptyBodyPdu(SmppCommand.GenericNack) { Status = SmppStatus.SystemError };
            }

            if (response == null)
            {
                return;
            }

            if (response.SequenceNumber == 0)
            {
                response.SequenceNumber = pdu.SequenceNumber;
            }

            await SendAsync(response, cancellationToken).ConfigureAwait(false);
        }

        private uint NextSequence()
        {
            while (true)
            {
                var current = Volatile.Read(ref _sequence);
                var next = current >= 0x7FFFFFFF ? 1 : current + 1;
                if (Interlocked.CompareExchange(ref _sequence, next, current) == current)
                {
                    return (uint)next;
                }
            }
        }

        private void FaultPending()
        {
            var error = new IOException("SMPP session closed.");
            foreach (var pending in _pending.Values)
            {
                pending.ReleaseWindow();
                pending.Completion.TrySetException(error);
            }

            _pending.Clear();
        }

        private sealed class PendingRequest
        {
            private readonly SemaphoreSlim _window;
            private int _released;

            public PendingRequest(SemaphoreSlim window)
            {
                _window = window;
                Completion = new TaskCompletionSource<SmppPdu>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public TaskCompletionSource<SmppPdu> Completion { get; private set; }

            public void ReleaseWindow()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    _window.Release();
                }
            }
        }
    }

    public static class SmppTransport
    {
        public static async Task<Stream> ConnectAsync(string host, int port, bool useTls, bool allowInvalidCertificate, CancellationToken cancellationToken)
        {
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(host, port).ConfigureAwait(false);
            Stream stream = tcp.GetStream();
            if (!useTls)
            {
                return new OwnedStream(stream, tcp);
            }

            var ssl = new System.Net.Security.SslStream(stream, false, (sender, certificate, chain, errors) => allowInvalidCertificate || errors == System.Net.Security.SslPolicyErrors.None);
            await ssl.AuthenticateAsClientAsync(host).ConfigureAwait(false);
            return new OwnedStream(ssl, tcp);
        }
    }

    internal sealed class OwnedStream : Stream
    {
        private readonly Stream _inner;
        private readonly TcpClient _client;

        public OwnedStream(Stream inner, TcpClient client)
        {
            _inner = inner;
            _client = client;
        }

        public override bool CanRead { get { return _inner.CanRead; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return _inner.CanWrite; } }
        public override long Length { get { return _inner.Length; } }
        public override long Position { get { return _inner.Position; } set { throw new NotSupportedException(); } }
        public override void Flush() { _inner.Flush(); }
        public override Task FlushAsync(CancellationToken cancellationToken) { return _inner.FlushAsync(cancellationToken); }
        public override int Read(byte[] buffer, int offset, int count) { return _inner.Read(buffer, offset, count); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { return _inner.ReadAsync(buffer, offset, count, cancellationToken); }
        public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { return _inner.WriteAsync(buffer, offset, count, cancellationToken); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _client.Close();
            }

            base.Dispose(disposing);
        }
    }
}
