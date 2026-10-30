using System;
using System.Collections.Generic;

namespace Smpp.Storage
{
    public enum TrafficType
    {
        Transactional = 1,
        Bulk = 2
    }

    public enum MessageDirection
    {
        Outbound = 1,
        Inbound = 2
    }

    public enum MessageStatus
    {
        Accepted = 1,
        Submitted = 2,
        Delivered = 3,
        Undeliverable = 4,
        Expired = 5,
        Rejected = 6,
        Failed = 7,
        Unknown = 8
    }

    public enum ProviderKind
    {
        SerbiaHttp = 1,
        OperatorSmpp = 2,
        BulkHttp = 3,
        BulkSmpp = 4,
        OperatorHttp = 5
    }

    public sealed class Account
    {
        public Account()
        {
            Name = string.Empty;
            SystemId = string.Empty;
            Password = string.Empty;
            AllowedIps = string.Empty;
            WebhookUrl = string.Empty;
            DefaultCountry = string.Empty;
        }

        public Guid Id { get; set; }

        public string Name { get; set; }

        public string SystemId { get; set; }

        public string Password { get; set; }

        public string AllowedIps { get; set; }

        public TrafficType TrafficType { get; set; }

        public string WebhookUrl { get; set; }

        public string DefaultCountry { get; set; }

        public int ThroughputPerSecond { get; set; }

        public bool Enabled { get; set; }
    }

    public sealed class Provider
    {
        public Provider()
        {
            Name = string.Empty;
            Country = string.Empty;
            Host = string.Empty;
            SystemId = string.Empty;
            Password = string.Empty;
            SystemType = string.Empty;
            HttpUrl = string.Empty;
            HttpAuthHeader = string.Empty;
            InboundSystemId = string.Empty;
            WebhookPath = string.Empty;
        }

        public Guid Id { get; set; }

        public string Name { get; set; }

        public ProviderKind Kind { get; set; }

        public string Country { get; set; }

        public string Host { get; set; }

        public int? Port { get; set; }

        public string SystemId { get; set; }

        public string Password { get; set; }

        public string SystemType { get; set; }

        public byte? SourceTon { get; set; }

        public byte? SourceNpi { get; set; }

        public byte? DestTon { get; set; }

        public byte? DestNpi { get; set; }

        public bool UseTls { get; set; }

        public bool AllowInvalidCertificate { get; set; }

        public string HttpUrl { get; set; }

        public string HttpAuthHeader { get; set; }

        public int ThroughputPerSecond { get; set; }

        public int WindowSize { get; set; }

        public string InboundSystemId { get; set; }

        public string WebhookPath { get; set; }

        public bool Enabled { get; set; }
    }

    public sealed class Route
    {
        public Route()
        {
            Country = string.Empty;
            NumberPrefixes = string.Empty;
        }

        public Guid Id { get; set; }

        public string Country { get; set; }

        public TrafficType TrafficType { get; set; }

        public Guid ProviderId { get; set; }

        public int Priority { get; set; }

        public string NumberPrefixes { get; set; }

        public bool Enabled { get; set; }
    }

    public sealed class SmsMessage
    {
        public SmsMessage()
        {
            Source = string.Empty;
            Destination = string.Empty;
            Text = string.Empty;
            Country = string.Empty;
            Error = string.Empty;
            ClientMessageId = string.Empty;
            TargetSystemId = string.Empty;
            Parts = new List<SmsPart>();
        }

        public Guid Id { get; set; }

        public Guid? AccountId { get; set; }

        public MessageDirection Direction { get; set; }

        public TrafficType TrafficType { get; set; }

        public string Source { get; set; }

        public string Destination { get; set; }

        public string Text { get; set; }

        public string Country { get; set; }

        public MessageStatus Status { get; set; }

        public string Error { get; set; }

        public Guid? ProviderId { get; set; }

        public string ClientMessageId { get; set; }

        public string TargetSystemId { get; set; }

        public bool ReceiptNotified { get; set; }

        public DateTime CreatedAt { get; set; }

        public List<SmsPart> Parts { get; set; }
    }

    public sealed class SmsPart
    {
        public SmsPart()
        {
            ProviderMessageId = string.Empty;
        }

        public long Id { get; set; }

        public Guid MessageId { get; set; }

        public int PartNumber { get; set; }

        public int PartCount { get; set; }

        public string ProviderMessageId { get; set; }

        public MessageStatus Status { get; set; }
    }

    public sealed class ProviderReceiptLink
    {
        public ProviderReceiptLink()
        {
            ProviderMessageId = string.Empty;
        }

        public Guid ProviderId { get; set; }

        public string ProviderMessageId { get; set; }

        public Guid MessageId { get; set; }

        public long PartId { get; set; }
    }

    public sealed class RouteSeed
    {
        public RouteSeed()
        {
            Country = string.Empty;
            ProviderName = string.Empty;
            NumberPrefixes = string.Empty;
        }

        public string Country { get; set; }

        public TrafficType TrafficType { get; set; }

        public string ProviderName { get; set; }

        public int Priority { get; set; }

        public string NumberPrefixes { get; set; }

        public bool Enabled { get; set; }
    }

    public sealed class SeedData
    {
        public SeedData()
        {
            Accounts = new List<Account>();
            Providers = new List<Provider>();
            Routes = new List<RouteSeed>();
        }

        public List<Account> Accounts { get; private set; }

        public List<Provider> Providers { get; private set; }

        public List<RouteSeed> Routes { get; private set; }
    }
}
