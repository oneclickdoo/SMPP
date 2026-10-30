namespace Smpp.Protocol
{
    public enum SmppCommand : uint
    {
        GenericNack = 0x80000000,
        BindReceiver = 0x00000001,
        BindReceiverResp = 0x80000001,
        BindTransmitter = 0x00000002,
        BindTransmitterResp = 0x80000002,
        SubmitSm = 0x00000004,
        SubmitSmResp = 0x80000004,
        DeliverSm = 0x00000005,
        DeliverSmResp = 0x80000005,
        Unbind = 0x00000006,
        UnbindResp = 0x80000006,
        BindTransceiver = 0x00000009,
        BindTransceiverResp = 0x80000009,
        EnquireLink = 0x00000015,
        EnquireLinkResp = 0x80000015
    }

    public static class SmppCommandInfo
    {
        public static bool IsResponse(SmppCommand command)
        {
            return ((uint)command & 0x80000000) != 0;
        }

        public static bool IsBind(SmppCommand command)
        {
            return command == SmppCommand.BindReceiver
                || command == SmppCommand.BindTransmitter
                || command == SmppCommand.BindTransceiver;
        }
    }

    public enum SmppBindRole
    {
        Receiver = 1,
        Transmitter = 2,
        Transceiver = 3
    }
}
