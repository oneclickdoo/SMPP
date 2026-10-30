using System;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Smpp.Storage;

namespace Smpp.Host
{
    public static class SeedLoader
    {
        public static SeedData Load(IConfiguration configuration)
        {
            var seed = new SeedData();
            foreach (var child in configuration.GetSection("Accounts").GetChildren())
            {
                seed.Accounts.Add(new Account
                {
                    Name = Value(child, "Name"),
                    SystemId = Value(child, "SystemId"),
                    Password = Value(child, "Password"),
                    AllowedIps = Value(child, "AllowedIps"),
                    TrafficType = ParseEnum(Value(child, "TrafficType"), TrafficType.Transactional),
                    WebhookUrl = Value(child, "WebhookUrl"),
                    DefaultCountry = Value(child, "DefaultCountry"),
                    ThroughputPerSecond = ParseInt(Value(child, "ThroughputPerSecond"), 10),
                    Enabled = ParseBool(Value(child, "Enabled"), true)
                });
            }

            foreach (var child in configuration.GetSection("Providers").GetChildren())
            {
                int port;
                int? parsedPort = int.TryParse(Value(child, "Port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ? (int?)port : null;
                seed.Providers.Add(new Provider
                {
                    Name = Value(child, "Name"),
                    Kind = ParseEnum(Value(child, "Kind"), ProviderKind.OperatorSmpp),
                    Country = Value(child, "Country"),
                    Host = Value(child, "Host"),
                    Port = parsedPort,
                    SystemId = Value(child, "SystemId"),
                    Password = Value(child, "Password"),
                    SystemType = Value(child, "SystemType"),
                    UseTls = ParseBool(Value(child, "UseTls"), false),
                    AllowInvalidCertificate = ParseBool(Value(child, "AllowInvalidCertificate"), false),
                    HttpUrl = Value(child, "HttpUrl"),
                    HttpAuthHeader = Value(child, "HttpAuthHeader"),
                    ThroughputPerSecond = ParseInt(Value(child, "ThroughputPerSecond"), 10),
                    WindowSize = ParseInt(Value(child, "WindowSize"), 10),
                    InboundSystemId = Value(child, "InboundSystemId"),
                    WebhookPath = Value(child, "WebhookPath"),
                    Enabled = ParseBool(Value(child, "Enabled"), false)
                });
            }

            foreach (var child in configuration.GetSection("Routes").GetChildren())
            {
                seed.Routes.Add(new RouteSeed
                {
                    Country = Value(child, "Country"),
                    TrafficType = ParseEnum(Value(child, "TrafficType"), TrafficType.Transactional),
                    ProviderName = Value(child, "Provider"),
                    Priority = ParseInt(Value(child, "Priority"), 1),
                    NumberPrefixes = Value(child, "NumberPrefixes"),
                    Enabled = ParseBool(Value(child, "Enabled"), true)
                });
            }

            return seed;
        }

        public static string ConnectionString(IConfiguration configuration)
        {
            return configuration["Database:ConnectionString"] ?? string.Empty;
        }

        public static string WebhookKey(IConfiguration configuration)
        {
            return configuration["SerbiaWebhookKey"] ?? string.Empty;
        }

        public static string[] HttpPrefixes(IConfiguration configuration)
        {
            var section = configuration.GetSection("Http:Prefixes").GetChildren();
            var list = new System.Collections.Generic.List<string>();
            foreach (var child in section)
            {
                if (!string.IsNullOrWhiteSpace(child.Value))
                {
                    var prefix = child.Value.Trim();
                    if (!prefix.EndsWith("/", StringComparison.Ordinal))
                    {
                        prefix += "/";
                    }

                    list.Add(prefix);
                }
            }

            if (list.Count == 0)
            {
                list.Add("http://localhost:8080/");
            }

            return list.ToArray();
        }

        public static IPAddress BindAddress(IConfiguration configuration)
        {
            var raw = configuration["SmppServer:BindAddress"];
            IPAddress address;
            if (string.IsNullOrWhiteSpace(raw) || raw == "0.0.0.0" || !IPAddress.TryParse(raw, out address))
            {
                return IPAddress.Any;
            }

            return address;
        }

        private static string Value(IConfiguration section, string name)
        {
            return section[name] ?? string.Empty;
        }

        private static int ParseInt(string raw, int fallback)
        {
            int value;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static bool ParseBool(string raw, bool fallback)
        {
            bool value;
            return bool.TryParse(raw, out value) ? value : fallback;
        }

        private static T ParseEnum<T>(string raw, T fallback) where T : struct
        {
            T value;
            return Enum.TryParse(raw, true, out value) ? value : fallback;
        }
    }
}
