using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Smpp.Gateway;
using Smpp.Protocol;
using Smpp.Storage;

namespace Smpp.Connectors
{
    public class HttpProviderConnector : IOutboundConnector
    {
        private readonly Provider _provider;
        private readonly HttpClient _http;
        private readonly ILogger _logger;
        private readonly RateLimiter _rate;

        protected HttpProviderConnector(Provider provider, HttpClient httpClient, ILogger logger)
        {
            if (provider == null)
            {
                throw new ArgumentNullException("provider");
            }

            _provider = provider;
            _http = httpClient ?? new HttpClient();
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            _rate = new RateLimiter(provider.ThroughputPerSecond);
        }

        public Guid ProviderId
        {
            get { return _provider.Id; }
        }

        public bool SupportsSegmentation
        {
            get { return false; }
        }

        public async Task<SubmitResult> SubmitAsync(OutboundSubmit submit, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_provider.HttpUrl))
            {
                return SubmitResult.Fail(SmppStatus.SystemError, "HTTP provider URL is empty.");
            }

            await _rate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var payload = new JObject
            {
                ["from"] = submit.Source,
                ["to"] = submit.Destination,
                ["text"] = submit.Text,
                ["id"] = submit.MessageId.ToString("N")
            };

            var request = new HttpRequestMessage(HttpMethod.Post, _provider.HttpUrl)
            {
                Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(_provider.HttpAuthHeader))
            {
                request.Headers.TryAddWithoutValidation("Authorization", _provider.HttpAuthHeader);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HTTP provider {Provider} request failed.", _provider.Name);
                return SubmitResult.Fail(SmppStatus.SystemError, ex.Message);
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return SubmitResult.Fail(SmppStatus.SubmitFailed, "HTTP provider returned " + (int)response.StatusCode + ".");
            }

            var providerId = submit.MessageId.ToString("N");
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var json = JObject.Parse(body);
                    var parsed = (string)json["messageId"] ?? (string)json["id"];
                    if (!string.IsNullOrEmpty(parsed))
                    {
                        providerId = parsed;
                    }
                }
                catch (Exception)
                {
                    providerId = body.Trim();
                }
            }

            return SubmitResult.Ok(new List<PartSubmitResult>
            {
                new PartSubmitResult
                {
                    PartNumber = 1,
                    PartCount = 1,
                    Success = true,
                    ProviderMessageId = providerId,
                    Text = submit.Text
                }
            });
        }
    }

    public sealed class SerbiaHttpConnector : HttpProviderConnector
    {
        public SerbiaHttpConnector(Provider provider, HttpClient httpClient, ILogger logger)
            : base(provider, httpClient, logger)
        {
        }
    }

    public sealed class HttpBulkConnector : HttpProviderConnector
    {
        public HttpBulkConnector(Provider provider, HttpClient httpClient, ILogger logger)
            : base(provider, httpClient, logger)
        {
        }
    }

    public static class SerbiaWebhook
    {
        public static bool TryParse(string json, Guid providerId, string targetSystemId, out InboundMessage mobileOriginated, out InboundReceipt receipt)
        {
            mobileOriginated = null;
            receipt = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var obj = JObject.Parse(json);
            var status = (string)obj["status"] ?? (string)obj["stat"];
            var messageId = (string)obj["messageId"] ?? (string)obj["id"] ?? string.Empty;
            if (!string.IsNullOrEmpty(status))
            {
                receipt = new InboundReceipt
                {
                    ProviderId = providerId,
                    ProviderMessageId = messageId,
                    Stat = status,
                    ErrorCode = (string)obj["error"] ?? string.Empty
                };
                return true;
            }

            mobileOriginated = new InboundMessage
            {
                ProviderId = providerId,
                ProviderMessageId = messageId,
                Source = (string)obj["from"] ?? string.Empty,
                Destination = (string)obj["to"] ?? string.Empty,
                Text = (string)obj["text"] ?? string.Empty,
                Country = Countries.Serbia,
                TargetSystemId = targetSystemId ?? string.Empty
            };
            return true;
        }
    }
}
