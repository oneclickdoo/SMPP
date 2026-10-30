using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Smpp.Storage
{
    public interface IMessageStore
    {
        Task InitializeAsync(CancellationToken cancellationToken);

        Task SeedIfEmptyAsync(SeedData seed, CancellationToken cancellationToken);

        Task<Account> FindAccountBySystemIdAsync(string systemId, CancellationToken cancellationToken);

        Task<Account> FindAccountAsync(Guid id, CancellationToken cancellationToken);

        Task<IList<Account>> GetEnabledAccountsAsync(CancellationToken cancellationToken);

        Task<IList<Provider>> GetProvidersAsync(CancellationToken cancellationToken);

        Task<IList<Route>> GetRoutesAsync(CancellationToken cancellationToken);

        Task InsertMessageAsync(SmsMessage message, CancellationToken cancellationToken);

        Task SaveSubmissionAsync(SmsMessage message, IList<SmsPart> parts, CancellationToken cancellationToken);

        Task UpdateMessageAsync(SmsMessage message, CancellationToken cancellationToken);

        Task<SmsMessage> GetMessageAsync(Guid id, CancellationToken cancellationToken);

        Task<ProviderReceiptLink> FindReceiptLinkAsync(Guid providerId, string providerMessageId, CancellationToken cancellationToken);

        Task UpdatePartStatusAsync(long partId, MessageStatus status, CancellationToken cancellationToken);
    }
}
