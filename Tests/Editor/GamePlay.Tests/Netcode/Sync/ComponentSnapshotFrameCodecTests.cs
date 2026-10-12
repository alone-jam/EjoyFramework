using System;
using System.Buffers.Binary;
using EjoyFramework.GamePlay.Netcode.Sync;
using NUnit.Framework;
using static EjoyFramework.GamePlay.Tests.Netcode.Sync.ComponentSnapshotTestData;

namespace EjoyFramework.GamePlay.Tests.Netcode.Sync
{
    public class ComponentSnapshotFrameCodecTests
    {
        [Test]
        public void EncodeMatchesIndependentGoldenAndPreservesUnusedDestination()
        {
            var destination = new byte[80];
            Array.Fill(destination, (byte)0xCC);
            var header = Header();
            Assert.That(Codec().TryEncode(in header, Records(), destination, out int written, out var error), Is.True);
            Assert.That(error, Is.EqualTo(ComponentSnapshotError.None));
            Assert.That(written, Is.EqualTo(68));
            Assert.That(destination.AsSpan(0, written).SequenceEqual(Golden()), Is.True);
            for (int i = written; i < destination.Length; i++) Assert.That(destination[i], Is.EqualTo(0xCC));
        }

        [Test]
        public void DecodeIndependentGoldenPublishesHeaderAndOwnedRecordsOnly()
        {
            var frame = Golden();
            var context = Context();
            var sentinel = Record(99, 99, 99, 1);
            var scratch = new[] { sentinel, sentinel, sentinel };
            Assert.That(Codec().TryDecode(frame, in context, scratch, out var header, out int count, out var error), Is.True);
            Assert.That(error, Is.EqualTo(ComponentSnapshotError.None));
            Assert.That(header, Is.EqualTo(Header()));
            Assert.That(header.SnapshotId, Is.Not.EqualTo((ulong)header.ServerTick));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(scratch[0].Entity.NetworkId, Is.EqualTo(0x01020304u));
            Assert.That(scratch[0].Entity.SpawnGeneration, Is.EqualTo(0x05060708u));
            Assert.That(scratch[0].State.Position, Is.EqualTo(211));
            Assert.That(scratch[0].State.Flags, Is.EqualTo(5));
            Assert.That(scratch[1].Entity.NetworkId, Is.EqualTo(0x11223344u));
            Assert.That(scratch[1].Entity.SpawnGeneration, Is.EqualTo(0x99AABBCCu));
            Assert.That(scratch[1].State.Position, Is.EqualTo(-512));
            Assert.That(scratch[1].State.Flags, Is.Zero);
            Assert.That(scratch[2], Is.EqualTo(sentinel));
        }

        [TestCase(0, 0x44, ComponentSnapshotError.InvalidMagic)]
        [TestCase(2, 0x54, ComponentSnapshotError.InvalidMagic)] // EJTI must not be accepted as EJCS.
        [TestCase(4, 2, ComponentSnapshotError.UnsupportedVersion)]
        [TestCase(5, 1, ComponentSnapshotError.UnsupportedVersion)]
        [TestCase(6, 49, ComponentSnapshotError.InvalidHeaderSize)]
        [TestCase(7, 1, ComponentSnapshotError.InvalidHeaderSize)]
        [TestCase(8, 9, ComponentSnapshotError.SessionEpochMismatch)]
        [TestCase(15, 2, ComponentSnapshotError.SessionEpochMismatch)]
        [TestCase(16, 0x45, ComponentSnapshotError.WorldEpochMismatch)]
        [TestCase(36, 0x35, ComponentSnapshotError.ComponentSchemaMismatch)]
        [TestCase(38, 0x79, ComponentSnapshotError.CodecRevisionMismatch)]
        [TestCase(40, 12, ComponentSnapshotError.StateBitCountMismatch)]
        [TestCase(44, 1, ComponentSnapshotError.FrameLengthMismatch)]
        [TestCase(44, 3, ComponentSnapshotError.FrameLengthMismatch)]
        [TestCase(57, 0x96, ComponentSnapshotError.NonCanonicalPadding)]
        [TestCase(67, 0x20, ComponentSnapshotError.NonCanonicalPadding)]
        public void MalformedFramesNeverPublishSuccessOutputs(int offset, byte value, ComponentSnapshotError expected)
        {
            var frame = Golden();
            frame[offset] = value;
            RejectDecode(frame, expected);
        }

        [TestCase(8, 8)]
        [TestCase(16, 4)]
        [TestCase(20, 8)]
        [TestCase(28, 4)]
        [TestCase(36, 2)]
        [TestCase(38, 2)]
        public void RequiredHeaderIdentitiesAreNonzero(int offset, int length)
        {
            var frame = Golden();
            Array.Clear(frame, offset, length);
            RejectDecode(frame, ComponentSnapshotError.InvalidHeader);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void EncoderRejectsZeroIdentitiesBeforeWriting(int field)
        {
            var header = new ComponentSnapshotHeader(field == 0 ? 0UL : 1UL, field == 1 ? 0u : 1u,
                field == 2 ? 0UL : 1UL, field == 3 ? 0u : 1u, 0,
                (ushort)(field == 4 ? 0 : 0x1234), (ushort)(field == 5 ? 0 : 0x5678), 13, 2);
            RejectEncode(header, Records(), ComponentSnapshotError.InvalidHeader);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void ContextZeroIsNeverAWildcard(int field)
        {
            var context = new ComponentSnapshotContext(field == 0 ? 0UL : 0x0102030405060708UL,
                field == 1 ? 0u : 0x11223344u, (ushort)(field == 2 ? 0 : 0x1234),
                (ushort)(field == 3 ? 0 : 0x5678), 13);
            RejectDecode(Golden(), ComponentSnapshotError.InvalidContext, context);
            RejectDecode(Golden(), ComponentSnapshotError.InvalidContext, default(ComponentSnapshotContext));
        }

        [TestCase(0, ComponentSnapshotError.SessionEpochMismatch)]
        [TestCase(1, ComponentSnapshotError.WorldEpochMismatch)]
        [TestCase(2, ComponentSnapshotError.ComponentSchemaMismatch)]
        [TestCase(3, ComponentSnapshotError.CodecRevisionMismatch)]
        [TestCase(4, ComponentSnapshotError.StateBitCountMismatch)]
        public void EveryExpectedContextFieldMustMatchExactly(int field, ComponentSnapshotError error)
        {
            var context = new ComponentSnapshotContext(field == 0 ? 9UL : 0x0102030405060708UL,
                field == 1 ? 9u : 0x11223344u, (ushort)(field == 2 ? 9 : 0x1234),
                (ushort)(field == 3 ? 9 : 0x5678), field == 4 ? 0u : 13u);
            RejectDecode(Golden(), error, context);
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(0x55667788u)]
        public void ProcessedAckMayBeZeroOrEqualServerTickWithoutBeingSnapshotIdentity(uint ack)
        {
            var header = Header(ack: ack, snapshotId: ulong.MaxValue);
            var frame = new byte[68];
            var context = Context();
            var scratch = new ComponentStateRecord<SnapshotTestState>[2];
            Assert.That(Codec().TryEncode(in header, Records(), frame, out _, out _), Is.True);
            Assert.That(Codec().TryDecode(frame, in context, scratch, out var decoded, out _, out _), Is.True);
            Assert.That(decoded.LastProcessedCommandTick, Is.EqualTo(ack));
            Assert.That(decoded.SnapshotId, Is.EqualTo(ulong.MaxValue));
            Assert.That(decoded.ServerTick, Is.EqualTo(0x55667788u));
        }

        [Test]
        public void ProcessedAckAfterServerTickIsRejectedInBothDirections()
        {
            RejectEncode(Header(tick: 10, ack: 11), Records(), ComponentSnapshotError.InvalidHeader);
            var frame = Golden();
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(32), 0x55667789);
            RejectDecode(frame, ComponentSnapshotError.InvalidHeader);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(47)]
        [TestCase(48)]
        [TestCase(57)]
        [TestCase(58)]
        [TestCase(66)]
        [TestCase(67)]
        [TestCase(69)]
        public void RejectsTruncatedHeadersLastRecordsAndTrailingBytes(int length)
        {
            var frame = new byte[length];
            Array.Copy(Golden(), frame, Math.Min(length, 68));
            RejectDecode(frame, length < 48 ? ComponentSnapshotError.HeaderTruncated : ComponentSnapshotError.FrameLengthMismatch);
        }

        [Test]
        public void NullAndDefaultSpansHaveEmptySpanSemantics()
        {
            RejectDecode(null, ComponentSnapshotError.HeaderTruncated);
            RejectDecode(Array.Empty<byte>(), ComponentSnapshotError.HeaderTruncated);
            RejectEncode(Header(), null, ComponentSnapshotError.EntityCountMismatch);
            RejectEncode(Header(0), null, ComponentSnapshotError.DestinationTooSmall, capacity: 0);
            RejectDecode(Golden(), ComponentSnapshotError.ScratchTooSmall, capacity: 0);
            RejectDecode(Golden(), ComponentSnapshotError.ScratchTooSmall, capacity: 1);
            RejectEncode(Header(), Records(), ComponentSnapshotError.DestinationTooSmall, capacity: 67);
        }

        [TestCase(0u, 1u, ComponentSnapshotError.InvalidEntityKey)]
        [TestCase(1u, 0u, ComponentSnapshotError.InvalidEntityKey)]
        [TestCase(0x01020304u, 99u, ComponentSnapshotError.EntityOrderViolation)]
        [TestCase(0x01020304u, 0x05060708u, ComponentSnapshotError.EntityOrderViolation)]
        [TestCase(1u, 1u, ComponentSnapshotError.EntityOrderViolation)]
        public void InvalidFinalRecordKeysFailBeforeAnyEncodingOrScratchWrites(uint id, uint generation, ComponentSnapshotError error)
        {
            var records = Records();
            records[1] = Record(id, generation);
            RejectEncode(Header(), records, error);
            var frame = Golden();
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(58), id);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(62), generation);
            var sentinel = Record(99, 99, 99, 1);
            var scratch = new[] { sentinel, sentinel };
            var context = Context();
            Assert.That(Codec().TryDecode(frame, in context, scratch, out var header, out int count, out var actual), Is.False);
            Assert.That(actual, Is.EqualTo(error));
            Assert.That(header, Is.EqualTo(default(ComponentSnapshotHeader)));
            Assert.That(count, Is.Zero);
            Assert.That(scratch, Is.EqualTo(new[] { sentinel, sentinel }));
        }

        [Test]
        public void FirstRecordAlsoRequiresNonzeroNetworkIdAndGeneration()
        {
            foreach (int offset in new[] { 48, 52 })
            {
                var frame = Golden();
                Array.Clear(frame, offset, 4);
                RejectDecode(frame, ComponentSnapshotError.InvalidEntityKey);
            }
            RejectEncode(Header(), new[] { Record(0, 1), Record(2, 1) }, ComponentSnapshotError.InvalidEntityKey);
            RejectEncode(Header(), new[] { Record(1, 0), Record(2, 1) }, ComponentSnapshotError.InvalidEntityKey);
        }

        [Test]
        public void IdentityAndTickMaximaRoundTripWithoutSignedComparisons()
        {
            var records = new[] { Record(1, 1, -512, 0), Record(uint.MaxValue, uint.MaxValue, 511, 5) };
            var header = new ComponentSnapshotHeader(ulong.MaxValue, uint.MaxValue, ulong.MaxValue,
                uint.MaxValue, uint.MaxValue, 0x1234, 0x5678, 13, 2);
            var context = new ComponentSnapshotContext(ulong.MaxValue, uint.MaxValue, 0x1234, 0x5678, 13);
            var frame = new byte[68];
            var scratch = new ComponentStateRecord<SnapshotTestState>[2];
            Assert.That(Codec().TryEncode(in header, records, frame, out _, out _), Is.True);
            Assert.That(Codec().TryDecode(frame, in context, scratch, out var decoded, out int count, out _), Is.True);
            Assert.That(decoded, Is.EqualTo(header));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(scratch, Is.EqualTo(records));
        }
    }
}
