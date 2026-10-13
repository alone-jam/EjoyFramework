using System;
using EjoyFramework.GamePlay.Netcode.Messages;
using EjoyFramework.GamePlay.Netcode.Session;
using EjoyFramework.GamePlay.Tests.Netcode.Messages;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests.Netcode.Session
{
    public class TickInputFrameCodecTests
    {
        private static TickInputFrameHeader Header(uint bits = 13, uint tick = 0x55667788)
            => new TickInputFrameHeader(0x0102030405060708UL, 0x11223344, tick, 0x1234, 0x5678, bits);
        private static TickInputFrameContext Context()
            => new TickInputFrameContext(0x0102030405060708UL, 0x11223344, 0x1234, 0x5678);
        private static byte[] Golden() => new byte[]
        {
            0x45, 0x4A, 0x54, 0x49, 0x01, 0x00, 0x20, 0x00,
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x44, 0x33, 0x22, 0x11, 0x88, 0x77, 0x66, 0x55,
            0x34, 0x12, 0x78, 0x56, 0x0D, 0x00, 0x00, 0x00,
            0x9D, 0x16 // buttons=5 (3 bits), signed acceleration=211 biased by 512 (10 bits).
        };

        private static void RejectDecode(byte[] frame, TickInputFrameError expected,
            TickInputFrameContext context, int maximum = TickInputFrameCodec.DefaultMaxPayloadBits)
        {
            var before = (byte[])frame.Clone();
            Assert.That(TickInputFrameCodec.TryDecode(frame, in context, out var header, out var payload,
                out var error, maximum), Is.False);
            Assert.That(error, Is.EqualTo(expected));
            Assert.That(header, Is.EqualTo(default(TickInputFrameHeader)));
            Assert.That(payload.IsEmpty, Is.True);
            Assert.That(frame, Is.EqualTo(before));
        }

        private static void RejectEncode(TickInputFrameHeader header, byte[] payload, TickInputFrameError expected,
            int capacity = 64, int maximum = TickInputFrameCodec.DefaultMaxPayloadBits)
        {
            var destination = new byte[capacity];
            Array.Fill(destination, (byte)0xCC);
            var before = (byte[])destination.Clone();
            Assert.That(TickInputFrameCodec.TryEncode(in header, payload, destination, out int written,
                out var error, maximum), Is.False);
            Assert.That(error, Is.EqualTo(expected));
            Assert.That(written, Is.Zero);
            Assert.That(destination, Is.EqualTo(before));
        }

        [Test]
        public void EncodeMatchesHandWrittenLittleEndianGoldenBytesAndDoesNotTouchSuffix()
        {
            var header = Header();
            var destination = new byte[40];
            Array.Fill(destination, (byte)0xCC);
            Assert.That(TickInputFrameCodec.TryEncode(in header, new byte[] { 0x9D, 0x16 }, destination,
                out int written, out var error), Is.True);
            Assert.That(error, Is.EqualTo(TickInputFrameError.None));
            Assert.That(written, Is.EqualTo(34));
            Assert.That(destination.AsSpan(0, written).SequenceEqual(Golden()), Is.True);
            for (int i = written; i < destination.Length; i++) Assert.That(destination[i], Is.EqualTo(0xCC));
        }

        [Test]
        public void DecodeGoldenHeaderAndBorrowedExactPayloadWithoutDependingOnEncoder()
        {
            var frame = Golden();
            var context = Context();
            Assert.That(TickInputFrameCodec.TryDecode(frame, in context, out var header, out var payload, out var error), Is.True);
            Assert.That(error, Is.EqualTo(TickInputFrameError.None));
            Assert.That(header.SessionEpoch, Is.EqualTo(0x0102030405060708UL));
            Assert.That(header.WorldEpoch, Is.EqualTo(0x11223344));
            Assert.That(header.CommandTick, Is.EqualTo(0x55667788));
            Assert.That(header.CommandSchemaId, Is.EqualTo(0x1234));
            Assert.That(header.CodecRevision, Is.EqualTo(0x5678));
            Assert.That(header.PayloadBitLength, Is.EqualTo(13));
            Assert.That(payload.SequenceEqual(new byte[] { 0x9D, 0x16 }), Is.True);
            frame[32] = 0x55;
            Assert.That(payload[0], Is.EqualTo(0x55)); // Borrowed view, not a hidden allocation/copy.
        }

        [TestCase(0, 0x44, TickInputFrameError.InvalidMagic)]
        [TestCase(4, 2, TickInputFrameError.UnsupportedVersion)]
        [TestCase(5, 1, TickInputFrameError.UnsupportedVersion)]
        [TestCase(6, 33, TickInputFrameError.InvalidHeaderSize)]
        [TestCase(7, 1, TickInputFrameError.InvalidHeaderSize)]
        [TestCase(8, 9, TickInputFrameError.SessionEpochMismatch)]
        [TestCase(15, 2, TickInputFrameError.SessionEpochMismatch)]
        [TestCase(16, 0x45, TickInputFrameError.WorldEpochMismatch)]
        [TestCase(24, 0x35, TickInputFrameError.CommandSchemaMismatch)]
        [TestCase(26, 0x79, TickInputFrameError.CodecRevisionMismatch)]
        [TestCase(33, 0xF6, TickInputFrameError.NonCanonicalPadding)]
        public void HeaderContextAndPaddingFailuresPublishNoOutputs(int offset, byte value, TickInputFrameError error)
        {
            var frame = Golden();
            frame[offset] = value;
            RejectDecode(frame, error, Context());
        }

        [TestCase(8, 8)]
        [TestCase(16, 4)]
        [TestCase(20, 4)]
        [TestCase(24, 2)]
        [TestCase(26, 2)]
        public void ZeroHeaderIdentityFieldsAreNotWildcards(int offset, int count)
        {
            var frame = Golden();
            Array.Clear(frame, offset, count);
            RejectDecode(frame, TickInputFrameError.InvalidHeader, Context());
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EveryExpectedContextFieldMustBeNonzero(int zeroField)
        {
            var context = new TickInputFrameContext(zeroField == 0 ? 0UL : 0x0102030405060708UL,
                zeroField == 1 ? 0u : 0x11223344u, (ushort)(zeroField == 2 ? 0 : 0x1234),
                (ushort)(zeroField == 3 ? 0 : 0x5678));
            RejectDecode(Golden(), TickInputFrameError.InvalidContext, context);
            RejectDecode(Golden(), TickInputFrameError.InvalidContext, default);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void EncoderRejectsEveryZeroHeaderIdentityBeforeDestinationWrites(int zeroField)
        {
            var header = new TickInputFrameHeader(zeroField == 0 ? 0UL : 1UL, zeroField == 1 ? 0u : 1u,
                zeroField == 2 ? 0u : 1u, (ushort)(zeroField == 3 ? 0 : 1), (ushort)(zeroField == 4 ? 0 : 1), 0);
            RejectEncode(header, Array.Empty<byte>(), TickInputFrameError.InvalidHeader);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(31)]
        [TestCase(32)]
        [TestCase(33)]
        [TestCase(35)]
        public void TruncatedOrTrailingFrameIsRejectedRatherThanPartiallyConsumed(int length)
        {
            var frame = new byte[length];
            Array.Copy(Golden(), frame, Math.Min(length, 34));
            RejectDecode(frame, length < 32 ? TickInputFrameError.HeaderTruncated : TickInputFrameError.PayloadLengthMismatch, Context());
        }

        [Test]
        public void HostileUintLengthAndConfiguredLimitsAreSymmetricAndBounded()
        {
            var frame = Golden();
            for (int i = 28; i < 32; i++) frame[i] = 0xFF;
            RejectDecode(frame, TickInputFrameError.PayloadTooLarge, Context());
            RejectEncode(Header(uint.MaxValue), Array.Empty<byte>(), TickInputFrameError.PayloadTooLarge);
            RejectDecode(Golden(), TickInputFrameError.PayloadTooLarge, Context(), maximum: 12);
            RejectEncode(Header(), new byte[] { 0x9D, 0x16 }, TickInputFrameError.PayloadTooLarge, maximum: 12);
            foreach (int maximum in new[] { -1, int.MaxValue, TickInputFrameCodec.MaximumPayloadBits + 1 })
            {
                RejectDecode(Golden(), TickInputFrameError.InvalidPayloadLimit, Context(), maximum);
                RejectEncode(Header(), new byte[] { 0x9D, 0x16 }, TickInputFrameError.InvalidPayloadLimit, maximum: maximum);
            }
        }

        [Test]
        public void EncoderRequiresExactCanonicalPayloadAndSufficientDestination()
        {
            RejectEncode(Header(), new byte[] { 0x9D }, TickInputFrameError.PayloadLengthMismatch);
            RejectEncode(Header(), new byte[] { 0x9D, 0x16, 0 }, TickInputFrameError.PayloadLengthMismatch);
            RejectEncode(Header(), new byte[] { 0x9D, 0xF6 }, TickInputFrameError.NonCanonicalPadding);
            RejectEncode(Header(), new byte[] { 0x9D, 0x16 }, TickInputFrameError.DestinationTooSmall, capacity: 33);
            RejectEncode(Header(0), new byte[] { 0 }, TickInputFrameError.PayloadLengthMismatch);
            RejectEncode(Header(0), Array.Empty<byte>(), TickInputFrameError.DestinationTooSmall, capacity: 0);
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(7u)]
        [TestCase(8u)]
        [TestCase(9u)]
        [TestCase(8192u)]
        public void ExactByteCeilingAndZeroPayloadBoundaryRoundTrip(uint bits)
        {
            var payload = new byte[(int)((bits + 7) / 8)];
            var frame = new byte[32 + payload.Length];
            var header = Header(bits, uint.MaxValue);
            var context = Context();
            int maximum = bits == 0 ? 0 : TickInputFrameCodec.DefaultMaxPayloadBits;
            Assert.That(TickInputFrameCodec.TryEncode(in header, payload, frame, out int written, out var error, maximum), Is.True);
            Assert.That(written, Is.EqualTo(frame.Length));
            Assert.That(TickInputFrameCodec.TryDecode(frame, in context, out var decoded, out var bytes, out error, maximum), Is.True);
            Assert.That(decoded.CommandTick, Is.EqualTo(uint.MaxValue));
            Assert.That(decoded.PayloadBitLength, Is.EqualTo(bits));
            Assert.That(bytes.Length, Is.EqualTo(payload.Length));
            Assert.That(error, Is.EqualTo(TickInputFrameError.None));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        public void EveryPartialByteWidthRejectsItsFirstUnusedHighBit(int bits)
        {
            byte invalid = (byte)(1 << bits);
            RejectEncode(Header((uint)bits), new[] { invalid }, TickInputFrameError.NonCanonicalPadding);
            var frame = new byte[33];
            Array.Copy(Golden(), frame, 32);
            frame[28] = (byte)bits;
            frame[32] = invalid;
            RejectDecode(frame, TickInputFrameError.NonCanonicalPadding, Context());
        }

        [Test]
        public void UnsignedIdentityMaximaAndByteAlignedNonzeroPayloadAreValid()
        {
            var header = new TickInputFrameHeader(ulong.MaxValue, uint.MaxValue, uint.MaxValue,
                ushort.MaxValue, ushort.MaxValue, 8);
            var context = new TickInputFrameContext(ulong.MaxValue, uint.MaxValue, ushort.MaxValue, ushort.MaxValue);
            var frame = new byte[33];
            Assert.That(TickInputFrameCodec.TryEncode(in header, new byte[] { 255 }, frame, out _, out _), Is.True);
            Assert.That(TickInputFrameCodec.TryDecode(frame, in context, out var decoded, out var payload, out _), Is.True);
            Assert.That(decoded, Is.EqualTo(header));
            Assert.That(payload[0], Is.EqualTo(255));
        }

        [Test]
        public void DefaultSourceIsTruncatedAndPublishesOnlyDefaultOutputs()
        {
            var context = Context();
            Assert.That(TickInputFrameCodec.TryDecode(default, in context, out var header, out var payload, out var error), Is.False);
            Assert.That(error, Is.EqualTo(TickInputFrameError.HeaderTruncated));
            Assert.That(header, Is.EqualTo(default(TickInputFrameHeader)));
            Assert.That(payload.IsEmpty, Is.True);
        }

        [Test]
        public void ConfiguredAbsoluteMaximumIsAcceptedButDefaultLimitStillRejectsIt()
        {
            var payload = new byte[TickInputFrameCodec.MaximumPayloadBits / 8];
            var frame = new byte[32 + payload.Length];
            var header = Header(TickInputFrameCodec.MaximumPayloadBits);
            var context = Context();
            Assert.That(TickInputFrameCodec.TryEncode(in header, payload, frame, out _, out _,
                TickInputFrameCodec.MaximumPayloadBits), Is.True);
            Assert.That(TickInputFrameCodec.TryDecode(frame, in context, out _, out var decoded, out _,
                TickInputFrameCodec.MaximumPayloadBits), Is.True);
            Assert.That(decoded.Length, Is.EqualTo(payload.Length));
            RejectDecode(frame, TickInputFrameError.PayloadTooLarge, context);
        }

        [TestCase(0, 0)]
        [TestCase(31, 0)]
        [TestCase(32, 0)]
        [TestCase(33, 0)]
        [TestCase(0, 4)]
        [TestCase(35, 4)]
        [TestCase(38, 4)]
        public void PayloadMayOverlapHeaderOrPayloadDestinationInEitherDirection(int payloadOffset, int frameOffset)
        {
            var buffer = new byte[80];
            Array.Fill(buffer, (byte)0xCC);
            buffer[payloadOffset] = 0x9D;
            buffer[payloadOffset + 1] = 0x16;
            var before = (byte[])buffer.Clone();
            var header = Header();
            Assert.That(TickInputFrameCodec.TryEncode(in header, buffer.AsSpan(payloadOffset, 2),
                buffer.AsSpan(frameOffset, 34), out int written, out var error), Is.True);
            Assert.That(written, Is.EqualTo(34));
            Assert.That(error, Is.EqualTo(TickInputFrameError.None));
            Assert.That(buffer.AsSpan(frameOffset, written).SequenceEqual(Golden()), Is.True);
            for (int i = 0; i < buffer.Length; i++)
                if (i < frameOffset || i >= frameOffset + written) Assert.That(buffer[i], Is.EqualTo(before[i]));
        }

        [Test]
        public void RejectedOverlappingPayloadLeavesEntireDestinationUntouched()
        {
            var buffer = new byte[34];
            Array.Fill(buffer, (byte)0xFF);
            var before = (byte[])buffer.Clone();
            var header = Header();
            Assert.That(TickInputFrameCodec.TryEncode(in header, buffer.AsSpan(0, 2), buffer, out int written, out var error), Is.False);
            Assert.That(error, Is.EqualTo(TickInputFrameError.NonCanonicalPadding));
            Assert.That(written, Is.Zero);
            Assert.That(buffer, Is.EqualTo(before));
        }

        [Test]
        public void ExplicitCommandLayoutRoundTripsThroughBitAndFrameCodecs()
        {
            Span<byte> command = stackalloc byte[2];
            var bits = new NetBitWriter(command, 13);
            Assert.That(bits.TryWriteBits(5, 3), Is.True); // Buttons.
            Assert.That(bits.TryWriteBits(211 + 512, 10), Is.True); // Signed fixed-unit acceleration, explicit bias.
            var header = Header((uint)bits.BitPosition);
            Span<byte> frame = stackalloc byte[34];
            Assert.That(TickInputFrameCodec.TryEncode(in header, bits.WrittenSpan, frame, out int written, out _), Is.True);
            var context = Context();
            // Tie the borrowed output to the stack buffer lifetime for Unity's C# compiler.
            ReadOnlySpan<byte> payload = frame;
            Assert.That(TickInputFrameCodec.TryDecode(frame.Slice(0, written), in context, out var decoded,
                out payload, out _), Is.True);
            var reader = new NetBitReader(payload, decoded.PayloadBitLength);
            reader.TryReadBits(3, out uint buttons);
            reader.TryReadBits(10, out uint acceleration);
            Assert.That(buttons, Is.EqualTo(5));
            Assert.That((int)acceleration - 512, Is.EqualTo(211));
            Assert.That(reader.RemainingBits, Is.Zero);
        }

        [Test]
        public void WarmEncodeDecodeAndRejectedFrameAllocateNothing()
        {
            var payload = new byte[] { 0x9D, 0x16 };
            var frame = new byte[34];
            var rejected = Golden();
            rejected[4] = 2;
            var header = Header();
            var context = Context();
            int successes = 0;
            TestDelegate body = () =>
            {
                successes = 0;
                for (int i = 0; i < 10000; i++)
                {
                    if (TickInputFrameCodec.TryEncode(in header, payload, frame, out int written, out _)
                        && TickInputFrameCodec.TryDecode(frame.AsSpan(0, written), in context, out var decoded, out var bytes, out _)
                        && decoded.CommandTick == header.CommandTick && bytes[0] == payload[0]
                        && !TickInputFrameCodec.TryDecode(rejected, in context, out _, out _, out var error)
                        && error == TickInputFrameError.UnsupportedVersion) successes++;
                }
            };
            for (int i = 0; i < 4; i++) body();
            NetcodeCodecAllocationTests.AssertZero(body);
            Assert.That(successes, Is.EqualTo(10000));
        }
    }
}
