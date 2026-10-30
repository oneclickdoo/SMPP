namespace Smpp.Protocol
{
    public static class SmppStatus
    {
        public const uint Ok = 0x00000000;
        public const uint InvalidMessageLength = 0x00000001;
        public const uint InvalidCommandLength = 0x00000002;
        public const uint InvalidCommandId = 0x00000003;
        public const uint InvalidBindStatus = 0x00000004;
        public const uint AlreadyBound = 0x00000005;
        public const uint SystemError = 0x00000008;
        public const uint InvalidSourceAddress = 0x0000000A;
        public const uint InvalidDestination = 0x0000000B;
        public const uint InvalidMessageId = 0x0000000C;
        public const uint BindFailed = 0x0000000D;
        public const uint InvalidPassword = 0x0000000E;
        public const uint InvalidSystemId = 0x0000000F;
        public const uint MessageQueueFull = 0x00000014;
        public const uint SubmitFailed = 0x00000045;
        public const uint Throttled = 0x00000058;
    }

    public static class SmppEsm
    {
        public const byte DeliveryReceipt = 0x04;
        public const byte Udhi = 0x40;

        public static bool IsDeliveryReceipt(byte esmClass)
        {
            return (esmClass & 0x3C) == DeliveryReceipt;
        }

        public static bool HasUserDataHeader(byte esmClass)
        {
            return (esmClass & Udhi) != 0;
        }
    }

    public static class SmppDataCoding
    {
        public const byte Gsm7 = 0x00;
        public const byte Ascii = 0x01;
        public const byte Latin1 = 0x03;
        public const byte Ucs2 = 0x08;
    }

    public static class TlvTag
    {
        public const ushort ReceiptedMessageId = 0x001E;
        public const ushort ScInterfaceVersion = 0x0210;
        public const ushort MessagePayload = 0x0424;
        public const ushort MessageState = 0x0427;
    }
}
