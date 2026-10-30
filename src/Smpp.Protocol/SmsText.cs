using System;
using System.Text;

namespace Smpp.Protocol
{
    public static class SmsText
    {
        public static string Decode(byte[] userData, byte dataCoding, byte esmClass)
        {
            var data = userData ?? new byte[0];
            if (data.Length == 0)
            {
                return string.Empty;
            }

            var udhLength = 0;
            if (SmppEsm.HasUserDataHeader(esmClass))
            {
                udhLength = data[0] + 1;
                if (udhLength > data.Length)
                {
                    udhLength = 0;
                }
            }

            if (dataCoding == SmppDataCoding.Ucs2)
            {
                var offset = udhLength;
                if (((data.Length - offset) % 2) != 0 && offset < data.Length)
                {
                    offset++;
                }

                return Encoding.BigEndianUnicode.GetString(data, offset, data.Length - offset);
            }

            if (dataCoding == SmppDataCoding.Latin1)
            {
                return Encoding.GetEncoding(28591).GetString(data, udhLength, data.Length - udhLength);
            }

            if (dataCoding == SmppDataCoding.Ascii)
            {
                return Encoding.ASCII.GetString(data, udhLength, data.Length - udhLength);
            }

            return DecodeGsm(data, udhLength);
        }

        public static void ApplyAddress(string address, byte? configuredTon, byte? configuredNpi, out byte ton, out byte npi)
        {
            if (configuredTon.HasValue)
            {
                ton = configuredTon.Value;
                npi = configuredNpi ?? (byte)0;
                return;
            }

            if (!string.IsNullOrEmpty(address) && IsDigits(address))
            {
                ton = 1;
                npi = 1;
                return;
            }

            ton = 5;
            npi = 0;
        }

        private static string DecodeGsm(byte[] data, int udhLength)
        {
            var skipSeptets = udhLength == 0 ? 0 : (udhLength * 8 + 6) / 7;
            var available = data.Length == 0 ? 0 : (data.Length * 8) / 7;
            var count = available - skipSeptets;
            if (count <= 0)
            {
                return string.Empty;
            }

            if (count > 1 && OctetSize(skipSeptets, count - 1) == data.Length)
            {
                var probe = GsmAlphabet.Unpack(data, count, skipSeptets);
                if (probe[count - 1] == 0)
                {
                    count--;
                }
            }

            return GsmAlphabet.DecodeSeptets(GsmAlphabet.Unpack(data, count, skipSeptets));
        }

        private static int OctetSize(int skipSeptets, int septetCount)
        {
            var bits = (skipSeptets + septetCount) * 7;
            return (bits + 7) / 8;
        }

        private static bool IsDigits(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
