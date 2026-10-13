using System;
using System.Net;
using System.Net.Sockets;

namespace EjoyFramework.GamePlay.Netcode.Transport
{
    public sealed partial class UdpNetTransport
    {
        private static bool Newer(uint value, uint previous) => unchecked((int)(value - previous)) > 0;
        private static uint Read32(byte[] bytes, int offset) => (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
        private static ulong Read64(byte[] bytes, int offset) => Read32(bytes, offset) | ((ulong)Read32(bytes, offset + 4) << 32);
        private static void Write32(byte[] bytes, int offset, uint value)
        { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (8 * i)); }
        private static void Write64(byte[] bytes, int offset, ulong value)
        { Write32(bytes, offset, (uint)value); Write32(bytes, offset + 4, (uint)(value >> 32)); }
        private void Packet(Peer peer, Kind kind, NetDeliveryMethod method = NetDeliveryMethod.ReliableOrdered,
            uint sequence = 0, byte[] data = null, int fragment = 0, int fragments = 0)
        {
            if (m_Socket == null) return;
            Array.Clear(m_Send, 0, HeaderBytes);
            Write32(m_Send, 0, Magic); m_Send[4] = 1; m_Send[5] = (byte)kind; m_Send[6] = (byte)method;
            Write64(m_Send, 8, peer.Nonce); Write64(m_Send, 16, peer.Token); Write32(m_Send, 24, sequence);
            int length = data?.Length ?? 0, offset = fragment * FragmentBytes;
            int size = data == null ? 0 : Math.Min(FragmentBytes, length - offset);
            Write32(m_Send, 28, (uint)length);
            m_Send[32] = (byte)fragment; m_Send[34] = (byte)fragments;
            m_Send[36] = (byte)size; m_Send[37] = (byte)(size >> 8);
            if (size > 0) Buffer.BlockCopy(data, offset, m_Send, HeaderBytes, size);
            try { m_Socket.SendTo(m_Send, 0, HeaderBytes + size, SocketFlags.None, peer.Endpoint); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock || e.SocketErrorCode == SocketError.NoBufferSpaceAvailable || e.SocketErrorCode == SocketError.ConnectionRefused || e.SocketErrorCode == SocketError.ConnectionReset) { }
        }
        private void Control(Peer peer, Kind kind) { Packet(peer, kind); peer.ControlSent = Now; }
        private void Transmit(Peer peer, Pending pending, NetDeliveryMethod method)
        {
            int count = Math.Max(1, (pending.Data.Length + FragmentBytes - 1) / FragmentBytes);
            for (int i = 0; i < count; i++) Packet(peer, Kind.Data, method, pending.Sequence, pending.Data, i, count);
            pending.Sent = Now;
            if (pending.FirstSent < 0) pending.FirstSent = pending.Sent;
        }
        private void Connected(Peer peer)
        {
            if (peer.Connected) return;
            peer.Connected = true;
            if (peer == m_Client) ClientState = NetConnectionState.Connected;
            if (!Enqueue(new Event { Kind = Kind.Ready, Id = peer.Id })) Drop(peer, true);
        }
        private void Receive(IPEndPoint endpoint, int length)
        {
            if (length < HeaderBytes || Read32(m_Receive, 0) != Magic || m_Receive[4] != 1 ||
                m_Receive[7] != 0 || m_Receive[33] != 0 || m_Receive[35] != 0 || m_Receive[38] != 0 || m_Receive[39] != 0)
            { RejectedDatagrams++; return; }
            var kind = (Kind)m_Receive[5]; var method = (NetDeliveryMethod)m_Receive[6];
            ulong nonce = Read64(m_Receive, 8), token = Read64(m_Receive, 16);
            uint sequence = Read32(m_Receive, 24), total = Read32(m_Receive, 28);
            int fragment = m_Receive[32], count = m_Receive[34], size = m_Receive[36] | m_Receive[37] << 8;
            if (kind < Kind.Hello || kind > Kind.Bye || method > NetDeliveryMethod.UnreliableSequenced || nonce == 0 ||
                (kind != Kind.Data && (length != HeaderBytes || total != 0 || size != 0 || fragment != 0 || count != 0 || method != NetDeliveryMethod.ReliableOrdered || (kind != Kind.Ack && sequence != 0))))
            { RejectedDatagrams++; return; }
            Peer peer = null;
            Peer endpointPeer = null;
            foreach (var candidate in m_Peers.Values)
            {
                if (!candidate.Endpoint.Equals(endpoint)) continue;
                if (candidate.Nonce == nonce) peer = candidate;
                if (!candidate.Connected || endpointPeer == null) endpointPeer = candidate;
            }
            if (kind == Kind.Hello && IsServer && token == 0)
            {
                if (peer == null)
                {
                    // One unconfirmed replacement per endpoint. An unauthenticated Hello must
                    // neither evict a live peer nor refresh its timeout.
                    if (endpointPeer != null && !endpointPeer.Connected) return;
                    if ((endpointPeer == null && m_Peers.Count >= m_MaxPeers) ||
                        m_Peers.Count >= m_MaxPeers * 2 || m_Events.Count + m_Peers.Count >= 256) return;
                    peer = new Peer { Id = NextId(), Endpoint = endpoint, Nonce = nonce, Token = RandomId(), Seen = Now };
                    m_Peers.Add(peer.Id, peer);
                }
                Control(peer, Kind.Challenge); return;
            }
            if (peer == null || peer.Nonce != nonce) { RejectedDatagrams++; return; }
            if (kind == Kind.Challenge && IsClient && !peer.Connected && token != 0 && (peer.Token == 0 || peer.Token == token))
            { peer.Token = token; peer.Seen = Now; Control(peer, Kind.Confirm); return; }
            if (token == 0 || peer.Token != token) { RejectedDatagrams++; return; }
            if (kind == Kind.Confirm && IsServer)
            {
                if (!peer.Connected)
                {
                    // Only a round trip with this candidate's fresh token can replace
                    // the previous session. Queue its disconnect before the new connect.
                    SnapshotPeers();
                    foreach (var previous in m_Scratch)
                        if (previous != peer && previous.Endpoint.Equals(endpoint)) Drop(previous, true);
                }
                peer.Seen = Now; Connected(peer); Control(peer, Kind.Ready); return;
            }
            if (kind == Kind.Ready && IsClient) { peer.Seen = Now; Connected(peer); return; }
            if (kind == Kind.Bye) { Drop(peer, false); return; }
            if (!peer.Connected) return;
            if (kind == Kind.Ping) { peer.Seen = Now; return; }
            if (kind == Kind.Ack)
            {
                if (peer.Outgoing.Count > 0 && peer.Outgoing.Peek().Sequence == sequence)
                {
                    peer.OutgoingBytes -= peer.Outgoing.Dequeue().Data.Length; peer.Seen = Now;
                    if (peer.Outgoing.Count > 0) Transmit(peer, peer.Outgoing.Peek(), NetDeliveryMethod.ReliableOrdered);
                }
                return;
            }
            if (kind != Kind.Data || total > MaximumMessageBytes || count != Math.Max(1, ((int)total + FragmentBytes - 1) / FragmentBytes) ||
                fragment >= count || size != Math.Min(FragmentBytes, (int)total - fragment * FragmentBytes) || length != HeaderBytes + size)
            { RejectedDatagrams++; return; }
            peer.Seen = Now;
            bool reliable = method == NetDeliveryMethod.ReliableOrdered;
            if (reliable && sequence == peer.ReceiveReliable) { Packet(peer, Kind.Ack, sequence: sequence); return; }
            if (reliable && sequence != unchecked(peer.ReceiveReliable + 1)) return;
            if (!reliable && !Newer(sequence, peer.ReceiveLoose)) return;
            var assembly = reliable ? peer.Reliable : peer.Loose;
            if (assembly == null || assembly.Sequence != sequence)
            {
                if (assembly != null && !Newer(sequence, assembly.Sequence)) return;
                assembly = new Assembly { Sequence = sequence, Data = new byte[total], Count = count };
                if (reliable) peer.Reliable = assembly; else peer.Loose = assembly;
            }
            if (assembly.Data.Length != total || assembly.Count != count) { RejectedDatagrams++; return; }
            ulong bit = 1UL << fragment;
            if ((assembly.Mask & bit) == 0)
            {
                if (size > 0) Buffer.BlockCopy(m_Receive, HeaderBytes, assembly.Data, fragment * FragmentBytes, size);
                assembly.Mask |= bit;
            }
            if (assembly.Mask != (1UL << count) - 1) return;
            // Reserve queue slots for disconnect/control events. Reliable frames are not ACKed until queued.
            if (m_Events.Count >= 256 || !Enqueue(new Event { Kind = Kind.Data, Id = peer.Id, Data = assembly.Data })) return;
            if (reliable) { peer.ReceiveReliable = sequence; peer.Reliable = null; Packet(peer, Kind.Ack, sequence: sequence); }
            else { peer.ReceiveLoose = sequence; peer.Loose = null; }
        }
    }
}
