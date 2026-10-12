using System;
using System.Reflection;
using EjoyFramework.Core.Ecs;
using EjoyFramework.GamePlay.Netcode.Messages;
using EjoyFramework.GamePlay.Netcode.Session;
using EjoyFramework.GamePlay.Netcode.Transport;
using EjoyFramework.GamePlay.Tests.Netcode.Messages;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests.Netcode.Session
{
    internal struct ServerTestCommand { public uint Value; }
    internal struct ServerTestCodec : ITickCommandCodec<ServerTestCommand>
    {
        public Exception Error;
        public ushort CommandSchemaId => 0x401;
        public ushort CodecRevision => 1;
        public bool TryDecode(ref NetBitReader reader, out ServerTestCommand command)
        {
            command = default;
            if (!reader.TryReadBits(8, out uint value)) return false;
            if (value == 255 && Error != null) throw Error;
            if (value > 10) return false;
            command = new ServerTestCommand { Value = value };
            return true;
        }
    }
    internal struct OtherServerTestCodec : ITickCommandCodec<ServerTestCommand>
    {
        public ushort CommandSchemaId => 0x401;
        public ushort CodecRevision => 1;
        public bool TryDecode(ref NetBitReader reader, out ServerTestCommand command)
            => new ServerTestCodec().TryDecode(ref reader, out command);
    }
    internal sealed class ServerTestSystem : ISystem, IDisposable
    {
        public Action<World, EcsTime> Action;
        public bool Disposed;
        public void Update(World world, CommandBuffer commands, in EcsTime time) => Action(world, time);
        public void Dispose() => Disposed = true;
    }
    internal sealed class ServerTestCapture : IEcsTickCompletedSink, IDisposable
    {
        public Action<World, EcsTime> Action;
        public bool Disposed;
        public void OnTickCompleted(World world, in EcsTime time) => Action(world, time);
        public void Dispose() => Disposed = true;
    }
    internal static class ServerTestFrames
    {
        internal static TickInputFrameContext Context(int id) => new TickInputFrameContext((ulong)id + 100, 7, 0x401, 1);
        internal static byte[] Frame(int id, uint tick, byte value)
        {
            var context = Context(id);
            var header = new TickInputFrameHeader(context.SessionEpoch, context.WorldEpoch, tick, 0x401, 1, 8);
            var frame = new byte[33];
            Assert.That(TickInputFrameCodec.TryEncode(in header, new[] { value }, frame, out _, out _), Is.True);
            return frame;
        }
        internal static TickServerOptions Options(int packets = 8, int bytes = 264, int events = 32, int globalBytes = 1056)
            => new TickServerOptions(maxPeers: 2, commandWindow: 8, maxPayloadBits: 8,
                maxPollEvents: events, maxPollDataBytes: globalBytes, maxPeerPacketsPerFrame: packets,
                maxPeerDataBytesPerFrame: bytes, tickRate: 8, maxTicksPerFrame: 4, maxFrameDelta: 1);
    }

    public class TickAuthoritativeServerTests
    {
        private struct Total { public int Calls; public uint Sum; }
        private World m_World;
        private Entity m_Total;
        private ServerFakeTransport m_Transport;
        private TickAuthoritativeServer<ServerTestCommand, ServerTestCodec> m_Server;
        private readonly TickPeerHandle[] m_Handles = new TickPeerHandle[2];
        private Exception m_CodecError;

        [SetUp]
        public void SetUp()
        {
            m_World = new World();
            m_Total = m_World.CreateEntity();
            m_World.Set(m_Total, new Total());
            m_Transport = new ServerFakeTransport();
            m_CodecError = new ApplicationException("Command codec failed.");
            m_Server = new TickAuthoritativeServer<ServerTestCommand, ServerTestCodec>(m_Transport, m_World,
                new ServerTestCodec { Error = m_CodecError }, ServerTestFrames.Options());
        }
        [TearDown]
        public void TearDown() { m_Server.Dispose(); m_World.Dispose(); }
        private void Connect(bool authorize = true)
        {
            m_Server.ConnectionObserved += handle =>
            {
                m_Handles[handle.SlotIndex] = handle;
                if (authorize)
                {
                    var context = ServerTestFrames.Context(handle.ConnectionId);
                    Assert.That(m_Server.AuthorizePeer(handle, in context), Is.EqualTo(TickPeerAuthorizationResult.Authorized));
                }
            };
            m_Server.Start(0);
            m_Transport.QueueConnected(1);
            m_Transport.QueueConnected(2);
            m_Server.RunFrame(0);
        }
        private TickPeerDiagnostics Peer(int slot)
        {
            Assert.That(m_Server.TryGetPeerDiagnostics(m_Handles[slot], out var value), Is.True);
            return value;
        }
        private void AddAccumulator()
        {
            m_Server.AddSystem(EcsPhase.TickSimulation, new ServerTestSystem { Action = (w, time) =>
            {
                ref var total = ref w.Get<Total>(m_Total);
                total.Calls++;
                var commands = m_Server.ActiveCommands;
                for (int i = 0; i < commands.Length; i++)
                {
                    Assert.That(commands[i].Selection.Tick, Is.EqualTo(time.Tick));
                    if (commands[i].Selection.HasCommand) total.Sum += commands[i].Selection.Command.Value;
                }
            } });
        }

        [Test]
        public void BurstsDoNotSimulateAndTwoPeersStillRunOneWorldTickAtATime()
        {
            AddAccumulator();
            Connect();
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 2, 2));
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 1, 1));
            m_Transport.QueueData(2, ServerTestFrames.Frame(2, 2, 3)); // Peer 2 is missing tick 1.
            int beforePolls = m_Transport.BoundedPollCalls;
            Assert.That(m_Server.RunFrame(0), Is.Zero);
            Assert.That(m_Transport.BoundedPollCalls, Is.EqualTo(beforePolls + 1));
            Assert.That(m_Transport.UnboundedPollCalls, Is.Zero);
            Assert.That(m_World.Get<Total>(m_Total).Calls, Is.Zero);
            Assert.That(m_Server.LastCompletedTick, Is.Zero);
            Assert.That(Peer(0).LastProcessedCommandTick, Is.Zero);
            Assert.That(m_Server.RunFrame(0.125), Is.EqualTo(1));
            Assert.That(m_World.Get<Total>(m_Total).Calls, Is.EqualTo(1));
            Assert.That(m_World.Get<Total>(m_Total).Sum, Is.EqualTo(1));
            Assert.That(Peer(0).LastProcessedCommandTick, Is.EqualTo(1));
            Assert.That(Peer(1).LastProcessedCommandTick, Is.Zero);
            Assert.That(m_Server.RunFrame(0.125), Is.EqualTo(1));
            Assert.That(m_World.Get<Total>(m_Total).Calls, Is.EqualTo(2));
            Assert.That(m_World.Get<Total>(m_Total).Sum, Is.EqualTo(6));
            Assert.That(Peer(1).LastProcessedCommandTick, Is.EqualTo(2));
            Assert.That(m_Server.ActiveCommands.IsEmpty, Is.True);
        }

        [Test]
        public void NoPeersAndCatchupStillDriveWorldOncePerCompletedTick()
        {
            AddAccumulator();
            m_Server.Start(0);
            Assert.That(m_Server.RunFrame(0.375), Is.EqualTo(3));
            Assert.That(m_World.Get<Total>(m_Total).Calls, Is.EqualTo(3));
            Assert.That(m_Server.LastCompletedTick, Is.EqualTo(3));
            Assert.That(m_Server.ConnectedPeerCount, Is.Zero);
            Assert.That(m_Transport.BoundedPollCalls, Is.EqualTo(1));
        }

        [Test]
        public void ConnectionObservationDoesNotAuthorizeAndUnknownTrafficNeverReachesCodec()
        {
            Connect(authorize: false);
            m_Transport.QueueData(99, ServerTestFrames.Frame(99, 1, 255));
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 1, 255));
            m_Server.RunFrame(0);
            Assert.That(m_Server.IsFaulted, Is.False);
            Assert.That(m_Server.ConnectedPeerCount, Is.EqualTo(2));
            Assert.That(m_Server.AuthorizedPeerCount, Is.Zero);
            Assert.That(Peer(0).IsAuthorized, Is.False);
            Assert.That(Peer(0).PendingCommandCount, Is.Zero);
            Assert.That(Peer(0).LastAdmission.Status, Is.EqualTo(TickCommandAdmissionStatus.NotEvaluated));
            var wrong = new TickInputFrameContext(1, 1, 9, 1);
            Assert.That(m_Server.AuthorizePeer(m_Handles[0], in wrong), Is.EqualTo(TickPeerAuthorizationResult.CodecMismatch));
            Assert.That(m_Server.AuthorizePeer(m_Handles[0], default), Is.EqualTo(TickPeerAuthorizationResult.InvalidContext));
        }

        [TestCase(1, 66)]
        [TestCase(2, 33)]
        public void PerPeerQuotasRunBeforeDecodeAndResetOnlyAtNextServerFrame(int packets, int bytes)
        {
            m_Server.Dispose();
            m_Transport = new ServerFakeTransport();
            m_Server = new TickAuthoritativeServer<ServerTestCommand, ServerTestCodec>(m_Transport, m_World,
                new ServerTestCodec { Error = m_CodecError }, ServerTestFrames.Options(packets, bytes));
            Connect();
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 1, 1));
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 2, 255)); // Would throw if parsed.
            m_Transport.QueueData(2, ServerTestFrames.Frame(2, 1, 3));
            m_Server.RunFrame(0);
            Assert.That(m_Server.IsFaulted, Is.False);
            Assert.That(Peer(0).QuotaDropsThisFrame, Is.EqualTo(1));
            Assert.That(Peer(0).PendingCommandCount, Is.EqualTo(1));
            Assert.That(Peer(1).PendingCommandCount, Is.EqualTo(1));
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 2, 2));
            m_Server.RunFrame(0);
            Assert.That(Peer(0).QuotaDropsThisFrame, Is.Zero);
            Assert.That(Peer(0).PendingCommandCount, Is.EqualTo(2));
            Assert.That(m_Transport.LastMaxEvents, Is.EqualTo(32));
            Assert.That(m_Transport.LastMaxBytes, Is.EqualTo(1056));
        }

        [Test]
        public void InvalidEpochIsRejectedWithoutAcknowledgmentOrSessionFault()
        {
            Connect();
            m_Transport.QueueData(1, ServerTestFrames.Frame(2, 1, 7));
            m_Server.RunFrame(0);
            Assert.That(Peer(0).LastAdmission.FrameError, Is.EqualTo(TickInputFrameError.SessionEpochMismatch));
            Assert.That(Peer(0).PendingCommandCount, Is.Zero);
            Assert.That(Peer(0).LastProcessedCommandTick, Is.Zero);
            Assert.That(m_Server.IsFaulted, Is.False);
        }

        [Test]
        public void CodecExceptionIsLatchedEvenWhenTransportSwallowsSubscriberExceptions()
        {
            Connect();
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 1, 255));
            m_Transport.QueueData(2, ServerTestFrames.Frame(2, 1, 1));
            Assert.That(Assert.Throws<ApplicationException>(() => m_Server.RunFrame(0.125)), Is.SameAs(m_CodecError));
            Assert.That(m_Server.IsFaulted, Is.True);
            Assert.That(m_Server.LastCompletedTick, Is.Zero);
            Assert.That(m_Server.ClockTick, Is.Zero);
            Assert.That(Peer(0).LastProcessedCommandTick, Is.Zero);
            Assert.That(Peer(1).PendingCommandCount, Is.Zero);
            Assert.That(m_Transport.ShutdownCalls, Is.EqualTo(1));
            Assert.That(m_Transport.SubscriberCount, Is.Zero);
            Assert.That(m_Transport.SwallowedExceptions, Is.Zero); // Server caught/latches internally, not via transport logging.
        }

        [Test]
        public void ThrowingConnectionObserverFaultsBeforeAnyTickAndCleansAllSubscriptions()
        {
            var error = new ApplicationException("Connection observer failed.");
            m_Server.ConnectionObserved += handle => throw error;
            m_Server.Start(0);
            m_Transport.QueueConnected(1);
            Assert.That(Assert.Throws<ApplicationException>(() => m_Server.RunFrame(0.125)), Is.SameAs(error));
            Assert.That(m_Server.LastCompletedTick, Is.Zero);
            Assert.That(m_Server.ClockTick, Is.Zero);
            Assert.That(m_Transport.SubscriberCount, Is.Zero);
            Assert.That(m_Transport.ShutdownCalls, Is.EqualTo(1));
        }

        [Test]
        public void SecondTickSimulationFailureKeepsEarlierCompletionAndNeverPublishesIncompleteAck()
        {
            var error = new ApplicationException("Second tick failed.");
            m_Server.AddSystem(EcsPhase.TickSimulation, new ServerTestSystem { Action = (w, t) =>
            {
                w.Get<Total>(m_Total).Calls++;
                if (t.Tick == 2) throw error;
            } });
            Connect();
            foreach (int id in new[] { 1, 2 })
            {
                m_Transport.QueueData(id, ServerTestFrames.Frame(id, 1, 1));
                m_Transport.QueueData(id, ServerTestFrames.Frame(id, 2, 2));
            }
            Assert.That(Assert.Throws<ApplicationException>(() => m_Server.RunFrame(0.25)), Is.SameAs(error));
            Assert.That(m_Server.LastCompletedTick, Is.EqualTo(1));
            Assert.That(m_Server.ClockTick, Is.EqualTo(2));
            Assert.That(m_World.Get<Total>(m_Total).Calls, Is.EqualTo(2));
            Assert.That(Peer(0).LastProcessedCommandTick, Is.EqualTo(1));
            Assert.That(Peer(1).LastProcessedCommandTick, Is.EqualTo(1));
            Assert.That(Peer(0).PendingCommandCount + Peer(1).PendingCommandCount, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => m_Server.RunFrame(0));
            Assert.That(m_Server.ActiveCommands.IsEmpty, Is.True);
        }

        [Test]
        public void FailingCaptureIsBeforeEveryPeerCompletion()
        {
            m_Server.Dispose();
            m_Transport = new ServerFakeTransport();
            var error = new ApplicationException("Capture failed.");
            var capture = new ServerTestCapture { Action = (w, t) =>
            {
                Assert.That(m_Server.ActiveCommands.Length, Is.EqualTo(2));
                Assert.That(Peer(0).LastProcessedCommandTick + Peer(1).LastProcessedCommandTick, Is.Zero);
                throw error;
            } };
            m_Server = new TickAuthoritativeServer<ServerTestCommand, ServerTestCodec>(m_Transport, m_World,
                default, ServerTestFrames.Options(), capture);
            AddAccumulator();
            Connect();
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 1, 1));
            m_Transport.QueueData(2, ServerTestFrames.Frame(2, 1, 2));
            Assert.That(Assert.Throws<ApplicationException>(() => m_Server.RunFrame(0.125)), Is.SameAs(error));
            Assert.That(m_Server.LastCompletedTick, Is.Zero);
            Assert.That(Peer(0).LastProcessedCommandTick + Peer(1).LastProcessedCommandTick, Is.Zero);
            Assert.That(m_World.Get<Total>(m_Total).Sum, Is.EqualTo(3));
            Assert.That(capture.Disposed, Is.False);
        }

        private static object Private(object target, string field)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(target);
        private TickCommandInbox<ServerTestCommand, ServerTestCodec> Inbox(int index)
        {
            object peer = ((Array)Private(m_Server, "m_Peers")).GetValue(index);
            return (TickCommandInbox<ServerTestCommand, ServerTestCodec>)Private(peer, "Inbox");
        }

        [Test]
        public void HalfBeginFailureFailsStartedFlightsAndClosesTheWholeSession()
        {
            Connect();
            // Test-only corruption: public APIs expose no inbox or slot mutation hook.
            var slots = (Array)Private(Inbox(1), "m_Slots");
            object occupied = slots.GetValue(1);
            occupied.GetType().GetField("Tick").SetValue(occupied, 99u);
            slots.SetValue(occupied, 1);
            Assert.Throws<InvalidOperationException>(() => m_Server.RunFrame(0.125));
            Assert.That(m_Server.IsFaulted, Is.True);
            Assert.That(Inbox(0).IsTickInFlight, Is.False);
            Assert.That(Inbox(0).IsClosed && Inbox(1).IsClosed, Is.True);
            Assert.That(Peer(0).LastProcessedCommandTick + Peer(1).LastProcessedCommandTick, Is.Zero);
            Assert.That(m_Server.LastCompletedTick, Is.Zero);
        }

        [Test]
        public void FlightsAreRevalidatedAfterCaptureBeforeAnyPeerCompletion()
        {
            m_Server.Dispose();
            m_Transport = new ServerFakeTransport();
            var capture = new ServerTestCapture { Action = (w, t) => Inbox(1).Close() }; // Test-only private corruption.
            m_Server = new TickAuthoritativeServer<ServerTestCommand, ServerTestCodec>(m_Transport, m_World,
                default, ServerTestFrames.Options(), capture);
            Connect();
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 1, 1));
            m_Transport.QueueData(2, ServerTestFrames.Frame(2, 1, 2));
            Assert.Throws<InvalidOperationException>(() => m_Server.RunFrame(0.125));
            Assert.That(Inbox(0).LastSimulatedTick, Is.Zero);
            Assert.That(m_Server.LastCompletedTick, Is.Zero);
            Assert.That(Peer(0).LastProcessedCommandTick + Peer(1).LastProcessedCommandTick, Is.Zero);
        }

        [Test]
        public void UnexpectedPollDuringEcsIsLatchedDroppedAndFaultsEvenIfTransportReturnsNormally()
        {
            m_Server.AddSystem(EcsPhase.TickSimulation, new ServerTestSystem { Action = (w, t) =>
            {
                w.Get<Total>(m_Total).Calls++;
                m_Transport.QueueData(1, ServerTestFrames.Frame(1, 2, 1));
                m_Transport.Poll(); // Forbidden external drive, not the server-owned pump.
            } });
            Connect();
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 1, 1));
            Assert.Throws<InvalidOperationException>(() => m_Server.RunFrame(0.125));
            Assert.That(m_Server.IsFaulted, Is.True);
            Assert.That(m_Server.LastCompletedTick, Is.Zero);
            Assert.That(Peer(0).LastProcessedCommandTick, Is.Zero);
            Assert.That(Peer(0).PendingCommandCount, Is.Zero);
        }

        [Test]
        public void RevokeDuringTickDefersUntilFullCommitAndNotificationsSeeConsistentState()
        {
            int notifications = 0;
            m_Server.PeerClosed += handle =>
            {
                notifications++;
                Assert.That(m_Server.LastCompletedTick, Is.EqualTo(1));
                Assert.That(m_Server.ActiveCommands.IsEmpty, Is.True);
                Assert.That(m_Server.TryGetPeerDiagnostics(handle, out var peer), Is.True);
                Assert.That(peer.IsConnected, Is.False);
                Assert.That(peer.LastProcessedCommandTick, Is.EqualTo(1));
            };
            m_Server.AddSystem(EcsPhase.TickSimulation, new ServerTestSystem { Action = (w, t) =>
            {
                if (t.Tick != 1) return;
                Assert.That(m_Server.RevokePeer(m_Handles[0]), Is.True);
                Assert.That(Peer(0).IsAuthorized, Is.True);
                Assert.That(Peer(0).RevocationPending, Is.True);
                Assert.That(m_Server.ActiveCommands.Length, Is.EqualTo(2));
                var context = ServerTestFrames.Context(2);
                Assert.Throws<InvalidOperationException>(() => m_Server.AuthorizePeer(m_Handles[1], in context));
            } });
            Connect();
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 1, 1));
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 2, 2));
            Assert.That(m_Server.RunFrame(0.25), Is.EqualTo(2));
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(Peer(0).LastProcessedCommandTick, Is.EqualTo(1));
            Assert.That(m_Server.AuthorizedPeerCount, Is.EqualTo(1));
            m_Server.PeerClosed -= null;
        }

        [Test]
        public void DisconnectQueuedDuringTickIsObservedOnlyByTheNextOwnedPump()
        {
            m_Server.AddSystem(EcsPhase.TickSimulation, new ServerTestSystem { Action = (w, t) =>
            {
                m_Transport.QueueDisconnected(1);
                Assert.That(Peer(0).IsConnected, Is.True);
            } });
            Connect();
            m_Transport.QueueData(1, ServerTestFrames.Frame(1, 1, 1));
            m_Server.RunFrame(0.125);
            Assert.That(Peer(0).IsConnected, Is.True);
            Assert.That(Peer(0).LastProcessedCommandTick, Is.EqualTo(1));
            m_Server.RunFrame(0);
            Assert.That(Peer(0).IsConnected, Is.False);
        }

        [Test]
        public void HandlesRejectSlotReuseAndDifferentServerInstancesIncludingDifferentCodecTypes()
        {
            Connect();
            var old = m_Handles[0];
            m_Server.RevokePeer(old);
            m_Server.RunFrame(0); // Drain the old disconnection before this fake reuses its ID.
            m_Transport.QueueConnected(1);
            m_Server.RunFrame(0);
            Assert.That(m_Handles[0].Generation, Is.GreaterThan(old.Generation));
            var context = ServerTestFrames.Context(1);
            Assert.That(m_Server.AuthorizePeer(old, in context), Is.EqualTo(TickPeerAuthorizationResult.InvalidPeer));
            Assert.That(m_Server.RevokePeer(old), Is.False);
            var transport = new ServerFakeTransport();
            using var otherWorld = new World();
            using var other = new TickAuthoritativeServer<ServerTestCommand, OtherServerTestCodec>(transport, otherWorld,
                default, ServerTestFrames.Options());
            TickPeerHandle foreign = default;
            other.ConnectionObserved += value => foreign = value;
            other.Start(0); transport.QueueConnected(1); other.RunFrame(0);
            Assert.That(foreign.ServerInstanceId, Is.Not.EqualTo(m_Handles[0].ServerInstanceId));
            Assert.That(other.AuthorizePeer(m_Handles[0], in context), Is.EqualTo(TickPeerAuthorizationResult.InvalidPeer));
        }

        [Test]
        public void StartupAndInputValidationAreBeforeSideEffectsAndStartupFailureIsTerminal()
        {
            Assert.Throws<InvalidOperationException>(() => m_Server.RunFrame(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Server.Start(-1));
            Assert.That(m_Transport.StartCalls + m_Transport.SubscriberCount, Is.Zero);
            var error = new ApplicationException("Start failed after partial transport setup.");
            m_Transport.StartError = error;
            Assert.That(Assert.Throws<ApplicationException>(() => m_Server.Start(0)), Is.SameAs(error));
            Assert.That(m_Transport.ShutdownCalls, Is.EqualTo(1));
            Assert.That(m_Transport.SubscriberCount, Is.Zero);
            Assert.That(m_Server.IsStarted, Is.False);
            Assert.That(m_Server.IsFaulted, Is.True);
            Assert.That(m_World.IsDisposed, Is.False);
        }

        [Test]
        public void InvalidDeltaIterationAndSetupChangesDoNotPumpOrAdvance()
        {
            m_Server.Start(0);
            foreach (double delta in new[] { -1d, double.NaN, double.PositiveInfinity })
                Assert.Throws<ArgumentOutOfRangeException>(() => m_Server.RunFrame(delta));
            m_World.Query().ForEach(e => Assert.Throws<InvalidOperationException>(() => m_Server.RunFrame(0)));
            Assert.Throws<InvalidOperationException>(() => m_Server.AddSystem(EcsPhase.TickSimulation, new ServerTestSystem()));
            Assert.That(m_Transport.BoundedPollCalls, Is.Zero);
            Assert.That(m_Server.LastCompletedTick, Is.Zero);
            Assert.That(m_Server.IsFaulted, Is.False);
        }

        [Test]
        public void OptionsRejectQuotasThatCannotAdmitOneMaximumConfiguredFrame()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickServerOptions(maxPeers: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickServerOptions(commandWindow: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickServerOptions(maxPollEvents: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickServerOptions(tickRate: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickServerOptions(maxFrameDelta: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickServerOptions(maxPayloadBits: int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickServerOptions(maxPayloadBits: 8,
                maxPollDataBytes: 32, maxPeerDataBytesPerFrame: 32));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickServerOptions(maxPayloadBits: 8,
                maxPeerDataBytesPerFrame: 32));
            var valid = new TickServerOptions(maxPayloadBits: 8, maxPollDataBytes: 33, maxPeerDataBytesPerFrame: 33);
            Assert.That(valid.MaximumFrameBytes, Is.EqualTo(33));
            Assert.That(new TickServerOptions().TickRate, Is.EqualTo(30));
        }

        [Test]
        public void DisposeOwnsTransportAndPrivateLoopButNotWorldGameSystemsOrCapture()
        {
            var system = new ServerTestSystem { Action = (w, t) => { } };
            m_Server.AddSystem(EcsPhase.TickSimulation, system);
            Connect();
            m_Server.Dispose();
            Assert.That(m_Transport.ShutdownCalls, Is.EqualTo(1));
            Assert.That(m_Transport.SubscriberCount, Is.Zero);
            Assert.That(system.Disposed, Is.False);
            Assert.That(m_World.IsDisposed, Is.False);
            Assert.DoesNotThrow(() => m_Server.Dispose());
        }

        [Test]
        public void IdleRevocation_RejectsDisposeReentryFromTransportKick()
        {
            AddAccumulator();
            Connect();
            int attempts = 0, closed = 0;
            m_Server.PeerClosed += peer => closed++;
            m_Transport.DuringKick = id =>
            {
                attempts++;
                Assert.Throws<InvalidOperationException>(() => m_Server.Dispose());
                Assert.That(m_Transport.ShutdownCalls, Is.Zero);
                Assert.That(m_Server.IsStarted, Is.True);
            };

            Assert.That(m_Server.RevokePeer(m_Handles[0]), Is.True);
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(closed, Is.EqualTo(1));
            Assert.That(m_Server.IsFaulted, Is.False);
            Assert.That(m_Transport.SubscriberCount, Is.EqualTo(3));
            Assert.That(m_Server.ConnectedPeerCount, Is.EqualTo(1));
            Assert.That(m_Server.RunFrame(0.125), Is.EqualTo(1));
            Assert.That(m_World.Get<Total>(m_Total).Calls, Is.EqualTo(1));
            Assert.That(m_Transport.ShutdownCalls, Is.Zero);
        }

        [Test]
        public void DeliveredDisconnectThenIdReuseInSamePoll_ProducesFreshHandle()
        {
            Connect();
            var previous = m_Handles[0];
            m_Transport.QueueDisconnected(1);
            m_Transport.QueueConnected(1); // No old events follow the delivered disconnect.
            m_Server.RunFrame(0);
            Assert.That(m_Server.IsFaulted, Is.False);
            Assert.That(m_Handles[0].Generation, Is.GreaterThan(previous.Generation));
            Assert.That(m_Server.TryGetPeerDiagnostics(previous, out _), Is.False);
            Assert.That(Peer(0).IsAuthorized, Is.True);
            Assert.That(m_Server.AuthorizedPeerCount, Is.EqualTo(2));
        }

        [Test]
        public void FinalTickCanComplete_ButNextAdvanceFaultsWithoutWrap()
        {
            m_Server.Dispose();
            m_Transport = new ServerFakeTransport();
            m_Server = new TickAuthoritativeServer<ServerTestCommand, ServerTestCodec>(m_Transport, m_World,
                default, new TickServerOptions(maxPeers: 2, maxPayloadBits: 8, tickRate: 8, initialTick: uint.MaxValue - 1));
            AddAccumulator();
            m_Server.Start(0);
            Assert.That(m_Server.RunFrame(0.125), Is.EqualTo(1));
            Assert.That(m_Server.LastCompletedTick, Is.EqualTo(uint.MaxValue));
            Assert.Throws<OverflowException>(() => m_Server.RunFrame(0.125));
            Assert.That(m_Server.LastCompletedTick, Is.EqualTo(uint.MaxValue));
            Assert.That(m_Server.ClockTick, Is.EqualTo(uint.MaxValue));
            Assert.That(m_World.Get<Total>(m_Total).Calls, Is.EqualTo(1));
            Assert.That(m_Server.IsFaulted, Is.True);
        }

        [Test]
        public void WarmFakeTransportAuthorityPathAllocatesNothingWithoutClaimingLoopbackZeroGc()
        {
            m_Server.AddSystem(EcsPhase.TickSimulation, new ServerTestSystem { Action = (w, t) => w.Get<Total>(m_Total).Calls++ });
            Connect();
            var context = ServerTestFrames.Context(1);
            var payload = new byte[] { 1 };
            var frame = new byte[33];
            int completed = 0;
            TestDelegate body = () =>
            {
                completed = 0;
                for (int i = 0; i < 1000; i++)
                {
                    var header = new TickInputFrameHeader(context.SessionEpoch, context.WorldEpoch,
                        m_Server.LastCompletedTick + 1, 0x401, 1, 8);
                    TickInputFrameCodec.TryEncode(in header, payload, frame, out _, out _);
                    m_Transport.QueueData(1, frame); // Fake stores a borrowed array until this immediate pump.
                    completed += m_Server.RunFrame(0.125);
                }
            };
            for (int i = 0; i < 4; i++) body();
            NetcodeCodecAllocationTests.AssertZero(body);
            Assert.That(completed, Is.EqualTo(1000));
        }
    }

    // Test double only: bounded fixed event storage, synchronous callbacks, deliberate subscriber
    // exception swallowing. Unlike real Loopback, this fake does not copy payloads/invocation lists.
    internal sealed class ServerFakeTransport : IBudgetedNetTransport
    {
        private struct Event { public byte Kind; public int Id; public ArraySegment<byte> Data; }
        private readonly Event[] m_Events = new Event[1024];
        private int m_Head, m_Count;
        public int StartCalls, ShutdownCalls, BoundedPollCalls, UnboundedPollCalls, SwallowedExceptions;
        public int LastMaxEvents, LastMaxBytes;
        public Exception StartError;
        public Action<int> DuringKick;
        public bool IsServer { get; private set; }
        public bool IsClient => false;
        public NetConnectionState ClientState => NetConnectionState.Disconnected;
        public event Action<int> OnClientConnected;
        public event Action<int> OnClientDisconnected;
        public event Action<int, ArraySegment<byte>> OnData;
        public int SubscriberCount => (OnClientConnected?.GetInvocationList().Length ?? 0)
            + (OnClientDisconnected?.GetInvocationList().Length ?? 0) + (OnData?.GetInvocationList().Length ?? 0);
        public void QueueConnected(int id) => Queue(new Event { Kind = 1, Id = id });
        public void QueueDisconnected(int id) => Queue(new Event { Kind = 2, Id = id });
        public void QueueData(int id, byte[] bytes) => Queue(new Event { Kind = 3, Id = id, Data = new ArraySegment<byte>(bytes) });
        private void Queue(Event value)
        {
            if (m_Count == m_Events.Length) throw new InvalidOperationException("Fake event capacity exhausted.");
            m_Events[(m_Head + m_Count++) % m_Events.Length] = value;
        }
        public void StartServer(int port) { StartCalls++; IsServer = true; if (StartError != null) throw StartError; }
        public void StopServer() => IsServer = false;
        public void Shutdown() { ShutdownCalls++; IsServer = false; Array.Clear(m_Events, 0, m_Events.Length); m_Head = 0; m_Count = 0; }
        public void Kick(int id) { DuringKick?.Invoke(id); QueueDisconnected(id); }
        public void Connect(string host, int port) => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public void Send(int id, ArraySegment<byte> data, NetDeliveryMethod method = NetDeliveryMethod.ReliableOrdered)
            => throw new NotSupportedException();
        public void Broadcast(ArraySegment<byte> data, NetDeliveryMethod method = NetDeliveryMethod.ReliableOrdered)
            => throw new NotSupportedException();
        public void Poll() { UnboundedPollCalls++; Poll(int.MaxValue, int.MaxValue); }
        public void Poll(int maxEvents, int maxDataBytes)
        {
            if (maxEvents < 0 || maxDataBytes < 0) throw new ArgumentOutOfRangeException();
            BoundedPollCalls++; LastMaxEvents = maxEvents; LastMaxBytes = maxDataBytes;
            int count = Math.Min(m_Count, maxEvents), bytes = 0;
            for (int i = 0; i < count && m_Count > 0; i++)
            {
                var item = m_Events[m_Head];
                if (item.Kind == 3 && item.Data.Count > 0 && maxDataBytes == 0) break;
                bool oversized = item.Kind == 3 && item.Data.Count > maxDataBytes;
                if (!oversized && item.Kind == 3 && item.Data.Count > maxDataBytes - bytes) break;
                m_Events[m_Head] = default; m_Head = (m_Head + 1) % m_Events.Length; m_Count--;
                if (oversized) continue;
                try
                {
                    if (item.Kind == 1) OnClientConnected?.Invoke(item.Id);
                    else if (item.Kind == 2) OnClientDisconnected?.Invoke(item.Id);
                    else { bytes += item.Data.Count; OnData?.Invoke(item.Id, item.Data); }
                }
                catch { SwallowedExceptions++; }
            }
        }
    }
}
