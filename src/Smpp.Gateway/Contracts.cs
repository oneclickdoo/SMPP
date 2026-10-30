using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Smpp.Protocol;
using Smpp.Storage;

namespace Smpp.Gateway
{
    public sealed class SendRequest
    {
        public SendRequest()
        {
            Source = string.Empty;
            Destination = string.Empty;
            Text = string.Empty;
            DefaultCountry = string.Empty;
        }

        public Guid? AccountId { get; set; }

        public TrafficType TrafficType { get; set; }

        public string Source { get; set; }

        public string Destination { get; set; }

        public string Text { get; set; }

        public string DefaultCountry { get; set; }

        public byte[] PassthroughUserData { get; set; }

        public byte PassthroughDataCoding { get; set; }

        public byte PassthroughEsmClass { get; set; }
    }

    public sealed class SendOutcome
    {
        public SendOutcome()
        {
            MessageId = string.Empty;
            Error = string.Empty;
        }

        public bool Accepted { get; set; }

        public uint Status { get; set; }

        public string MessageId { get; set; }

        public string Error { get; set; }

        public static SendOutcome Ok(string messageId)
        {
            return new SendOutcome { Accepted = true, Status = SmppStatus.Ok, MessageId = messageId ?? string.Empty };
        }

        public static SendOutcome Reject(uint status, string error)
        {
            return new SendOutcome { Accepted = false, Status = status, Error = error ?? string.Empty };
        }
    }

    public sealed class OutboundSubmit
    {
        public OutboundSubmit()
        {
            Source = string.Empty;
            Destination = string.Empty;
            Text = string.Empty;
            Segments = new List<SmsSegment>();
        }

        public Guid MessageId { get; set; }

        public string Source { get; set; }

        public string Destination { get; set; }

        public string Text { get; set; }

        public bool RequestDeliveryReceipt { get; set; }

        public IList<SmsSegment> Segments { get; set; }
    }

    public sealed class PartSubmitResult
    {
        public PartSubmitResult()
        {
            ProviderMessageId = string.Empty;
            Error = string.Empty;
            Text = string.Empty;
        }

        public int PartNumber { get; set; }

        public int PartCount { get; set; }

        public bool Success { get; set; }

        public string ProviderMessageId { get; set; }

        public string Error { get; set; }

        public string Text { get; set; }
    }

    public sealed class SubmitResult
    {
        public SubmitResult()
        {
            Error = string.Empty;
            Parts = new List<PartSubmitResult>();
        }

        public bool Success { get; set; }

        public string Error { get; set; }

        public uint Status { get; set; }

        public IList<PartSubmitResult> Parts { get; set; }

        public static SubmitResult Ok(IList<PartSubmitResult> parts)
        {
            return new SubmitResult { Success = true, Status = Smpp.Protocol.SmppStatus.Ok, Parts = parts ?? new List<PartSubmitResult>() };
        }

        public static SubmitResult Fail(uint status, string error)
        {
            return new SubmitResult { Success = false, Status = status, Error = error ?? string.Empty };
        }
    }

    public interface IOutboundConnector
    {
        Guid ProviderId { get; }

        bool SupportsSegmentation { get; }

        Task<SubmitResult> SubmitAsync(OutboundSubmit submit, CancellationToken cancellationToken);
    }

    public interface IConnectorRegistry
    {
        IOutboundConnector Find(Guid providerId);
    }

    public sealed class InboundMessage
    {
        public InboundMessage()
        {
            ProviderMessageId = string.Empty;
            Source = string.Empty;
            Destination = string.Empty;
            Text = string.Empty;
            Country = string.Empty;
            TargetSystemId = string.Empty;
        }

        public Guid? ProviderId { get; set; }

        public string ProviderMessageId { get; set; }

        public string Source { get; set; }

        public string Destination { get; set; }

        public string Text { get; set; }

        public string Country { get; set; }

        public string TargetSystemId { get; set; }
    }

    public sealed class InboundReceipt
    {
        public InboundReceipt()
        {
            ProviderMessageId = string.Empty;
            Stat = string.Empty;
            ErrorCode = string.Empty;
        }

        public Guid ProviderId { get; set; }

        public string ProviderMessageId { get; set; }

        public string Stat { get; set; }

        public byte? MessageState { get; set; }

        public string ErrorCode { get; set; }
    }

    public interface IInboundHandler
    {
        Task HandleMobileOriginatedAsync(InboundMessage message, CancellationToken cancellationToken);

        Task HandleDeliveryReceiptAsync(InboundReceipt receipt, CancellationToken cancellationToken);
    }

    public interface IClientNotifier
    {
        Task DeliverReceiptAsync(SmsMessage message, CancellationToken cancellationToken);

        Task DeliverInboundAsync(SmsMessage message, CancellationToken cancellationToken);
    }
}
