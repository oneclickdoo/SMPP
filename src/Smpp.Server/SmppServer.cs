using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Smpp.Protocol;

namespace Smpp.Server
{
    public sealed class SmppServerOptions
    {
        public SmppServerOptions()
        {
            BindAddress = IPAddress.Any;
            Port = 2775;
            SystemId = "smpp-gw";
            WindowSize = 100;
            EnquireLinkInterval = TimeSpan.FromSeconds(30);
            ResponseTimeout = TimeSpan.FromSeconds(10);
            WorkerCount = 4;
            QueueCapacity = 1000;
        }

        public IPAddress BindAddress { get; set; }

        public int Port { get; set; }

        public string SystemId { get; set; }

        public bool UseTls { get; set; }

        public X509Certificate2 Certificate { get; set; }

        public int WindowSize { get; set; }

        public TimeSpan EnquireLinkInterval { get; set; }

        public TimeSpan ResponseTimeout { get; set; }

        public int WorkerCount { get; set; }

        public int QueueCapacity { get; set; }
    }

    public sealed class BindContext
    {
        public BindContext()
        {
            SystemId = string.Empty;
            Password = string.Empty;
            SystemType = string.Empty;
            RemoteAddress = IPAddress.None;
        }

        public string SystemId { get; set; }

        public string Password { get; set; }

        public string SystemType { get; set; }

        public SmppBindRole Role { get; set; }

        public IPAddress RemoteAddress { get; set; }
    }

    public sealed class BindDecision
    {
        public bool Accepted { get; set; }

        public uint Status { get; set; }

        public Guid AccountId { get; set; }

        public int ThroughputPerSecond { get; set; }

        public object State { get; set; }

        public static BindDecision Reject(uint status)
        {
            return new BindDecision { Accepted = false, Status = status };
        }

        public static BindDecision Allow(Guid accountId, int throughputPerSecond, object state)
        {
            return new BindDecision
            {
                Accepted = true,
                Status = SmppStatus.Ok,
                AccountId = accountId,
                ThroughputPerSecond = throughputPerSecond,
                State = state
            };
        }
    }

    public sealed class BoundSession
    {
        public BoundSession()
        {
            SystemId = string.Empty;
        }

        public string SystemId { get; set; }

        public Guid AccountId { get; set; }

        public SmppBindRole Role { get; set; }

        public int ThroughputPerSecond { get; set; }

        public object State { get; set; }
    }

    public sealed class ServerSubmitResult
    {
        public ServerSubmitResult()
        {
            MessageId = string.Empty;
        }

        public uint Status { get; set; }

        public string MessageId { get; set; }

        public Func<CancellationToken, Task> AfterResponseAsync { get; set; }

        public static ServerSubmitResult Ok(string messageId)
        {
            return new ServerSubmitResult { Status = SmppStatus.Ok, MessageId = messageId ?? string.Empty };
        }

        public static ServerSubmitResult Fail(uint status)
        {
            return new ServerSubmitResult { Status = status, MessageId = string.Empty };
        }
    }

    public sealed class SmppServer : IDisposable
    {
        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private readonly Dictionary<string, ClientConnection> _sessions = new Dictionary<string, ClientConnection>(StringComparer.Ordinal);
        private TcpListener _listener;
        private Channel<SubmitWork> _queue;
        private int _port;

        public SmppServer(ILogger logger)
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public Func<BindContext, CancellationToken, Task<BindDecision>> AuthenticateAsync { get; set; }

        public Func<BoundSession, SmPdu, CancellationToken, Task<ServerSubmitResult>> SubmitAsync { get; set; }

        public int BoundPort
        {
            get { return _port; }
        }

        public async Task RunAsync(SmppServerOptions options, CancellationToken cancellationToken, TaskCompletionSource<bool> started)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            if (options.UseTls && options.Certificate == null)
            {
                throw new InvalidOperationException("SMPP server TLS requires a certificate.");
            }

            _queue = Channel.CreateBounded<SubmitWork>(new BoundedChannelOptions(options.QueueCapacity <= 0 ? 1000 : options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });

            var workers = Math.Max(1, options.WorkerCount);
            for (var i = 0; i < workers; i++)
            {
                var ignored = Task.Run(() => ProcessQueueAsync(cancellationToken));
            }

            _listener = new TcpListener(options.BindAddress ?? IPAddress.Any, options.Port);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _logger.LogInformation("SMPP server listening on port {Port}.", _port);
            if (started != null)
            {
                started.TrySetResult(true);
            }

            using (cancellationToken.Register(StopListener))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }
                    catch (SocketException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    client.NoDelay = true;
                    var ignored = Task.Run(() => HandleClientAsync(client, options, cancellationToken));
                }
            }
        }

        public bool TryGetBound(string systemId, out BoundSession session)
        {
            session = null;
            if (string.IsNullOrEmpty(systemId))
            {
                return false;
            }

            lock (_sync)
            {
                ClientConnection connection;
                if (!_sessions.TryGetValue(systemId, out connection))
                {
                    return false;
                }

                session = connection.Info;
                return true;
            }
        }

        public IList<BoundSession> GetBoundSessions()
        {
            lock (_sync)
            {
                var list = new List<BoundSession>(_sessions.Count);
                foreach (var pair in _sessions)
                {
                    list.Add(pair.Value.Info);
                }

                return list;
            }
        }

        public async Task DeliverAsync(string systemId, SmPdu deliver, CancellationToken cancellationToken)
        {
            ClientConnection connection;
            lock (_sync)
            {
                if (!_sessions.TryGetValue(systemId, out connection))
                {
                    throw new InvalidOperationException("SMPP client is not bound.");
                }
            }

            deliver.Command = SmppCommand.DeliverSm;
            var response = await connection.Session.RequestAsync(deliver, cancellationToken).ConfigureAwait(false);
            if (response.Status != SmppStatus.Ok)
            {
                throw new InvalidOperationException("Client rejected deliver_sm with status 0x" + response.Status.ToString("X8") + ".");
            }
        }

        public void Dispose()
        {
            StopListener();
        }

        private async Task HandleClientAsync(TcpClient client, SmppServerOptions options, CancellationToken cancellationToken)
        {
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            var address = remote == null ? IPAddress.None : remote.Address;
            try
            {
                Stream stream = client.GetStream();
                if (options.UseTls)
                {
                    var ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsServerAsync(options.Certificate).ConfigureAwait(false);
                    stream = ssl;
                }

                using (var bindTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    bindTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    var frame = await SmppIo.ReadFrameAsync(stream, bindTimeout.Token).ConfigureAwait(false);
                    if (frame == null)
                    {
                        return;
                    }

                    var pdu = PduCodec.Decode(frame);
                    var bind = pdu as BindPdu;
                    if (bind == null || !SmppCommandInfo.IsBind(bind.Command))
                    {
                        await WriteOnceAsync(stream, new EmptyBodyPdu(SmppCommand.GenericNack)
                        {
                            Status = SmppStatus.InvalidBindStatus,
                            SequenceNumber = pdu.SequenceNumber
                        }).ConfigureAwait(false);
                        return;
                    }

                    var decision = await InvokeAuthenticateAsync(new BindContext
                    {
                        SystemId = bind.SystemId,
                        Password = bind.Password,
                        SystemType = bind.SystemType,
                        Role = bind.Role,
                        RemoteAddress = address
                    }, cancellationToken).ConfigureAwait(false);

                    var responseCommand = bind.Command == SmppCommand.BindTransmitter
                        ? SmppCommand.BindTransmitterResp
                        : bind.Command == SmppCommand.BindReceiver
                            ? SmppCommand.BindReceiverResp
                            : SmppCommand.BindTransceiverResp;

                    if (decision == null || !decision.Accepted)
                    {
                        var status = decision == null ? SmppStatus.BindFailed : decision.Status;
                        await WriteOnceAsync(stream, new BindRespPdu(responseCommand)
                        {
                            Status = status,
                            SequenceNumber = bind.SequenceNumber,
                            SystemId = string.Empty
                        }).ConfigureAwait(false);
                        return;
                    }

                    var session = new SmppSession(stream, new SmppSessionOptions
                    {
                        WindowSize = options.WindowSize,
                        ResponseTimeout = options.ResponseTimeout,
                        EnquireLinkInterval = options.EnquireLinkInterval
                    }, _logger);

                    var connection = new ClientConnection
                    {
                        Session = session,
                        Info = new BoundSession
                        {
                            SystemId = bind.SystemId,
                            AccountId = decision.AccountId,
                            Role = bind.Role,
                            ThroughputPerSecond = decision.ThroughputPerSecond,
                            State = decision.State
                        },
                        Rate = new RateLimiter(decision.ThroughputPerSecond)
                    };
                    session.OnRequest = (request, token) => OnSessionRequestAsync(connection, request, token);
                    Register(connection);
                    var receiveLoop = session.RunReceiveLoopAsync(cancellationToken);

                    var bindResponse = new BindRespPdu(responseCommand)
                    {
                        Status = SmppStatus.Ok,
                        SequenceNumber = bind.SequenceNumber,
                        SystemId = options.SystemId ?? "smpp-gw"
                    };
                    bindResponse.Tlvs.Add(Tlv.Byte(TlvTag.ScInterfaceVersion, 0x34));
                    await session.SendAsync(bindResponse, cancellationToken).ConfigureAwait(false);
                    session.StartEnquireLink();
                    _logger.LogInformation("SMPP client {SystemId} bound from {Address} as {Role}.", bind.SystemId, address, bind.Role);
                    await receiveLoop.ConfigureAwait(false);
                    Unregister(connection);
                    session.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SMPP client connection from {Address} closed.", address);
            }
            finally
            {
                client.Close();
            }
        }

        private async Task<SmppPdu> OnSessionRequestAsync(ClientConnection connection, SmppPdu pdu, CancellationToken cancellationToken)
        {
            var submit = pdu as SmPdu;
            if (submit != null && submit.Command == SmppCommand.SubmitSm)
            {
                if (connection.Info.Role == SmppBindRole.Receiver)
                {
                    return new SmRespPdu(SmppCommand.SubmitSmResp)
                    {
                        Status = SmppStatus.InvalidBindStatus,
                        MessageId = string.Empty
                    };
                }

                var work = new SubmitWork { Connection = connection, Pdu = submit };
                if (!_queue.Writer.TryWrite(work))
                {
                    return new SmRespPdu(SmppCommand.SubmitSmResp)
                    {
                        Status = SmppStatus.MessageQueueFull,
                        MessageId = string.Empty
                    };
                }

                return null;
            }

            if (pdu.Command == SmppCommand.Unbind)
            {
                Unregister(connection);
                return new EmptyBodyPdu(SmppCommand.UnbindResp);
            }

            return new EmptyBodyPdu(SmppCommand.GenericNack) { Status = SmppStatus.InvalidCommandId };
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    SubmitWork work;
                    while (_queue.Reader.TryRead(out work))
                    {
                        await HandleSubmitAsync(work, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task HandleSubmitAsync(SubmitWork work, CancellationToken cancellationToken)
        {
            ServerSubmitResult result;
            try
            {
                await work.Connection.Rate.WaitAsync(cancellationToken).ConfigureAwait(false);
                var handler = SubmitAsync;
                result = handler == null
                    ? ServerSubmitResult.Fail(SmppStatus.SystemError)
                    : await handler(work.Connection.Info, work.Pdu, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Submit from {SystemId} failed.", work.Connection.Info.SystemId);
                result = ServerSubmitResult.Fail(SmppStatus.SystemError);
            }

            if (result == null)
            {
                result = ServerSubmitResult.Fail(SmppStatus.SystemError);
            }

            var response = new SmRespPdu(SmppCommand.SubmitSmResp)
            {
                SequenceNumber = work.Pdu.SequenceNumber,
                Status = result.Status,
                MessageId = result.MessageId ?? string.Empty
            };

            try
            {
                await work.Connection.Session.SendAsync(response, cancellationToken).ConfigureAwait(false);
                if (result.AfterResponseAsync != null && result.Status == SmppStatus.Ok)
                {
                    await result.AfterResponseAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not answer submit_sm for {SystemId}.", work.Connection.Info.SystemId);
            }
        }

        private async Task<BindDecision> InvokeAuthenticateAsync(BindContext context, CancellationToken cancellationToken)
        {
            var handler = AuthenticateAsync;
            if (handler == null)
            {
                return BindDecision.Reject(SmppStatus.BindFailed);
            }

            return await handler(context, cancellationToken).ConfigureAwait(false);
        }

        private void Register(ClientConnection connection)
        {
            ClientConnection previous = null;
            lock (_sync)
            {
                ClientConnection existing;
                if (_sessions.TryGetValue(connection.Info.SystemId, out existing))
                {
                    previous = existing;
                }

                _sessions[connection.Info.SystemId] = connection;
            }

            if (previous != null)
            {
                previous.Session.Close();
            }
        }

        private void Unregister(ClientConnection connection)
        {
            lock (_sync)
            {
                ClientConnection current;
                if (_sessions.TryGetValue(connection.Info.SystemId, out current) && ReferenceEquals(current, connection))
                {
                    _sessions.Remove(connection.Info.SystemId);
                }
            }
        }

        private static async Task WriteOnceAsync(Stream stream, SmppPdu pdu)
        {
            var bytes = PduCodec.Encode(pdu);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private void StopListener()
        {
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                }
            }
            catch (Exception)
            {
            }
        }

        private sealed class ClientConnection
        {
            public SmppSession Session { get; set; }

            public BoundSession Info { get; set; }

            public RateLimiter Rate { get; set; }
        }

        private sealed class SubmitWork
        {
            public ClientConnection Connection { get; set; }

            public SmPdu Pdu { get; set; }
        }
    }

    public static class IpAllowList
    {
        public static bool IsAllowed(string allowedIps, IPAddress remote)
        {
            if (string.IsNullOrWhiteSpace(allowedIps))
            {
                return true;
            }

            if (remote == null)
            {
                return false;
            }

            var normalized = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
            var parts = allowedIps.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                IPAddress parsed;
                if (!IPAddress.TryParse(parts[i].Trim(), out parsed))
                {
                    continue;
                }

                if (parsed.IsIPv4MappedToIPv6)
                {
                    parsed = parsed.MapToIPv4();
                }

                if (parsed.Equals(normalized))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
