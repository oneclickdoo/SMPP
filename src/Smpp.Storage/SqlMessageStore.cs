using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Smpp.Storage
{
    public sealed class SqlMessageStore : IMessageStore
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private readonly object _cacheGate = new object();
        private IList<Provider> _providers = new List<Provider>();
        private IList<Route> _routes = new List<Route>();
        private DateTime _cachedAtUtc = DateTime.MinValue;

        public SqlMessageStore(string connectionString, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("SQL Server connection string is required.", "connectionString");
            }

            _connectionString = connectionString;
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            TryEnsureDatabase();
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                await connection.ExecuteAsync(Command(SqlSchema.TableScript, null, cancellationToken, 60)).ConfigureAwait(false);
            }

            _logger.LogInformation("SQL Server schema is ready.");
        }

        public async Task SeedIfEmptyAsync(SeedData seed, CancellationToken cancellationToken)
        {
            if (seed == null)
            {
                return;
            }

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                var accounts = await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(1) FROM dbo.Accounts", null, cancellationToken)).ConfigureAwait(false);
                var providers = await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(1) FROM dbo.Providers", null, cancellationToken)).ConfigureAwait(false);
                using (var transaction = connection.BeginTransaction())
                {
                    if (accounts == 0 && providers == 0)
                    {
                        foreach (var account in seed.Accounts)
                        {
                            PrepareAccount(account);
                            await connection.ExecuteAsync(Command(InsertAccountSql, account, cancellationToken, 30, transaction)).ConfigureAwait(false);
                        }

                        foreach (var provider in seed.Providers)
                        {
                            if (provider.Id == Guid.Empty)
                            {
                                provider.Id = Guid.NewGuid();
                            }

                            await connection.ExecuteAsync(Command(InsertProviderSql, provider, cancellationToken, 30, transaction)).ConfigureAwait(false);
                        }

                        foreach (var route in seed.Routes)
                        {
                            var provider = FindProvider(seed.Providers, route.ProviderName);
                            await InsertRouteAsync(connection, transaction, route, provider.Id, cancellationToken).ConfigureAwait(false);
                        }

                        _logger.LogInformation("Seeded {Accounts} accounts, {Providers} providers and {Routes} routes.", seed.Accounts.Count, seed.Providers.Count, seed.Routes.Count);
                    }
                    else
                    {
                        var addedProviders = 0;
                        var addedRoutes = 0;
                        var existing = (await connection.QueryAsync<Provider>(Command("SELECT Id, Name FROM dbo.Providers", null, cancellationToken, 30, transaction)).ConfigureAwait(false)).ToList();
                        foreach (var provider in seed.Providers)
                        {
                            if (FindProviderOrNull(existing, provider.Name) != null)
                            {
                                continue;
                            }

                            if (provider.Id == Guid.Empty)
                            {
                                provider.Id = Guid.NewGuid();
                            }

                            await connection.ExecuteAsync(Command(InsertProviderSql, provider, cancellationToken, 30, transaction)).ConfigureAwait(false);
                            existing.Add(provider);
                            addedProviders++;
                        }

                        var existingRoutes = (await connection.QueryAsync<Route>(Command("SELECT ProviderId, Country, TrafficType FROM dbo.Routes", null, cancellationToken, 30, transaction)).ConfigureAwait(false)).ToList();
                        foreach (var route in seed.Routes)
                        {
                            var provider = FindProviderOrNull(existing, route.ProviderName);
                            if (provider == null || RouteExists(existingRoutes, provider.Id, route))
                            {
                                continue;
                            }

                            await InsertRouteAsync(connection, transaction, route, provider.Id, cancellationToken).ConfigureAwait(false);
                            existingRoutes.Add(new Route
                            {
                                ProviderId = provider.Id,
                                Country = route.Country,
                                TrafficType = route.TrafficType
                            });
                            addedRoutes++;
                        }

                        if (addedProviders > 0 || addedRoutes > 0)
                        {
                            _logger.LogInformation("Added {Providers} providers and {Routes} routes from configuration.", addedProviders, addedRoutes);
                        }
                    }

                    transaction.Commit();
                }
            }

            InvalidateCache();
        }

        private static bool RouteExists(IList<Route> routes, Guid providerId, RouteSeed route)
        {
            for (var i = 0; i < routes.Count; i++)
            {
                var existing = routes[i];
                if (existing.ProviderId == providerId
                    && existing.TrafficType == route.TrafficType
                    && string.Equals(existing.Country, route.Country, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task InsertRouteAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, RouteSeed route, Guid providerId, CancellationToken cancellationToken)
        {
            await connection.ExecuteAsync(Command(
                @"INSERT INTO dbo.Routes (Id, Country, TrafficType, ProviderId, Priority, NumberPrefixes, Enabled)
                  VALUES (@Id, @Country, @TrafficType, @ProviderId, @Priority, @NumberPrefixes, @Enabled)",
                new
                {
                    Id = Guid.NewGuid(),
                    route.Country,
                    route.TrafficType,
                    ProviderId = providerId,
                    route.Priority,
                    NumberPrefixes = route.NumberPrefixes ?? string.Empty,
                    route.Enabled
                },
                cancellationToken,
                30,
                transaction)).ConfigureAwait(false);
        }

        public async Task<Account> FindAccountBySystemIdAsync(string systemId, CancellationToken cancellationToken)
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                return await connection.QuerySingleOrDefaultAsync<Account>(Command(
                    "SELECT " + AccountColumns + " FROM dbo.Accounts WHERE SystemId = @SystemId",
                    new { SystemId = systemId },
                    cancellationToken)).ConfigureAwait(false);
            }
        }

        public async Task<Account> FindAccountAsync(Guid id, CancellationToken cancellationToken)
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                return await connection.QuerySingleOrDefaultAsync<Account>(Command(
                    "SELECT " + AccountColumns + " FROM dbo.Accounts WHERE Id = @Id",
                    new { Id = id },
                    cancellationToken)).ConfigureAwait(false);
            }
        }

        public async Task<IList<Account>> GetEnabledAccountsAsync(CancellationToken cancellationToken)
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                var rows = await connection.QueryAsync<Account>(Command(
                    "SELECT " + AccountColumns + " FROM dbo.Accounts WHERE Enabled = 1",
                    null,
                    cancellationToken)).ConfigureAwait(false);
                return rows.ToList();
            }
        }

        public async Task<IList<Provider>> GetProvidersAsync(CancellationToken cancellationToken)
        {
            await EnsureCacheAsync(cancellationToken).ConfigureAwait(false);
            return _providers;
        }

        public async Task<IList<Route>> GetRoutesAsync(CancellationToken cancellationToken)
        {
            await EnsureCacheAsync(cancellationToken).ConfigureAwait(false);
            return _routes;
        }

        public async Task InsertMessageAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            if (message.Id == Guid.Empty)
            {
                message.Id = Guid.NewGuid();
            }

            if (message.CreatedAt == DateTime.MinValue)
            {
                message.CreatedAt = DateTime.UtcNow;
            }

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                await connection.ExecuteAsync(Command(
                    @"INSERT INTO dbo.SmsMessages
                      (Id, AccountId, Direction, TrafficType, Source, Destination, Text, Country, Status, Error, ProviderId, ClientMessageId, TargetSystemId, ReceiptNotified, CreatedAt)
                      VALUES
                      (@Id, @AccountId, @Direction, @TrafficType, @Source, @Destination, @Text, @Country, @Status, @Error, @ProviderId, @ClientMessageId, @TargetSystemId, @ReceiptNotified, @CreatedAt)",
                    MessageParameters(message),
                    cancellationToken)).ConfigureAwait(false);
            }
        }

        public async Task SaveSubmissionAsync(SmsMessage message, IList<SmsPart> parts, CancellationToken cancellationToken)
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var transaction = connection.BeginTransaction())
            {
                await connection.ExecuteAsync(Command(UpdateMessageSql, MessageParameters(message), cancellationToken, 30, transaction)).ConfigureAwait(false);
                if (parts != null)
                {
                    foreach (var part in parts)
                    {
                        part.MessageId = message.Id;
                        var id = await connection.ExecuteScalarAsync(Command(
                            @"INSERT INTO dbo.MessageParts (MessageId, PartNumber, PartCount, ProviderMessageId, Status)
                              VALUES (@MessageId, @PartNumber, @PartCount, @ProviderMessageId, @Status);
                              SELECT CAST(SCOPE_IDENTITY() AS bigint);",
                            new
                            {
                                part.MessageId,
                                part.PartNumber,
                                part.PartCount,
                                ProviderMessageId = TrimId(part.ProviderMessageId),
                                part.Status
                            },
                            cancellationToken,
                            30,
                            transaction)).ConfigureAwait(false);
                        part.Id = Convert.ToInt64(id);
                        if (!string.IsNullOrEmpty(part.ProviderMessageId) && message.ProviderId.HasValue)
                        {
                            await connection.ExecuteAsync(Command(
                                @"INSERT INTO dbo.ProviderReceiptLinks (ProviderId, ProviderMessageId, MessageId, PartId)
                                  VALUES (@ProviderId, @ProviderMessageId, @MessageId, @PartId)",
                                new
                                {
                                    ProviderId = message.ProviderId.Value,
                                    ProviderMessageId = TrimId(part.ProviderMessageId),
                                    MessageId = message.Id,
                                    PartId = part.Id
                                },
                                cancellationToken,
                                30,
                                transaction)).ConfigureAwait(false);
                        }
                    }
                }

                transaction.Commit();
            }
        }

        public async Task UpdateMessageAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                await connection.ExecuteAsync(Command(UpdateMessageSql, MessageParameters(message), cancellationToken)).ConfigureAwait(false);
            }
        }

        public async Task<SmsMessage> GetMessageAsync(Guid id, CancellationToken cancellationToken)
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                var message = await connection.QuerySingleOrDefaultAsync<SmsMessage>(Command(
                    "SELECT " + MessageColumns + " FROM dbo.SmsMessages WHERE Id = @Id",
                    new { Id = id },
                    cancellationToken)).ConfigureAwait(false);
                if (message == null)
                {
                    return null;
                }

                var parts = await connection.QueryAsync<SmsPart>(Command(
                    "SELECT Id, MessageId, PartNumber, PartCount, ProviderMessageId, Status FROM dbo.MessageParts WHERE MessageId = @Id ORDER BY PartNumber",
                    new { Id = id },
                    cancellationToken)).ConfigureAwait(false);
                message.Parts = parts.ToList();
                return message;
            }
        }

        public async Task<ProviderReceiptLink> FindReceiptLinkAsync(Guid providerId, string providerMessageId, CancellationToken cancellationToken)
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                return await connection.QuerySingleOrDefaultAsync<ProviderReceiptLink>(Command(
                    @"SELECT ProviderId, ProviderMessageId, MessageId, PartId
                      FROM dbo.ProviderReceiptLinks
                      WHERE ProviderId = @ProviderId AND ProviderMessageId = @ProviderMessageId",
                    new { ProviderId = providerId, ProviderMessageId = providerMessageId },
                    cancellationToken)).ConfigureAwait(false);
            }
        }

        public async Task UpdatePartStatusAsync(long partId, MessageStatus status, CancellationToken cancellationToken)
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                await connection.ExecuteAsync(Command(
                    "UPDATE dbo.MessageParts SET Status = @Status WHERE Id = @Id",
                    new { Id = partId, Status = status },
                    cancellationToken)).ConfigureAwait(false);
            }
        }

        private async Task EnsureCacheAsync(CancellationToken cancellationToken)
        {
            if (DateTime.UtcNow - _cachedAtUtc < TimeSpan.FromSeconds(30) && _cachedAtUtc != DateTime.MinValue)
            {
                return;
            }

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                var providers = (await connection.QueryAsync<Provider>(Command("SELECT " + ProviderColumns + " FROM dbo.Providers", null, cancellationToken)).ConfigureAwait(false)).ToList();
                var routes = (await connection.QueryAsync<Route>(Command("SELECT Id, Country, TrafficType, ProviderId, Priority, NumberPrefixes, Enabled FROM dbo.Routes", null, cancellationToken)).ConfigureAwait(false)).ToList();
                lock (_cacheGate)
                {
                    _providers = providers;
                    _routes = routes;
                    _cachedAtUtc = DateTime.UtcNow;
                }
            }
        }

        private void InvalidateCache()
        {
            lock (_cacheGate)
            {
                _cachedAtUtc = DateTime.MinValue;
            }
        }

        private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        private void TryEnsureDatabase()
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(_connectionString);
                var database = builder.InitialCatalog;
                if (string.IsNullOrWhiteSpace(database))
                {
                    throw new InvalidOperationException("The connection string must include Database or Initial Catalog.");
                }

                QuoteName(database);
                builder.InitialCatalog = "master";
                using (var connection = new SqlConnection(builder.ConnectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "IF DB_ID(@name) IS NULL CREATE DATABASE " + QuoteName(database);
                        command.Parameters.AddWithValue("@name", database);
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create the gateway database from master. The configured database must already exist.");
            }
        }

        private static Provider FindProvider(IList<Provider> providers, string name)
        {
            var provider = FindProviderOrNull(providers, name);
            if (provider == null)
            {
                throw new InvalidOperationException("Route references unknown provider '" + name + "'.");
            }

            return provider;
        }

        private static Provider FindProviderOrNull(IList<Provider> providers, string name)
        {
            for (var i = 0; i < providers.Count; i++)
            {
                if (string.Equals(providers[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return providers[i];
                }
            }

            return null;
        }

        private static void PrepareAccount(Account account)
        {
            if (account.Id == Guid.Empty)
            {
                account.Id = Guid.NewGuid();
            }

            if (string.IsNullOrWhiteSpace(account.SystemId) || account.SystemId.Length > 15)
            {
                throw new InvalidOperationException("Account system id must be 1 to 15 characters.");
            }

            if (!string.IsNullOrEmpty(account.Password) && account.Password.Length > 8)
            {
                throw new InvalidOperationException("Account password '" + account.SystemId + "' is longer than 8 characters. SMPP bind cannot carry it.");
            }
        }

        private static string TrimId(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= 64 ? value : value.Substring(0, 64);
        }

        private static string QuoteName(string name)
        {
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                var ok = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_';
                if (!ok)
                {
                    throw new InvalidOperationException("Database name contains unsupported characters.");
                }
            }

            return "[" + name + "]";
        }

        private static object MessageParameters(SmsMessage message)
        {
            return new
            {
                message.Id,
                message.AccountId,
                message.Direction,
                message.TrafficType,
                message.Source,
                message.Destination,
                message.Text,
                message.Country,
                message.Status,
                Error = message.Error ?? string.Empty,
                message.ProviderId,
                message.ClientMessageId,
                message.TargetSystemId,
                message.ReceiptNotified,
                message.CreatedAt
            };
        }

        private static CommandDefinition Command(string text, object param, CancellationToken cancellationToken, int timeout = 30, IDbTransaction transaction = null)
        {
            return new CommandDefinition(text, param, transaction, timeout, cancellationToken: cancellationToken);
        }

        private const string AccountColumns = "Id, Name, SystemId, Password, AllowedIps, TrafficType, WebhookUrl, DefaultCountry, ThroughputPerSecond, Enabled";

        private const string ProviderColumns = "Id, Name, Kind, Country, Host, Port, SystemId, Password, SystemType, SourceTon, SourceNpi, DestTon, DestNpi, UseTls, AllowInvalidCertificate, HttpUrl, HttpAuthHeader, ThroughputPerSecond, WindowSize, InboundSystemId, WebhookPath, Enabled";

        private const string MessageColumns = "Id, AccountId, Direction, TrafficType, Source, Destination, Text, Country, Status, Error, ProviderId, ClientMessageId, TargetSystemId, ReceiptNotified, CreatedAt";

        private const string InsertAccountSql = @"
INSERT INTO dbo.Accounts
(Id, Name, SystemId, Password, AllowedIps, TrafficType, WebhookUrl, DefaultCountry, ThroughputPerSecond, Enabled)
VALUES
(@Id, @Name, @SystemId, @Password, @AllowedIps, @TrafficType, @WebhookUrl, @DefaultCountry, @ThroughputPerSecond, @Enabled)";

        private const string InsertProviderSql = @"
INSERT INTO dbo.Providers
(Id, Name, Kind, Country, Host, Port, SystemId, Password, SystemType, SourceTon, SourceNpi, DestTon, DestNpi, UseTls, AllowInvalidCertificate, HttpUrl, HttpAuthHeader, ThroughputPerSecond, WindowSize, InboundSystemId, WebhookPath, Enabled)
VALUES
(@Id, @Name, @Kind, @Country, @Host, @Port, @SystemId, @Password, @SystemType, @SourceTon, @SourceNpi, @DestTon, @DestNpi, @UseTls, @AllowInvalidCertificate, @HttpUrl, @HttpAuthHeader, @ThroughputPerSecond, @WindowSize, @InboundSystemId, @WebhookPath, @Enabled)";

        private const string UpdateMessageSql = @"
UPDATE dbo.SmsMessages
SET Status = @Status,
    Error = @Error,
    ProviderId = @ProviderId,
    ReceiptNotified = @ReceiptNotified,
    TargetSystemId = @TargetSystemId
WHERE Id = @Id";
    }
}
