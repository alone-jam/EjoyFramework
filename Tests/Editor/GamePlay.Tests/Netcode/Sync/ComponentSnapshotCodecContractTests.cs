using System;
using System.Threading.Tasks;
using EjoyFramework.GamePlay.Netcode.Messages;
using EjoyFramework.GamePlay.Netcode.Sync;
using EjoyFramework.GamePlay.Tests.Netcode.Messages;
using NUnit.Framework;
using Is = NUnit.Framework.Is;
using static EjoyFramework.GamePlay.Tests.Netcode.Sync.ComponentSnapshotTestData;
#if UNITY_5_3_OR_NEWER
using UnityEngine.TestTools.Constraints;
#endif

namespace EjoyFramework.GamePlay.Tests.Netcode.Sync
{
    public class ComponentSnapshotCodecContractTests
    {
        [TestCase(float.NaN, (byte)0)]
        [TestCase(float.PositiveInfinity, (byte)0)]
        [TestCase(float.NegativeInfinity, (byte)0)]
        [TestCase(-513f, (byte)0)]
        [TestCase(512f, (byte)0)]
        [TestCase(0.5f, (byte)0)]
        [TestCase(0f, (byte)2)]
        [TestCase(0f, (byte)255)]
        public void ExplicitCodecRejectsInvalidLastValueWithoutPublishingSendableLength(float position, byte flags)
        {
            var records = Records();
            records[1] = Record(0x11223344, 1, position, flags);
            RejectEncode(Header(), records, ComponentSnapshotError.StateRejected, preflight: false);
        }

        [Test]
        public void InvalidFinalTypedValueMayLeaveScratchWorkButNeverPublishesIt()
        {
            var frame = Golden();
            frame[66] = 2; // Reserved flag bit, with otherwise canonical padding and identities.
            var sentinel = Record(99, 99, 99, 1);
            var scratch = new[] { sentinel, sentinel };
            var context = Context();
            var header = Header();
            int count = 99;
            Assert.That(Codec().TryDecode(frame, in context, scratch, out header, out count, out var error), Is.False);
            Assert.That(error, Is.EqualTo(ComponentSnapshotError.StateRejected));
            Assert.That(header, Is.EqualTo(default(ComponentSnapshotHeader)));
            Assert.That(count, Is.Zero);
            // Authoritative data must live elsewhere; no guarantee is made about scratch contents on failure.
            Assert.That(sentinel.State.Position, Is.EqualTo(99));
        }

        [TestCase(SnapshotCodecFault.ReturnFalse, ComponentSnapshotError.StateRejected)]
        [TestCase(SnapshotCodecFault.OverConsume, ComponentSnapshotError.StateRejected)]
        [TestCase(SnapshotCodecFault.Short, ComponentSnapshotError.CodecBitCountMismatch)]
        [TestCase(SnapshotCodecFault.WrongRemaining, ComponentSnapshotError.CodecBitCountMismatch)]
        public void EachTypedCallMustSucceedAndConsumeExactlyItsBoundedRecord(SnapshotCodecFault fault, ComponentSnapshotError expected)
        {
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotFaultCodec>(new SnapshotFaultCodec(fault));
            var header = Header();
            var context = Context();
            var scratch = new ComponentStateRecord<SnapshotTestState>[2];
            Assert.That(codec.TryEncode(in header, Records(), new byte[68], out int written, out var error), Is.False);
            Assert.That(error, Is.EqualTo(expected));
            Assert.That(written, Is.Zero);
            Assert.That(codec.TryDecode(Golden(), in context, scratch, out var decoded, out int count, out error), Is.False);
            Assert.That(error, Is.EqualTo(expected));
            Assert.That(decoded, Is.EqualTo(default(ComponentSnapshotHeader)));
            Assert.That(count, Is.Zero);
        }

        [Test]
        public void ThrowingCodecIsAProgrammingErrorAndLeavesSuccessOutputsUnpublished()
        {
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotFaultCodec>(
                new SnapshotFaultCodec(SnapshotCodecFault.Throw));
            var header = Header();
            var context = Context();
            var decoded = Header();
            int written = 99, count = 99;
            var frame = new byte[68];
            var scratch = new ComponentStateRecord<SnapshotTestState>[2];
            Assert.Throws<InvalidOperationException>(() => codec.TryEncode(in header, Records(), frame, out written, out _));
            Assert.That(written, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => codec.TryDecode(Golden(), in context, scratch,
                out decoded, out count, out _));
            Assert.That(decoded, Is.EqualTo(default(ComponentSnapshotHeader)));
            Assert.That(count, Is.Zero);
        }

        [Test]
        public void ThrowingDefinitionAlsoCannotLeakPreviousSuccessOutputs()
        {
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotThrowingDefinitionCodec>(default);
            var header = Header();
            var context = Context();
            var decoded = Header();
            int written = 99, count = 99;
            Assert.Throws<InvalidOperationException>(() => codec.TryEncode(in header, Records(), new byte[68], out written, out _));
            Assert.That(written, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => codec.TryDecode(Golden(), in context,
                new ComponentStateRecord<SnapshotTestState>[2], out decoded, out count, out _));
            Assert.That(decoded, Is.EqualTo(default(ComponentSnapshotHeader)));
            Assert.That(count, Is.Zero);
        }

        [Test]
        public void DisjointWorkspacesAllowWireAndScratchReuseAfterCopyingDecodedValues()
        {
            // Strict API contract: wire, typed records, scratch and out-parameters never alias.
            var wire = Golden();
            var context = Context();
            var scratch = new ComponentStateRecord<SnapshotTestState>[2];
            Assert.That(Codec().TryDecode(wire, in context, scratch, out var header, out int count, out _), Is.True);
            var authoritative = new[] { scratch[0], scratch[1] };
            Array.Fill(wire, (byte)0xFF);
            scratch[0] = Record(99, 99, 99, 1);
            scratch[1] = default;
            Assert.That(count, Is.EqualTo(2));
            Assert.That(header, Is.EqualTo(Header()));
            Assert.That(authoritative, Is.EqualTo(Records()));
            var destination = new byte[68];
            Assert.That(Codec().TryEncode(in header, authoritative, destination, out _, out _), Is.True);
            Array.Clear(authoritative, 0, authoritative.Length);
            Assert.That(destination, Is.EqualTo(Golden()));
        }

        [Test]
        public void StatelessCodecCanBeSharedWhenConcurrentOperationsUseDisjointStorage()
        {
            var codec = Codec();
            var successes = new bool[32];
            Parallel.For(0, successes.Length, i =>
            {
                var records = Records();
                var header = Header(snapshotId: (ulong)i + 1);
                var context = Context();
                var wire = new byte[68];
                var scratch = new ComponentStateRecord<SnapshotTestState>[2];
                successes[i] = codec.TryEncode(in header, records, wire, out int written, out _)
                    && codec.TryDecode(wire.AsSpan(0, written), in context, scratch, out var decoded, out int count, out _)
                    && decoded.SnapshotId == (ulong)i + 1 && count == 2
                    && scratch[0].State.Position == 211 && scratch[1].Entity.NetworkId == 0x11223344;
            });
            Assert.That(successes, Is.All.True);
        }

        [Test]
        public void TypedCodecUnitContractValidatesBeforeWritingAndReturnsOnlyOwnedFields()
        {
            var codec = new SnapshotTestCodec();
            foreach (byte flags in new byte[] { 0, 1, 4, 5 })
            {
                foreach (int position in new[] { -512, 0, 511 })
                {
                    var value = new SnapshotTestState(position, flags);
                    var bytes = new byte[2];
                    var writer = new NetBitWriter(bytes, 13);
                    Assert.That(codec.TryWrite(ref writer, in value), Is.True);
                    Assert.That(writer.BitPosition, Is.EqualTo(13));
                    Assert.That(writer.RemainingBits, Is.Zero);
                    var reader = new NetBitReader(bytes, 13);
                    Assert.That(codec.TryRead(ref reader, out var decoded), Is.True);
                    Assert.That(decoded, Is.EqualTo(value));
                    Assert.That(reader.BitPosition, Is.EqualTo(13));
                    Assert.That(reader.RemainingBits, Is.Zero);
                }
            }
        }

        [Test]
        public void WarmSuccessfulAndRejectedFramesUseExistingAllocationHelper()
        {
            var codec = Codec();
            var records = Records();
            var header = Header();
            var context = Context();
            var wire = new byte[68];
            var scratch = new ComponentStateRecord<SnapshotTestState>[2];
            var rejected = Golden();
            rejected[66] = 2;
            int successes = 0;
            TestDelegate body = () =>
            {
                successes = 0;
                for (int i = 0; i < 10000; i++)
                {
                    if (codec.TryEncode(in header, records, wire, out int written, out _)
                        && codec.TryDecode(wire.AsSpan(0, written), in context, scratch, out var decoded, out int count, out _)
                        && count == 2 && decoded.SnapshotId == header.SnapshotId
                        && scratch[0].State.Position == 211 && scratch[1].State.Position == -512
                        && !codec.TryDecode(rejected, in context, scratch, out decoded, out count, out var error)
                        && count == 0 && decoded.SnapshotId == 0 && error == ComponentSnapshotError.StateRejected) successes++;
                }
            };
            for (int i = 0; i < 4; i++) body();
            NetcodeCodecAllocationTests.AssertZero(body);
            Assert.That(successes, Is.EqualTo(10000));
        }

        [Test]
        public void AllocationPositiveControlDetectsARealRetainedAllocation()
        {
            byte[] retained = null;
            TestDelegate allocate = () => retained = new byte[256];
            for (int i = 0; i < 32; i++) allocate();
#if UNITY_5_3_OR_NEWER
            Assert.That(allocate, new AllocatingGCMemoryConstraint());
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
            allocate();
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.GreaterThanOrEqualTo(256));
#endif
            GC.KeepAlive(retained);
        }
    }
}
