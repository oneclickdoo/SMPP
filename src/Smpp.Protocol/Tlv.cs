using System;

namespace Smpp.Protocol
{
    public sealed class Tlv
    {
        public Tlv(ushort tag, byte[] value)
        {
            Tag = tag;
            Value = value ?? new byte[0];
        }

        public ushort Tag { get; private set; }

        public byte[] Value { get; private set; }

        public static Tlv Byte(ushort tag, byte value)
        {
            return new Tlv(tag, new[] { value });
        }

        public static Tlv CString(ushort tag, string value)
        {
            var text = value ?? string.Empty;
            var bytes = new byte[text.Length + 1];
            for (var i = 0; i < text.Length; i++)
            {
                bytes[i] = text[i] <= 127 ? (byte)text[i] : (byte)'?';
            }

            return new Tlv(tag, bytes);
        }

        public string ReadCString()
        {
            var length = Value.Length;
            if (length > 0 && Value[length - 1] == 0)
            {
                length--;
            }

            return System.Text.Encoding.ASCII.GetString(Value, 0, length);
        }
    }

    public static class SecureCompare
    {
        public static bool FixedEquals(string left, string right)
        {
            var a = System.Text.Encoding.UTF8.GetBytes(left ?? string.Empty);
            var b = System.Text.Encoding.UTF8.GetBytes(right ?? string.Empty);
            var max = Math.Max(a.Length, b.Length);
            var diff = a.Length ^ b.Length;
            for (var i = 0; i < max; i++)
            {
                var av = i < a.Length ? a[i] : (byte)0;
                var bv = i < b.Length ? b[i] : (byte)0;
                diff |= av ^ bv;
            }

            return diff == 0;
        }
    }
}
