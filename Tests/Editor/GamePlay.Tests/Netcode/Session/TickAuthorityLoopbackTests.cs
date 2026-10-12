using System;
using EjoyFramework.Core.Ecs;
using EjoyFramework.GamePlay.Netcode.Session;
using EjoyFramework.GamePlay.Netcode.Transport;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests.Netcode.Session
{
    public class TickAuthorityLoopbackTests
    {
        private struct State { public int Calls; public uint Sum; }

        [Test]
        public void TwoRealLoopbackPeersOutOfOrderAndMissingCommandsDriveOnlyServerFrames()
        {
            var pair = LoopbackTransport.CreatePair();
            var second = LoopbackTransport.CreateClient(pair.server);
            using var world = new World();
            var entity = world.CreateEntity();
            world.Set(entity, new State());
            using var server = new TickAuthoritativeServer<ServerTestCommand, ServerTestCodec>(
                (IBudgetedNetTransport)pair.server, world, default, ServerTestFrames.Options());
            var handles = new TickPeerHandle[2];
            server.ConnectionObserved += handle =>
            {
                handles[handle.SlotIndex] = handle;
                var context = ServerTestFrames.Context(handle.ConnectionId);
                // Local test fixture authorization only; observing a network connection is not authentication.
                Assert.That(server.AuthorizePeer(handle, in context), Is.EqualTo(TickPeerAuthorizationResult.Authorized));
            };
            server.AddSystem(EcsPhase.TickSimulation, new ServerTestSystem { Action = (w, t) =>
            {
                w.Get<State>(entity).Calls++;
                foreach (var item in server.ActiveCommands)
                    if (item.Selection.HasCommand) w.Get<State>(entity).Sum += item.Selection.Command.Value;
            } });
            server.Start(0);
            pair.client.Connect("local", 0);
            second.Connect("local", 0);
            try
            {
                Assert.That(server.RunFrame(0), Is.Zero); // The only server transport pump is inside RunFrame.
                pair.client.Send(0, new ArraySegment<byte>(ServerTestFrames.Frame(handles[0].ConnectionId, 2, 2)));
                pair.client.Send(0, new ArraySegment<byte>(ServerTestFrames.Frame(handles[0].ConnectionId, 1, 1)));
                second.Send(0, new ArraySegment<byte>(ServerTestFrames.Frame(handles[1].ConnectionId, 2, 3)));
                Assert.That(server.RunFrame(0), Is.Zero);
                Assert.That(world.Get<State>(entity).Calls, Is.Zero);
                Assert.That(server.RunFrame(0.125), Is.EqualTo(1));
                Assert.That(server.TryGetPeerDiagnostics(handles[1], out var missing), Is.True);
                Assert.That(missing.LastProcessedCommandTick, Is.Zero);
                Assert.That(server.RunFrame(0.125), Is.EqualTo(1));
                Assert.That(world.Get<State>(entity).Calls, Is.EqualTo(2));
                Assert.That(world.Get<State>(entity).Sum, Is.EqualTo(6));
                Assert.That(server.LastCompletedTick, Is.EqualTo(2));
            }
            finally { pair.client.Shutdown(); second.Shutdown(); }
        }

        [Test]
        public void UnauthenticatedLoopbackBytesStillConsumeGlobalPumpBudget()
        {
            var pair = LoopbackTransport.CreatePair();
            var second = LoopbackTransport.CreateClient(pair.server);
            using var world = new World();
            var options = ServerTestFrames.Options(packets: 2, bytes: 33, events: 8, globalBytes: 33);
            using var server = new TickAuthoritativeServer<ServerTestCommand, ServerTestCodec>(
                (IBudgetedNetTransport)pair.server, world, default, options);
            var handles = new TickPeerHandle[2];
            server.ConnectionObserved += handle =>
            {
                handles[handle.SlotIndex] = handle;
                if (handle.SlotIndex != 0) return; // Second peer remains untrusted.
                var context = ServerTestFrames.Context(handle.ConnectionId);
                server.AuthorizePeer(handle, in context);
            };
            server.Start(0);
            pair.client.Connect("local", 0); second.Connect("local", 0);
            try
            {
                server.RunFrame(0);
                second.Send(0, new ArraySegment<byte>(ServerTestFrames.Frame(handles[1].ConnectionId, 1, 1)));
                pair.client.Send(0, new ArraySegment<byte>(ServerTestFrames.Frame(handles[0].ConnectionId, 1, 2)));
                server.RunFrame(0);
                Assert.That(server.TryGetPeerDiagnostics(handles[0], out var waiting), Is.True);
                Assert.That(waiting.PendingCommandCount, Is.Zero); // First peer's data stayed at the budgeted FIFO head.
                server.RunFrame(0);
                Assert.That(server.TryGetPeerDiagnostics(handles[0], out var admitted), Is.True);
                Assert.That(admitted.PendingCommandCount, Is.EqualTo(1));
                Assert.That(server.TryGetPeerDiagnostics(handles[1], out var untrusted), Is.True);
                Assert.That(untrusted.IsAuthorized, Is.False);
                Assert.That(untrusted.PendingCommandCount, Is.Zero);
            }
            finally { pair.client.Shutdown(); second.Shutdown(); }
        }

        [Test]
        public void RealDisconnectDuringSimulationCannotCloseAnInFlightPeerBeforeCommit()
        {
            var pair = LoopbackTransport.CreatePair();
            using var world = new World();
            using var server = new TickAuthoritativeServer<ServerTestCommand, ServerTestCodec>(
                (IBudgetedNetTransport)pair.server, world, default, ServerTestFrames.Options());
            TickPeerHandle handle = default;
            server.ConnectionObserved += value =>
            {
                handle = value;
                var context = ServerTestFrames.Context(value.ConnectionId);
                server.AuthorizePeer(value, in context);
            };
            server.AddSystem(EcsPhase.TickSimulation, new ServerTestSystem { Action = (w, t) => pair.client.Disconnect() });
            server.Start(0); pair.client.Connect("local", 0);
            try
            {
                server.RunFrame(0);
                pair.client.Send(0, new ArraySegment<byte>(ServerTestFrames.Frame(handle.ConnectionId, 1, 1)));
                server.RunFrame(0.125);
                Assert.That(server.TryGetPeerDiagnostics(handle, out var committed), Is.True);
                Assert.That(committed.IsConnected, Is.True);
                Assert.That(committed.LastProcessedCommandTick, Is.EqualTo(1));
                server.RunFrame(0);
                Assert.That(server.TryGetPeerDiagnostics(handle, out var disconnected), Is.True);
                Assert.That(disconnected.IsConnected, Is.False);
                Assert.That(disconnected.LastProcessedCommandTick, Is.EqualTo(1));
            }
            finally { pair.client.Shutdown(); }
        }
    }
}
