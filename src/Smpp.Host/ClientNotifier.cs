using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Smpp.Gateway;
using Smpp.Protocol;
using Smpp.Server;
using Smpp.Storage;

namespace Smpp.Host
{
    public sealed class ClientNotifier : IClientNotifier
    {
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly SmppServer _server;
        private readonly IMessageStore _store;
        private readonly HttpClient _http;
        private readonly ILogger _logger;

        public ClientNotifier(SmppServer server, IMessageStore store, HttpClient http, ILogger logger)
        {
            _server = server;
            _store = store;
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public async Task DeliverReceiptAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            if (message == null || !message.AccountId.HasValue)
            {
                return;
            }

            var account = await _store.FindAccountAsync(message.AccountId.Value, cancellationToken).ConfigureAwait(false);
            if (account == null)
            {
                return;
            }

            BoundSession bound;
            if (_server != null && _server.TryGetBound(account.SystemId, out bound) && bound.Role != SmppBindRole.Transmitter)
            {
                try
                {
                    await _server.DeliverAsync(account.SystemId, BuildReceipt(message), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not push DLR to SMPP client {SystemId}.", account.SystemId);
                }
            }

            await PostAsync(account.WebhookUrl, new
            {
                kind = "dlr",
                id = message.ClientMessageId,
                from = message.Source,
                to = message.Destination,
                status = message.Status.ToString()
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task DeliverInboundAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            var accounts = await _store.GetEnabledAccountsAsync(cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < accounts.Count; i++)
            {
                var account = accounts[i];
                if (!string.IsNullOrEmpty(message.TargetSystemId) && !string.Equals(account.SystemId, message.TargetSystemId, StringComparison.Ordinal))
                {
                    continue;
                }

                BoundSession bound;
                if (_server != null && _server.TryGetBound(account.SystemId, out bound) && bound.Role != SmppBindRole.Transmitter)
                {
                    var parts = SmsSegmenter.Segment(message.Text ?? string.Empty);
                    for (var p = 0; p < parts.Count; p++)
                    {
                        var deliver = new SmPdu(SmppCommand.DeliverSm)
                        {
                            SourceAddress = message.Source,
                            DestinationAddress = message.Destination,
                            EsmClass = parts[p].EsmClass,
                            DataCoding = parts[p].DataCoding
                        };
                        byte sourceTon;
                        byte sourceNpi;
                        byte destTon;
                        byte destNpi;
                        SmsText.ApplyAddress(message.Source, null, null, out sourceTon, out sourceNpi);
                        SmsText.ApplyAddress(message.Destination, null, null, out destTon, out destNpi);
                        deliver.SourceTon = sourceTon;
                        deliver.SourceNpi = sourceNpi;
                        deliver.DestTon = destTon;
                        deliver.DestNpi = destNpi;
                        PduCodec.ApplyUserData(deliver, parts[p].UserData);
                        try
                        {
                            await _server.DeliverAsync(account.SystemId, deliver, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Could not push MO to SMPP client {SystemId}.", account.SystemId);
                        }
                    }
                }

                await PostAsync(account.WebhookUrl, new
                {
                    kind = "mo",
                    id = message.ClientMessageId,
                    from = message.Source,
                    to = message.Destination,
                    text = message.Text
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task PostAsync(string url, object body, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                var content = new StringContent(JsonConvert.SerializeObject(body, Json), Encoding.UTF8, "application/json");
                await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Webhook {Url} failed.", url);
            }
        }

        private static SmPdu BuildReceipt(SmsMessage message)
        {
            var submitted = message.Parts == null ? 1 : message.Parts.Count;
            if (submitted == 0)
            {
                submitted = 1;
            }

            var delivered = 0;
            if (message.Parts != null)
            {
                for (var i = 0; i < message.Parts.Count; i++)
                {
                    if (message.Parts[i].Status == MessageStatus.Delivered)
                    {
                        delivered++;
                    }
                }
            }

            var stat = MessageStatusMap.ToStat(message.Status);
            var text = DeliveryReceipts.Format(message.ClientMessageId, submitted, delivered, message.CreatedAt, DateTime.UtcNow, stat, message.Status == MessageStatus.Delivered ? "000" : "001", message.Text);
            var pdu = new SmPdu(SmppCommand.DeliverSm)
            {
                SourceAddress = message.Destination,
                DestinationAddress = message.Source,
                EsmClass = SmppEsm.DeliveryReceipt,
                DataCoding = 0x00,
                ShortMessage = Encoding.ASCII.GetBytes(text)
            };
            byte sourceTon;
            byte sourceNpi;
            byte destTon;
            byte destNpi;
            SmsText.ApplyAddress(pdu.SourceAddress, null, null, out sourceTon, out sourceNpi);
            SmsText.ApplyAddress(pdu.DestinationAddress, null, null, out destTon, out destNpi);
            pdu.SourceTon = sourceTon;
            pdu.SourceNpi = sourceNpi;
            pdu.DestTon = destTon;
            pdu.DestNpi = destNpi;
            pdu.Tlvs.Add(Tlv.CString(TlvTag.ReceiptedMessageId, message.ClientMessageId));
            pdu.Tlvs.Add(Tlv.Byte(TlvTag.MessageState, MessageStatusMap.ToMessageState(message.Status)));
            return pdu;
        }
    }
}
