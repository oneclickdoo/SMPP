using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Smpp.Protocol
{
    public sealed class ParsedReceipt
    {
        public ParsedReceipt()
        {
            MessageId = string.Empty;
            Stat = string.Empty;
            ErrorCode = string.Empty;
        }

        public string MessageId { get; set; }

        public string Stat { get; set; }

        public string ErrorCode { get; set; }

        public byte? MessageState { get; set; }
    }

    public static class DeliveryReceipts
    {
        private static readonly Regex IdPattern = new Regex(@"\bid:(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StatPattern = new Regex(@"\bstat:([A-Za-z0-9]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ErrPattern = new Regex(@"\berr:(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string Format(string id, int submitted, int delivered, DateTime submittedAtUtc, DateTime doneAtUtc, string stat, string error, string text)
        {
            var snippet = SanitizeSnippet(text);
            return string.Format(
                CultureInfo.InvariantCulture,
                "id:{0} sub:{1:000} dlvrd:{2:000} submit date:{3:yyMMddHHmm} done date:{4:yyMMddHHmm} stat:{5} err:{6} text:{7}",
                id ?? string.Empty,
                submitted,
                delivered,
                submittedAtUtc,
                doneAtUtc,
                string.IsNullOrEmpty(stat) ? "UNKNOWN" : stat,
                string.IsNullOrEmpty(error) ? "000" : error,
                snippet);
        }

        public static bool TryParse(SmPdu pdu, out ParsedReceipt receipt)
        {
            receipt = null;
            if (pdu == null)
            {
                return false;
            }

            var stateTlv = pdu.FindTlv(TlvTag.MessageState);
            var idTlv = pdu.FindTlv(TlvTag.ReceiptedMessageId);
            var looksLikeReceipt = SmppEsm.IsDeliveryReceipt(pdu.EsmClass) || stateTlv != null;
            var text = DecodeLoose(pdu.GetUserData());
            if (!looksLikeReceipt && text.IndexOf("stat:", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var parsed = new ParsedReceipt();
            var idMatch = IdPattern.Match(text);
            var statMatch = StatPattern.Match(text);
            var errMatch = ErrPattern.Match(text);
            if (idMatch.Success)
            {
                parsed.MessageId = idMatch.Groups[1].Value;
            }

            if (statMatch.Success)
            {
                parsed.Stat = statMatch.Groups[1].Value;
            }

            if (errMatch.Success)
            {
                parsed.ErrorCode = errMatch.Groups[1].Value;
            }

            if (idTlv != null)
            {
                var id = idTlv.ReadCString();
                if (!string.IsNullOrEmpty(id))
                {
                    parsed.MessageId = id;
                }
            }

            if (stateTlv != null && stateTlv.Value.Length > 0)
            {
                parsed.MessageState = stateTlv.Value[0];
            }

            if (string.IsNullOrEmpty(parsed.MessageId) && string.IsNullOrEmpty(parsed.Stat) && !parsed.MessageState.HasValue)
            {
                return false;
            }

            receipt = parsed;
            return true;
        }

        private static string DecodeLoose(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return string.Empty;
            }

            var ascii = Encoding.ASCII.GetString(data);
            if (ascii.IndexOf("stat:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ascii;
            }

            var gsm = SmsText.Decode(data, SmppDataCoding.Gsm7, 0x00);
            if (gsm.IndexOf("stat:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return gsm;
            }

            return ascii;
        }

        private static string SanitizeSnippet(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var length = Math.Min(20, text.Length);
            var builder = new StringBuilder(length);
            for (var i = 0; i < length; i++)
            {
                builder.Append(text[i] < 128 ? text[i] : '?');
            }

            return builder.ToString();
        }
    }
}
