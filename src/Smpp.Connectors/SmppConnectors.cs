using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Smpp.Client;
using Smpp.Gateway;
using Smpp.Protocol;
using Smpp.Storage;

namespace Smpp.Connectors
{
    public sealed class SmppLinkManager
    {
        private readonly ILogger _logger;
        private readonly Dictionary<Guid, SmppLink> _links = new Dictionary<Guid, SmppLink>();
        private readonly List<Task> _tasks = new List<Task>();

        public SmppLinkManager(ILogger logger)
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public IInboundHandler Handler { get; set; }

        public void Track(Provider provider)
        {
            if (provider == null || !provider.Enabled)
            {
                return;
            }

            if (provider.Kind != ProviderKind.OperatorSmpp && provider.Kind != ProviderKind.BulkSmpp)
            {
                return;
            }

            _links[provider.Id] = new SmppLink(provider, _logger);
        }

        public void Start(CancellationToken cancellationToken)
        {
            foreach (var pair in _links)
            {
                pair.Value.Handler = Handler;
                var link = pair.Value;
                _tasks.Add(Task.Run(() => link.RunAsync(cancellationToken)));
            }
        }

        public IList<SmppLink> ActiveLinks
        {
            get
            {
                var list = new List<SmppLink>(_links.Count);
                foreach (var pair in _links)
                {
                    list.Add(pair.Value);
                }

                return list;
            }
        }

        public Task<SubmitResult> SubmitAsync(Guid providerId, OutboundSubmit submit, CancellationToken cancellationToken)
        {
            SmppLink link;
            if (!_links.TryGetValue(providerId, out link))
            {
                return Task.FromResult(SubmitResult.Fail(SmppStatus.SystemError, "SMPP link is not configured."));
            }

            return link.SubmitAsync(submit, cancellationToken);
        }
    }

    public class SmppProviderConnector : IOutboundConnector
    {
        private readonly SmppLinkManager _links;
        private readonly Provider _provider;

        protected SmppProviderConnector(SmppLinkManager links, Provider provider)
        {
            _links = links;
            _provider = provider;
        }

        public Guid ProviderId
        {
            get { return _provider.Id; }
        }

        public bool SupportsSegmentation
        {
            get { return true; }
        }

        public Task<SubmitResult> SubmitAsync(OutboundSubmit submit, CancellationToken cancellationToken)
        {
            return _links.SubmitAsync(_provider.Id, submit, cancellationToken);
        }
    }

    public sealed class OperatorSmppConnector : SmppProviderConnector
    {
        public OperatorSmppConnector(SmppLinkManager links, Provider provider)
            : base(links, provider)
        {
        }
    }

    public sealed class BulkSmppConnector : SmppProviderConnector
    {
        public BulkSmppConnector(SmppLinkManager links, Provider provider)
            : base(links, provider)
        {
        }
    }

    public sealed class SmppLink
    {
        private readonly Provider _provider;
        private readonly ILogger _logger;
        private readonly RateLimiter _rate;
        private readonly object _sync = new object();
        private SmppClient _client;

        public SmppLink(Provider provider, ILogger logger)
        {
            _provider = provider;
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            _rate = new RateLimiter(provider.ThroughputPerSecond);
        }

        public IInboundHandler Handler { get; set; }

        public bool IsBound
        {
            get
            {
                lock (_sync)
                {
                    return _client != null && _client.IsBound;
                }
            }
        }

        public string Name
        {
            get { return _provider.Name; }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var delay = TimeSpan.FromSeconds(1);
            while (!cancellationToken.IsCancellationRequested)
            {
                SmppClient client = null;
                try
                {
                    client = new SmppClient(_logger);
                    client.DeliverReceived = OnDeliverAsync;
                    await client.ConnectAndBindAsync(new SmppClientOptions
                    {
                        Host = _provider.Host,
                        Port = _provider.Port ?? 2775,
                        SystemId = _provider.SystemId,
                        Password = _provider.Password,
                        SystemType = _provider.SystemType,
                        Role = SmppBindRole.Transceiver,
                        UseTls = _provider.UseTls,
                        AllowInvalidCertificate = _provider.AllowInvalidCertificate,
                        WindowSize = _provider.WindowSize <= 0 ? 10 : _provider.WindowSize
                    }, cancellationToken).ConfigureAwait(false);
                    lock (_sync)
                    {
                        _client = client;
                    }

                    delay = TimeSpan.FromSeconds(1);
                    await client.Completion.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SMPP link {Provider} dropped.", _provider.Name);
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_client, client))
                        {
                            _client = null;
                        }
                    }

                    if (client != null)
                    {
                        client.Dispose();
                    }
                }

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var seconds = delay.TotalSeconds * 2;
                if (seconds > 30)
                {
                    seconds = 30;
                }

                delay = TimeSpan.FromSeconds(seconds);
            }
        }

        public async Task<SubmitResult> SubmitAsync(OutboundSubmit submit, CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;
            SmppClient client = null;
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
            {
                lock (_sync)
                {
                    client = _client;
                }

                if (client != null && client.IsBound)
                {
                    break;
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                client = null;
            }

            if (client == null || !client.IsBound)
            {
                return SubmitResult.Fail(SmppStatus.SystemError, "SMPP session is not bound.");
            }

            await _rate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var parts = new List<PartSubmitResult>();
            var segments = submit.Segments ?? new List<SmsSegment>();
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                byte sourceTon;
                byte sourceNpi;
                byte destTon;
                byte destNpi;
                SmsText.ApplyAddress(submit.Source, _provider.SourceTon, _provider.SourceNpi, out sourceTon, out sourceNpi);
                SmsText.ApplyAddress(submit.Destination, _provider.DestTon, _provider.DestNpi, out destTon, out destNpi);
                var pdu = new SmPdu(SmppCommand.SubmitSm)
                {
                    SourceTon = sourceTon,
                    SourceNpi = sourceNpi,
                    SourceAddress = submit.Source,
                    DestTon = destTon,
                    DestNpi = destNpi,
                    DestinationAddress = submit.Destination,
                    EsmClass = segment.EsmClass,
                    RegisteredDelivery = submit.RequestDeliveryReceipt ? (byte)1 : (byte)0,
                    DataCoding = segment.DataCoding
                };
                PduCodec.ApplyUserData(pdu, segment.UserData ?? new byte[0]);
                SmppPdu response;
                try
                {
                    response = await client.SubmitAsync(pdu, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    parts.Add(new PartSubmitResult
                    {
                        PartNumber = segment.PartNumber,
                        PartCount = segment.PartCount,
                        Success = false,
                        Error = ex.Message,
                        Text = segment.Text
                    });
                    if (AnyAccepted(parts))
                    {
                        return SubmitResult.Ok(parts);
                    }

                    return SubmitResult.Fail(SmppStatus.SystemError, ex.Message);
                }

                var smResp = response as SmRespPdu;
                if (response.Status != SmppStatus.Ok)
                {
                    parts.Add(new PartSubmitResult
                    {
                        PartNumber = segment.PartNumber,
                        PartCount = segment.PartCount,
                        Success = false,
                        Error = "Provider rejected the part.",
                        Text = segment.Text
                    });
                    if (AnyAccepted(parts))
                    {
                        return SubmitResult.Ok(parts);
                    }

                    return SubmitResult.Fail(response.Status, "Provider rejected part " + segment.PartNumber + ".");
                }

                parts.Add(new PartSubmitResult
                {
                    PartNumber = segment.PartNumber,
                    PartCount = segment.PartCount,
                    Success = true,
                    ProviderMessageId = smResp == null ? string.Empty : smResp.MessageId,
                    Text = segment.Text
                });
            }

            if (parts.Count == 0)
            {
                return SubmitResult.Fail(SmppStatus.SubmitFailed, "Nothing was submitted.");
            }

            return SubmitResult.Ok(parts);
        }

        private static bool AnyAccepted(IList<PartSubmitResult> parts)
        {
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i].Success)
                {
                    return true;
                }
            }

            return false;
        }

        private async Task OnDeliverAsync(SmPdu pdu, CancellationToken cancellationToken)
        {
            var handler = Handler;
            if (handler == null)
            {
                return;
            }

            ParsedReceipt receipt;
            if (DeliveryReceipts.TryParse(pdu, out receipt))
            {
                await handler.HandleDeliveryReceiptAsync(new InboundReceipt
                {
                    ProviderId = _provider.Id,
                    ProviderMessageId = receipt.MessageId,
                    Stat = receipt.Stat,
                    MessageState = receipt.MessageState,
                    ErrorCode = receipt.ErrorCode
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            await handler.HandleMobileOriginatedAsync(new InboundMessage
            {
                ProviderId = _provider.Id,
                Source = pdu.SourceAddress,
                Destination = pdu.DestinationAddress,
                Text = SmsText.Decode(pdu.GetUserData(), pdu.DataCoding, pdu.EsmClass),
                Country = _provider.Country,
                TargetSystemId = _provider.InboundSystemId
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
