using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using EjoyFramework.GamePlay.Netcode.Transport;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests.Netcode
{
    public sealed class UdpNetTransportTests
    {
        private UdpNetTransport server, client;
        private int peer;
        [SetUp] public void Setup()
        {
            server = new UdpNetTransport(retrySeconds: 0.02, timeoutSeconds: 1);
            client = new UdpNetTransport(retrySeconds: 0.02, timeoutSeconds: 1);
            server.OnClientConnected += id => peer = id;
            server.StartServer(0); client.Connect("127.0.0.1", server.LocalPort);
            Pump(() => peer > 0 && client.ClientState == NetConnectionState.Connected);
        }
        [TearDown] public void Cleanup() { client.Dispose(); server.Dispose(); peer = 0; }
        private void Pump(Func<bool> done)
        {
            var time = Stopwatch.StartNew();
            do { server.Poll(); client.Poll(); Thread.Sleep(1); } while (!done() && time.Elapsed.TotalSeconds < 4);
            Assert.That(done(), Is.True, "UDP loopback timeout");
            Assert.That(server.CallbackFailures + client.CallbackFailures, Is.Zero);
        }
        [Test] public void ReliableFragmentsCopyCallerBufferAndRemainOrdered()
        {
            int count = 0; bool correct = true;
            server.OnData += (id, data) => { count++; correct &= data.Count == 65536 && data.Array[data.Offset] == count && data.Array[data.Offset + data.Count - 1] == count; };
            var buffer = new byte[65538];
            for (int i = 1; i <= 3; i++)
            {
                for (int j = 0; j < buffer.Length; j++) buffer[j] = (byte)i;
                client.Send(0, new ArraySegment<byte>(buffer, 1, 65536));
            }
            Array.Clear(buffer, 0, buffer.Length); Pump(() => count == 3); Assert.That(correct, Is.True);
        }
        [Test] public void BoundedPumpDefersDataAndRejectsReentry()
        {
            int count = 0;
            server.OnData += (id, data) => { count++; Assert.Throws<InvalidOperationException>(() => server.Poll(0, 0)); };
            for (int i = 0; i < 4; i++) client.Send(0, new ArraySegment<byte>(new byte[10]), NetDeliveryMethod.UnreliableSequenced);
            server.Poll(0, 0); Assert.That(count, Is.Zero);
            server.Poll(2, 10); Assert.That(count, Is.EqualTo(1));
            server.Poll(2, 0); Assert.That(count, Is.EqualTo(1));
            server.Poll(1, 5); Assert.That(count, Is.EqualTo(1), "Oversized head is discarded, not delivered.");
            server.Poll(); Assert.That(count, Is.EqualTo(3));
            Assert.That(server.CallbackFailures, Is.Zero);
        }
        [Test] public void DisconnectAndReconnectUseNewServerIdentity()
        {
            int previous = peer, closed = 0;
            server.OnClientDisconnected += id => { if (id == previous) closed++; };
            client.Disconnect(); Pump(() => closed == 1);
            peer = 0; client.Connect("127.0.0.1", server.LocalPort);
            Pump(() => peer > 0 && client.ClientState == NetConnectionState.Connected);
            Assert.That(peer, Is.Not.EqualTo(previous));
        }
        [Test] public void SilentPeerTimesOutAndMalformedDatagramsAreRejected()
        {
            int closed = 0; server.OnClientDisconnected += id => closed++;
            using (var raw = new UdpClient(AddressFamily.InterNetwork))
            {
                raw.Send(new byte[40], 40, new IPEndPoint(IPAddress.Loopback, server.LocalPort));
            }
            server.Poll(); Assert.That(server.RejectedDatagrams, Is.GreaterThan(0));
            var time = Stopwatch.StartNew();
            while (closed == 0 && time.Elapsed.TotalSeconds < 3) { server.Poll(); Thread.Sleep(5); }
            Assert.That(closed, Is.EqualTo(1));
        }
        [Test] public void SendLimitsAndUnknownDeliveryFailBeforeQueueing()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => client.Send(0, new ArraySegment<byte>(new byte[65537])));
            Assert.Throws<ArgumentOutOfRangeException>(() => client.Send(0, default, (NetDeliveryMethod)255));
            for (int i = 0; i < 64; i++) client.Send(0, default);
            Assert.Throws<InvalidOperationException>(() => client.Send(0, default));
        }
    }
}
