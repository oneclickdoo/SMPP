using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Smpp.Connectors;
using Smpp.Gateway;
using Smpp.Protocol;
using Smpp.Server;
using Smpp.Storage;

namespace Smpp.Host
{
    public sealed class GatewayHost
    {
        public async Task<int> RunAsync()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var configuration = new ConfigurationBuilder()
                .SetBasePath(baseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .Build();

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });
            var logger = loggerFactory.CreateLogger("Smpp.Host");
            var store = new SqlMessageStore(SeedLoader.ConnectionString(configuration), loggerFactory.CreateLogger("Smpp.Storage"));
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await store.SeedIfEmptyAsync(SeedLoader.Load(configuration), CancellationToken.None).ConfigureAwait(false);

            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var links = new SmppLinkManager(loggerFactory.CreateLogger("Smpp.Connectors"));
            var registry = new ConnectorRegistry(links, http, loggerFactory.CreateLogger("Smpp.Connectors"));
            var providers = await store.GetProvidersAsync(CancellationToken.None).ConfigureAwait(false);
            registry.Load(providers);

            var server = new SmppServer(loggerFactory.CreateLogger("Smpp.Server"));
            var notifier = new ClientNotifier(server, store, http, logger);
            var gateway = new MessageGateway(store, registry, notifier, loggerFactory.CreateLogger("Smpp.Gateway"));
            WireServer(server, store, gateway);

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, args) =>
            {
                args.Cancel = true;
                cts.Cancel();
            };

            registry.Start(gateway, cts.Token);
            var serverEnabled = !string.Equals(configuration["SmppServer:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
            var serverOptions = BuildServerOptions(configuration);
            var started = new TaskCompletionSource<bool>();
            Task serverTask;
            if (serverEnabled)
            {
                serverTask = Task.Run(() => server.RunAsync(serverOptions, cts.Token, started));
                var ignored = serverTask.ContinueWith(
                    task =>
                    {
                        if (task.IsFaulted && task.Exception != null)
                        {
                            started.TrySetException(task.Exception.InnerExceptions);
                        }
                    },
                    TaskScheduler.Default);
            }
            else
            {
                serverTask = Task.CompletedTask;
                started.TrySetResult(true);
                logger.LogInformation("SMPP server is disabled.");
            }
            var httpServer = new HttpApiServer(
                SeedLoader.HttpPrefixes(configuration),
                gateway,
                store,
                server,
                () => DescribeLinks(links),
                SeedLoader.WebhookKey(configuration),
                logger);
            var httpTask = httpServer.RunAsync(cts.Token);
            var stopped = new TaskCompletionSource<bool>();
            using (cts.Token.Register(() => stopped.TrySetResult(true)))
            {
                await started.Task.ConfigureAwait(false);
                logger.LogInformation("SMPP gateway is running. Press Ctrl+C to stop.");
                await Task.WhenAny(serverTask, httpTask, stopped.Task).ConfigureAwait(false);
            }

            cts.Cancel();
            server.Dispose();
            http.Dispose();
            loggerFactory.Dispose();
            return 0;
        }

        private static void WireServer(SmppServer server, IMessageStore store, MessageGateway gateway)
        {
            server.AuthenticateAsync = async (context, cancellationToken) =>
            {
                var account = await store.FindAccountBySystemIdAsync(context.SystemId, cancellationToken).ConfigureAwait(false);
                if (account == null || !account.Enabled)
                {
                    return BindDecision.Reject(SmppStatus.InvalidSystemId);
                }

                if (!SecureCompare.FixedEquals(account.Password, context.Password))
                {
                    return BindDecision.Reject(SmppStatus.InvalidPassword);
                }

                if (!IpAllowList.IsAllowed(account.AllowedIps, context.RemoteAddress))
                {
                    return BindDecision.Reject(SmppStatus.BindFailed);
                }

                return BindDecision.Allow(account.Id, account.ThroughputPerSecond, new AccountBindState
                {
                    TrafficType = account.TrafficType,
                    DefaultCountry = account.DefaultCountry
                });
            };

            server.SubmitAsync = async (session, pdu, cancellationToken) =>
            {
                var state = session.State as AccountBindState ?? new AccountBindState();
                var text = SmsText.Decode(pdu.GetUserData(), pdu.DataCoding, pdu.EsmClass);
                var request = new SendRequest
                {
                    AccountId = session.AccountId,
                    TrafficType = state.TrafficType,
                    Source = pdu.SourceAddress,
                    Destination = pdu.DestinationAddress,
                    Text = text,
                    DefaultCountry = state.DefaultCountry
                };
                if (SmppEsm.HasUserDataHeader(pdu.EsmClass))
                {
                    request.PassthroughUserData = pdu.GetUserData();
                    request.PassthroughDataCoding = pdu.DataCoding;
                    request.PassthroughEsmClass = pdu.EsmClass;
                }

                var outcome = await gateway.SendAsync(request, cancellationToken).ConfigureAwait(false);
                return outcome.Accepted ? ServerSubmitResult.Ok(outcome.MessageId) : ServerSubmitResult.Fail(outcome.Status);
            };
        }

        private static SmppServerOptions BuildServerOptions(IConfiguration configuration)
        {
            var port = 2775;
            int.TryParse(configuration["SmppServer:Port"], out port);
            if (port <= 0)
            {
                port = 2775;
            }

            var options = new SmppServerOptions
            {
                BindAddress = SeedLoader.BindAddress(configuration),
                Port = port,
                SystemId = string.IsNullOrWhiteSpace(configuration["SmppServer:SystemId"]) ? "smpp-gw" : configuration["SmppServer:SystemId"]
            };

            var useTls = false;
            bool.TryParse(configuration["SmppServer:UseTls"], out useTls);
            options.UseTls = useTls;
            var certificatePath = configuration["SmppServer:CertificatePath"];
            if (useTls)
            {
                if (string.IsNullOrWhiteSpace(certificatePath) || !File.Exists(certificatePath))
                {
                    throw new InvalidOperationException("SmppServer:CertificatePath is required when TLS is enabled.");
                }

                options.Certificate = new X509Certificate2(certificatePath, configuration["SmppServer:CertificatePassword"] ?? string.Empty);
            }

            return options;
        }

        private static string DescribeLinks(SmppLinkManager links)
        {
            var builder = new StringBuilder();
            var active = links.ActiveLinks;
            for (var i = 0; i < active.Count; i++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(active[i].Name);
                builder.Append(active[i].IsBound ? "=bound" : "=down");
            }

            return builder.ToString();
        }

        private sealed class AccountBindState
        {
            public AccountBindState()
            {
                DefaultCountry = string.Empty;
            }

            public TrafficType TrafficType { get; set; }

            public string DefaultCountry { get; set; }
        }
    }
}
