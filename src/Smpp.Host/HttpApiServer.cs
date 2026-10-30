using System;
using System.IO;
using System.Net;
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
    public sealed class HttpApiServer
    {
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly HttpListener _listener = new HttpListener();
        private readonly MessageGateway _gateway;
        private readonly IMessageStore _store;
        private readonly SmppServer _server;
        private readonly Func<string> _linkSummary;
        private readonly string _webhookKey;
        private readonly ILogger _logger;

        public HttpApiServer(string[] prefixes, MessageGateway gateway, IMessageStore store, SmppServer server, Func<string> linkSummary, string webhookKey, ILogger logger)
        {
            for (var i = 0; i < prefixes.Length; i++)
            {
                _listener.Prefixes.Add(prefixes[i]);
            }

            _gateway = gateway;
            _store = store;
            _server = server;
            _linkSummary = linkSummary;
            _webhookKey = webhookKey ?? string.Empty;
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _listener.Start();
            _logger.LogInformation("HTTP API listening on {Prefixes}.", string.Join(", ", _listener.Prefixes));
            using (cancellationToken.Register(() =>
            {
                try
                {
                    _listener.Stop();
                }
                catch (ObjectDisposedException)
                {
                }
            }))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    var ignored = Task.Run(() => HandleAsync(context));
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url.AbsolutePath.TrimEnd('/');
                var method = context.Request.HttpMethod ?? string.Empty;
                if (method == "GET" && (path.Length == 0 || string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase)))
                {
                    var bound = _server == null ? 0 : _server.GetBoundSessions().Count;
                    await WriteAsync(context.Response, 200, new
                    {
                        status = "ok",
                        smppBoundClients = bound,
                        providers = _linkSummary == null ? string.Empty : _linkSummary()
                    }).ConfigureAwait(false);
                    return;
                }

                if (method == "POST" && string.Equals(path, "/api/messages", StringComparison.OrdinalIgnoreCase))
                {
                    await SendAsync(context).ConfigureAwait(false);
                    return;
                }

                if (method == "POST" && path.StartsWith("/webhooks/", StringComparison.OrdinalIgnoreCase))
                {
                    await WebhookAsync(context, path.Substring("/webhooks/".Length)).ConfigureAwait(false);
                    return;
                }

                await WriteAsync(context.Response, 404, new { status = "not-found" }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTP request failed.");
                try
                {
                    await WriteAsync(context.Response, 500, new { status = "error", error = "Request failed." }).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }

        private async Task SendAsync(HttpListenerContext context)
        {
            var systemId = context.Request.Headers["X-System-Id"] ?? string.Empty;
            var password = context.Request.Headers["X-Password"] ?? string.Empty;
            var account = await _store.FindAccountBySystemIdAsync(systemId, CancellationToken.None).ConfigureAwait(false);
            if (account == null || !account.Enabled || !SecureCompare.FixedEquals(account.Password, password))
            {
                await WriteAsync(context.Response, 401, new { status = "unauthorized" }).ConfigureAwait(false);
                return;
            }

            SendBody body;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
            {
                var json = await reader.ReadToEndAsync().ConfigureAwait(false);
                body = JsonConvert.DeserializeObject<SendBody>(json) ?? new SendBody();
            }

            var outcome = await _gateway.SendAsync(new SendRequest
            {
                AccountId = account.Id,
                TrafficType = account.TrafficType,
                Source = body.From,
                Destination = body.To,
                Text = body.Text,
                DefaultCountry = account.DefaultCountry
            }, CancellationToken.None).ConfigureAwait(false);

            if (outcome.Accepted)
            {
                await WriteAsync(context.Response, 200, new { id = outcome.MessageId, status = "accepted" }).ConfigureAwait(false);
                return;
            }

            var code = outcome.Status == SmppStatus.Throttled || outcome.Status == SmppStatus.MessageQueueFull
                ? 429
                : outcome.Status == SmppStatus.SystemError || outcome.Status == SmppStatus.SubmitFailed
                    ? 502
                    : 400;
            await WriteAsync(context.Response, code, new { status = "rejected", error = outcome.Error, smppStatus = outcome.Status }).ConfigureAwait(false);
        }

        private async Task WebhookAsync(HttpListenerContext context, string webhookPath)
        {
            if (!string.IsNullOrEmpty(_webhookKey))
            {
                var provided = context.Request.Headers["X-Webhook-Key"] ?? string.Empty;
                if (!SecureCompare.FixedEquals(_webhookKey, provided))
                {
                    await WriteAsync(context.Response, 401, new { status = "unauthorized" }).ConfigureAwait(false);
                    return;
                }
            }

            string json;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                json = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var providers = await _store.GetProvidersAsync(CancellationToken.None).ConfigureAwait(false);
            Provider matched = null;
            Provider onlyHttp = null;
            var httpCount = 0;
            for (var i = 0; i < providers.Count; i++)
            {
                var candidate = providers[i];
                if (candidate.Kind != ProviderKind.SerbiaHttp && candidate.Kind != ProviderKind.OperatorHttp)
                {
                    continue;
                }

                httpCount++;
                onlyHttp = candidate;
                if (string.Equals(candidate.WebhookPath, webhookPath, StringComparison.OrdinalIgnoreCase))
                {
                    matched = candidate;
                }
            }

            if (matched == null && httpCount == 1 && string.Equals(webhookPath, "serbia", StringComparison.OrdinalIgnoreCase))
            {
                matched = onlyHttp;
            }

            if (matched == null)
            {
                await WriteAsync(context.Response, 404, new { status = "unknown-provider" }).ConfigureAwait(false);
                return;
            }

            InboundMessage mobile;
            InboundReceipt receipt;
            if (!Smpp.Connectors.SerbiaWebhook.TryParse(json, matched.Id, matched.InboundSystemId ?? string.Empty, out mobile, out receipt))
            {
                await WriteAsync(context.Response, 400, new { status = "invalid" }).ConfigureAwait(false);
                return;
            }

            if (receipt != null)
            {
                await _gateway.HandleDeliveryReceiptAsync(receipt, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _gateway.HandleMobileOriginatedAsync(mobile, CancellationToken.None).ConfigureAwait(false);
            }

            await WriteAsync(context.Response, 200, new { status = "accepted" }).ConfigureAwait(false);
        }

        private static async Task WriteAsync(HttpListenerResponse response, int status, object body)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body, Json));
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            response.OutputStream.Close();
        }

        private sealed class SendBody
        {
            public SendBody()
            {
                From = string.Empty;
                To = string.Empty;
                Text = string.Empty;
            }

            public string From { get; set; }

            public string To { get; set; }

            public string Text { get; set; }
        }
    }
}
