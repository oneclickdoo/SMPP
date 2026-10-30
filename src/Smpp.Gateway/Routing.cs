using System;
using System.Collections.Generic;
using System.Text;
using Smpp.Protocol;
using Smpp.Storage;

namespace Smpp.Gateway
{
    public static class Countries
    {
        public const string Serbia = "RS";
        public const string Bosnia = "BA";
        public const string Montenegro = "ME";
        public const string Macedonia = "MK";
    }

    public static class CountryPrefix
    {
        public static string Resolve(string digits)
        {
            if (string.IsNullOrEmpty(digits))
            {
                return null;
            }

            if (digits.StartsWith("381", StringComparison.Ordinal))
            {
                return Countries.Serbia;
            }

            if (digits.StartsWith("387", StringComparison.Ordinal))
            {
                return Countries.Bosnia;
            }

            if (digits.StartsWith("382", StringComparison.Ordinal))
            {
                return Countries.Montenegro;
            }

            if (digits.StartsWith("389", StringComparison.Ordinal))
            {
                return Countries.Macedonia;
            }

            return null;
        }

        public static string CallingCode(string country)
        {
            switch (country)
            {
                case Countries.Serbia:
                    return "381";
                case Countries.Bosnia:
                    return "387";
                case Countries.Montenegro:
                    return "382";
                case Countries.Macedonia:
                    return "389";
                default:
                    return null;
            }
        }
    }

    public static class Msisdn
    {
        public static string Normalize(string raw, string defaultCountry)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var digits = new StringBuilder(raw.Length);
            var trimmed = raw.Trim();
            for (var i = 0; i < trimmed.Length; i++)
            {
                var ch = trimmed[i];
                if (ch >= '0' && ch <= '9')
                {
                    digits.Append(ch);
                }
            }

            var value = digits.ToString();
            if (value.StartsWith("00", StringComparison.Ordinal))
            {
                value = value.Substring(2);
            }

            if (value.StartsWith("0", StringComparison.Ordinal))
            {
                var code = CountryPrefix.CallingCode(defaultCountry);
                if (!string.IsNullOrEmpty(code))
                {
                    value = code + value.Substring(1);
                }
            }

            return value;
        }
    }

    public sealed class RouteDecision
    {
        public RouteDecision()
        {
            Reason = string.Empty;
            Providers = new List<Provider>();
        }

        public bool Accepted { get; set; }

        public uint Status { get; set; }

        public string Reason { get; set; }

        public IList<Provider> Providers { get; set; }

        public static RouteDecision Reject(uint status, string reason)
        {
            return new RouteDecision { Accepted = false, Status = status, Reason = reason ?? string.Empty };
        }

        public static RouteDecision Accept(IList<Provider> providers)
        {
            return new RouteDecision { Accepted = true, Status = SmppStatus.Ok, Providers = providers };
        }
    }

    public static class RoutePolicy
    {
        public static RouteDecision Decide(string country, TrafficType traffic, IList<Route> routes, IList<Provider> providers, string destination)
        {
            if (string.IsNullOrEmpty(country))
            {
                return RouteDecision.Reject(SmppStatus.InvalidDestination, "Destination country is not supported.");
            }

            if (traffic == TrafficType.Bulk && (country == Countries.Montenegro || country == Countries.Macedonia))
            {
                return RouteDecision.Reject(SmppStatus.InvalidDestination, "Bulk is not available for this country.");
            }

            var national = NationalNumber(country, destination);
            var specific = new List<PrefixRoute>();
            var fallback = new List<Route>();
            if (routes != null)
            {
                for (var i = 0; i < routes.Count; i++)
                {
                    var route = routes[i];
                    if (!route.Enabled || route.TrafficType != traffic || !string.Equals(route.Country, country, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var provider = Find(providers, route.ProviderId);
                    if (provider == null || !provider.Enabled)
                    {
                        continue;
                    }

                    var length = PrefixLength(route.NumberPrefixes, national);
                    if (length > 0)
                    {
                        specific.Add(new PrefixRoute { Route = route, Length = length, Provider = provider });
                    }
                    else if (string.IsNullOrWhiteSpace(route.NumberPrefixes) || string.IsNullOrEmpty(national))
                    {
                        fallback.Add(route);
                    }
                }
            }

            specific.Sort(CompareSpecific);
            fallback.Sort((left, right) => left.Priority.CompareTo(right.Priority));

            var selected = new List<Provider>();
            for (var i = 0; i < specific.Count; i++)
            {
                AddProvider(selected, specific[i].Provider);
            }

            for (var i = 0; i < fallback.Count; i++)
            {
                AddProvider(selected, Find(providers, fallback[i].ProviderId));
            }

            if (selected.Count == 0)
            {
                return RouteDecision.Reject(SmppStatus.SubmitFailed, "No route for this destination.");
            }

            return RouteDecision.Accept(selected);
        }

        private static string NationalNumber(string country, string destination)
        {
            if (string.IsNullOrEmpty(destination))
            {
                return string.Empty;
            }

            var code = CountryPrefix.CallingCode(country);
            if (!string.IsNullOrEmpty(code) && destination.StartsWith(code, StringComparison.Ordinal))
            {
                return destination.Substring(code.Length);
            }

            return destination;
        }

        private static int PrefixLength(string prefixes, string national)
        {
            if (string.IsNullOrWhiteSpace(prefixes) || string.IsNullOrEmpty(national))
            {
                return 0;
            }

            var best = 0;
            var parts = prefixes.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var prefix = parts[i].Trim();
                if (prefix.Length > best && national.StartsWith(prefix, StringComparison.Ordinal))
                {
                    best = prefix.Length;
                }
            }

            return best;
        }

        private static int CompareSpecific(PrefixRoute left, PrefixRoute right)
        {
            var length = right.Length.CompareTo(left.Length);
            return length != 0 ? length : left.Route.Priority.CompareTo(right.Route.Priority);
        }

        private static void AddProvider(IList<Provider> selected, Provider provider)
        {
            if (provider == null)
            {
                return;
            }

            for (var i = 0; i < selected.Count; i++)
            {
                if (selected[i].Id == provider.Id)
                {
                    return;
                }
            }

            selected.Add(provider);
        }

        private sealed class PrefixRoute
        {
            public Route Route { get; set; }

            public int Length { get; set; }

            public Provider Provider { get; set; }
        }

        private static Provider Find(IList<Provider> providers, Guid id)
        {
            if (providers == null)
            {
                return null;
            }

            for (var i = 0; i < providers.Count; i++)
            {
                if (providers[i].Id == id)
                {
                    return providers[i];
                }
            }

            return null;
        }
    }

    public static class MessageStatusMap
    {
        public static MessageStatus FromReceipt(string stat, byte? state)
        {
            if (state.HasValue)
            {
                switch (state.Value)
                {
                    case 1:
                        return MessageStatus.Submitted;
                    case 2:
                        return MessageStatus.Delivered;
                    case 3:
                        return MessageStatus.Expired;
                    case 4:
                        return MessageStatus.Failed;
                    case 5:
                        return MessageStatus.Undeliverable;
                    case 6:
                        return MessageStatus.Accepted;
                    case 8:
                        return MessageStatus.Rejected;
                    default:
                        return MessageStatus.Unknown;
                }
            }

            var name = (stat ?? string.Empty).Trim().ToUpperInvariant();
            switch (name)
            {
                case "DELIVRD":
                    return MessageStatus.Delivered;
                case "UNDELIV":
                case "UNDELIVERABLE":
                    return MessageStatus.Undeliverable;
                case "EXPIRED":
                    return MessageStatus.Expired;
                case "REJECTD":
                case "REJECTED":
                    return MessageStatus.Rejected;
                case "ACCEPTD":
                case "ACCEPTED":
                    return MessageStatus.Accepted;
                case "ENROUTE":
                    return MessageStatus.Submitted;
                case "DELETED":
                    return MessageStatus.Failed;
                default:
                    return MessageStatus.Unknown;
            }
        }

        public static string ToStat(MessageStatus status)
        {
            switch (status)
            {
                case MessageStatus.Delivered:
                    return "DELIVRD";
                case MessageStatus.Undeliverable:
                    return "UNDELIV";
                case MessageStatus.Expired:
                    return "EXPIRED";
                case MessageStatus.Rejected:
                    return "REJECTD";
                case MessageStatus.Accepted:
                    return "ACCEPTD";
                case MessageStatus.Submitted:
                    return "ENROUTE";
                case MessageStatus.Failed:
                    return "DELETED";
                default:
                    return "UNKNOWN";
            }
        }

        public static byte ToMessageState(MessageStatus status)
        {
            switch (status)
            {
                case MessageStatus.Delivered:
                    return 2;
                case MessageStatus.Expired:
                    return 3;
                case MessageStatus.Failed:
                    return 4;
                case MessageStatus.Undeliverable:
                    return 5;
                case MessageStatus.Accepted:
                    return 6;
                case MessageStatus.Rejected:
                    return 8;
                case MessageStatus.Unknown:
                    return 7;
                default:
                    return 1;
            }
        }

        public static bool IsTerminal(MessageStatus status)
        {
            return status == MessageStatus.Delivered
                || status == MessageStatus.Undeliverable
                || status == MessageStatus.Expired
                || status == MessageStatus.Rejected
                || status == MessageStatus.Failed;
        }
    }
}
