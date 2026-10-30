using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Smpp.Gateway;
using Smpp.Protocol;
using Smpp.Storage;
using Xunit;

namespace Smpp.Gateway.Tests
{
    public sealed class RoutingTests
    {
        [Fact]
        public void SerbiaTransactional_SelectsHttpProvider()
        {
            var http = Provider("serbia-http", ProviderKind.SerbiaHttp, true);
            var decision = RoutePolicy.Decide(Countries.Serbia, TrafficType.Transactional, new[]
            {
                Route(Countries.Serbia, TrafficType.Transactional, http.Id, 1)
            }, new[] { http }, null);

            Assert.True(decision.Accepted);
            Assert.Equal(http.Id, decision.Providers[0].Id);
        }

        [Fact]
        public void SerbiaBulk_CanUseHttpProvider()
        {
            var http = Provider("serbia-http", ProviderKind.SerbiaHttp, true);
            var decision = RoutePolicy.Decide(Countries.Serbia, TrafficType.Bulk, new[]
            {
                Route(Countries.Serbia, TrafficType.Bulk, http.Id, 1)
            }, new[] { http }, null);

            Assert.True(decision.Accepted);
            Assert.Equal(ProviderKind.SerbiaHttp, decision.Providers[0].Kind);
        }

        [Fact]
        public void MontenegroBulk_IsRejected()
        {
            var decision = RoutePolicy.Decide(Countries.Montenegro, TrafficType.Bulk, new Route[0], new Provider[0], null);

            Assert.False(decision.Accepted);
            Assert.Equal(SmppStatus.InvalidDestination, decision.Status);
        }

        [Fact]
        public void MacedoniaBulk_IsRejected()
        {
            var decision = RoutePolicy.Decide(Countries.Macedonia, TrafficType.Bulk, new Route[0], new Provider[0], null);

            Assert.False(decision.Accepted);
            Assert.Equal(SmppStatus.InvalidDestination, decision.Status);
        }

        [Fact]
        public void BosniaTransactional_SelectsOperatorSmpp()
        {
            var smpp = Provider("ba-operator", ProviderKind.OperatorSmpp, true);
            var decision = RoutePolicy.Decide(Countries.Bosnia, TrafficType.Transactional, new[]
            {
                Route(Countries.Bosnia, TrafficType.Transactional, smpp.Id, 1)
            }, new[] { smpp }, null);

            Assert.True(decision.Accepted);
            Assert.Equal(ProviderKind.OperatorSmpp, decision.Providers[0].Kind);
        }

        [Fact]
        public void BosniaBulk_UsesBulkProviderInsteadOfOperator()
        {
            var smpp = Provider("ba-operator", ProviderKind.OperatorSmpp, true);
            var bulk = Provider("bulk-smpp", ProviderKind.BulkSmpp, true);
            var decision = RoutePolicy.Decide(Countries.Bosnia, TrafficType.Bulk, new[]
            {
                Route(Countries.Bosnia, TrafficType.Transactional, smpp.Id, 1),
                Route(Countries.Bosnia, TrafficType.Bulk, bulk.Id, 1)
            }, new[] { smpp, bulk }, null);

            Assert.True(decision.Accepted);
            Assert.Single(decision.Providers);
            Assert.Equal(bulk.Id, decision.Providers[0].Id);
        }

        [Fact]
        public void NationalNumber_UsesDefaultCountry()
        {
            Assert.Equal("38164111222", Msisdn.Normalize("064111222", Countries.Serbia));
            Assert.Equal(Countries.Serbia, CountryPrefix.Resolve(Msisdn.Normalize("+38164111222", null)));
        }

        [Fact]
        public void Schema_CreatesCoreTables()
        {
            Assert.Contains("dbo.Accounts", SqlSchema.TableScript);
            Assert.Contains("dbo.Providers", SqlSchema.TableScript);
            Assert.Contains("dbo.Routes", SqlSchema.TableScript);
            Assert.Contains("dbo.SmsMessages", SqlSchema.TableScript);
            Assert.Contains("dbo.MessageParts", SqlSchema.TableScript);
            Assert.Contains("dbo.ProviderReceiptLinks", SqlSchema.TableScript);
            Assert.Contains("WebhookPath", SqlSchema.TableScript);
            Assert.Contains("NumberPrefixes", SqlSchema.TableScript);
        }

        [Fact]
        public void OperatorPrefixes_SelectSeparateTunnels()
        {
            var mts = Provider("mts-http", ProviderKind.OperatorHttp, true);
            var a1 = Provider("a1-http", ProviderKind.OperatorHttp, true);
            var globaltel = Provider("globaltel-http", ProviderKind.OperatorHttp, true);
            var bulk = Provider("bulk-smpp", ProviderKind.BulkSmpp, true);
            var routes = new[]
            {
                PrefixRoute(Countries.Serbia, TrafficType.Transactional, mts.Id, 1, "64,65,66"),
                PrefixRoute(Countries.Serbia, TrafficType.Transactional, a1.Id, 1, "60,61,68"),
                PrefixRoute(Countries.Serbia, TrafficType.Transactional, globaltel.Id, 1, "677"),
                PrefixRoute(Countries.Serbia, TrafficType.Bulk, mts.Id, 1, "64,65,66"),
                Route(Countries.Serbia, TrafficType.Bulk, bulk.Id, 2)
            };
            var providers = new[] { mts, a1, globaltel, bulk };

            var mtsDecision = RoutePolicy.Decide(Countries.Serbia, TrafficType.Transactional, routes, providers, "38164111222");
            var a1Decision = RoutePolicy.Decide(Countries.Serbia, TrafficType.Transactional, routes, providers, "38160111222");
            var globaltelDecision = RoutePolicy.Decide(Countries.Serbia, TrafficType.Transactional, routes, providers, "381677111222");
            var unknown = RoutePolicy.Decide(Countries.Serbia, TrafficType.Transactional, routes, providers, "38167111222");
            var bulkDecision = RoutePolicy.Decide(Countries.Serbia, TrafficType.Bulk, routes, providers, "38164111222");

            Assert.Equal(mts.Id, mtsDecision.Providers[0].Id);
            Assert.Equal(a1.Id, a1Decision.Providers[0].Id);
            Assert.Equal(globaltel.Id, globaltelDecision.Providers[0].Id);
            Assert.False(unknown.Accepted);
            Assert.Equal(2, bulkDecision.Providers.Count);
            Assert.Equal(mts.Id, bulkDecision.Providers[0].Id);
            Assert.Equal(bulk.Id, bulkDecision.Providers[1].Id);
        }

        private static Provider Provider(string name, ProviderKind kind, bool enabled)
        {
            return new Provider
            {
                Id = Guid.NewGuid(),
                Name = name,
                Kind = kind,
                Enabled = enabled
            };
        }

        private static Route Route(string country, TrafficType traffic, Guid providerId, int priority)
        {
            return new Route
            {
                Id = Guid.NewGuid(),
                Country = country,
                TrafficType = traffic,
                ProviderId = providerId,
                Priority = priority,
                Enabled = true
            };
        }

        private static Route PrefixRoute(string country, TrafficType traffic, Guid providerId, int priority, string prefixes)
        {
            var route = Route(country, traffic, providerId, priority);
            route.NumberPrefixes = prefixes;
            return route;
        }
    }

    public sealed class GatewaySendTests
    {
        [Fact]
        public async Task SerbiaSend_UsesHttpConnector()
        {
            var http = new Provider { Id = Guid.NewGuid(), Name = "serbia-http", Kind = ProviderKind.SerbiaHttp, Enabled = true };
            var store = Store(http, Countries.Serbia, TrafficType.Transactional);
            var connector = new FakeConnector { ProviderId = http.Id, SupportsSegmentation = false };
            var gateway = Gateway(store, connector);
            var outcome = await gateway.SendAsync(new SendRequest
            {
                Source = "INFO",
                Destination = "38164111222",
                Text = "Zdravo",
                TrafficType = TrafficType.Transactional
            }, CancellationToken.None);

            Assert.True(outcome.Accepted);
            Assert.Single(connector.Sent);
            Assert.Equal("38164111222", connector.Sent[0].Destination);
        }

        [Fact]
        public async Task MontenegroBulk_DoesNotCallConnector()
        {
            var bulk = new Provider { Id = Guid.NewGuid(), Name = "bulk", Kind = ProviderKind.BulkSmpp, Enabled = true };
            var store = Store(bulk, Countries.Montenegro, TrafficType.Bulk);
            var connector = new FakeConnector { ProviderId = bulk.Id, SupportsSegmentation = true };
            var gateway = Gateway(store, connector);
            var outcome = await gateway.SendAsync(new SendRequest
            {
                Source = "INFO",
                Destination = "38267111222",
                Text = "Bulk",
                TrafficType = TrafficType.Bulk
            }, CancellationToken.None);

            Assert.False(outcome.Accepted);
            Assert.Equal(SmppStatus.InvalidDestination, outcome.Status);
            Assert.Empty(connector.Sent);
        }

        [Fact]
        public async Task DeliveryReceipt_UpdatesMessageAndNotifiesOnce()
        {
            var http = new Provider { Id = Guid.NewGuid(), Name = "serbia-http", Kind = ProviderKind.SerbiaHttp, Enabled = true };
            var store = Store(http, Countries.Serbia, TrafficType.Transactional);
            var connector = new FakeConnector
            {
                ProviderId = http.Id,
                SupportsSegmentation = false,
                ProviderMessageId = "provider-1"
            };
            var notifier = new RecordingNotifier();
            var gateway = new MessageGateway(store, new FakeRegistry(connector), notifier, NullLogger.Instance);
            var outcome = await gateway.SendAsync(new SendRequest
            {
                AccountId = Guid.NewGuid(),
                Source = "INFO",
                Destination = "+38164111222",
                Text = "Status",
                TrafficType = TrafficType.Transactional
            }, CancellationToken.None);

            await gateway.HandleDeliveryReceiptAsync(new InboundReceipt
            {
                ProviderId = http.Id,
                ProviderMessageId = "provider-1",
                Stat = "DELIVRD"
            }, CancellationToken.None);
            await gateway.HandleDeliveryReceiptAsync(new InboundReceipt
            {
                ProviderId = http.Id,
                ProviderMessageId = "provider-1",
                Stat = "DELIVRD"
            }, CancellationToken.None);

            var message = await store.GetMessageAsync(GuidFromClientId(outcome.MessageId), CancellationToken.None);
            Assert.Equal(MessageStatus.Delivered, message.Status);
            Assert.True(message.ReceiptNotified);
            Assert.Single(notifier.Receipts);
        }

        private static MemoryMessageStore Store(Provider provider, string country, TrafficType traffic)
        {
            var store = new MemoryMessageStore();
            store.Providers.Add(provider);
            store.Routes.Add(new Route
            {
                Id = Guid.NewGuid(),
                Country = country,
                TrafficType = traffic,
                ProviderId = provider.Id,
                Priority = 1,
                Enabled = true
            });
            return store;
        }

        private static MessageGateway Gateway(MemoryMessageStore store, FakeConnector connector)
        {
            return new MessageGateway(store, new FakeRegistry(connector), new RecordingNotifier(), NullLogger.Instance);
        }

        private static Guid GuidFromClientId(string clientMessageId)
        {
            return Guid.ParseExact(clientMessageId, "N");
        }
    }

    public sealed class FakeConnector : IOutboundConnector
    {
        public FakeConnector()
        {
            Sent = new List<OutboundSubmit>();
            ProviderMessageId = "provider-id";
        }

        public Guid ProviderId { get; set; }

        public bool SupportsSegmentation { get; set; }

        public string ProviderMessageId { get; set; }

        public List<OutboundSubmit> Sent { get; private set; }

        public Task<SubmitResult> SubmitAsync(OutboundSubmit submit, CancellationToken cancellationToken)
        {
            Sent.Add(submit);
            return Task.FromResult(SubmitResult.Ok(new List<PartSubmitResult>
            {
                new PartSubmitResult
                {
                    PartNumber = 1,
                    PartCount = 1,
                    Success = true,
                    ProviderMessageId = ProviderMessageId,
                    Text = submit.Text
                }
            }));
        }
    }

    public sealed class FakeRegistry : IConnectorRegistry
    {
        private readonly IOutboundConnector _connector;

        public FakeRegistry(IOutboundConnector connector)
        {
            _connector = connector;
        }

        public IOutboundConnector Find(Guid providerId)
        {
            return _connector != null && _connector.ProviderId == providerId ? _connector : null;
        }
    }

    public sealed class RecordingNotifier : IClientNotifier
    {
        public RecordingNotifier()
        {
            Receipts = new List<SmsMessage>();
            Inbound = new List<SmsMessage>();
        }

        public List<SmsMessage> Receipts { get; private set; }

        public List<SmsMessage> Inbound { get; private set; }

        public Task DeliverReceiptAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            Receipts.Add(message);
            return Task.FromResult(0);
        }

        public Task DeliverInboundAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            Inbound.Add(message);
            return Task.FromResult(0);
        }
    }
}
