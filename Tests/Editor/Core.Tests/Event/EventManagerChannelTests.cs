//------------------------------------------------------------
// EjoyGame Framework Tests
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using EjoyFramework.Core;
using EjoyFramework.Core.Event;
using NUnit.Framework;

namespace EjoyFramework.Tests.Event
{
    /// <summary>
    /// EventManager 驱动的 struct 通道（WS5-M2）：同类型同实例、每帧派发、关停一并关闭。
    /// </summary>
    public class EventManagerChannelTests
    {
        private const int PingId = 7201;

        private struct HitEvent
        {
            public int Target;
        }

        private struct HealEvent
        {
            public int Amount;
        }

        public sealed class PingArgs : GameEventArgs
        {
            public override int Id { get { return PingId; } }
            public override void Clear() { }
        }

        [SetUp]
        public void Setup()
        {
            Framework.MarkMainThread();
        }

        [Test]
        public void GetChannel_SameTypeSameInstance_OwnedByMainThread()
        {
            var em = new EventManager();
            EventChannel<HitEvent> hit = em.GetChannel<HitEvent>();

            Assert.AreSame(hit, em.GetChannel<HitEvent>());
            Assert.AreNotSame(hit, em.GetChannel<HealEvent>());
            Assert.AreEqual(Framework.MainThreadId, hit.OwnerThreadId);
            em.Shutdown();
        }

        [Test]
        public void Update_DispatchesChannelQueues_AfterGameEventArgs()
        {
            var em = new EventManager();
            var order = new List<string>();
            em.Subscribe(PingId, (s, e) => order.Add("args"));
            EventChannel<HitEvent> hit = em.GetChannel<HitEvent>();
            EventChannel<HealEvent> heal = em.GetChannel<HealEvent>();
            hit.Subscribe((in HitEvent e) => order.Add("hit" + e.Target));
            heal.Subscribe((in HealEvent e) => order.Add("heal" + e.Amount));

            heal.Fire(new HealEvent { Amount = 3 });
            hit.Fire(new HitEvent { Target = 1 });
            em.Fire(this, ReferencePool.Acquire<PingArgs>());
            Assert.AreEqual(0, order.Count);

            em.Update(0.016f, 0.016f);
            CollectionAssert.AreEqual(new[] { "args", "hit1", "heal3" }, order, "先 GameEventArgs 队列，再按通道创建顺序");
            em.Shutdown();
        }

        [Test]
        public void GetChannel_FromWorker_SameChannel_FireDispatchedOnUpdate()
        {
            var em = new EventManager();
            EventChannel<HitEvent> hit = em.GetChannel<HitEvent>();
            int total = 0;
            hit.Subscribe((in HitEvent e) => total += e.Target);

            EventChannel<HitEvent> fromWorker = null;
            var worker = new Thread(() =>
            {
                fromWorker = em.GetChannel<HitEvent>();
                fromWorker.Fire(new HitEvent { Target = 5 });
            });
            worker.Start();
            Assert.IsTrue(worker.Join(10000));

            Assert.AreSame(hit, fromWorker);
            em.Update(0.016f, 0.016f);
            Assert.AreEqual(5, total);
            em.Shutdown();
        }

        [Test]
        public void Shutdown_ClosesChannels_LateGetChannelIsAlreadyClosed()
        {
            var em = new EventManager();
            EventChannel<HitEvent> hit = em.GetChannel<HitEvent>();
            EventChannelHandler<HitEvent> handler = (in HitEvent e) => { };
            hit.Subscribe(handler);
            hit.Fire(new HitEvent());

            em.Shutdown();
            Assert.IsTrue(hit.IsShutdown);
            Assert.AreEqual(0, hit.HandlerCount);
            Assert.AreEqual(0, hit.QueuedCount);
            Assert.DoesNotThrow(() => hit.Unsubscribe(handler));

            EventChannel<HealEvent> late = em.GetChannel<HealEvent>();
            Assert.IsTrue(late.IsShutdown, "关停后才取的通道直接处于关停态");
            Assert.DoesNotThrow(() => late.Unsubscribe((in HealEvent e) => { }), "晚到的 OnDestroy 退订不抛出");
            late.Fire(new HealEvent());
            Assert.AreEqual(0, late.QueuedCount);
        }
    }
}
