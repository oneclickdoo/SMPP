using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Logging;
using Smpp.Gateway;
using Smpp.Storage;

namespace Smpp.Connectors
{
    public sealed class ConnectorRegistry : IConnectorRegistry
    {
        private readonly SmppLinkManager _links;
        private readonly HttpClient _http;
        private readonly ILogger _logger;
        private readonly Dictionary<Guid, IOutboundConnector> _connectors = new Dictionary<Guid, IOutboundConnector>();

        public ConnectorRegistry(SmppLinkManager links, HttpClient httpClient, ILogger logger)
        {
            _links = links ?? new SmppLinkManager(logger);
            _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public SmppLinkManager Links
        {
            get { return _links; }
        }

        public void Load(IList<Provider> providers)
        {
            _connectors.Clear();
            if (providers == null)
            {
                return;
            }

            for (var i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                if (!provider.Enabled)
                {
                    continue;
                }

                IOutboundConnector connector;
                switch (provider.Kind)
                {
                    case ProviderKind.SerbiaHttp:
                    case ProviderKind.OperatorHttp:
                        connector = new SerbiaHttpConnector(provider, _http, _logger);
                        break;
                    case ProviderKind.BulkHttp:
                        connector = new HttpBulkConnector(provider, _http, _logger);
                        break;
                    case ProviderKind.OperatorSmpp:
                        _links.Track(provider);
                        connector = new OperatorSmppConnector(_links, provider);
                        break;
                    case ProviderKind.BulkSmpp:
                        _links.Track(provider);
                        connector = new BulkSmppConnector(_links, provider);
                        break;
                    default:
                        continue;
                }

                _connectors[provider.Id] = connector;
            }
        }

        public void Start(IInboundHandler handler, CancellationToken cancellationToken)
        {
            _links.Handler = handler;
            _links.Start(cancellationToken);
        }

        public IOutboundConnector Find(Guid providerId)
        {
            IOutboundConnector connector;
            return _connectors.TryGetValue(providerId, out connector) ? connector : null;
        }
    }
}
