using System;
using EjoyFramework.GamePlay.Netcode.Messages;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests.Netcode.Messages
{
    public class NetBitStreamTests
    {
        [Test]
        public void HandExpectedLsbFirstBitsCrossBytesAndZeroPrefilledTailPadding()
        {
            var bytes = new byte[] { 255, 255, 255 };
            var writer = new NetBitWriter(bytes, 13);
            Assert.That(bytes, Is.EqualTo(new byte[] { 255, 255, 255 })); // Construction is nonmutating.
            Assert.That(writer.TryWriteBits(5, 3), Is.True);
            Assert.That(bytes[0], Is.EqualTo(5));
            Assert.That(writer.TryWriteBits(0x2D3, 10), Is.True);
            Assert.That(bytes, Is.EqualTo(new byte[] { 0x9D, 0x16, 0xFF }));
            Assert.That(writer.BitPosition, Is.EqualTo(13));
            Assert.That(writer.RemainingBits, Is.Zero);
            Assert.That(writer.BitLimit, Is.EqualTo(13));
            Assert.That(writer.WrittenByteCount, Is.EqualTo(2));
            Assert.That(writer.WrittenSpan.SequenceEqual(new byte[] { 0x9D, 0x16 }), Is.True);
            var reader = new NetBitReader(bytes, 13);
            Assert.That(reader.TryReadBits(3, out uint first), Is.True);
            Assert.That(first, Is.EqualTo(5));
            Assert.That(reader.TryReadBits(10, out uint second), Is.True);
            Assert.That(second, Is.EqualTo(0x2D3));
            Assert.That(reader.BitPosition, Is.EqualTo(13));
            Assert.That(reader.RemainingBits, Is.Zero);
        }

        [Test]
        public void Full32BitsWorkAfterUnalignedPrefixWithoutShiftMaskWrapping()
        {
            var bytes = new byte[] { 255, 255, 255, 255, 255 };
            var writer = new NetBitWriter(bytes);
            Assert.That(writer.TryWriteBits(5, 3), Is.True);
            Assert.That(writer.TryWriteBits(0xFEDCBA98u, 32), Is.True);
            Assert.That(bytes, Is.EqualTo(new byte[] { 0xC5, 0xD4, 0xE5, 0xF6, 0x07 }));
            var reader = new NetBitReader(bytes, writer.BitPosition);
            reader.TryReadBits(3, out _);
            Assert.That(reader.TryReadBits(32, out uint value), Is.True);
            Assert.That(value, Is.EqualTo(0xFEDCBA98u));
            Assert.That(reader.TryReadBits(1, out value), Is.False);
            Assert.That(value, Is.Zero);
            Assert.That(reader.BitPosition, Is.EqualTo(35));
        }

        [Test]
        public void ZeroWidthIsNoOpAndDefaultEmptyStreamsAreValid()
        {
            NetBitWriter writer = default;
            NetBitReader reader = default;
            Assert.That(writer.TryWriteBits(0, 0), Is.True);
            Assert.That(reader.TryReadBits(0, out uint value), Is.True);
            Assert.That(value, Is.Zero);
            Assert.That(writer.WrittenSpan.IsEmpty, Is.True);
            Assert.That(writer.BitPosition + reader.BitPosition, Is.Zero);
            Assert.That(writer.TryWriteBits(0, 1), Is.False);
            Assert.That(reader.TryReadBits(1, out value), Is.False);
            Assert.That(value, Is.Zero);
            var fromNullArray = new NetBitWriter((byte[])null);
            Assert.That(fromNullArray.BitLimit, Is.Zero);
        }

        [TestCase(-1)]
        [TestCase(33)]
        [TestCase(int.MinValue)]
        [TestCase(int.MaxValue)]
        public void InvalidWidthsThrowBeforeChangingCursorOrData(int width)
        {
            var bytes = new byte[] { 0xAA, 0xBB };
            var writer = new NetBitWriter(bytes);
            writer.TryWriteBits(5, 3);
            var before = (byte[])bytes.Clone();
            bool rejected = false;
            try { writer.TryWriteBits(0, width); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert.That(rejected, Is.True);
            Assert.That(writer.BitPosition, Is.EqualTo(3));
            Assert.That(bytes, Is.EqualTo(before));
            var reader = new NetBitReader(bytes);
            reader.TryReadBits(3, out _);
            rejected = false;
            try { reader.TryReadBits(width, out _); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert.That(rejected, Is.True);
            Assert.That(reader.BitPosition, Is.EqualTo(3));
        }

        [TestCase(1u, 0)]
        [TestCase(8u, 3)]
        [TestCase(0x80000000u, 31)]
        public void ValuesTooWideAreRejectedNotTruncatedEvenWithNoRemainingCapacity(uint value, int width)
        {
            var bytes = new byte[] { 0xA5 };
            var writer = new NetBitWriter(bytes, 0);
            bool rejected = false;
            try { writer.TryWriteBits(value, width); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert.That(rejected, Is.True);
            Assert.That(bytes[0], Is.EqualTo(0xA5));
            Assert.That(writer.BitPosition, Is.Zero);
        }

        [Test]
        public void InsufficientBitsPreserveFullBufferAndBothCursors()
        {
            var bytes = new byte[] { 0xCC, 0xDD };
            var writer = new NetBitWriter(bytes, 9);
            writer.TryWriteBits(7, 3);
            var before = (byte[])bytes.Clone();
            Assert.That(writer.TryWriteBits(127, 7), Is.False);
            Assert.That(writer.BitPosition, Is.EqualTo(3));
            Assert.That(writer.RemainingBits, Is.EqualTo(6));
            Assert.That(bytes, Is.EqualTo(before));
            var reader = new NetBitReader(bytes, 9);
            reader.TryReadBits(3, out _);
            uint output = uint.MaxValue;
            Assert.That(reader.TryReadBits(7, out output), Is.False);
            Assert.That(output, Is.Zero);
            Assert.That(reader.BitPosition, Is.EqualTo(3));
            Assert.That(reader.RemainingBits, Is.EqualTo(6));
            Assert.That(writer.TryWriteBits(63, 6), Is.True);
            Assert.That(bytes[1], Is.EqualTo(1));
        }

        [TestCase(-1L)]
        [TestCase(9L)]
        [TestCase(long.MaxValue)]
        public void ExplicitLimitsRejectInvalidWideLengthsWithoutAllocatingLargeBuffers(long bits)
        {
            var bytes = new byte[] { 0xFF };
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new NetBitWriter(bytes, bits); });
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new NetBitReader(bytes, bits); });
            Assert.That(bytes[0], Is.EqualTo(0xFF));
        }

        [Test]
        public void AllWidthsAndStartingOffsetsRoundTripIncludingUnsignedMaximum()
        {
            for (int offset = 0; offset < 8; offset++)
            {
                for (int width = 0; width <= 32; width++)
                {
                    var bytes = new byte[5];
                    var writer = new NetBitWriter(bytes, offset + width);
                    uint expected = width == 32 ? uint.MaxValue : (1u << width) - 1;
                    Assert.That(writer.TryWriteBits(0, offset), Is.True);
                    Assert.That(writer.TryWriteBits(expected, width), Is.True);
                    var reader = new NetBitReader(bytes, offset + width);
                    reader.TryReadBits(offset, out _);
                    Assert.That(reader.TryReadBits(width, out uint actual), Is.True);
                    Assert.That(actual, Is.EqualTo(expected));
                    Assert.That(reader.RemainingBits, Is.Zero);
                }
            }
        }

        [Test]
        public void TenThousandSingleBitsStayWithinExplicitLimit()
        {
            var bytes = new byte[1250];
            var writer = new NetBitWriter(bytes, 10000);
            for (int i = 0; i < 10000; i++) Assert.That(writer.TryWriteBits((uint)(i & 1), 1), Is.True);
            var reader = new NetBitReader(bytes, 10000);
            for (int i = 0; i < 10000; i++)
            {
                Assert.That(reader.TryReadBits(1, out uint value), Is.True);
                Assert.That(value, Is.EqualTo((uint)(i & 1)));
            }
            Assert.That(writer.WrittenByteCount, Is.EqualTo(bytes.Length));
            Assert.That(reader.TryReadBits(1, out _), Is.False);
        }

        [Test]
        public void WarmBitReadWriteAndCapacityRejectionAllocateNothing()
        {
            var bytes = new byte[5];
            int successful = 0;
            TestDelegate body = () =>
            {
                successful = 0;
                for (int i = 0; i < 10000; i++)
                {
                    var writer = new NetBitWriter(bytes, 35);
                    writer.TryWriteBits(5, 3);
                    writer.TryWriteBits(uint.MaxValue, 32);
                    var reader = new NetBitReader(writer.WrittenSpan, writer.BitPosition);
                    reader.TryReadBits(3, out _);
                    if (reader.TryReadBits(32, out uint value) && value == uint.MaxValue
                        && !writer.TryWriteBits(0, 1) && !reader.TryReadBits(1, out _)) successful++;
                }
            };
            for (int i = 0; i < 4; i++) body();
            NetcodeCodecAllocationTests.AssertZero(body);
            Assert.That(successful, Is.EqualTo(10000));
        }
    }
}
