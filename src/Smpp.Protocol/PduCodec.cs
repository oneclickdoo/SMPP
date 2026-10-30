using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Smpp.Protocol
{
    public abstract class SmppPdu
    {
        protected SmppPdu(SmppCommand command)
        {
            Command = command;
            Tlvs = new List<Tlv>();
        }

        public SmppCommand Command { get; set; }

        public uint Status { get; set; }

        public uint SequenceNumber { get; set; }

        public List<Tlv> Tlvs { get; private set; }

        public abstract void WriteBody(SmppWriter writer);

        public abstract void ReadBody(SmppReader reader);

        public Tlv FindTlv(ushort tag)
        {
            for (var i = 0; i < Tlvs.Count; i++)
            {
                if (Tlvs[i].Tag == tag)
                {
                    return Tlvs[i];
                }
            }

            return null;
        }
    }

    public sealed class BindPdu : SmppPdu
    {
        public BindPdu(SmppCommand command)
            : base(command)
        {
            SystemId = string.Empty;
            Password = string.Empty;
            SystemType = string.Empty;
            AddressRange = string.Empty;
            InterfaceVersion = 0x34;
        }

        public string SystemId { get; set; }

        public string Password { get; set; }

        public string SystemType { get; set; }

        public byte InterfaceVersion { get; set; }

        public byte AddrTon { get; set; }

        public byte AddrNpi { get; set; }

        public string AddressRange { get; set; }

        public SmppBindRole Role
        {
            get
            {
                if (Command == SmppCommand.BindTransmitter)
                {
                    return SmppBindRole.Transmitter;
                }

                if (Command == SmppCommand.BindReceiver)
                {
                    return SmppBindRole.Receiver;
                }

                return SmppBindRole.Transceiver;
            }
        }

        public override void WriteBody(SmppWriter writer)
        {
            writer.WriteCString(SystemId, 16);
            writer.WriteCString(Password, 9);
            writer.WriteCString(SystemType, 13);
            writer.WriteByte(InterfaceVersion);
            writer.WriteByte(AddrTon);
            writer.WriteByte(AddrNpi);
            writer.WriteCString(AddressRange, 41);
            writer.WriteTlvs(Tlvs);
        }

        public override void ReadBody(SmppReader reader)
        {
            SystemId = reader.ReadCString(16);
            Password = reader.ReadCString(9);
            SystemType = reader.ReadCString(13);
            InterfaceVersion = reader.ReadByte();
            AddrTon = reader.ReadByte();
            AddrNpi = reader.ReadByte();
            AddressRange = reader.ReadCString(41);
            Tlvs.AddRange(reader.ReadTlvs());
        }
    }

    public sealed class BindRespPdu : SmppPdu
    {
        public BindRespPdu(SmppCommand command)
            : base(command)
        {
            SystemId = string.Empty;
        }

        public string SystemId { get; set; }

        public override void WriteBody(SmppWriter writer)
        {
            writer.WriteCString(SystemId, 16);
            writer.WriteTlvs(Tlvs);
        }

        public override void ReadBody(SmppReader reader)
        {
            if (reader.Remaining == 0)
            {
                SystemId = string.Empty;
                return;
            }

            SystemId = reader.ReadCString(16);
            Tlvs.AddRange(reader.ReadTlvs());
        }
    }

    public sealed class SmPdu : SmppPdu
    {
        public SmPdu(SmppCommand command)
            : base(command)
        {
            ServiceType = string.Empty;
            SourceAddress = string.Empty;
            DestinationAddress = string.Empty;
            ScheduleDeliveryTime = string.Empty;
            ValidityPeriod = string.Empty;
            ShortMessage = new byte[0];
        }

        public string ServiceType { get; set; }

        public byte SourceTon { get; set; }

        public byte SourceNpi { get; set; }

        public string SourceAddress { get; set; }

        public byte DestTon { get; set; }

        public byte DestNpi { get; set; }

        public string DestinationAddress { get; set; }

        public byte EsmClass { get; set; }

        public byte ProtocolId { get; set; }

        public byte PriorityFlag { get; set; }

        public string ScheduleDeliveryTime { get; set; }

        public string ValidityPeriod { get; set; }

        public byte RegisteredDelivery { get; set; }

        public byte ReplaceIfPresent { get; set; }

        public byte DataCoding { get; set; }

        public byte DefaultMessageId { get; set; }

        public byte[] ShortMessage { get; set; }

        public byte[] GetUserData()
        {
            var payload = FindTlv(TlvTag.MessagePayload);
            if (payload != null)
            {
                return payload.Value;
            }

            return ShortMessage ?? new byte[0];
        }

        public override void WriteBody(SmppWriter writer)
        {
            var body = ShortMessage ?? new byte[0];
            if (body.Length > 254)
            {
                throw new InvalidDataException("short_message exceeds 254 octets. Use message_payload.");
            }

            writer.WriteCString(ServiceType, 6);
            writer.WriteByte(SourceTon);
            writer.WriteByte(SourceNpi);
            writer.WriteCString(SourceAddress, 21);
            writer.WriteByte(DestTon);
            writer.WriteByte(DestNpi);
            writer.WriteCString(DestinationAddress, 21);
            writer.WriteByte(EsmClass);
            writer.WriteByte(ProtocolId);
            writer.WriteByte(PriorityFlag);
            writer.WriteCString(ScheduleDeliveryTime, 17);
            writer.WriteCString(ValidityPeriod, 17);
            writer.WriteByte(RegisteredDelivery);
            writer.WriteByte(ReplaceIfPresent);
            writer.WriteByte(DataCoding);
            writer.WriteByte(DefaultMessageId);
            writer.WriteByte((byte)body.Length);
            writer.WriteBytes(body);
            writer.WriteTlvs(Tlvs);
        }

        public override void ReadBody(SmppReader reader)
        {
            ServiceType = reader.ReadCString(6);
            SourceTon = reader.ReadByte();
            SourceNpi = reader.ReadByte();
            SourceAddress = reader.ReadCString(21);
            DestTon = reader.ReadByte();
            DestNpi = reader.ReadByte();
            DestinationAddress = reader.ReadCString(21);
            EsmClass = reader.ReadByte();
            ProtocolId = reader.ReadByte();
            PriorityFlag = reader.ReadByte();
            ScheduleDeliveryTime = reader.ReadCString(17);
            ValidityPeriod = reader.ReadCString(17);
            RegisteredDelivery = reader.ReadByte();
            ReplaceIfPresent = reader.ReadByte();
            DataCoding = reader.ReadByte();
            DefaultMessageId = reader.ReadByte();
            var length = reader.ReadByte();
            ShortMessage = reader.ReadBytes(length);
            Tlvs.AddRange(reader.ReadTlvs());
        }
    }

    public sealed class SmRespPdu : SmppPdu
    {
        public SmRespPdu(SmppCommand command)
            : base(command)
        {
            MessageId = string.Empty;
        }

        public string MessageId { get; set; }

        public override void WriteBody(SmppWriter writer)
        {
            writer.WriteCString(MessageId, 65);
            writer.WriteTlvs(Tlvs);
        }

        public override void ReadBody(SmppReader reader)
        {
            if (reader.Remaining == 0)
            {
                MessageId = string.Empty;
                return;
            }

            MessageId = reader.ReadCString(65);
            Tlvs.AddRange(reader.ReadTlvs());
        }
    }

    public sealed class EmptyBodyPdu : SmppPdu
    {
        public EmptyBodyPdu(SmppCommand command)
            : base(command)
        {
        }

        public override void WriteBody(SmppWriter writer)
        {
            writer.WriteTlvs(Tlvs);
        }

        public override void ReadBody(SmppReader reader)
        {
            Tlvs.AddRange(reader.ReadTlvs());
        }
    }

    public sealed class RawPdu : SmppPdu
    {
        public RawPdu(SmppCommand command)
            : base(command)
        {
            Body = new byte[0];
        }

        public byte[] Body { get; set; }

        public override void WriteBody(SmppWriter writer)
        {
            writer.WriteBytes(Body);
        }

        public override void ReadBody(SmppReader reader)
        {
            Body = reader.ReadBytes(reader.Remaining);
        }
    }

    public sealed class SmppWriter
    {
        private readonly MemoryStream _stream = new MemoryStream();

        public void WriteByte(byte value)
        {
            _stream.WriteByte(value);
        }

        public void WriteUInt16(ushort value)
        {
            var bytes = new byte[2];
            Endian.WriteUInt16(bytes, 0, value);
            _stream.Write(bytes, 0, 2);
        }

        public void WriteUInt32(uint value)
        {
            var bytes = new byte[4];
            Endian.WriteUInt32(bytes, 0, value);
            _stream.Write(bytes, 0, 4);
        }

        public void WriteCString(string value, int maxIncludingNull)
        {
            var text = value ?? string.Empty;
            var bytes = Encoding.ASCII.GetBytes(text);
            if (bytes.Length + 1 > maxIncludingNull)
            {
                throw new ArgumentException("SMPP C-octet string is too long.");
            }

            if (bytes.Length > 0)
            {
                _stream.Write(bytes, 0, bytes.Length);
            }

            _stream.WriteByte(0);
        }

        public void WriteBytes(byte[] value)
        {
            if (value == null || value.Length == 0)
            {
                return;
            }

            _stream.Write(value, 0, value.Length);
        }

        public void WriteTlvs(IList<Tlv> tlvs)
        {
            if (tlvs == null)
            {
                return;
            }

            for (var i = 0; i < tlvs.Count; i++)
            {
                var data = tlvs[i].Value ?? new byte[0];
                WriteUInt16(tlvs[i].Tag);
                WriteUInt16((ushort)data.Length);
                WriteBytes(data);
            }
        }

        public byte[] ToArray()
        {
            return _stream.ToArray();
        }
    }

    public sealed class SmppReader
    {
        private readonly byte[] _data;
        private int _offset;

        public SmppReader(byte[] data)
        {
            _data = data ?? new byte[0];
        }

        public int Remaining
        {
            get { return _data.Length - _offset; }
        }

        public byte ReadByte()
        {
            Ensure(1);
            return _data[_offset++];
        }

        public ushort ReadUInt16()
        {
            Ensure(2);
            var value = Endian.ReadUInt16(_data, _offset);
            _offset += 2;
            return value;
        }

        public string ReadCString(int maxIncludingNull)
        {
            var start = _offset;
            var limit = Math.Min(_data.Length, start + maxIncludingNull);
            while (_offset < limit && _data[_offset] != 0)
            {
                _offset++;
            }

            if (_offset >= _data.Length || _data[_offset] != 0)
            {
                throw new InvalidDataException("SMPP C-octet string is not terminated.");
            }

            var value = Encoding.ASCII.GetString(_data, start, _offset - start);
            _offset++;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            if (count == 0)
            {
                return new byte[0];
            }

            Ensure(count);
            var value = new byte[count];
            Buffer.BlockCopy(_data, _offset, value, 0, count);
            _offset += count;
            return value;
        }

        public List<Tlv> ReadTlvs()
        {
            var list = new List<Tlv>();
            while (Remaining > 0)
            {
                if (Remaining < 4)
                {
                    throw new InvalidDataException("Truncated SMPP TLV.");
                }

                var tag = ReadUInt16();
                var length = ReadUInt16();
                list.Add(new Tlv(tag, ReadBytes(length)));
            }

            return list;
        }

        private void Ensure(int count)
        {
            if (Remaining < count)
            {
                throw new InvalidDataException("Unexpected end of SMPP PDU.");
            }
        }
    }

    public static class PduCodec
    {
        public const int MaxPduLength = 131072;

        public static byte[] Encode(SmppPdu pdu)
        {
            if (pdu == null)
            {
                throw new ArgumentNullException("pdu");
            }

            var bodyWriter = new SmppWriter();
            pdu.WriteBody(bodyWriter);
            var body = bodyWriter.ToArray();
            var packet = new byte[16 + body.Length];
            WriteUInt32(packet, 0, (uint)packet.Length);
            WriteUInt32(packet, 4, (uint)pdu.Command);
            WriteUInt32(packet, 8, pdu.Status);
            WriteUInt32(packet, 12, pdu.SequenceNumber);
            Buffer.BlockCopy(body, 0, packet, 16, body.Length);
            return packet;
        }

        public static SmppPdu Decode(byte[] data)
        {
            if (data == null || data.Length < 16)
            {
                throw new InvalidDataException("SMPP PDU is shorter than the header.");
            }

            var length = ReadUInt32(data, 0);
            if (length != data.Length || length > MaxPduLength)
            {
                throw new InvalidDataException("SMPP command length does not match the buffer.");
            }

            var command = (SmppCommand)ReadUInt32(data, 4);
            var status = ReadUInt32(data, 8);
            var sequence = ReadUInt32(data, 12);
            var body = new byte[data.Length - 16];
            Buffer.BlockCopy(data, 16, body, 0, body.Length);
            var pdu = Create(command);
            pdu.Status = status;
            pdu.SequenceNumber = sequence;
            pdu.ReadBody(new SmppReader(body));
            return pdu;
        }

        public static void ApplyUserData(SmPdu pdu, byte[] userData)
        {
            var data = userData ?? new byte[0];
            if (data.Length > 254)
            {
                pdu.ShortMessage = new byte[0];
                pdu.Tlvs.Add(new Tlv(TlvTag.MessagePayload, data));
                return;
            }

            pdu.ShortMessage = data;
        }

        private static SmppPdu Create(SmppCommand command)
        {
            switch (command)
            {
                case SmppCommand.BindReceiver:
                case SmppCommand.BindTransmitter:
                case SmppCommand.BindTransceiver:
                    return new BindPdu(command);
                case SmppCommand.BindReceiverResp:
                case SmppCommand.BindTransmitterResp:
                case SmppCommand.BindTransceiverResp:
                    return new BindRespPdu(command);
                case SmppCommand.SubmitSm:
                case SmppCommand.DeliverSm:
                    return new SmPdu(command);
                case SmppCommand.SubmitSmResp:
                case SmppCommand.DeliverSmResp:
                    return new SmRespPdu(command);
                case SmppCommand.EnquireLink:
                case SmppCommand.EnquireLinkResp:
                case SmppCommand.Unbind:
                case SmppCommand.UnbindResp:
                case SmppCommand.GenericNack:
                    return new EmptyBodyPdu(command);
                default:
                    return new RawPdu(command);
            }
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            Endian.WriteUInt32(buffer, offset, value);
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return Endian.ReadUInt32(buffer, offset);
        }
    }

    internal static class Endian
    {
        public static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        public static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        public static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        public static uint ReadUInt32(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24)
                | ((uint)buffer[offset + 1] << 16)
                | ((uint)buffer[offset + 2] << 8)
                | buffer[offset + 3];
        }
    }
}
