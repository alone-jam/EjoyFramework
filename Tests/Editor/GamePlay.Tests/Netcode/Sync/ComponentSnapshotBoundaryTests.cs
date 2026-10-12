using System;
using System.Buffers.Binary;
using EjoyFramework.GamePlay.Netcode.Sync;
using NUnit.Framework;
using static EjoyFramework.GamePlay.Tests.Netcode.Sync.ComponentSnapshotTestData;

namespace EjoyFramework.GamePlay.Tests.Netcode.Sync
{
    public class ComponentSnapshotBoundaryTests
    {
        [Test]
        public void LimitsAreImmutableValidatedAndDefaultStructFailsClosed()
        {
            var limits = ComponentSnapshotLimits.Default;
            Assert.That(limits.MaxRecords, Is.EqualTo(256));
            Assert.That(limits.MaxStateBits, Is.EqualTo(2048));
            Assert.That(limits.MaxFrameBytes, Is.EqualTo(128 * 1024));
            Assert.That(limits.IsValid, Is.True);
            Assert.That(default(ComponentSnapshotLimits).IsValid, Is.False);
            RejectEncode(Header(), Records(), ComponentSnapshotError.InvalidLimits, default(ComponentSnapshotLimits));
            RejectDecode(Golden(), ComponentSnapshotError.InvalidLimits, limits: default(ComponentSnapshotLimits));
            var uninitialized = default(ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotTestCodec>);
            var context = Context();
            Assert.That(uninitialized.TryDecode(Golden(), in context, new ComponentStateRecord<SnapshotTestState>[2],
                out var header, out int count, out var error), Is.False);
            Assert.That(error, Is.EqualTo(ComponentSnapshotError.InvalidLimits));
            Assert.That(header, Is.EqualTo(default(ComponentSnapshotHeader)));
            Assert.That(count, Is.Zero);
        }

        [TestCase(-1, 13, 68)]
        [TestCase(16385, 13, 68)]
        [TestCase(int.MaxValue, 13, 68)]
        [TestCase(2, -1, 68)]
        [TestCase(2, 65537, 68)]
        [TestCase(2, int.MaxValue, 68)]
        [TestCase(2, 13, -1)]
        [TestCase(2, 13, 47)]
        [TestCase(2, 13, 8388609)]
        [TestCase(2, 13, int.MaxValue)]
        public void InvalidConfigurationThrowsBeforeItCanReachWireArithmetic(int records, int bits, int bytes)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ComponentSnapshotLimits(records, bits, bytes));
        }

        [Test]
        public void HeaderCountMustEqualInputRecordCountIncludingZero()
        {
            RejectEncode(Header(1), Records(), ComponentSnapshotError.EntityCountMismatch);
            RejectEncode(Header(0), Records(), ComponentSnapshotError.EntityCountMismatch);
            RejectEncode(Header(uint.MaxValue), Records(), ComponentSnapshotError.EntityCountMismatch);
        }

        [Test]
        public void IndependentConfiguredCapsApplySymmetricallyBeforeRecordWork()
        {
            foreach (var entry in new[]
            {
                (new ComponentSnapshotLimits(1, 13, 68), ComponentSnapshotError.TooManyRecords),
                (new ComponentSnapshotLimits(2, 12, 68), ComponentSnapshotError.StateTooLarge),
                (new ComponentSnapshotLimits(2, 13, 67), ComponentSnapshotError.FrameTooLarge)
            })
            {
                RejectEncode(Header(), Records(), entry.Item2, entry.Item1);
                RejectDecode(Golden(), entry.Item2, limits: entry.Item1);
            }
            var exact = new ComponentSnapshotLimits(2, 13, 68);
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotTestCodec>(default, exact);
            var context = Context();
            var header = Header();
            var frame = new byte[68];
            Assert.That(codec.TryEncode(in header, Records(), frame, out int written, out _), Is.True);
            Assert.That(written, Is.EqualTo(68));
            Assert.That(codec.TryDecode(frame, in context, new ComponentStateRecord<SnapshotTestState>[2], out _, out _, out _), Is.True);
        }

        [Test]
        public void HostileCountsAndWidthsNeverDriveOffsetsOrLoops()
        {
            var hostileCount = Golden();
            BinaryPrimitives.WriteUInt32LittleEndian(hostileCount.AsSpan(44), uint.MaxValue);
            RejectDecode(hostileCount, ComponentSnapshotError.TooManyRecords);
            var hostileWidth = Golden();
            BinaryPrimitives.WriteUInt32LittleEndian(hostileWidth.AsSpan(40), uint.MaxValue);
            // Match hostile context as well, proving that width cannot reach byte-ceiling arithmetic unchecked.
            RejectDecode(hostileWidth, ComponentSnapshotError.StateTooLarge, Context(uint.MaxValue));
            RejectEncode(Header(bits: uint.MaxValue), Records(), ComponentSnapshotError.StateTooLarge);
        }

        [Test]
        public void LegalAxisMaximaWhoseProductExceedsFrameBudgetFailBeforeScratchOrCodecWork()
        {
            var limits = new ComponentSnapshotLimits(ComponentSnapshotLimits.MaximumRecords,
                ComponentSnapshotLimits.MaximumStateBits, ComponentSnapshotLimits.MaximumFrameBytes);
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotZeroCodec>(
                new SnapshotZeroCodec((uint)limits.MaxStateBits), limits);
            var header = Header((uint)limits.MaxRecords, (uint)limits.MaxStateBits);
            var records = new ComponentStateRecord<SnapshotTestState>[limits.MaxRecords];
            Assert.That(codec.TryEncode(in header, records, Span<byte>.Empty, out int written, out var error), Is.False);
            Assert.That(error, Is.EqualTo(ComponentSnapshotError.FrameTooLarge));
            Assert.That(written, Is.Zero);
            var frame = Golden();
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(40), (uint)limits.MaxStateBits);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(44), (uint)limits.MaxRecords);
            var context = Context((uint)limits.MaxStateBits);
            Assert.That(codec.TryDecode(frame, in context, Span<ComponentStateRecord<SnapshotTestState>>.Empty,
                out var decoded, out int count, out error), Is.False);
            Assert.That(error, Is.EqualTo(ComponentSnapshotError.FrameTooLarge));
            Assert.That(decoded, Is.EqualTo(default(ComponentSnapshotHeader)));
            Assert.That(count, Is.Zero);
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(7u)]
        [TestCase(8u)]
        [TestCase(9u)]
        [TestCase(2048u)]
        [TestCase(65536u)]
        public void ZeroStateTagsAndExactBitAndByteBoundariesRoundTrip(uint bits)
        {
            int stride = 8 + (int)((bits + 7UL) / 8UL);
            var limits = new ComponentSnapshotLimits(2, (int)bits, 48 + 2 * stride);
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotZeroCodec>(new SnapshotZeroCodec(bits), limits);
            var header = Header(bits: bits);
            var context = Context(bits);
            var frame = new byte[limits.MaxFrameBytes];
            Array.Fill(frame, (byte)0xFF);
            var scratch = new ComponentStateRecord<SnapshotTestState>[2];
            Assert.That(codec.TryEncode(in header, Records(), frame, out int written, out var error), Is.True);
            Assert.That(written, Is.EqualTo(frame.Length));
            Assert.That(error, Is.EqualTo(ComponentSnapshotError.None));
            Assert.That(codec.TryDecode(frame, in context, scratch, out var decoded, out int count, out error), Is.True);
            Assert.That(decoded, Is.EqualTo(header));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(scratch[1].Entity, Is.EqualTo(Records()[1].Entity));
            Assert.That(scratch[1].State, Is.EqualTo(default(SnapshotTestState)));
            for (int i = 56; i < 48 + stride; i++) Assert.That(frame[i], Is.Zero);
            for (int i = 56 + stride; i < frame.Length; i++) Assert.That(frame[i], Is.Zero);
        }

        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(3u)]
        [TestCase(4u)]
        [TestCase(5u)]
        [TestCase(6u)]
        [TestCase(7u)]
        public void EachRecordOwnsItsCanonicalPaddingNotJustTheLastRecord(uint bits)
        {
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotZeroCodec>(new SnapshotZeroCodec(bits));
            var header = Header(bits: bits);
            var context = Context(bits);
            foreach (int paddingOffset in new[] { 56, 65 })
            {
                var frame = new byte[66];
                Assert.That(codec.TryEncode(in header, Records(), frame, out _, out _), Is.True);
                frame[paddingOffset] = (byte)(1 << (int)bits);
                Assert.That(codec.TryDecode(frame, in context, new ComponentStateRecord<SnapshotTestState>[2],
                    out var decoded, out int count, out var error), Is.False);
                Assert.That(error, Is.EqualTo(ComponentSnapshotError.NonCanonicalPadding));
                Assert.That(decoded, Is.EqualTo(default(ComponentSnapshotHeader)));
                Assert.That(count, Is.Zero);
            }
        }

        [Test]
        public void EmptyBatchIsAHeaderNotAnOmissionDespawnOrAnEmptyInputFrame()
        {
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotZeroCodec>(
                new SnapshotZeroCodec(0), new ComponentSnapshotLimits(0, 0, 48));
            var header = Header(0, 0, snapshotId: 1, tick: 1, ack: 0);
            var context = Context(0);
            var frame = new byte[48];
            Assert.That(codec.TryEncode(in header, default, frame, out int written, out _), Is.True);
            Assert.That(written, Is.EqualTo(48));
            Assert.That(codec.TryDecode(frame, in context, default, out var decoded, out int count, out _), Is.True);
            Assert.That(decoded, Is.EqualTo(header));
            Assert.That(count, Is.Zero);
        }

        [TestCase(256)]
        [TestCase(10000)]
        [TestCase(16384)]
        public void BoundedLargeBatchesPreserveOrderAndEveryOwnedValue(int size)
        {
            var limits = new ComponentSnapshotLimits(size, 13, 48 + size * 10);
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotTestCodec>(default, limits);
            var records = new ComponentStateRecord<SnapshotTestState>[size];
            for (int i = 0; i < size; i++) records[i] = Record((uint)i + 1, uint.MaxValue, i % 1024 - 512, 5);
            var frame = new byte[limits.MaxFrameBytes];
            var scratch = new ComponentStateRecord<SnapshotTestState>[size];
            var header = Header((uint)size);
            var context = Context();
            Assert.That(codec.TryEncode(in header, records, frame, out _, out _), Is.True);
            Assert.That(codec.TryDecode(frame, in context, scratch, out _, out int count, out _), Is.True);
            Assert.That(count, Is.EqualTo(size));
            Assert.That(scratch, Is.EqualTo(records));
        }

        [Test]
        public void HighestSchemaAndRevisionAreAcceptedWhenCodecAndContextMatch()
        {
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotZeroCodec>(
                new SnapshotZeroCodec(0, ushort.MaxValue, ushort.MaxValue));
            var header = new ComponentSnapshotHeader(1, 1, 1, 1, 0, ushort.MaxValue, ushort.MaxValue, 0, 1);
            var context = new ComponentSnapshotContext(1, 1, ushort.MaxValue, ushort.MaxValue, 0);
            var wire = new byte[56];
            var scratch = new ComponentStateRecord<SnapshotTestState>[1];
            Assert.That(codec.TryEncode(in header, new[] { Record(1, 1) }, wire, out _, out _), Is.True);
            Assert.That(codec.TryDecode(wire, in context, scratch, out var decoded, out int count, out _), Is.True);
            Assert.That(decoded, Is.EqualTo(header));
            Assert.That(count, Is.EqualTo(1));
            Assert.That(scratch[0].Entity.NetworkId, Is.EqualTo(1));
        }

        [TestCase(0, 0x5678, 13u, ComponentSnapshotError.InvalidCodec)]
        [TestCase(0x1234, 0, 13u, ComponentSnapshotError.InvalidCodec)]
        [TestCase(0x1235, 0x5678, 13u, ComponentSnapshotError.CodecIdentityMismatch)]
        [TestCase(0x1234, 0x5679, 13u, ComponentSnapshotError.CodecIdentityMismatch)]
        [TestCase(0x1234, 0x5678, 12u, ComponentSnapshotError.CodecIdentityMismatch)]
        [TestCase(0x1234, 0x5678, uint.MaxValue, ComponentSnapshotError.CodecIdentityMismatch)]
        public void MatchingWireAndContextCannotBypassSelectedCodecIdentity(ushort schema, ushort revision,
            uint bits, ComponentSnapshotError expectedError)
        {
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotZeroCodec>(new SnapshotZeroCodec(bits, schema, revision));
            var context = Context();
            var header = Header();
            Assert.That(codec.TryEncode(in header, Records(), new byte[68], out int written, out var error), Is.False);
            Assert.That(error, Is.EqualTo(expectedError));
            Assert.That(written, Is.Zero);
            Assert.That(codec.TryDecode(Golden(), in context, new ComponentStateRecord<SnapshotTestState>[2],
                out var decoded, out int count, out error), Is.False);
            Assert.That(error, Is.EqualTo(expectedError));
            Assert.That(decoded, Is.EqualTo(default(ComponentSnapshotHeader)));
            Assert.That(count, Is.Zero);
        }
    }
}
