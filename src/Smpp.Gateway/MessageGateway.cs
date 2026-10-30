using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Smpp.Protocol;
using Smpp.Storage;

namespace Smpp.Gateway
{
    public sealed class MessageGateway : IInboundHandler
    {
        private readonly IMessageStore _store;
        private readonly IConnectorRegistry _registry;
        private readonly IClientNotifier _notifier;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _receiptLock = new SemaphoreSlim(1, 1);

        public MessageGateway(IMessageStore store, IConnectorRegistry registry, IClientNotifier notifier, ILogger logger)
        {
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }

            if (registry == null)
            {
                throw new ArgumentNullException("registry");
            }

            _store = store;
            _registry = registry;
            _notifier = notifier;
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public async Task<SendOutcome> SendAsync(SendRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            var source = (request.Source ?? string.Empty).Trim();
            if (source.Length == 0 || source.Length > 20)
            {
                return SendOutcome.Reject(SmppStatus.InvalidSourceAddress, "Source address is invalid.");
            }

            if (request.Text != null && request.Text.Length > 5000)
            {
                return SendOutcome.Reject(SmppStatus.InvalidMessageLength, "Text is too long.");
            }

            var destination = Msisdn.Normalize(request.Destination, request.DefaultCountry);
            if (destination.Length < 8 || destination.Length > 20)
            {
                return SendOutcome.Reject(SmppStatus.InvalidDestination, "Destination address is invalid.");
            }

            var country = CountryPrefix.Resolve(destination);
            var routes = await _store.GetRoutesAsync(cancellationToken).ConfigureAwait(false);
            var providers = await _store.GetProvidersAsync(cancellationToken).ConfigureAwait(false);
            var decision = RoutePolicy.Decide(country, request.TrafficType, routes, providers, destination);
            if (!decision.Accepted)
            {
                return SendOutcome.Reject(decision.Status, decision.Reason);
            }

            var message = new SmsMessage
            {
                Id = Guid.NewGuid(),
                AccountId = request.AccountId,
                Direction = MessageDirection.Outbound,
                TrafficType = request.TrafficType,
                Source = source,
                Destination = destination,
                Text = request.Text ?? string.Empty,
                Country = country,
                Status = MessageStatus.Accepted,
                CreatedAt = DateTime.UtcNow
            };
            message.ClientMessageId = message.Id.ToString("N");
            await _store.InsertMessageAsync(message, cancellationToken).ConfigureAwait(false);

            string lastError = "No connector accepted the message.";
            uint lastStatus = SmppStatus.SubmitFailed;
            for (var i = 0; i < decision.Providers.Count; i++)
            {
                var provider = decision.Providers[i];
                var connector = _registry.Find(provider.Id);
                if (connector == null)
                {
                    lastError = "Connector for " + provider.Name + " is not loaded.";
                    continue;
                }

                var submit = new OutboundSubmit
                {
                    MessageId = message.Id,
                    Source = source,
                    Destination = destination,
                    Text = message.Text,
                    RequestDeliveryReceipt = true,
                    Segments = BuildSegments(connector, request, message.Text)
                };

                SubmitResult result;
                try
                {
                    result = await connector.SubmitAsync(submit, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Provider {Provider} threw while submitting {MessageId}.", provider.Name, message.ClientMessageId);
                    lastError = ex.Message;
                    lastStatus = SmppStatus.SystemError;
                    continue;
                }

                if (result == null || !result.Success)
                {
                    lastError = result == null ? lastError : result.Error;
                    lastStatus = result == null ? lastStatus : result.Status;
                    continue;
                }

                message.ProviderId = provider.Id;
                message.Status = MessageStatus.Submitted;
                message.Error = string.Empty;
                var parts = new List<SmsPart>();
                for (var p = 0; p < result.Parts.Count; p++)
                {
                    var part = result.Parts[p];
                    parts.Add(new SmsPart
                    {
                        MessageId = message.Id,
                        PartNumber = part.PartNumber,
                        PartCount = part.PartCount,
                        ProviderMessageId = part.ProviderMessageId,
                        Status = part.Success ? MessageStatus.Submitted : MessageStatus.Failed
                    });
                }

                await _store.SaveSubmissionAsync(message, parts, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Accepted {MessageId} via {Provider} to {Destination}.", message.ClientMessageId, provider.Name, destination);
                return SendOutcome.Ok(message.ClientMessageId);
            }

            message.Status = MessageStatus.Failed;
            message.Error = string.IsNullOrEmpty(lastError) ? "Submit failed." : lastError;
            await _store.UpdateMessageAsync(message, cancellationToken).ConfigureAwait(false);
            return SendOutcome.Reject(lastStatus == 0 ? SmppStatus.SubmitFailed : lastStatus, message.Error);
        }

        public async Task HandleMobileOriginatedAsync(InboundMessage inbound, CancellationToken cancellationToken)
        {
            if (inbound == null)
            {
                return;
            }

            var destination = Msisdn.Normalize(inbound.Destination, null);
            var source = Msisdn.Normalize(inbound.Source, null);
            var country = !string.IsNullOrEmpty(inbound.Country) ? inbound.Country : CountryPrefix.Resolve(source);
            var message = new SmsMessage
            {
                Id = Guid.NewGuid(),
                Direction = MessageDirection.Inbound,
                TrafficType = TrafficType.Transactional,
                Source = string.IsNullOrEmpty(source) ? inbound.Source ?? string.Empty : source,
                Destination = string.IsNullOrEmpty(destination) ? inbound.Destination ?? string.Empty : destination,
                Text = inbound.Text ?? string.Empty,
                Country = country ?? string.Empty,
                Status = MessageStatus.Delivered,
                ProviderId = inbound.ProviderId,
                TargetSystemId = inbound.TargetSystemId ?? string.Empty,
                CreatedAt = DateTime.UtcNow
            };
            message.ClientMessageId = message.Id.ToString("N");
            await _store.InsertMessageAsync(message, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Stored MO {MessageId} from {Source}.", message.ClientMessageId, message.Source);
            if (_notifier != null)
            {
                await _notifier.DeliverInboundAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task HandleDeliveryReceiptAsync(InboundReceipt receipt, CancellationToken cancellationToken)
        {
            if (receipt == null || string.IsNullOrEmpty(receipt.ProviderMessageId))
            {
                return;
            }

            SmsMessage notify = null;
            await _receiptLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var link = await _store.FindReceiptLinkAsync(receipt.ProviderId, receipt.ProviderMessageId, cancellationToken).ConfigureAwait(false);
                if (link == null)
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    link = await _store.FindReceiptLinkAsync(receipt.ProviderId, receipt.ProviderMessageId, cancellationToken).ConfigureAwait(false);
                }

                if (link == null)
                {
                    _logger.LogWarning("No message is linked to provider id {ProviderMessageId}.", receipt.ProviderMessageId);
                    return;
                }

                var status = MessageStatusMap.FromReceipt(receipt.Stat, receipt.MessageState);
                await _store.UpdatePartStatusAsync(link.PartId, status, cancellationToken).ConfigureAwait(false);
                var message = await _store.GetMessageAsync(link.MessageId, cancellationToken).ConfigureAwait(false);
                if (message == null)
                {
                    return;
                }

                var allTerminal = message.Parts.Count > 0;
                var allDelivered = message.Parts.Count > 0;
                MessageStatus aggregate = MessageStatus.Delivered;
                for (var i = 0; i < message.Parts.Count; i++)
                {
                    var partStatus = message.Parts[i].Status;
                    if (!MessageStatusMap.IsTerminal(partStatus))
                    {
                        allTerminal = false;
                    }

                    if (partStatus != MessageStatus.Delivered)
                    {
                        allDelivered = false;
                        if (MessageStatusMap.IsTerminal(partStatus))
                        {
                            aggregate = partStatus;
                        }
                    }
                }

                if (!allTerminal)
                {
                    message.Status = MessageStatus.Submitted;
                }
                else if (allDelivered)
                {
                    message.Status = MessageStatus.Delivered;
                }
                else
                {
                    message.Status = aggregate;
                }

                if (allTerminal && !message.ReceiptNotified)
                {
                    message.ReceiptNotified = true;
                    notify = message;
                }

                await _store.UpdateMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _receiptLock.Release();
            }

            if (notify != null && _notifier != null)
            {
                try
                {
                    await _notifier.DeliverReceiptAsync(notify, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not deliver the receipt for {MessageId}.", notify.ClientMessageId);
                }
            }
        }

        private static IList<SmsSegment> BuildSegments(IOutboundConnector connector, SendRequest request, string text)
        {
            if (request.PassthroughUserData != null)
            {
                return new[]
                {
                    SmsSegmenter.Passthrough(request.PassthroughUserData, request.PassthroughDataCoding, request.PassthroughEsmClass, text)
                };
            }

            if (!connector.SupportsSegmentation)
            {
                return new[]
                {
                    new SmsSegment
                    {
                        Text = text ?? string.Empty,
                        PartNumber = 1,
                        PartCount = 1
                    }
                };
            }

            return SmsSegmenter.Segment(text ?? string.Empty);
        }
    }
}
