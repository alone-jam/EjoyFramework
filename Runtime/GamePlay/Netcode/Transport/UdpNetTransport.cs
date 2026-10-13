using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace EjoyFramework.GamePlay.Netcode.Transport
{
    /// <summary>Owner-thread, bounded UDP transport. Defaults to IPv4 loopback.
    /// Reliable messages use stop-and-wait with fragmentation and retransmission; snapshots can use
    /// an independent sequenced channel. Session nonces reject stale traffic, not authenticate users.
    /// No encryption or Internet congestion control is provided.</summary>
    public sealed partial class UdpNetTransport : IBudgetedNetTransport, IDisposable
    {
        public const int MaximumMessageBytes = 65536;
        internal const int HeaderBytes = 40, FragmentBytes = 1100;
        private const uint Magic = 0x554A5945;
        private enum Kind : byte { Hello = 1, Challenge, Confirm, Ready, Data, Ack, Ping, Bye }
        private sealed class Assembly
        {
            public uint Sequence;
            public byte[] Data;
            public ulong Mask;
            public int Count;
        }
        private sealed class Pending { public uint Sequence; public byte[] Data; public double Sent = -1, FirstSent = -1; }
        private sealed class Peer
        {
            public int Id;
            public IPEndPoint Endpoint;
            public ulong Nonce, Token;
            public bool Connected;
            public double Seen, ControlSent = -1;
            public uint SendReliable, ReceiveReliable, SendLoose, ReceiveLoose;
            public readonly Queue<Pending> Outgoing = new Queue<Pending>();
            public int OutgoingBytes;
            public Assembly Reliable, Loose;
        }
        private struct Event { public Kind Kind; public int Id; public byte[] Data; }
        private readonly int m_Owner = Environment.CurrentManagedThreadId;
        private readonly IPAddress m_BindAddress;
        private readonly double m_Retry, m_Timeout;
        private readonly int m_MaxPeers;
        private readonly Stopwatch m_Time = Stopwatch.StartNew();
        private readonly Dictionary<int, Peer> m_Peers = new Dictionary<int, Peer>();
        private readonly Queue<Event> m_Events = new Queue<Event>();
        private readonly List<Peer> m_Scratch = new List<Peer>();
        private readonly byte[] m_Receive = new byte[65536], m_Send = new byte[HeaderBytes + FragmentBytes];
        private Socket m_Socket;
        private Peer m_Client;
        private int m_NextId, m_QueuedBytes, m_Epoch;
        private bool m_Polling;
        public bool IsServer { get; private set; }
        public bool IsClient { get; private set; }
        public NetConnectionState ClientState { get; private set; }
        public int LocalPort => m_Socket == null ? 0 : ((IPEndPoint)m_Socket.LocalEndPoint).Port;
        public long Retransmissions { get; private set; }
        public long RejectedDatagrams { get; private set; }
        public long CallbackFailures { get; private set; }
        public event Action<int> OnClientConnected;
        public event Action<int> OnClientDisconnected;
        public event Action<int, ArraySegment<byte>> OnData;

        public UdpNetTransport(IPAddress bindAddress = null, int maxPeers = 8, double retrySeconds = 0.1, double timeoutSeconds = 10)
        {
            m_BindAddress = bindAddress ?? IPAddress.Loopback;
            if (m_BindAddress.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("IPv4 address required.", nameof(bindAddress));
            if (maxPeers < 1 || maxPeers > 128) throw new ArgumentOutOfRangeException(nameof(maxPeers));
            if (double.IsNaN(retrySeconds) || double.IsInfinity(retrySeconds) || retrySeconds < 0.01 || retrySeconds > 2) throw new ArgumentOutOfRangeException(nameof(retrySeconds));
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds < retrySeconds * 4 || timeoutSeconds > 120) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            m_MaxPeers = maxPeers; m_Retry = retrySeconds; m_Timeout = timeoutSeconds;
        }
        private double Now => m_Time.Elapsed.TotalSeconds;
        private void Owner() { if (Environment.CurrentManagedThreadId != m_Owner) throw new InvalidOperationException("Use the transport on its owner thread."); }
        private void Open(int port)
        {
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { socket.Bind(new IPEndPoint(m_BindAddress, port)); socket.Blocking = false; }
            catch { socket.Dispose(); throw; }
            m_Socket = socket;
        }
        public void StartServer(int port)
        {
            Owner(); if (m_Socket != null) throw new InvalidOperationException("Transport is already started.");
            Open(port); IsServer = true;
        }
        public void Connect(string host, int port)
        {
            Owner(); if (m_Socket != null) throw new InvalidOperationException("Transport is already started.");
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork || port < 1 || port > 65535)
                throw new ArgumentException("An IPv4 literal and valid destination port are required.");
            Open(0); IsClient = true; ClientState = NetConnectionState.Connecting;
            m_Client = new Peer { Id = NextId(), Endpoint = new IPEndPoint(address, port), Nonce = RandomId(), Seen = Now };
            m_Peers.Add(m_Client.Id, m_Client); Control(m_Client, Kind.Hello);
        }
        private int NextId() { if (m_NextId == int.MaxValue) throw new InvalidOperationException("Connection ID space exhausted."); return ++m_NextId; }
        private static ulong RandomId()
        {
            var bytes = new byte[8]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            ulong value = BitConverter.ToUInt64(bytes, 0); return value == 0 ? 1 : value;
        }
        public void Send(int connectionId, ArraySegment<byte> data, NetDeliveryMethod method = NetDeliveryMethod.ReliableOrdered)
        {
            Owner();
            if (data.Count > MaximumMessageBytes || (data.Array == null && data.Count != 0)) throw new ArgumentOutOfRangeException(nameof(data));
            if (method < NetDeliveryMethod.ReliableOrdered || method > NetDeliveryMethod.UnreliableSequenced) throw new ArgumentOutOfRangeException(nameof(method));
            Peer peer = m_Client;
            if (IsServer) m_Peers.TryGetValue(connectionId, out peer);
            if (peer == null || !peer.Connected) throw new InvalidOperationException("Peer is not connected.");
            if (method == NetDeliveryMethod.ReliableOrdered && (peer.Outgoing.Count >= 64 || peer.OutgoingBytes + data.Count > 1024 * 1024))
                throw new InvalidOperationException("Reliable send budget exhausted; apply backpressure.");
            var copy = new byte[data.Count]; if (data.Count != 0) Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Count);
            if (method == NetDeliveryMethod.ReliableOrdered)
            {
                var pending = new Pending { Sequence = ++peer.SendReliable, Data = copy };
                peer.Outgoing.Enqueue(pending); peer.OutgoingBytes += copy.Length;
                if (peer.Outgoing.Count == 1) Transmit(peer, pending, method);
            }
            else Transmit(peer, new Pending { Sequence = ++peer.SendLoose, Data = copy }, method);
        }
        public void Broadcast(ArraySegment<byte> data, NetDeliveryMethod method = NetDeliveryMethod.ReliableOrdered)
        {
            Owner(); if (!IsServer) throw new InvalidOperationException("Server role required.");
            foreach (var peer in m_Peers.Values) if (peer.Connected) Send(peer.Id, data, method);
        }
        public void Kick(int connectionId) { Owner(); if (IsServer && m_Peers.TryGetValue(connectionId, out var peer)) Drop(peer, true); }
        public void Disconnect()
        {
            Owner(); if (!IsClient) return;
            if (m_Client != null) Drop(m_Client, true);
            CloseSocket();
        }
        public void StopServer()
        {
            Owner(); if (!IsServer) return;
            SnapshotPeers(); foreach (var peer in m_Scratch) Drop(peer, true);
            CloseSocket();
        }
        public void Shutdown() { Owner(); StopServer(); Disconnect(); m_Events.Clear(); m_QueuedBytes = 0; m_Epoch++; }
        public void Dispose() => Shutdown();
        private void CloseSocket() { m_Socket?.Dispose(); m_Socket = null; IsServer = IsClient = false; ClientState = NetConnectionState.Disconnected; }
        private void Drop(Peer peer, bool notifyRemote)
        {
            if (!m_Peers.Remove(peer.Id)) return;
            if (notifyRemote && peer.Token != 0) Control(peer, Kind.Bye);
            bool connected = peer.Connected; peer.Connected = false;
            if (connected || peer == m_Client) Enqueue(new Event { Kind = Kind.Bye, Id = peer.Id });
            if (peer == m_Client) { m_Client = null; ClientState = NetConnectionState.Disconnected; }
        }
        private void SnapshotPeers() { m_Scratch.Clear(); foreach (var peer in m_Peers.Values) m_Scratch.Add(peer); }
        public void Poll() => Poll(256, 4 * 1024 * 1024);
        public void Poll(int maxEvents, int maxDataBytes)
        {
            Owner(); if (maxEvents < 0 || maxDataBytes < 0) throw new ArgumentOutOfRangeException();
            if (m_Polling) throw new InvalidOperationException("Poll cannot overlap or reenter.");
            m_Polling = true;
            try
            {
                int count = Math.Min(maxEvents, m_Events.Count), bytes = 0, epoch = m_Epoch;
                for (int i = 0; i < count && m_Events.Count > 0 && epoch == m_Epoch; i++)
                {
                    var evt = m_Events.Peek(); int size = evt.Data?.Length ?? 0;
                    if (size > maxDataBytes && maxDataBytes > 0) { m_Events.Dequeue(); m_QueuedBytes -= size; continue; }
                    if (size > maxDataBytes - bytes) break;
                    m_Events.Dequeue(); m_QueuedBytes -= size; bytes += size; Dispatch(evt);
                }
                // Incoming events wait until the next Poll; callbacks cannot expand the captured queue.
                if (m_Socket == null) return;
                for (int i = 0; i < 256; i++)
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0); int length;
                    try { length = m_Socket.ReceiveFrom(m_Receive, ref remote); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { break; }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset || e.SocketErrorCode == SocketError.ConnectionRefused || e.SocketErrorCode == SocketError.MessageSize) { continue; }
                    Receive((IPEndPoint)remote, length);
                }
                SnapshotPeers(); double now = Now;
                foreach (var peer in m_Scratch)
                {
                    if (now - peer.Seen > m_Timeout) { Drop(peer, true); continue; }
                    if (now - peer.ControlSent >= (peer.Connected ? Math.Min(0.25, m_Timeout / 4) : m_Retry))
                        Control(peer, peer.Connected ? Kind.Ping : IsServer ? Kind.Challenge : peer.Token == 0 ? Kind.Hello : Kind.Confirm);
                    if (peer.Outgoing.Count > 0 && peer.Outgoing.Peek().FirstSent >= 0 && now - peer.Outgoing.Peek().FirstSent > m_Timeout)
                    { Drop(peer, true); continue; }
                    if (peer.Connected && peer.Outgoing.Count > 0 && now - peer.Outgoing.Peek().Sent >= m_Retry)
                    { Retransmissions++; Transmit(peer, peer.Outgoing.Peek(), NetDeliveryMethod.ReliableOrdered); }
                }
            }
            finally { m_Polling = false; }
        }
        private bool Enqueue(Event evt)
        {
            int size = evt.Data?.Length ?? 0;
            if (m_Events.Count >= 512 || size > 4 * 1024 * 1024 - m_QueuedBytes) return false;
            m_Events.Enqueue(evt); m_QueuedBytes += size; return true;
        }
        private void Dispatch(Event evt)
        {
            Delegate handlers = evt.Kind == Kind.Data ? (Delegate)OnData : evt.Kind == Kind.Ready ? OnClientConnected : OnClientDisconnected;
            if (handlers == null) return;
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    if (evt.Kind == Kind.Data) ((Action<int, ArraySegment<byte>>)handler)(evt.Id, new ArraySegment<byte>(evt.Data));
                    else ((Action<int>)handler)(evt.Id);
                }
                catch { CallbackFailures++; }
            }
        }
    }
}
