//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using EjoyFramework.GamePlay.Netcode.Transport;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests.Netcode.Transport
{
    /// <summary>Budget contract and legacy regression tests using real in-process loopback queues.</summary>
    [TestFixture]
    public class BudgetedLoopbackTests
    {
        private INetTransport m_Server;
        private INetTransport m_Client;
        private Recorder m_Recorder;

        private sealed class Recorder
        {
            public readonly List<string> Events = new List<string>();
            public int DeliveredBytes;

            public Recorder(INetTransport transport)
            {
                transport.OnClientConnected += id => Events.Add("connect:" + id);
                transport.OnClientDisconnected += id => Events.Add("disconnect:" + id);
                transport.OnData += (id, data) =>
                {
                    DeliveredBytes += data.Count;
                    string bytes = data.Count == 0 ? string.Empty :
                        BitConverter.ToString(data.Array, data.Offset, data.Count);
                    Events.Add("data:" + id + ":" + bytes);
                };
            }
        }

        [SetUp]
        public void SetUp()
        {
            (m_Server, m_Client) = LoopbackTransport.CreatePair();
            m_Recorder = new Recorder(m_Server);
        }

        [TearDown]
        public void TearDown()
        {
            m_Client.Shutdown();
            m_Server.Shutdown();
        }

        [Test]
        public void Factories_ExposeOptionalBudgetCapability_WithoutChangingLegacyReturnType()
        {
            Assert.IsInstanceOf<IBudgetedNetTransport>(m_Server);
            Assert.IsInstanceOf<IBudgetedNetTransport>(m_Client);
            INetTransport extra = LoopbackTransport.CreateClient(m_Server);
            try { Assert.IsInstanceOf<IBudgetedNetTransport>(extra); }
            finally { extra.Shutdown(); }
        }

        [TestCase(-1, 1, "maxEvents")]
        [TestCase(int.MinValue, 0, "maxEvents")]
        [TestCase(1, -1, "maxDataBytes")]
        [TestCase(0, int.MinValue, "maxDataBytes")]
        [TestCase(-1, -1, "maxEvents")]
        public void Poll_NegativeBudget_ThrowsWithoutConsumingQueue(int events, int bytes, string parameter)
        {
            m_Client.Connect("ignored", 0);
            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => BudgetedServer.Poll(events, bytes));
            Assert.AreEqual(parameter, error.ParamName);
            CollectionAssert.IsEmpty(m_Recorder.Events);

            BudgetedServer.Poll(1, 0);
            CollectionAssert.AreEqual(new[] { "connect:0" }, m_Recorder.Events);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(int.MaxValue)]
        public void Poll_ZeroEvents_NeitherDeliversNorDiscards(int bytes)
        {
            m_Client.Connect("ignored", 0);
            Send(1, 2);
            Send();
            m_Client.Disconnect();

            BudgetedServer.Poll(0, bytes);
            CollectionAssert.IsEmpty(m_Recorder.Events);
            m_Server.Poll();
            CollectionAssert.AreEqual(new[]
            {
                "connect:0", "data:0:01-02", "data:0:", "disconnect:0"
            }, m_Recorder.Events);
        }

        [Test]
        public void Poll_EmptyQueueAndMaximumBudgets_LeaveGuardReusable()
        {
            BudgetedServer.Poll(0, 0);
            BudgetedServer.Poll(int.MaxValue, int.MaxValue);
            m_Server.Poll();
            m_Client.Connect("ignored", 0);
            Send(1);

            BudgetedServer.Poll(int.MaxValue, int.MaxValue);
            CollectionAssert.AreEqual(new[] { "connect:0", "data:0:01" }, m_Recorder.Events);
        }

        [Test]
        public void Poll_ControlAndZeroByteData_EachChargeOneEvent()
        {
            m_Client.Connect("ignored", 0);
            m_Client.Send(0, default(ArraySegment<byte>));
            Send();
            m_Client.Disconnect();

            BudgetedServer.Poll(1, 0);
            CollectionAssert.AreEqual(new[] { "connect:0" }, m_Recorder.Events);
            BudgetedServer.Poll(1, 0);
            CollectionAssert.AreEqual(new[] { "connect:0", "data:0:" }, m_Recorder.Events);
            BudgetedServer.Poll(1, 0);
            CollectionAssert.AreEqual(new[] { "connect:0", "data:0:", "data:0:" }, m_Recorder.Events);
            BudgetedServer.Poll(1, 0);
            CollectionAssert.AreEqual(new[]
            {
                "connect:0", "data:0:", "data:0:", "disconnect:0"
            }, m_Recorder.Events);
            Assert.AreEqual(0, m_Recorder.DeliveredBytes);
        }

        [Test]
        public void Poll_DisconnectConsumesEventBudget_BeforeLaterConnection()
        {
            m_Client.Connect("ignored", 0);
            m_Client.Disconnect();
            m_Client.Connect("ignored", 0);

            BudgetedServer.Poll(2, 0);
            CollectionAssert.AreEqual(new[] { "connect:0", "disconnect:0" }, m_Recorder.Events);
            BudgetedServer.Poll(1, 0);
            CollectionAssert.AreEqual(new[] { "connect:0", "disconnect:0", "connect:1" }, m_Recorder.Events);
        }

        [Test]
        public void Poll_DataEventCap_PreservesSendOrderAcrossPolls()
        {
            ConnectAndDrain();
            Send(1);
            Send(2);
            Send(3);

            BudgetedServer.Poll(2, int.MaxValue);
            CollectionAssert.AreEqual(new[] { "data:0:01", "data:0:02" }, m_Recorder.Events);
            BudgetedServer.Poll(2, int.MaxValue);
            CollectionAssert.AreEqual(new[] { "data:0:01", "data:0:02", "data:0:03" }, m_Recorder.Events);
        }

        [Test]
        public void Poll_PacketFitsTotalButNotRemaining_LeavesHeadAndFollowingEventsInPlace()
        {
            ConnectAndDrain();
            Send(1, 2);
            Send(3, 4, 5);
            Send();
            Send(6);

            BudgetedServer.Poll(10, 4);
            CollectionAssert.AreEqual(new[] { "data:0:01-02" }, m_Recorder.Events);
            Assert.AreEqual(2, m_Recorder.DeliveredBytes);
            BudgetedServer.Poll(10, 4);
            CollectionAssert.AreEqual(new[]
            {
                "data:0:01-02", "data:0:03-04-05", "data:0:", "data:0:06"
            }, m_Recorder.Events);
            Assert.AreEqual(6, m_Recorder.DeliveredBytes);
        }

        [Test]
        public void Poll_ExactByteBudget_StillProcessesFollowingZeroByteAndControlEvents()
        {
            ConnectAndDrain();
            Send(1, 2);
            Send();
            m_Client.Disconnect();

            BudgetedServer.Poll(3, 2);
            CollectionAssert.AreEqual(new[]
            {
                "data:0:01-02", "data:0:", "disconnect:0"
            }, m_Recorder.Events);
            Assert.AreEqual(2, m_Recorder.DeliveredBytes);
        }

        [Test]
        public void Poll_IndividuallyOversizedPackets_AreDroppedUnderOneEventChargeEach()
        {
            ConnectAndDrain();
            Send(1, 2, 3);
            Send(4, 5, 6);
            Send();
            Send(7);

            BudgetedServer.Poll(1, 2);
            CollectionAssert.IsEmpty(m_Recorder.Events);
            BudgetedServer.Poll(1, 2);
            CollectionAssert.IsEmpty(m_Recorder.Events);
            BudgetedServer.Poll(1, 2);
            CollectionAssert.AreEqual(new[] { "data:0:" }, m_Recorder.Events);
            m_Server.Poll();
            CollectionAssert.AreEqual(new[] { "data:0:", "data:0:07" }, m_Recorder.Events);
            Assert.AreEqual(1, m_Recorder.DeliveredBytes);
        }

        [Test]
        public void Poll_OversizedDrop_DoesNotChargeDeliveredByteBudget()
        {
            ConnectAndDrain();
            Send(1, 2);
            Send(3, 4, 5, 6, 7);
            Send(8, 9);

            BudgetedServer.Poll(3, 4);
            CollectionAssert.AreEqual(new[] { "data:0:01-02", "data:0:08-09" }, m_Recorder.Events);
            Assert.AreEqual(4, m_Recorder.DeliveredBytes);
        }

        [Test]
        public void Poll_ByteBudgetExhausted_StillDropsIndividuallyOversizedHead()
        {
            ConnectAndDrain();
            Send(1, 2);
            Send(3, 4, 5);
            m_Client.Disconnect();

            BudgetedServer.Poll(3, 2);
            CollectionAssert.AreEqual(new[] { "data:0:01-02", "disconnect:0" }, m_Recorder.Events);
            m_Server.Poll();
            Assert.AreEqual(2, m_Recorder.Events.Count, "The oversized packet must not reappear.");
        }

        [Test]
        public void Poll_ZeroBytes_PausesAtPositiveHeadWithoutSkippingOrDropping()
        {
            m_Client.Connect("ignored", 0);
            Send();
            Send(1, 2, 3);
            Send();
            m_Client.Disconnect();

            BudgetedServer.Poll(10, 0);
            CollectionAssert.AreEqual(new[] { "connect:0", "data:0:" }, m_Recorder.Events);
            BudgetedServer.Poll(10, 0);
            CollectionAssert.AreEqual(new[] { "connect:0", "data:0:" }, m_Recorder.Events);
            BudgetedServer.Poll(10, 3);
            CollectionAssert.AreEqual(new[]
            {
                "connect:0", "data:0:", "data:0:01-02-03", "data:0:", "disconnect:0"
            }, m_Recorder.Events);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Poll_CallbackEnqueuedData_WaitsForNextQueueCountSnapshot(bool budgeted)
        {
            ConnectAndDrain();
            Send(1);
            Send(2);
            m_Server.OnData += (id, data) =>
            {
                if (data.Array[data.Offset] == 1) Send(3);
            };

            Pump(m_Server, budgeted);
            CollectionAssert.AreEqual(new[] { "data:0:01", "data:0:02" }, m_Recorder.Events);
            Pump(m_Server, budgeted);
            CollectionAssert.AreEqual(new[] { "data:0:01", "data:0:02", "data:0:03" }, m_Recorder.Events);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Poll_CallbackEnqueuedControlAndEmptyData_WaitForNextPoll(bool budgeted)
        {
            m_Server.OnClientConnected += id =>
            {
                Send();
                m_Client.Disconnect();
            };
            m_Client.Connect("ignored", 0);

            Pump(m_Server, budgeted);
            CollectionAssert.AreEqual(new[] { "connect:0" }, m_Recorder.Events);
            Pump(m_Server, budgeted);
            CollectionAssert.AreEqual(new[] { "connect:0", "data:0:", "disconnect:0" }, m_Recorder.Events);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Poll_CallbackClearsQueue_StopsSnapshotAndDefersReplacementEvents(bool reconnect)
        {
            ConnectAndDrain();
            Send(1);
            Send(2);
            m_Server.OnData += (id, data) =>
            {
                if (data.Array[data.Offset] != 1) return;
                m_Server.Shutdown();
                if (!reconnect) return;
                m_Server.StartServer(0);
                m_Client.Connect("ignored", 0);
                Send(3);
            };

            Assert.DoesNotThrow(() => BudgetedServer.Poll(10, 10));
            CollectionAssert.AreEqual(new[] { "data:0:01" }, m_Recorder.Events);
            Assert.DoesNotThrow(() => BudgetedServer.Poll(10, 10));
            CollectionAssert.AreEqual(reconnect
                ? new[] { "data:0:01", "connect:1", "data:1:03" }
                : new[] { "data:0:01" }, m_Recorder.Events);
        }

        [Test]
        public void Poll_CallbackRefillsQueue_StopsAtCapturedCountEvenWithSpareBudget()
        {
            ConnectAndDrain();
            Send();
            m_Server.OnData += (id, data) => Send();

            BudgetedServer.Poll(10, 0);
            CollectionAssert.AreEqual(new[] { "data:0:" }, m_Recorder.Events);
            BudgetedServer.Poll(10, 0);
            CollectionAssert.AreEqual(new[] { "data:0:", "data:0:" }, m_Recorder.Events);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Poll_ReentryAcrossEitherOverload_IsRejectedAndDoesNotReleaseOuterGuard(
            bool outerBudgeted, bool innerBudgeted)
        {
            ConnectAndDrain();
            Send(1);
            Send(2);
            List<Exception> failures = new List<Exception>();
            m_Server.OnData += (id, data) =>
            {
                failures.Add(CaptureFailure(() => Pump(m_Server, innerBudgeted)));
                failures.Add(CaptureFailure(() => BudgetedServer.Poll(0, 0)));
            };

            Assert.DoesNotThrow(() => Pump(m_Server, outerBudgeted));
            CollectionAssert.AreEqual(new[] { "data:0:01", "data:0:02" }, m_Recorder.Events);
            Assert.AreEqual(4, failures.Count);
            foreach (Exception failure in failures) Assert.IsInstanceOf<InvalidOperationException>(failure);

            Send(3);
            Assert.DoesNotThrow(() => Pump(m_Server, !outerBudgeted));
            CollectionAssert.AreEqual(new[] { "data:0:01", "data:0:02", "data:0:03" }, m_Recorder.Events);
            Assert.AreEqual(6, failures.Count);
            foreach (Exception failure in failures) Assert.IsInstanceOf<InvalidOperationException>(failure);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Poll_ConcurrentConsumer_IsRejectedBeforeTouchingQueue(bool outerBudgeted, bool innerBudgeted)
        {
            ConnectAndDrain();
            Send(1);
            Send(2);
            Exception rejection = null;
            bool joined = false;
            Thread contender = new Thread(() => rejection = CaptureFailure(() => Pump(m_Server, innerBudgeted)));
            contender.IsBackground = true;
            m_Server.OnData += (id, data) =>
            {
                if (data.Array[data.Offset] != 1) return;
                contender.Start();
                // The owner is inside Dispatch; only a competing Poll runs on this thread.
                joined = contender.Join(TimeSpan.FromSeconds(5));
            };

            Pump(m_Server, outerBudgeted);
            Assert.IsTrue(joined, "Overlapping Poll must fail promptly, not block waiting for its owner.");
            Assert.IsInstanceOf<InvalidOperationException>(rejection);
            CollectionAssert.AreEqual(new[] { "data:0:01", "data:0:02" }, m_Recorder.Events);
            Send(3);
            Assert.DoesNotThrow(() => Pump(m_Server, !outerBudgeted));
            Assert.AreEqual("data:0:03", m_Recorder.Events[2]);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Poll_HandlerExceptions_RemainIsolatedAndGuardIsReleased(bool budgeted)
        {
            int throwingCalls = 0;
            m_Server.OnClientConnected += id => { throwingCalls++; throw new InvalidOperationException("connect"); };
            m_Server.OnData += (id, data) => { throwingCalls++; throw new InvalidOperationException("data"); };
            m_Server.OnClientDisconnected += id => { throwingCalls++; throw new InvalidOperationException("disconnect"); };
            Recorder laterHandler = new Recorder(m_Server);
            m_Client.Connect("ignored", 0);
            Send(1);
            Send(2);
            m_Client.Disconnect();

            Assert.DoesNotThrow(() => Pump(m_Server, budgeted));
            CollectionAssert.AreEqual(new[]
            {
                "connect:0", "data:0:01", "data:0:02", "disconnect:0"
            }, laterHandler.Events);
            Assert.AreEqual(4, throwingCalls);
            m_Client.Connect("ignored", 0);
            Assert.DoesNotThrow(() => Pump(m_Server, !budgeted));
            Assert.AreEqual("connect:1", laterHandler.Events[4]);
            Assert.AreEqual(5, throwingCalls);
        }

        [Test]
        public void Poll_ThrowingDataHandler_DoesNotBypassByteBudget()
        {
            ConnectAndDrain();
            m_Server.OnData += (id, data) => throw new InvalidOperationException("data");
            Recorder laterHandler = new Recorder(m_Server);
            Send(1, 2);
            Send(3, 4);

            Assert.DoesNotThrow(() => BudgetedServer.Poll(10, 3));
            CollectionAssert.AreEqual(new[] { "data:0:01-02" }, laterHandler.Events);
            Assert.DoesNotThrow(() => BudgetedServer.Poll(10, 3));
            CollectionAssert.AreEqual(new[] { "data:0:01-02", "data:0:03-04" }, laterHandler.Events);
        }

        [Test]
        public void Poll_GuardIsPerInstance_CallbackCanPumpPeer()
        {
            ConnectAndDrain();
            Recorder clientRecorder = new Recorder(m_Client);
            m_Server.OnData += (id, data) =>
            {
                m_Server.Send(id, data);
                ((IBudgetedNetTransport)m_Client).Poll(1, 1);
            };
            Send(7);

            BudgetedServer.Poll(1, 1);
            CollectionAssert.AreEqual(new[] { "data:0:07" }, clientRecorder.Events);
        }

        [Test]
        public void ParameterlessPoll_CallbackClearsQueue_PreservesLegacyFailureAndReleasesGuard()
        {
            ConnectAndDrain();
            Send(1);
            Send(2);
            m_Server.OnData += (id, data) =>
            {
                if (data.Array[data.Offset] == 1) m_Server.Shutdown();
            };

            // The original fixed-count loop attempts its second Dequeue after Shutdown clears it.
            Assert.Throws<InvalidOperationException>(() => m_Server.Poll());
            CollectionAssert.AreEqual(new[] { "data:0:01" }, m_Recorder.Events);
            m_Server.StartServer(0);
            m_Client.Connect("ignored", 0);
            Send(3);

            Assert.DoesNotThrow(() => BudgetedServer.Poll(10, 10));
            CollectionAssert.AreEqual(new[]
            {
                "data:0:01", "connect:1", "data:1:03"
            }, m_Recorder.Events);
        }

        [Test]
        public void ParameterlessPoll_AfterBoundedPoll_DrainsWholeRemainingSnapshotWithoutByteLimit()
        {
            ConnectAndDrain();
            Send(1);
            Send(2, 3, 4, 5);
            Send();
            m_Client.Disconnect();

            BudgetedServer.Poll(1, 1);
            CollectionAssert.AreEqual(new[] { "data:0:01" }, m_Recorder.Events);
            m_Server.Poll();
            CollectionAssert.AreEqual(new[]
            {
                "data:0:01", "data:0:02-03-04-05", "data:0:", "disconnect:0"
            }, m_Recorder.Events);
        }

        [Test]
        public void Poll_ServerSendAndBroadcast_PreserveSlicesOrderingAndTargeting()
        {
            ConnectAndDrain();
            INetTransport extra = LoopbackTransport.CreateClient(m_Server);
            try
            {
                extra.Connect("ignored", 0);
                extra.Poll();
                m_Server.Poll();
                Recorder first = new Recorder(m_Client);
                Recorder second = new Recorder(extra);
                byte[] payload = { 99, 0, 128, 255, 99 };
                m_Server.Send(0, new ArraySegment<byte>(payload, 1, 3));
                m_Server.Broadcast(new ArraySegment<byte>(new byte[] { 7 }));
                m_Server.Send(1, new ArraySegment<byte>(new byte[] { 8 }));

                ((IBudgetedNetTransport)m_Client).Poll(10, 3);
                ((IBudgetedNetTransport)extra).Poll(1, 1);
                CollectionAssert.AreEqual(new[] { "data:0:00-80-FF" }, first.Events);
                CollectionAssert.AreEqual(new[] { "data:1:07" }, second.Events);
                ((IBudgetedNetTransport)m_Client).Poll(10, 3);
                ((IBudgetedNetTransport)extra).Poll(1, 1);
                CollectionAssert.AreEqual(new[] { "data:0:00-80-FF", "data:0:07" }, first.Events);
                CollectionAssert.AreEqual(new[] { "data:1:07", "data:1:08" }, second.Events);
            }
            finally { extra.Shutdown(); }
        }

        [Test]
        public void Poll_LargeQueue_MakesBoundedOrderedProgress()
        {
            const int total = 10001;
            const int eventBudget = 97;
            const int byteBudget = 53;
            ConnectAndDrain();
            List<byte> received = new List<byte>();
            m_Server.OnData += (id, data) => received.Add(data.Array[data.Offset]);
            for (int i = 0; i < total; i++) Send((byte)(i % 251));

            for (int start = 0; start < total; start += byteBudget)
            {
                int before = received.Count;
                BudgetedServer.Poll(eventBudget, byteBudget);
                Assert.AreEqual(Math.Min(byteBudget, total - start), received.Count - before);
            }

            Assert.AreEqual(total, received.Count);
            Assert.AreEqual(total, m_Recorder.DeliveredBytes);
            for (int i = 0; i < total; i++) Assert.AreEqual((byte)(i % 251), received[i]);
            BudgetedServer.Poll(int.MaxValue, int.MaxValue);
            Assert.AreEqual(total, received.Count);
        }

        private IBudgetedNetTransport BudgetedServer => (IBudgetedNetTransport)m_Server;

        private void ConnectAndDrain()
        {
            m_Client.Connect("ignored", 0);
            m_Server.Poll();
            m_Client.Poll();
            m_Recorder.Events.Clear();
        }

        private void Send(params byte[] payload)
        {
            m_Client.Send(0, new ArraySegment<byte>(payload));
        }

        private static void Pump(INetTransport transport, bool budgeted)
        {
            if (budgeted) ((IBudgetedNetTransport)transport).Poll(int.MaxValue, int.MaxValue);
            else transport.Poll();
        }

        private static Exception CaptureFailure(Action action)
        {
            try { action(); return null; }
            catch (Exception error) { return error; }
        }
    }
}
