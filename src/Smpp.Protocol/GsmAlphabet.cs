using System;
using System.Collections.Generic;
using System.Text;

namespace Smpp.Protocol
{
    public static class GsmAlphabet
    {
        private const string Basic =
            "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ\u001bÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

        private static readonly Dictionary<char, byte> Extension = new Dictionary<char, byte>
        {
            { '\f', 0x0A },
            { '^', 0x14 },
            { '{', 0x28 },
            { '}', 0x29 },
            { '\\', 0x2F },
            { '[', 0x3C },
            { '~', 0x3D },
            { ']', 0x3E },
            { '|', 0x40 },
            { '€', 0x65 }
        };

        private static readonly Dictionary<byte, char> ExtensionReverse = BuildReverse();

        public static int BasicLength
        {
            get { return Basic.Length; }
        }

        public static bool TryEncode(string text, out byte[] septets)
        {
            var source = text ?? string.Empty;
            var list = new List<byte>(source.Length);
            for (var i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                if (ch == '\u001b')
                {
                    septets = null;
                    return false;
                }

                var basic = Basic.IndexOf(ch);
                if (basic >= 0)
                {
                    list.Add((byte)basic);
                    continue;
                }

                byte extended;
                if (Extension.TryGetValue(ch, out extended))
                {
                    list.Add(0x1B);
                    list.Add(extended);
                    continue;
                }

                septets = null;
                return false;
            }

            septets = list.ToArray();
            return true;
        }

        public static string DecodeSeptets(byte[] septets)
        {
            if (septets == null || septets.Length == 0)
            {
                return string.Empty;
            }

            var text = new StringBuilder(septets.Length);
            for (var i = 0; i < septets.Length; i++)
            {
                var value = septets[i];
                if (value == 0x1B)
                {
                    if (i + 1 >= septets.Length)
                    {
                        break;
                    }

                    i++;
                    char extended;
                    if (ExtensionReverse.TryGetValue(septets[i], out extended))
                    {
                        text.Append(extended);
                    }

                    continue;
                }

                if (value < Basic.Length)
                {
                    text.Append(Basic[value]);
                }
            }

            return text.ToString();
        }

        public static byte[] Pack(byte[] septets, int bitOffset)
        {
            var count = septets == null ? 0 : septets.Length;
            var totalBits = bitOffset + (count * 7);
            var octets = new byte[(totalBits + 7) / 8];
            for (var i = 0; i < count; i++)
            {
                var pos = bitOffset + (i * 7);
                var index = pos / 8;
                var shift = pos % 8;
                var value = septets[i] & 0x7F;
                octets[index] |= (byte)(value << shift);
                if (shift > 1)
                {
                    octets[index + 1] |= (byte)(value >> (8 - shift));
                }
            }

            return octets;
        }

        public static byte[] PackWithUdh(byte[] udh, byte[] septets)
        {
            var header = udh ?? new byte[0];
            var skipSeptets = (header.Length * 8 + 6) / 7;
            var packed = Pack(septets, skipSeptets * 7);
            Buffer.BlockCopy(header, 0, packed, 0, header.Length);
            return packed;
        }

        public static byte[] Unpack(byte[] octets, int septetCount, int skipSeptets)
        {
            var result = new byte[septetCount];
            var source = octets ?? new byte[0];
            var bitOffset = skipSeptets * 7;
            for (var i = 0; i < septetCount; i++)
            {
                var pos = bitOffset + (i * 7);
                var index = pos / 8;
                var shift = pos % 8;
                if (index >= source.Length)
                {
                    break;
                }

                var value = source[index] >> shift;
                if (shift > 1 && index + 1 < source.Length)
                {
                    value |= source[index + 1] << (8 - shift);
                }

                result[i] = (byte)(value & 0x7F);
            }

            return result;
        }

        private static Dictionary<byte, char> BuildReverse()
        {
            var map = new Dictionary<byte, char>();
            foreach (var pair in Extension)
            {
                map[pair.Value] = pair.Key;
            }

            return map;
        }
    }

    public sealed class SmsSegment
    {
        public SmsSegment()
        {
            UserData = new byte[0];
            Text = string.Empty;
        }

        public byte[] UserData { get; set; }

        public byte DataCoding { get; set; }

        public byte EsmClass { get; set; }

        public int PartNumber { get; set; }

        public int PartCount { get; set; }

        public string Text { get; set; }

        public bool IsPassthrough { get; set; }
    }

    public static class SmsSegmenter
    {
        private static int _reference;

        public static IList<SmsSegment> Segment(string text)
        {
            var source = text ?? string.Empty;
            byte[] septets;
            if (GsmAlphabet.TryEncode(source, out septets))
            {
                return SegmentGsm(source, septets);
            }

            return SegmentUcs2(source);
        }

        public static SmsSegment Passthrough(byte[] userData, byte dataCoding, byte esmClass, string text)
        {
            return new SmsSegment
            {
                UserData = userData ?? new byte[0],
                DataCoding = dataCoding,
                EsmClass = esmClass,
                PartNumber = 1,
                PartCount = 1,
                Text = text ?? string.Empty,
                IsPassthrough = true
            };
        }

        private static IList<SmsSegment> SegmentGsm(string text, byte[] septets)
        {
            if (septets.Length <= 160)
            {
                return new[]
                {
                    new SmsSegment
                    {
                        UserData = GsmAlphabet.Pack(septets, 0),
                        DataCoding = SmppDataCoding.Gsm7,
                        EsmClass = 0x00,
                        PartNumber = 1,
                        PartCount = 1,
                        Text = text
                    }
                };
            }

            var total = (septets.Length + 152) / 153;
            var reference = NextReference();
            var parts = new List<SmsSegment>(total);
            for (var i = 0; i < total; i++)
            {
                var offset = i * 153;
                var count = Math.Min(153, septets.Length - offset);
                var slice = new byte[count];
                Buffer.BlockCopy(septets, offset, slice, 0, count);
                var udh = new[] { (byte)0x05, (byte)0x00, (byte)0x03, reference, (byte)total, (byte)(i + 1) };
                parts.Add(new SmsSegment
                {
                    UserData = GsmAlphabet.PackWithUdh(udh, slice),
                    DataCoding = SmppDataCoding.Gsm7,
                    EsmClass = SmppEsm.Udhi,
                    PartNumber = i + 1,
                    PartCount = total,
                    Text = GsmAlphabet.DecodeSeptets(slice)
                });
            }

            return parts;
        }

        private static IList<SmsSegment> SegmentUcs2(string text)
        {
            if (text.Length <= 70)
            {
                return new[]
                {
                    new SmsSegment
                    {
                        UserData = Encoding.BigEndianUnicode.GetBytes(text),
                        DataCoding = SmppDataCoding.Ucs2,
                        EsmClass = 0x00,
                        PartNumber = 1,
                        PartCount = 1,
                        Text = text
                    }
                };
            }

            var total = (text.Length + 66) / 67;
            var reference = NextReference();
            var parts = new List<SmsSegment>(total);
            for (var i = 0; i < total; i++)
            {
                var offset = i * 67;
                var count = Math.Min(67, text.Length - offset);
                var slice = text.Substring(offset, count);
                var encoded = Encoding.BigEndianUnicode.GetBytes(slice);
                var userData = new byte[6 + encoded.Length];
                userData[0] = 0x05;
                userData[1] = 0x00;
                userData[2] = 0x03;
                userData[3] = reference;
                userData[4] = (byte)total;
                userData[5] = (byte)(i + 1);
                Buffer.BlockCopy(encoded, 0, userData, 6, encoded.Length);
                parts.Add(new SmsSegment
                {
                    UserData = userData,
                    DataCoding = SmppDataCoding.Ucs2,
                    EsmClass = SmppEsm.Udhi,
                    PartNumber = i + 1,
                    PartCount = total,
                    Text = slice
                });
            }

            return parts;
        }

        private static byte NextReference()
        {
            var value = System.Threading.Interlocked.Increment(ref _reference) & 0xFF;
            return value == 0 ? (byte)1 : (byte)value;
        }
    }
}
