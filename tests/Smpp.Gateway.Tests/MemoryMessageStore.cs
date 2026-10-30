using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Smpp.Storage;

namespace Smpp.Gateway.Tests
{
    public sealed class MemoryMessageStore : IMessageStore
    {
        private readonly object _gate = new object();
        private readonly List<SmsMessage> _messages = new List<SmsMessage>();
        private readonly List<ProviderReceiptLink> _links = new List<ProviderReceiptLink>();
        private long _nextPartId = 1;

        public MemoryMessageStore()
        {
            Accounts = new List<Account>();
            Providers = new List<Provider>();
            Routes = new List<Route>();
        }

        public List<Account> Accounts { get; private set; }

        public List<Provider> Providers { get; private set; }

        public List<Route> Routes { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public Task SeedIfEmptyAsync(SeedData seed, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public Task<Account> FindAccountBySystemIdAsync(string systemId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(Accounts.FirstOrDefault(account => account.SystemId == systemId));
            }
        }

        public Task<Account> FindAccountAsync(Guid id, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(Accounts.FirstOrDefault(account => account.Id == id));
            }
        }

        public Task<IList<Account>> GetEnabledAccountsAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult((IList<Account>)Accounts.Where(account => account.Enabled).ToList());
            }
        }

        public Task<IList<Provider>> GetProvidersAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult((IList<Provider>)Providers.ToList());
            }
        }

        public Task<IList<Route>> GetRoutesAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult((IList<Route>)Routes.ToList());
            }
        }

        public Task InsertMessageAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (message.Parts == null)
                {
                    message.Parts = new List<SmsPart>();
                }

                _messages.Add(message);
            }

            return Task.FromResult(0);
        }

        public Task SaveSubmissionAsync(SmsMessage message, IList<SmsPart> parts, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var stored = _messages.First(item => item.Id == message.Id);
                stored.Status = message.Status;
                stored.Error = message.Error;
                stored.ProviderId = message.ProviderId;
                stored.ReceiptNotified = message.ReceiptNotified;
                stored.Parts = new List<SmsPart>();
                if (parts != null)
                {
                    for (var i = 0; i < parts.Count; i++)
                    {
                        parts[i].Id = _nextPartId++;
                        parts[i].MessageId = message.Id;
                        stored.Parts.Add(parts[i]);
                        if (!string.IsNullOrEmpty(parts[i].ProviderMessageId) && message.ProviderId.HasValue)
                        {
                            _links.Add(new ProviderReceiptLink
                            {
                                ProviderId = message.ProviderId.Value,
                                ProviderMessageId = parts[i].ProviderMessageId,
                                MessageId = message.Id,
                                PartId = parts[i].Id
                            });
                        }
                    }
                }
            }

            return Task.FromResult(0);
        }

        public Task UpdateMessageAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var stored = _messages.First(item => item.Id == message.Id);
                stored.Status = message.Status;
                stored.Error = message.Error;
                stored.ProviderId = message.ProviderId;
                stored.ReceiptNotified = message.ReceiptNotified;
                stored.TargetSystemId = message.TargetSystemId;
            }

            return Task.FromResult(0);
        }

        public Task<SmsMessage> GetMessageAsync(Guid id, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(_messages.FirstOrDefault(item => item.Id == id));
            }
        }

        public Task<ProviderReceiptLink> FindReceiptLinkAsync(Guid providerId, string providerMessageId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(_links.FirstOrDefault(link => link.ProviderId == providerId && link.ProviderMessageId == providerMessageId));
            }
        }

        public Task UpdatePartStatusAsync(long partId, MessageStatus status, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                foreach (var message in _messages)
                {
                    for (var i = 0; i < message.Parts.Count; i++)
                    {
                        if (message.Parts[i].Id == partId)
                        {
                            message.Parts[i].Status = status;
                        }
                    }
                }
            }

            return Task.FromResult(0);
        }
    }
}
