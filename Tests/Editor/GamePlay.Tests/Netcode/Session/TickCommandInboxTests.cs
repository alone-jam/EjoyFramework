using System;
using System.Threading;
using EjoyFramework.GamePlay.Netcode.Messages;
using EjoyFramework.GamePlay.Netcode.Session;
using EjoyFramework.GamePlay.Tests.Netcode.Messages;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests.Netcode.Session
{
    internal struct InboxTestCommand
    {
        public int Axis;
        public uint Buttons;
        public float Gain;
    }

    // Explicit 43-bit test schema: biased axis[8], buttons[3], IEEE-754 gain[32].
    // No raw struct layout, native pointers or codec-side simulation state.
    internal struct InboxTestCodec : ITickCommandCodec<InboxTestCommand>
    {
        public ushort CommandSchemaId => 0x101;
        public ushort CodecRevision => 2;
        public bool TryDecode(ref NetBitReader reader, out InboxTestCommand command)
        {
            command = default;
            if (!reader.TryReadBits(8, out uint axis) || !reader.TryReadBits(3, out uint buttons)
                || !reader.TryReadBits(32, out uint gainBits)) return false;
            float gain = BitConverter.Int32BitsToSingle(unchecked((int)gainBits));
            if (axis < 28 || axis > 228 || (buttons & ~5u) != 0 || float.IsNaN(gain)
                || float.IsInfinity(gain) || gain < 0 || gain > 1) return false;
            command = new InboxTestCommand { Axis = (int)axis - 128, Buttons = buttons, Gain = gain };
            return true;
        }
    }

    internal static class InboxTestFrames
    {
        internal static TickInputFrameContext Context(ulong session = 11, uint world = 22)
            => new TickInputFrameContext(session, world, 0x101, 2);
        internal static byte[] Frame(uint tick, int axis = 0, uint buttons = 0, float gain = 0,
            int extraBits = 0, TickInputFrameContext? context = null)
        {
            var expected = context ?? Context();
            var payload = new byte[(43 + extraBits + 7) / 8];
            var writer = new NetBitWriter(payload, 43 + extraBits);
            Assert.That(writer.TryWriteBits((uint)(axis + 128), 8), Is.True);
            Assert.That(writer.TryWriteBits(buttons, 3), Is.True);
            Assert.That(writer.TryWriteBits(unchecked((uint)BitConverter.SingleToInt32Bits(gain)), 32), Is.True);
            Assert.That(writer.TryWriteBits(0, extraBits), Is.True);
            return Wrap(tick, writer.WrittenSpan, (uint)writer.BitPosition, expected);
        }
        internal static byte[] Wrap(uint tick, ReadOnlySpan<byte> payload, uint bits, TickInputFrameContext context)
        {
            var header = new TickInputFrameHeader(context.SessionEpoch, context.WorldEpoch, tick,
                context.CommandSchemaId, context.CodecRevision, bits);
            var frame = new byte[32 + payload.Length];
            Assert.That(TickInputFrameCodec.TryEncode(in header, payload, frame, out int written, out var error), Is.True);
            Assert.That(error, Is.EqualTo(TickInputFrameError.None));
            Assert.That(written, Is.EqualTo(frame.Length));
            return frame;
        }
    }

    public class TickCommandInboxTests
    {
        private TickCommandInbox<InboxTestCommand, InboxTestCodec> m_Inbox;

        [SetUp]
        public void SetUp()
        {
            var context = InboxTestFrames.Context();
            m_Inbox = new TickCommandInbox<InboxTestCommand, InboxTestCodec>(in context, default,
                lastSimulatedTick: 10, windowSize: 4);
        }
        [TearDown]
        public void TearDown() => m_Inbox.Close();

        private object[] State() => new object[] { m_Inbox.LastSimulatedTick, m_Inbox.LastProcessedCommandTick,
            m_Inbox.PendingCommandCount, m_Inbox.IsTickInFlight, m_Inbox.IsFaulted, m_Inbox.IsClosed, m_Inbox.Context };
        private static void Outcome(TickCommandAdmissionResult result, TickCommandAdmissionStatus status,
            TickInputFrameError error = TickInputFrameError.None)
        {
            Assert.That(result.Status, Is.EqualTo(status));
            Assert.That(result.FrameError, Is.EqualTo(error));
        }
        private void Rejected(byte[] frame, TickCommandAdmissionStatus status, TickInputFrameError error = TickInputFrameError.None)
        {
            var before = State();
            Outcome(m_Inbox.TryAdmit(frame), status, error);
            Assert.That(State(), Is.EqualTo(before));
        }

        [Test]
        public void DefaultAdmissionResultNeverClaimsAPacketWasAccepted()
        {
            TickCommandAdmissionResult result = default;
            Assert.That(result.Status, Is.EqualTo(TickCommandAdmissionStatus.NotEvaluated));
            Assert.That(result.FrameError, Is.EqualTo(TickInputFrameError.None));
        }

        [Test]
        public void OutOfOrderFutureAdmissionHasNoReceiveDrivenAdvancementAndConsumesInTickOrder()
        {
            foreach (uint tick in new uint[] { 14, 12, 11, 13 })
                Outcome(m_Inbox.TryAdmit(InboxTestFrames.Frame(tick, (int)tick)), TickCommandAdmissionStatus.Accepted);
            Assert.That(m_Inbox.LastSimulatedTick, Is.EqualTo(10));
            Assert.That(m_Inbox.LastProcessedCommandTick, Is.Zero);
            Assert.That(m_Inbox.PendingCommandCount, Is.EqualTo(4));
            Assert.That(m_Inbox.WindowSize, Is.EqualTo(4));
            for (uint tick = 11; tick <= 14; tick++)
            {
                var selection = m_Inbox.BeginTick(tick);
                Assert.That(selection.Tick, Is.EqualTo(tick));
                Assert.That(selection.HasCommand, Is.True);
                Assert.That(selection.Command.Axis, Is.EqualTo((int)tick));
                Assert.That(m_Inbox.LastSimulatedTick, Is.EqualTo(tick - 1));
                m_Inbox.CompleteTick(tick);
                Assert.That(m_Inbox.LastSimulatedTick, Is.EqualTo(tick));
                Assert.That(m_Inbox.LastProcessedCommandTick, Is.EqualTo(tick));
            }
            Assert.That(m_Inbox.PendingCommandCount, Is.Zero);
        }

        [TestCase(3)]
        [TestCase(1024)]
        public void WholeWindowRetainsEveryDistinctFutureTickWithoutGrowthOrCollision(int window)
        {
            var context = InboxTestFrames.Context();
            var inbox = new TickCommandInbox<InboxTestCommand, InboxTestCodec>(in context, default, windowSize: window);
            for (uint tick = (uint)window; tick > 0; tick--)
                Outcome(inbox.TryAdmit(InboxTestFrames.Frame(tick, (int)(tick % 201) - 100)), TickCommandAdmissionStatus.Accepted);
            Assert.That(inbox.PendingCommandCount, Is.EqualTo(window));
            for (uint tick = 1; tick <= (uint)window; tick++)
            {
                var selection = inbox.BeginTick(tick);
                Assert.That(selection.HasCommand, Is.True);
                Assert.That(selection.Command.Axis, Is.EqualTo((int)(tick % 201) - 100));
                inbox.CompleteTick(tick);
            }
            Assert.That(inbox.WindowSize, Is.EqualTo(window));
            Assert.That(inbox.PendingCommandCount, Is.Zero);
            Assert.That(inbox.LastProcessedCommandTick, Is.EqualTo((uint)window));
            inbox.Close();
        }

        [Test]
        public void WindowBoundsAreInclusiveAtUpperEdgeAndNeverEvictAnotherLiveTick()
        {
            Outcome(m_Inbox.TryAdmit(InboxTestFrames.Frame(11, 7)), TickCommandAdmissionStatus.Accepted);
            Outcome(m_Inbox.TryAdmit(InboxTestFrames.Frame(14, 8)), TickCommandAdmissionStatus.Accepted);
            Rejected(InboxTestFrames.Frame(10), TickCommandAdmissionStatus.StaleTick);
            Rejected(InboxTestFrames.Frame(1), TickCommandAdmissionStatus.StaleTick);
            Rejected(InboxTestFrames.Frame(15, 99), TickCommandAdmissionStatus.OutsideWindow); // Same modulo slot as 11.
            Rejected(InboxTestFrames.Frame(uint.MaxValue), TickCommandAdmissionStatus.OutsideWindow);
            Assert.That(m_Inbox.BeginTick(11).Command.Axis, Is.EqualTo(7));
            m_Inbox.CompleteTick(11);
            Outcome(m_Inbox.TryAdmit(InboxTestFrames.Frame(15, 9)), TickCommandAdmissionStatus.Accepted);
            m_Inbox.BeginTick(12); m_Inbox.CompleteTick(12);
            m_Inbox.BeginTick(13); m_Inbox.CompleteTick(13);
            Assert.That(m_Inbox.BeginTick(14).Command.Axis, Is.EqualTo(8));
            m_Inbox.CompleteTick(14);
            Assert.That(m_Inbox.BeginTick(15).Command.Axis, Is.EqualTo(9));
            m_Inbox.CompleteTick(15);
            Assert.That(m_Inbox.PendingCommandCount, Is.Zero);
        }

        [Test]
        public void FirstValidCommandWinsAndDuplicateDoesNotPromiseIdenticalOrRevalidatedPayload()
        {
            Outcome(m_Inbox.TryAdmit(InboxTestFrames.Frame(11, 7)), TickCommandAdmissionStatus.Accepted);
            Rejected(InboxTestFrames.Frame(11, 8), TickCommandAdmissionStatus.Duplicate);
            Rejected(InboxTestFrames.Frame(11, 101), TickCommandAdmissionStatus.Duplicate);
            var selected = m_Inbox.BeginTick(11);
            Assert.That(selected.Command.Axis, Is.EqualTo(7));
            m_Inbox.CompleteTick(11);
            Rejected(InboxTestFrames.Frame(11, 7), TickCommandAdmissionStatus.StaleTick);
        }

        [TestCase(101, 0u, 0f)]
        [TestCase(-101, 0u, 0f)]
        [TestCase(0, 2u, 0f)]
        [TestCase(0, 0u, float.NaN)]
        [TestCase(0, 0u, float.PositiveInfinity)]
        [TestCase(0, 0u, float.NegativeInfinity)]
        [TestCase(0, 0u, -0.1f)]
        [TestCase(0, 0u, 1.1f)]
        public void InvalidFirstCommandDoesNotOccupySlotOrHideLaterValidCommand(int axis, uint buttons, float gain)
        {
            Rejected(InboxTestFrames.Frame(11, axis, buttons, gain), TickCommandAdmissionStatus.CommandRejected);
            Outcome(m_Inbox.TryAdmit(InboxTestFrames.Frame(11, -100, 5, 1)), TickCommandAdmissionStatus.Accepted);
            var selection = m_Inbox.BeginTick(11);
            Assert.That(selection.Command.Axis, Is.EqualTo(-100));
            Assert.That(selection.Command.Buttons, Is.EqualTo(5));
            Assert.That(selection.Command.Gain, Is.EqualTo(1));
            m_Inbox.CompleteTick(11);
        }

        [Test]
        public void SchemaDecoderMustConsumeExactlyAllPayloadBits()
        {
            Rejected(InboxTestFrames.Frame(11, extraBits: 1), TickCommandAdmissionStatus.PayloadNotFullyConsumed);
            var shortCommand = InboxTestFrames.Wrap(11, new byte[] { 128 }, 8, InboxTestFrames.Context());
            Rejected(shortCommand, TickCommandAdmissionStatus.CommandRejected);
            Outcome(m_Inbox.TryAdmit(InboxTestFrames.Frame(11)), TickCommandAdmissionStatus.Accepted);
        }

        private struct ResettingReaderCodec : ITickCommandCodec<InboxTestCommand>
        {
            public ushort CommandSchemaId => 0x101;
            public ushort CodecRevision => 2;
            public bool TryDecode(ref NetBitReader reader, out InboxTestCommand command)
            {
                reader = default; // RemainingBits alone must not count as consumption of the original payload.
                command = new InboxTestCommand { Axis = 99 };
                return true;
            }
        }

        [Test]
        public void DecoderCannotClaimExactConsumptionByReplacingReaderWithEmptyState()
        {
            var context = InboxTestFrames.Context();
            var inbox = new TickCommandInbox<InboxTestCommand, ResettingReaderCodec>(in context, default);
            Outcome(inbox.TryAdmit(InboxTestFrames.Frame(1)), TickCommandAdmissionStatus.PayloadNotFullyConsumed);
            Assert.That(inbox.PendingCommandCount, Is.Zero);
            Assert.That(inbox.LastSimulatedTick, Is.Zero);
            inbox.Close();
        }

        [Test]
        public void WireFailureRetainsFrameErrorAndDoesNotPublishAnyCommand()
        {
            var frame = InboxTestFrames.Frame(11);
            frame[4] = 2;
            Rejected(frame, TickCommandAdmissionStatus.FrameRejected, TickInputFrameError.UnsupportedVersion);
            Rejected(Array.Empty<byte>(), TickCommandAdmissionStatus.FrameRejected, TickInputFrameError.HeaderTruncated);
            frame = InboxTestFrames.Frame(11);
            Array.Clear(frame, 20, 4);
            Rejected(frame, TickCommandAdmissionStatus.FrameRejected, TickInputFrameError.InvalidHeader);
            frame = InboxTestFrames.Frame(11);
            frame[frame.Length - 1] |= 0x80;
            Rejected(frame, TickCommandAdmissionStatus.FrameRejected, TickInputFrameError.NonCanonicalPadding);
        }

        [Test]
        public void AdmissionCopiesCommandAndSelectionCopiesDoNotMutateRetainedCommand()
        {
            var frame = InboxTestFrames.Frame(11, 23, 5, 0.5f);
            Outcome(m_Inbox.TryAdmit(frame), TickCommandAdmissionStatus.Accepted);
            Array.Fill(frame, (byte)0xFF);
            var selection = m_Inbox.BeginTick(11);
            var command = selection.Command;
            command.Axis = -50;
            Assert.That(selection.Command.Axis, Is.EqualTo(23));
            Assert.That(selection.Command.Buttons, Is.EqualTo(5));
            Assert.That(selection.Command.Gain, Is.EqualTo(0.5f));
            m_Inbox.CompleteTick(11);
            Assert.That(selection.Command.Axis, Is.EqualTo(23));
        }

        [Test]
        public void MissingInputIsSealedWithoutFallbackAndOnlyActualConsumptionAdvancesProcessedTick()
        {
            Outcome(m_Inbox.TryAdmit(InboxTestFrames.Frame(12)), TickCommandAdmissionStatus.Accepted); // Default-valued command is still present.
            var missing = m_Inbox.BeginTick(11);
            Assert.That(missing.HasCommand, Is.False);
            Assert.That(missing.Command, Is.EqualTo(default(InboxTestCommand)));
            Rejected(InboxTestFrames.Frame(11, 99), TickCommandAdmissionStatus.TickInFlight);
            Rejected(InboxTestFrames.Frame(13, 99), TickCommandAdmissionStatus.TickInFlight);
            m_Inbox.CompleteTick(11);
            Assert.That(m_Inbox.LastSimulatedTick, Is.EqualTo(11));
            Assert.That(m_Inbox.LastProcessedCommandTick, Is.Zero);
            var actual = m_Inbox.BeginTick(12);
            Assert.That(actual.HasCommand, Is.True);
            Assert.That(actual.Command, Is.EqualTo(default(InboxTestCommand)));
            Assert.That(m_Inbox.LastProcessedCommandTick, Is.Zero); // Begin is not completion/acknowledgment.
            m_Inbox.CompleteTick(12);
            Assert.That(m_Inbox.LastProcessedCommandTick, Is.EqualTo(12));
            Assert.That(m_Inbox.BeginTick(13).HasCommand, Is.False);
            m_Inbox.CompleteTick(13);
            Assert.That(m_Inbox.LastProcessedCommandTick, Is.EqualTo(12));
        }

        [Test]
        public void UIntMaximumCanCompleteButNextBeginCannotWrapAndGenesisZeroIsAllowed()
        {
            var context = InboxTestFrames.Context();
            var inbox = new TickCommandInbox<InboxTestCommand, InboxTestCodec>(in context, default, uint.MaxValue - 2, 4);
            Outcome(inbox.TryAdmit(InboxTestFrames.Frame(uint.MaxValue, 1)), TickCommandAdmissionStatus.Accepted);
            Outcome(inbox.TryAdmit(InboxTestFrames.Frame(1)), TickCommandAdmissionStatus.StaleTick);
            Assert.That(inbox.BeginTick(uint.MaxValue - 1).HasCommand, Is.False);
            inbox.CompleteTick(uint.MaxValue - 1);
            Assert.That(inbox.BeginTick(uint.MaxValue).HasCommand, Is.True);
            inbox.CompleteTick(uint.MaxValue);
            Assert.That(inbox.LastSimulatedTick, Is.EqualTo(uint.MaxValue));
            Assert.That(inbox.LastProcessedCommandTick, Is.EqualTo(uint.MaxValue));
            Assert.Throws<OverflowException>(() => inbox.BeginTick(0));
            Assert.That(inbox.IsTickInFlight, Is.False);
            inbox.Close();
            var genesis = new TickCommandInbox<InboxTestCommand, InboxTestCodec>(in context, default, windowSize: 1);
            Assert.That(genesis.LastSimulatedTick, Is.Zero);
            genesis.BeginTick(1); genesis.CompleteTick(1);
            Assert.That(genesis.LastProcessedCommandTick, Is.Zero);
            genesis.Close();
        }

        [Test]
        public void WrongLifecycleCallsAreExceptionsAndDoNotAdvanceOrUnseal()
        {
            var before = State();
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Inbox.BeginTick(10));
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Inbox.BeginTick(12));
            Assert.Throws<InvalidOperationException>(() => m_Inbox.CompleteTick(11));
            Assert.Throws<InvalidOperationException>(() => m_Inbox.FailTick(11));
            Assert.That(State(), Is.EqualTo(before));
            m_Inbox.BeginTick(11);
            before = State();
            Assert.Throws<InvalidOperationException>(() => m_Inbox.BeginTick(11));
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Inbox.CompleteTick(12));
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Inbox.FailTick(12));
            Assert.That(State(), Is.EqualTo(before));
            m_Inbox.CompleteTick(11);
            Assert.Throws<InvalidOperationException>(() => m_Inbox.CompleteTick(11));
        }

        [Test]
        public void FailedExecutionFaultsUntilCloseWithoutClaimingProcessedInputOrRollback()
        {
            m_Inbox.TryAdmit(InboxTestFrames.Frame(11, 3));
            m_Inbox.TryAdmit(InboxTestFrames.Frame(12, 4));
            m_Inbox.BeginTick(11);
            m_Inbox.FailTick(11);
            Assert.That(m_Inbox.IsFaulted, Is.True);
            Assert.That(m_Inbox.IsTickInFlight, Is.False);
            Assert.That(m_Inbox.LastSimulatedTick, Is.EqualTo(10));
            Assert.That(m_Inbox.LastProcessedCommandTick, Is.Zero);
            Assert.That(m_Inbox.PendingCommandCount, Is.EqualTo(2));
            Rejected(InboxTestFrames.Frame(13), TickCommandAdmissionStatus.Faulted);
            Assert.Throws<InvalidOperationException>(() => m_Inbox.BeginTick(11));
            Assert.Throws<InvalidOperationException>(() => m_Inbox.CompleteTick(11));
            m_Inbox.Close();
            Assert.That(m_Inbox.PendingCommandCount, Is.Zero);
            Rejected(InboxTestFrames.Frame(13), TickCommandAdmissionStatus.Closed);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CloseIsTerminalIncludingInFlightAndOldObjectCallbacksCannotReachNewContext(bool inFlight)
        {
            m_Inbox.TryAdmit(InboxTestFrames.Frame(11));
            if (inFlight) m_Inbox.BeginTick(11);
            m_Inbox.Close();
            Assert.That(m_Inbox.IsClosed, Is.True);
            Assert.That(m_Inbox.IsTickInFlight, Is.False);
            Assert.That(m_Inbox.PendingCommandCount, Is.Zero);
            Assert.That(m_Inbox.LastSimulatedTick, Is.EqualTo(10));
            Assert.DoesNotThrow(() => m_Inbox.Close());
            Rejected(Array.Empty<byte>(), TickCommandAdmissionStatus.Closed);
            Assert.Throws<InvalidOperationException>(() => m_Inbox.BeginTick(11));
            Assert.Throws<InvalidOperationException>(() => m_Inbox.CompleteTick(11));
            var context = InboxTestFrames.Context(session: 12);
            var next = new TickCommandInbox<InboxTestCommand, InboxTestCodec>(in context, default, 10, 4);
            Outcome(next.TryAdmit(InboxTestFrames.Frame(11)), TickCommandAdmissionStatus.FrameRejected,
                TickInputFrameError.SessionEpochMismatch);
            Outcome(next.TryAdmit(InboxTestFrames.Frame(11, context: context)), TickCommandAdmissionStatus.Accepted);
            Outcome(m_Inbox.TryAdmit(InboxTestFrames.Frame(11, context: context)), TickCommandAdmissionStatus.Closed);
            Assert.That(next.LastSimulatedTick, Is.EqualTo(10));
            next.Close();
        }

        [Test]
        public void ExpectedWorldSchemaAndRevisionRemainOwnerSuppliedNotDerivedFromWire()
        {
            var expected = m_Inbox.Context;
            Rejected(InboxTestFrames.Frame(11, context: InboxTestFrames.Context(world: 23)),
                TickCommandAdmissionStatus.FrameRejected, TickInputFrameError.WorldEpochMismatch);
            var schema = new TickInputFrameContext(11, 22, 0x102, 2);
            Rejected(InboxTestFrames.Frame(11, context: schema), TickCommandAdmissionStatus.FrameRejected,
                TickInputFrameError.CommandSchemaMismatch);
            var revision = new TickInputFrameContext(11, 22, 0x101, 3);
            Rejected(InboxTestFrames.Frame(11, context: revision), TickCommandAdmissionStatus.FrameRejected,
                TickInputFrameError.CodecRevisionMismatch);
            Assert.That(m_Inbox.Context, Is.EqualTo(expected));
        }

        private struct ThrowingCodec : ITickCommandCodec<InboxTestCommand>
        {
            public Exception Error;
            public ushort CommandSchemaId => 0x101;
            public ushort CodecRevision => 2;
            public bool TryDecode(ref NetBitReader reader, out InboxTestCommand command)
            {
                var probe = reader;
                probe.TryReadBits(8, out uint axis);
                if (axis == 229) throw Error;
                return new InboxTestCodec().TryDecode(ref reader, out command);
            }
        }

        [Test]
        public void CodecExceptionPropagatesUnchangedWithoutPublicationAdvancementOrExecutionFault()
        {
            var context = InboxTestFrames.Context();
            var error = new ApplicationException("Codec failure.");
            var inbox = new TickCommandInbox<InboxTestCommand, ThrowingCodec>(in context, new ThrowingCodec { Error = error });
            Assert.That(Assert.Throws<ApplicationException>(() => inbox.TryAdmit(InboxTestFrames.Frame(1, 101))), Is.SameAs(error));
            Assert.That(inbox.PendingCommandCount, Is.Zero);
            Assert.That(inbox.LastSimulatedTick, Is.Zero);
            Assert.That(inbox.LastProcessedCommandTick, Is.Zero);
            Assert.That(inbox.IsFaulted || inbox.IsTickInFlight, Is.False);
            Outcome(inbox.TryAdmit(InboxTestFrames.Frame(1, 4)), TickCommandAdmissionStatus.Accepted);
            Outcome(inbox.TryAdmit(InboxTestFrames.Frame(1, 101)), TickCommandAdmissionStatus.Duplicate); // No second codec invocation.
            Assert.That(inbox.BeginTick(1).Command.Axis, Is.EqualTo(4));
            inbox.CompleteTick(1);
            inbox.Close();
        }

        private struct ValueMutatingCodec : ITickCommandCodec<InboxTestCommand>
        {
            public int Calls;
            public ushort CommandSchemaId => 0x101;
            public ushort CodecRevision => 2;
            public bool TryDecode(ref NetBitReader reader, out InboxTestCommand command)
            {
                Calls++;
                command = default;
                return Calls == 1 && new InboxTestCodec().TryDecode(ref reader, out command);
            }
        }

        [Test]
        public void CodecValueStateIsCopiedForEveryAttemptRatherThanPersistingDecodeMutations()
        {
            var context = InboxTestFrames.Context();
            var codec = new ValueMutatingCodec();
            var inbox = new TickCommandInbox<InboxTestCommand, ValueMutatingCodec>(in context, codec);
            Outcome(inbox.TryAdmit(InboxTestFrames.Frame(1)), TickCommandAdmissionStatus.Accepted);
            Outcome(inbox.TryAdmit(InboxTestFrames.Frame(2)), TickCommandAdmissionStatus.Accepted);
            Assert.That(codec.Calls, Is.Zero);
            inbox.Close();
        }

        private struct ReentrantCodec : ITickCommandCodec<InboxTestCommand>
        {
            public Action DuringDecode;
            public ushort CommandSchemaId => 0x101;
            public ushort CodecRevision => 2;
            public bool TryDecode(ref NetBitReader reader, out InboxTestCommand command)
            {
                DuringDecode(); // Deliberate contract violation: the guard must protect inbox state.
                return new InboxTestCodec().TryDecode(ref reader, out command);
            }
        }

        [Test]
        public void ReentrantCodecCannotAdvanceCloseOrPublishAnotherCommand()
        {
            var context = InboxTestFrames.Context();
            TickCommandInbox<InboxTestCommand, ReentrantCodec> inbox = null;
            var frame = InboxTestFrames.Frame(1);
            var codec = new ReentrantCodec { DuringDecode = () =>
            {
                Outcome(inbox.TryAdmit(frame), TickCommandAdmissionStatus.DecodeInProgress);
                Assert.Throws<InvalidOperationException>(() => inbox.BeginTick(1));
                Assert.Throws<InvalidOperationException>(() => inbox.Close());
            } };
            inbox = new TickCommandInbox<InboxTestCommand, ReentrantCodec>(in context, codec);
            Outcome(inbox.TryAdmit(frame), TickCommandAdmissionStatus.Accepted);
            Assert.That(inbox.PendingCommandCount, Is.EqualTo(1));
            Assert.That(inbox.LastSimulatedTick, Is.Zero);
            inbox.Close();
        }

        [Test]
        public void ConstructorRejectsInvalidBoundsContextAndCodecAgreementBeforeAllocation()
        {
            var context = InboxTestFrames.Context();
            Assert.Throws<ArgumentException>(() => new TickCommandInbox<InboxTestCommand, InboxTestCodec>(default, default));
            foreach (int window in new[] { -1, 0, 1025, int.MaxValue })
                Assert.Throws<ArgumentOutOfRangeException>(() => new TickCommandInbox<InboxTestCommand, InboxTestCodec>(
                    in context, default, windowSize: window));
            foreach (int maximum in new[] { -1, TickInputFrameCodec.MaximumPayloadBits + 1, int.MaxValue })
                Assert.Throws<ArgumentOutOfRangeException>(() => new TickCommandInbox<InboxTestCommand, InboxTestCodec>(
                    in context, default, maxPayloadBits: maximum));
            var wrongSchema = new TickInputFrameContext(11, 22, 999, 2);
            var wrongRevision = new TickInputFrameContext(11, 22, 0x101, 3);
            Assert.Throws<ArgumentException>(() => new TickCommandInbox<InboxTestCommand, InboxTestCodec>(in wrongSchema, default));
            Assert.Throws<ArgumentException>(() => new TickCommandInbox<InboxTestCommand, InboxTestCodec>(in wrongRevision, default));
            var bounded = new TickCommandInbox<InboxTestCommand, InboxTestCodec>(in context, default, maxPayloadBits: 0);
            Outcome(bounded.TryAdmit(InboxTestFrames.Frame(1)), TickCommandAdmissionStatus.FrameRejected, TickInputFrameError.PayloadTooLarge);
            bounded.Close();
        }

        [Test]
        public void OwnerThreadGuardsEveryOperationAndReadWithoutChangingState()
        {
            var frame = InboxTestFrames.Frame(11);
            var before = State();
            Action[] operations =
            {
                () => m_Inbox.TryAdmit(frame), () => m_Inbox.BeginTick(11), () => m_Inbox.CompleteTick(11),
                () => m_Inbox.FailTick(11), () => m_Inbox.Close(), () => { _ = m_Inbox.Context; },
                () => { _ = m_Inbox.LastSimulatedTick; }, () => { _ = m_Inbox.LastProcessedCommandTick; },
                () => { _ = m_Inbox.WindowSize; }, () => { _ = m_Inbox.PendingCommandCount; },
                () => { _ = m_Inbox.IsClosed; }, () => { _ = m_Inbox.IsFaulted; }, () => { _ = m_Inbox.IsTickInFlight; }
            };
            var errors = new Exception[operations.Length];
            var thread = new Thread(() =>
            {
                for (int i = 0; i < operations.Length; i++)
                    try { operations[i](); } catch (Exception error) { errors[i] = error; }
            }) { IsBackground = true };
            thread.Start();
            Assert.That(thread.Join(10000), Is.True);
            foreach (var error in errors) Assert.That(error, Is.TypeOf<InvalidOperationException>());
            Assert.That(State(), Is.EqualTo(before));
        }

        [Test]
        public void WarmAdmissionSelectionCompletionAndOrdinaryRejectsAllocateNothing()
        {
            var context = InboxTestFrames.Context();
            var inbox = new TickCommandInbox<InboxTestCommand, InboxTestCodec>(in context, default, windowSize: 1);
            var frame = InboxTestFrames.Frame(1, 7);
            var invalid = InboxTestFrames.Frame(1, 101);
            var payload = frame.AsSpan(32).ToArray();
            var invalidPayload = invalid.AsSpan(32).ToArray();
            int successes = 0;
            TestDelegate body = () =>
            {
                successes = 0;
                for (int i = 0; i < 10000; i++)
                {
                    uint tick = inbox.LastSimulatedTick + 1;
                    var header = new TickInputFrameHeader(context.SessionEpoch, context.WorldEpoch, tick,
                        context.CommandSchemaId, context.CodecRevision, 43);
                    TickInputFrameCodec.TryEncode(in header, invalidPayload, invalid, out _, out _);
                    if (inbox.TryAdmit(invalid).Status == TickCommandAdmissionStatus.CommandRejected) successes++;
                    TickInputFrameCodec.TryEncode(in header, payload, frame, out _, out _);
                    if (inbox.TryAdmit(frame).Status == TickCommandAdmissionStatus.Accepted) successes++;
                    if (inbox.TryAdmit(frame).Status == TickCommandAdmissionStatus.Duplicate) successes++;
                    var selection = inbox.BeginTick(tick);
                    if (selection.HasCommand && selection.Command.Axis == 7) successes++;
                    inbox.CompleteTick(tick);
                    if (inbox.TryAdmit(frame).Status == TickCommandAdmissionStatus.StaleTick) successes++;
                }
            };
            for (int i = 0; i < 4; i++) body();
            NetcodeCodecAllocationTests.AssertZero(body); // Existing helper includes a separate positive-control test.
            Assert.That(successes, Is.EqualTo(50000));
            Assert.That(inbox.PendingCommandCount, Is.Zero);
            inbox.Close();
        }
    }
}
