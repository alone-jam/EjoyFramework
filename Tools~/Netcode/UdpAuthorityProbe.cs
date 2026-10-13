using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using EjoyFramework.Core.Ecs;
using EjoyFramework.GamePlay.Netcode.Messages;
using EjoyFramework.GamePlay.Netcode.Session;
using EjoyFramework.GamePlay.Netcode.Transport;

internal static class UdpAuthorityProbe
{
    private struct Command { public byte Value; }
    private struct Codec : ITickCommandCodec<Command>
    {
        public ushort CommandSchemaId => 0xFA01;
        public ushort CodecRevision => 1;
        public bool TryDecode(ref NetBitReader reader, out Command command)
        {
            command = default;
            if (!reader.TryReadBits(8, out uint value) || value > 1 || reader.RemainingBits != 0) return false;
            command.Value = (byte)value; return true;
        }
    }
    private sealed class State { public int Sum, Accepted, Missing; }
    private sealed class Simulation : ISystem
    {
        public TickAuthoritativeServer<Command, Codec> Server;
        public readonly Dictionary<int, State> States = new Dictionary<int, State>();
        public int Calls;
        public void Update(World world, CommandBuffer buffer, in EcsTime time)
        {
            Calls++;
            foreach (var active in Server.ActiveCommands)
            {
                var state = States[active.Peer.ConnectionId];
                if (active.Selection.HasCommand) { state.Accepted++; state.Sum += active.Selection.Command.Value; }
                else state.Missing++;
            }
        }
    }
    private static byte[] Snapshot(uint tick, State state, bool final)
    {
        var data = new byte[20]; data[0] = 0xFA; data[1] = final ? (byte)2 : (byte)1;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), tick);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), state.Sum);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), state.Accepted);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), state.Missing); return data;
    }
    public static int Run(string[] args)
    {
        int port = int.Parse(args[1]); string report = args[2]; int role = args[0] == "authority-server" ? 0 : int.Parse(args[3]);
        int rate = int.Parse(args[role == 0 ? 3 : 4]);
        using var transport = new UdpNetTransport(maxPeers: 2, retrySeconds: 0.04, timeoutSeconds: 4);
        var watch = Stopwatch.StartNew();
        int connected = 0, disconnected = 0;
        transport.OnClientConnected += id => connected++;
        transport.OnClientDisconnected += id => disconnected++;
        if (role == 0)
        {
            using var world = new World();
            using var server = new TickAuthoritativeServer<Command, Codec>(transport, world, new Codec(),
                new TickServerOptions(maxPeers: 2, commandWindow: 64, maxPayloadBits: 8, tickRate: rate));
            var simulation = new Simulation { Server = server }; server.AddSystem(EcsPhase.TickSimulation, simulation);
            server.ConnectionObserved += handle =>
            {
                var context = new TickInputFrameContext((ulong)handle.ConnectionId + 100, 1, 0xFA01, 1);
                if (server.AuthorizePeer(handle, in context) != TickPeerAuthorizationResult.Authorized) throw new InvalidOperationException("Authorization failed");
                simulation.States.Add(handle.ConnectionId, new State());
                var welcome = new TickSessionWelcome(context, server.ClockTick, (ushort)rate, handle.ConnectionId);
                var bytes = new byte[TickSessionWelcomeCodec.Size];
                if (!TickSessionWelcomeCodec.TryEncode(in welcome, bytes)) throw new InvalidOperationException("Welcome encode failed");
                transport.Send(handle.ConnectionId, new ArraySegment<byte>(bytes));
            };
            var live = new HashSet<int>(); server.ConnectionObserved += handle => live.Add(handle.ConnectionId);
            server.PeerClosed += handle => live.Remove(handle.ConnectionId);
            server.Start(port);
            File.WriteAllText(report + ".ready", JsonSerializer.Serialize(new { port = transport.LocalPort, pid = Environment.ProcessId, address = "127.0.0.1" }));
            double previous = watch.Elapsed.TotalSeconds; uint lastSnapshot = 0; bool finalSent = false;
            while (watch.Elapsed.TotalSeconds < 18)
            {
                double now = watch.Elapsed.TotalSeconds, delta = Math.Min(0.2, now - previous); previous = now;
                server.RunFrame(finalSent ? 0 : delta);
                bool final = server.ClockTick >= rate * 6;
                if ((!finalSent && final) || (!final && server.ClockTick - lastSnapshot >= 3))
                {
                    foreach (int id in live) transport.Send(id, new ArraySegment<byte>(Snapshot(server.ClockTick, simulation.States[id], final)),
                        final ? NetDeliveryMethod.ReliableOrdered : NetDeliveryMethod.UnreliableSequenced);
                    lastSnapshot = server.ClockTick; finalSent |= final;
                }
                if (finalSent && disconnected == 2) break;
                Thread.Sleep(2);
            }
            bool passed = connected == 2 && disconnected == 2 && simulation.Calls == server.ClockTick && server.ClockTick >= rate * 6 && !server.IsFaulted && transport.CallbackFailures == 0;
            var states = new List<object>();
            foreach (var pair in simulation.States)
            {
                var state = pair.Value; passed &= state.Sum == state.Accepted && state.Accepted > rate * 3;
                states.Add(new { id = pair.Key, sum = state.Sum, accepted = state.Accepted, missing = state.Missing });
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new { mode = args[0], pid = Environment.ProcessId, passed, connected, disconnected,
                tickRate = rate, ticks = server.ClockTick, simulationCalls = simulation.Calls, states, callbackFailures = transport.CallbackFailures }));
            return passed ? 0 : 1;
        }
        TickSessionWelcome welcomeState = default;
        bool initialized = false, finished = false, correct = true; uint lastTick = 0, lastSent = 0;
        int snapshots = 0, sum = 0, packets = 0; double started = 0;
        transport.OnData += (id, data) =>
        {
            var bytes = new ReadOnlySpan<byte>(data.Array, data.Offset, data.Count);
            if (TickSessionWelcomeCodec.TryDecode(bytes, 0xFA01, 1, out var welcome))
            { welcomeState = welcome; initialized = true; started = watch.Elapsed.TotalSeconds; lastSent = welcome.ServerTick; return; }
            if (bytes.Length != 20 || bytes[0] != 0xFA) { correct = false; return; }
            uint tick = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4));
            if (tick < lastTick) return;
            lastTick = tick; snapshots++; sum = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8));
            correct &= sum == BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(12));
            if (bytes[1] == 2) finished = true;
        };
        transport.Connect("127.0.0.1", port);
        while (!finished && watch.Elapsed.TotalSeconds < 18)
        {
            transport.Poll();
            if (initialized && !finished)
            {
                uint estimated = welcomeState.ServerTick + (uint)((watch.Elapsed.TotalSeconds - started) * welcomeState.TickRate);
                uint horizon = estimated + (uint)(rate / 3);
                // Redundant future command frames tolerate packet loss; admission never runs simulation.
                uint start = Math.Max(welcomeState.ServerTick + 1, lastSent > 7 ? lastSent - 7 : 1);
                for (uint tick = start; tick <= horizon; tick++)
                {
                    var h = new TickInputFrameHeader(welcomeState.Context.SessionEpoch, 1, tick, 0xFA01, 1, 8);
                    var frame = new byte[TickInputFrameCodec.HeaderSize + 1];
                    if (!TickInputFrameCodec.TryEncode(in h, new byte[] { 1 }, frame, out _, out _)) throw new InvalidOperationException("Encode failed");
                    transport.Send(0, new ArraySegment<byte>(frame), NetDeliveryMethod.Unreliable); packets++;
                }
                lastSent = Math.Max(lastSent, horizon);
            }
            Thread.Sleep(role == 1 ? 5 : 33);
        }
        transport.Disconnect(); transport.Poll();
        bool clientPassed = finished && correct && initialized && snapshots > 20 && sum > rate * 3 && transport.CallbackFailures == 0;
        File.WriteAllText(report, JsonSerializer.Serialize(new { mode = args[0], pid = Environment.ProcessId, role, passed = clientPassed, connected, disconnected,
            tickRate = rate, serverTick = lastTick, snapshots, sum, packets, pollIntervalMs = role == 1 ? 5 : 33, callbackFailures = transport.CallbackFailures }));
        return clientPassed ? 0 : 1;
    }
}
