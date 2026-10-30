using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Smpp.Protocol;
using Smpp.Server;

namespace Smpp.Simulator
{
    public sealed class SimulatorSmsc : IDisposable
    {
        private readonly SmppServer _server = new SmppServer(NullLogger.Instance);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<SmPdu> _received = new List<SmPdu>();
        private int _ids;

        public SimulatorSmsc()
        {
            SystemId = "demo";
            Password = "demo";
            SendDeliveryReceipts = true;
        }

        public int ListenPort { get; set; }

        public int Port { get; private set; }

        public string SystemId { get; set; }

        public string Password { get; set; }

        public bool SendDeliveryReceipts { get; set; }

        public IList<SmPdu> Received
        {
            get
            {
                lock (_received)
                {
                    return _received.ToArray();
                }
            }
        }

        public async Task StartAsync()
        {
            _server.AuthenticateAsync = (context, cancellationToken) =>
            {
                if (!string.Equals(context.SystemId, SystemId, StringComparison.Ordinal)
                    || !SecureCompare.FixedEquals(Password ?? string.Empty, context.Password ?? string.Empty))
                {
                    return Task.FromResult(BindDecision.Reject(SmppStatus.InvalidPassword));
                }

                return Task.FromResult(BindDecision.Allow(Guid.NewGuid(), 100, null));
            };

            _server.SubmitAsync = (session, pdu, cancellationToken) =>
            {
                lock (_received)
                {
                    _received.Add(pdu);
                }

                var id = System.Threading.Interlocked.Increment(ref _ids).ToString();
                var result = ServerSubmitResult.Ok(id);
                if (SendDeliveryReceipts)
                {
                    result.AfterResponseAsync = token => _server.DeliverAsync(session.SystemId, BuildReceipt(id, pdu), token);
                }

                return Task.FromResult(result);
            };

            var started = new TaskCompletionSource<bool>();
            var ignored = Task.Run(() => _server.RunAsync(new SmppServerOptions
            {
                BindAddress = IPAddress.Loopback,
                Port = ListenPort,
                SystemId = "simulator"
            }, _cts.Token, started));
            Observe(ignored);
            await started.Task.ConfigureAwait(false);
            Port = _server.BoundPort;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _server.Dispose();
            _cts.Dispose();
        }

        private static SmPdu BuildReceipt(string id, SmPdu submit)
        {
            var text = DeliveryReceipts.Format(id, 1, 1, DateTime.UtcNow, DateTime.UtcNow, "DELIVRD", "000", string.Empty);
            return new SmPdu(SmppCommand.DeliverSm)
            {
                SourceTon = submit.DestTon,
                SourceNpi = submit.DestNpi,
                SourceAddress = submit.DestinationAddress,
                DestTon = submit.SourceTon,
                DestNpi = submit.SourceNpi,
                DestinationAddress = submit.SourceAddress,
                EsmClass = SmppEsm.DeliveryReceipt,
                DataCoding = 0x00,
                ShortMessage = Encoding.ASCII.GetBytes(text)
            };
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(completed =>
            {
                var ignored = completed.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
