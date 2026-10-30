using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Smpp.Protocol;

namespace Smpp.Client
{
    public sealed class SmppClientOptions
    {
        public SmppClientOptions()
        {
            Host = "127.0.0.1";
            Port = 2775;
            SystemId = string.Empty;
            Password = string.Empty;
            SystemType = string.Empty;
            Role = SmppBindRole.Transceiver;
            WindowSize = 10;
            EnquireLinkInterval = TimeSpan.FromSeconds(30);
            ResponseTimeout = TimeSpan.FromSeconds(10);
        }

        public string Host { get; set; }

        public int Port { get; set; }

        public string SystemId { get; set; }

        public string Password { get; set; }

        public string SystemType { get; set; }

        public SmppBindRole Role { get; set; }

        public bool UseTls { get; set; }

        public bool AllowInvalidCertificate { get; set; }

        public int WindowSize { get; set; }

        public TimeSpan EnquireLinkInterval { get; set; }

        public TimeSpan ResponseTimeout { get; set; }
    }

    public sealed class SmppClient : IDisposable
    {
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private SmppSession _session;
        private int _bound;

        public SmppClient(ILogger logger)
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public bool IsBound
        {
            get { return Volatile.Read(ref _bound) == 1; }
        }

        public Func<SmPdu, CancellationToken, Task> DeliverReceived { get; set; }

        public Task Completion
        {
            get { return _session == null ? Task.FromResult(true) : _session.Completion; }
        }

        public async Task ConnectAndBindAsync(SmppClientOptions options, CancellationToken cancellationToken)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            var stream = await SmppTransport.ConnectAsync(options.Host, options.Port, options.UseTls, options.AllowInvalidCertificate, cancellationToken).ConfigureAwait(false);
            _session = new SmppSession(stream, new SmppSessionOptions
            {
                WindowSize = options.WindowSize,
                ResponseTimeout = options.ResponseTimeout,
                EnquireLinkInterval = options.EnquireLinkInterval
            }, _logger);
            _session.OnRequest = OnRequestAsync;
            var receive = _session.RunReceiveLoopAsync(_cts.Token);
            Observe(receive);

            var bind = new BindPdu(ToCommand(options.Role))
            {
                SystemId = options.SystemId ?? string.Empty,
                Password = options.Password ?? string.Empty,
                SystemType = options.SystemType ?? string.Empty,
                InterfaceVersion = 0x34
            };

            SmppPdu response;
            try
            {
                response = await _session.RequestAsync(bind, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _session.Close();
                throw;
            }

            if (response.Status != SmppStatus.Ok)
            {
                _session.Close();
                throw new InvalidOperationException("SMPP bind failed with status 0x" + response.Status.ToString("X8") + ".");
            }

            Volatile.Write(ref _bound, 1);
            _session.StartEnquireLink();
            _logger.LogInformation("SMPP client bound to {Host}:{Port} as {SystemId}.", options.Host, options.Port, options.SystemId);
        }

        public Task<SmppPdu> SubmitAsync(SmPdu submit, CancellationToken cancellationToken)
        {
            if (_session == null || !IsBound)
            {
                throw new InvalidOperationException("SMPP client is not bound.");
            }

            submit.Command = SmppCommand.SubmitSm;
            return _session.RequestAsync(submit, cancellationToken);
        }

        public void Dispose()
        {
            Volatile.Write(ref _bound, 0);
            if (_session != null)
            {
                _session.Dispose();
            }

            _cts.Cancel();
            _cts.Dispose();
        }

        private async Task<SmppPdu> OnRequestAsync(SmppPdu pdu, CancellationToken cancellationToken)
        {
            var deliver = pdu as SmPdu;
            if (deliver != null && deliver.Command == SmppCommand.DeliverSm)
            {
                var handler = DeliverReceived;
                if (handler != null)
                {
                    await handler(deliver, cancellationToken).ConfigureAwait(false);
                }

                return new SmRespPdu(SmppCommand.DeliverSmResp);
            }

            if (pdu.Command == SmppCommand.Unbind)
            {
                Volatile.Write(ref _bound, 0);
                return new EmptyBodyPdu(SmppCommand.UnbindResp);
            }

            return new EmptyBodyPdu(SmppCommand.GenericNack) { Status = SmppStatus.InvalidCommandId };
        }

        private static SmppCommand ToCommand(SmppBindRole role)
        {
            if (role == SmppBindRole.Transmitter)
            {
                return SmppCommand.BindTransmitter;
            }

            if (role == SmppBindRole.Receiver)
            {
                return SmppCommand.BindReceiver;
            }

            return SmppCommand.BindTransceiver;
        }

        private void Observe(Task task)
        {
            task.ContinueWith(completed =>
            {
                Volatile.Write(ref _bound, 0);
                if (completed.IsFaulted && completed.Exception != null)
                {
                    _logger.LogDebug(completed.Exception, "SMPP client receive loop ended.");
                }
            }, TaskScheduler.Default);
        }
    }
}
