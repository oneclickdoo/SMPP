using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Smpp.Client;
using Smpp.Protocol;
using Smpp.Simulator;
using Xunit;

namespace Smpp.Protocol.Tests
{
    public sealed class PduTests
    {
        [Fact]
        public void EnquireLink_HasTheFixedHeader()
        {
            var bytes = PduCodec.Encode(new EmptyBodyPdu(SmppCommand.EnquireLink) { SequenceNumber = 1 });

            Assert.Equal(new byte[]
            {
                0x00, 0x00, 0x00, 0x10,
                0x00, 0x00, 0x00, 0x15,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x01
            }, bytes);
        }

        [Fact]
        public void BindTransceiver_RoundtripsCredentials()
        {
            var bind = new BindPdu(SmppCommand.BindTransceiver)
            {
                SystemId = "demo",
                Password = "secret",
                SystemType = "smpp",
                InterfaceVersion = 0x34,
                AddrTon = 1,
                AddrNpi = 1,
                AddressRange = "381",
                SequenceNumber = 7
            };

            var decoded = (BindPdu)PduCodec.Decode(PduCodec.Encode(bind));

            Assert.Equal(SmppCommand.BindTransceiver, decoded.Command);
            Assert.Equal("demo", decoded.SystemId);
            Assert.Equal("secret", decoded.Password);
            Assert.Equal("smpp", decoded.SystemType);
            Assert.Equal(0x34, decoded.InterfaceVersion);
            Assert.Equal("381", decoded.AddressRange);
            Assert.Equal(7u, decoded.SequenceNumber);
        }

        [Fact]
        public void SubmitSm_RoundtripsPayloadAndAddresses()
        {
            var submit = new SmPdu(SmppCommand.SubmitSm)
            {
                SourceTon = 5,
                SourceNpi = 0,
                SourceAddress = "INFO",
                DestTon = 1,
                DestNpi = 1,
                DestinationAddress = "38164111222",
                EsmClass = 0,
                RegisteredDelivery = 1,
                DataCoding = SmppDataCoding.Ucs2,
                SequenceNumber = 3
            };
            var text = Encoding.BigEndianUnicode.GetBytes("Čaša");
            PduCodec.ApplyUserData(submit, text);

            var decoded = (SmPdu)PduCodec.Decode(PduCodec.Encode(submit));

            Assert.Equal("INFO", decoded.SourceAddress);
            Assert.Equal("38164111222", decoded.DestinationAddress);
            Assert.Equal(SmppDataCoding.Ucs2, decoded.DataCoding);
            Assert.Equal(text, decoded.GetUserData());
        }

        [Fact]
        public void MessagePayload_IsUsedWhenUserDataExceedsShortMessage()
        {
            var submit = new SmPdu(SmppCommand.SubmitSm)
            {
                SourceAddress = "INFO",
                DestinationAddress = "38164111222"
            };
            var payload = new byte[300];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i % 255);
            }

            PduCodec.ApplyUserData(submit, payload);
            var decoded = (SmPdu)PduCodec.Decode(PduCodec.Encode(submit));

            Assert.Empty(decoded.ShortMessage);
            Assert.Equal(payload, decoded.GetUserData());
        }
    }

    public sealed class TextTests
    {
        [Fact]
        public void GsmAlphabet_Has128BasicCharacters()
        {
            Assert.Equal(128, GsmAlphabet.BasicLength);
        }

        [Fact]
        public void GsmText_Roundtrips()
        {
            Assert.Equal("hello", Roundtrip("hello"));
            Assert.Equal("abcdefg", Roundtrip("abcdefg"));
            Assert.Equal("10€", Roundtrip("10€"));
        }

        [Fact]
        public void LongGsmText_IsSplitAndReassembled()
        {
            var text = new string('A', 200);
            var parts = SmsSegmenter.Segment(text);

            Assert.Equal(2, parts.Count);
            Assert.Equal(SmppEsm.Udhi, parts[0].EsmClass);
            Assert.Equal(text, string.Concat(parts.Select(part => SmsText.Decode(part.UserData, part.DataCoding, part.EsmClass))));
        }

        [Fact]
        public void SerbianLatin_UsesUcs2()
        {
            var parts = SmsSegmenter.Segment("Čaša");

            Assert.Equal(SmppDataCoding.Ucs2, parts[0].DataCoding);
            Assert.Equal("Čaša", SmsText.Decode(parts[0].UserData, parts[0].DataCoding, parts[0].EsmClass));
        }

        [Fact]
        public void LongUcs2Text_IsSplitInto67CharacterParts()
        {
            var text = new string('č', 80);
            var parts = SmsSegmenter.Segment(text);

            Assert.Equal(2, parts.Count);
            Assert.Equal(text, string.Concat(parts.Select(part => SmsText.Decode(part.UserData, part.DataCoding, part.EsmClass))));
        }

        private static string Roundtrip(string text)
        {
            var parts = SmsSegmenter.Segment(text);
            Assert.Single(parts);
            return SmsText.Decode(parts[0].UserData, parts[0].DataCoding, parts[0].EsmClass);
        }
    }

    public sealed class ReceiptTests
    {
        [Fact]
        public void DeliveryReceipt_ParsesIdAndStat()
        {
            var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            var text = DeliveryReceipts.Format("abc123", 1, 1, now, now, "DELIVRD", "000", "Hello");
            var pdu = new SmPdu(SmppCommand.DeliverSm)
            {
                EsmClass = SmppEsm.DeliveryReceipt,
                ShortMessage = Encoding.ASCII.GetBytes(text)
            };
            pdu.Tlvs.Add(Tlv.CString(TlvTag.ReceiptedMessageId, "abc123"));
            pdu.Tlvs.Add(Tlv.Byte(TlvTag.MessageState, 2));

            ParsedReceipt receipt;
            Assert.True(DeliveryReceipts.TryParse(pdu, out receipt));
            Assert.Equal("abc123", receipt.MessageId);
            Assert.Equal("DELIVRD", receipt.Stat);
            Assert.Equal((byte)2, receipt.MessageState);
        }
    }

    public sealed class SessionTests
    {
        [Fact]
        public async Task Client_SubmitsAndReceivesDeliveryReceipt()
        {
            using (var simulator = new SimulatorSmsc { ListenPort = 0, SystemId = "demo", Password = "demo" })
            {
                await simulator.StartAsync();
                using (var client = new SmppClient(NullLogger.Instance))
                {
                    var receipt = new TaskCompletionSource<SmPdu>();
                    await client.ConnectAndBindAsync(new SmppClientOptions
                    {
                        Host = "127.0.0.1",
                        Port = simulator.Port,
                        SystemId = "demo",
                        Password = "demo",
                        EnquireLinkInterval = TimeSpan.FromMinutes(5)
                    }, CancellationToken.None);
                    client.DeliverReceived = (pdu, cancellationToken) =>
                    {
                        receipt.TrySetResult(pdu);
                        return Task.CompletedTask;
                    };

                    var submit = new SmPdu(SmppCommand.SubmitSm)
                    {
                        SourceTon = 5,
                        SourceNpi = 0,
                        SourceAddress = "INFO",
                        DestTon = 1,
                        DestNpi = 1,
                        DestinationAddress = "38164111222",
                        DataCoding = SmppDataCoding.Gsm7,
                        RegisteredDelivery = 1
                    };
                    PduCodec.ApplyUserData(submit, GsmAlphabet.Pack(new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F }, 0));
                    var response = (SmRespPdu)await client.SubmitAsync(submit, CancellationToken.None);
                    var completed = await Task.WhenAny(receipt.Task, Task.Delay(5000));

                    Assert.Equal(SmppStatus.Ok, response.Status);
                    Assert.False(string.IsNullOrEmpty(response.MessageId));
                    Assert.Same(receipt.Task, completed);
                    ParsedReceipt parsed;
                    Assert.True(DeliveryReceipts.TryParse(await receipt.Task, out parsed));
                    Assert.Equal("DELIVRD", parsed.Stat);
                    Assert.Equal(response.MessageId, parsed.MessageId);
                }
            }
        }
    }
}
