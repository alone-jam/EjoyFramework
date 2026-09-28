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
    public class EventChannelTests
    {
        private struct HitEvent
        {
            public int Target;
            public float Damage;
        }

        // 捕获 FrameworkLog 输出：既断言异常被记录，也避免错误日志走到 Unity 控制台。
        private sealed class CaptureLogHelper : FrameworkLog.ILogHelper
        {
            public readonly List<string> Errors = new List<string>();
            public int Warnings;

            public void Log(LogLevel level, object message)
            {
                lock (Errors)
                {
                    if (level == LogLevel.Error) Errors.Add(message.ToString());
                    else if (level == LogLevel.Warning) Warnings++;
                }
            }
        }

        private int m_MethodGroupHits;
        private bool m_InstalledLogHelper;

        private void OnHit(in HitEvent e)
        {
            m_MethodGroupHits++;
        }

        [SetUp]
        public void Setup()
        {
            m_MethodGroupHits = 0;
        }

        [TearDown]
        public void TearDown()
        {
            if (m_InstalledLogHelper)
            {
                // EditMode 下默认没有日志 helper（只有运行时 BaseComponent 会装），还原为 null。
                FrameworkLog.SetLogHelper(null);
                m_InstalledLogHelper = false;
            }
        }

        private CaptureLogHelper InstallCaptureLog()
        {
            var log = new CaptureLogHelper();
            FrameworkLog.SetLogHelper(log);
            m_InstalledLogHelper = true;
            return log;
        }

        [Test]
        public void FireNow_CallsHandlersInSubscriptionOrder()
        {
            var channel = new EventChannel<HitEvent>();
            var order = new List<int>();
            EventChannelHandler<HitEvent> a = (in HitEvent e) => order.Add(e.Target * 10 + 1);
            EventChannelHandler<HitEvent> b = (in HitEvent e) => order.Add(e.Target * 10 + 2);
            channel.Subscribe(a);
            channel.Subscribe(b);

            channel.FireNow(new HitEvent { Target = 7, Damage = 1.5f });

            CollectionAssert.AreEqual(new[] { 71, 72 }, order);
            Assert.AreEqual(2, channel.HandlerCount);
        }

        [Test]
        public void Subscribe_Duplicate_Throws_IncludingEqualMethodGroup()
        {
            var channel = new EventChannel<HitEvent>();
            EventChannelHandler<HitEvent> cached = OnHit;
            channel.Subscribe(cached);

            Assert.Throws<FrameworkException>(() => channel.Subscribe(cached));
            Assert.Throws<FrameworkException>(() => channel.Subscribe(OnHit), "每次新建的方法组委托按目标 + 方法判等");
            Assert.Throws<FrameworkException>(() => channel.Subscribe(null));
            Assert.AreEqual(1, channel.HandlerCount);

            channel.FireNow(new HitEvent());
            Assert.AreEqual(1, m_MethodGroupHits);
        }

        [Test]
        public void Unsubscribe_Missing_Throws_TryUnsubscribeReturnsFalse()
        {
            var channel = new EventChannel<HitEvent>();
            EventChannelHandler<HitEvent> handler = (in HitEvent e) => { };
            Assert.Throws<FrameworkException>(() => channel.Unsubscribe(handler));
            Assert.Throws<FrameworkException>(() => channel.Unsubscribe(null));
            Assert.IsFalse(channel.TryUnsubscribe(handler));
            Assert.IsFalse(channel.TryUnsubscribe(null));

            channel.Subscribe(OnHit);
            Assert.IsTrue(channel.Check(OnHit));
            channel.Unsubscribe(OnHit);   // 等价的新委托也能退订
            Assert.IsFalse(channel.Check(OnHit));
            Assert.AreEqual(0, channel.HandlerCount);
        }

        [Test]
        public void UnsubscribeLaterHandler_DuringDispatch_SkipsIt()
        {
            var channel = new EventChannel<HitEvent>();
            var calls = new List<string>();
            EventChannelHandler<HitEvent> c = (in HitEvent e) => calls.Add("c");
            EventChannelHandler<HitEvent> a = (in HitEvent e) =>
            {
                calls.Add("a");
                channel.TryUnsubscribe(c);
            };
            EventChannelHandler<HitEvent> b = (in HitEvent e) => calls.Add("b");
            channel.Subscribe(a);
            channel.Subscribe(b);
            channel.Subscribe(c);

            channel.FireNow(new HitEvent());
            CollectionAssert.AreEqual(new[] { "a", "b" }, calls, "本轮还没轮到的处理器被退订后不再调用");
            Assert.AreEqual(2, channel.HandlerCount);

            calls.Clear();
            channel.FireNow(new HitEvent());
            CollectionAssert.AreEqual(new[] { "a", "b" }, calls);
        }

        [Test]
        public void SelfUnsubscribe_DuringDispatch_DoesNotSkipNext()
        {
            var channel = new EventChannel<HitEvent>();
            var calls = new List<string>();
            EventChannelHandler<HitEvent> a = null;
            a = (in HitEvent e) =>
            {
                calls.Add("a");
                channel.Unsubscribe(a);
            };
            EventChannelHandler<HitEvent> b = (in HitEvent e) => calls.Add("b");
            channel.Subscribe(a);
            channel.Subscribe(b);

            channel.FireNow(new HitEvent());
            channel.FireNow(new HitEvent());

            CollectionAssert.AreEqual(new[] { "a", "b", "b" }, calls);
            Assert.AreEqual(1, channel.HandlerCount);
        }

        [Test]
        public void SubscribeDuringDispatch_JoinsNextRound_AndNestedFireNow()
        {
            var channel = new EventChannel<HitEvent>();
            var lateTargets = new List<int>();
            EventChannelHandler<HitEvent> late = (in HitEvent e) => lateTargets.Add(e.Target);
            EventChannelHandler<HitEvent> a = (in HitEvent e) =>
            {
                if (e.Target == 0)
                {
                    channel.Subscribe(late);
                    channel.FireNow(new HitEvent { Target = 1 });   // 嵌套：新处理器参与
                }
            };
            channel.Subscribe(a);

            channel.FireNow(new HitEvent { Target = 0 });
            CollectionAssert.AreEqual(new[] { 1 }, lateTargets, "外层本轮不调用派发中新订阅的处理器");

            channel.FireNow(new HitEvent { Target = 2 });
            CollectionAssert.AreEqual(new[] { 1, 2 }, lateTargets);
            Assert.AreEqual(2, channel.HandlerCount);
        }

        [Test]
        public void UnsubscribeAll_DuringDispatch_StopsRemainingHandlers()
        {
            var channel = new EventChannel<HitEvent>();
            int bHits = 0;
            EventChannelHandler<HitEvent> a = (in HitEvent e) => channel.UnsubscribeAll();
            EventChannelHandler<HitEvent> b = (in HitEvent e) => bHits++;
            channel.Subscribe(a);
            channel.Subscribe(b);

            channel.FireNow(new HitEvent());
            Assert.AreEqual(0, bHits);
            Assert.AreEqual(0, channel.HandlerCount);

            channel.Subscribe(b);
            channel.FireNow(new HitEvent());
            Assert.AreEqual(1, bHits);
            Assert.AreEqual(1, channel.HandlerCount);
        }

        [Test]
        public void HandlerException_IsLoggedAndIsolated()
        {
            var log = InstallCaptureLog();
            var channel = new EventChannel<HitEvent>();
            int goodHits = 0;
            EventChannelHandler<HitEvent> bad = (in HitEvent e) => { throw new InvalidOperationException("boom-" + e.Target); };
            EventChannelHandler<HitEvent> good = (in HitEvent e) => goodHits++;
            channel.Subscribe(bad);
            channel.Subscribe(good);

            channel.FireNow(new HitEvent { Target = 1 });
            Assert.AreEqual(1, goodHits);
            Assert.AreEqual(1, log.Errors.Count);
            StringAssert.Contains("boom-1", log.Errors[0]);

            for (int i = 0; i < 3; i++) channel.Fire(new HitEvent { Target = i });
            Assert.AreEqual(3, channel.DispatchQueued(), "异常不打断排队事件的派发");
            Assert.AreEqual(4, goodHits);
            Assert.AreEqual(4, log.Errors.Count);
        }

        [Test]
        public void Fire_QueuesUntilDispatch_FiresFromHandlersWaitForNextDispatch()
        {
            var channel = new EventChannel<HitEvent>();
            var seen = new List<int>();
            EventChannelHandler<HitEvent> handler = (in HitEvent e) =>
            {
                seen.Add(e.Target);
                if (e.Target < 10) channel.Fire(new HitEvent { Target = e.Target + 10 });
            };
            channel.Subscribe(handler);

            channel.Fire(new HitEvent { Target = 1 });
            channel.Fire(new HitEvent { Target = 2 });
            Assert.AreEqual(2, channel.QueuedCount);
            Assert.AreEqual(0, seen.Count, "Fire 只排队");

            Assert.AreEqual(2, channel.DispatchQueued());
            CollectionAssert.AreEqual(new[] { 1, 2 }, seen);
            Assert.AreEqual(2, channel.QueuedCount, "派发期间 Fire 的事件留到下一次");

            Assert.AreEqual(2, channel.DispatchQueued());
            CollectionAssert.AreEqual(new[] { 1, 2, 11, 12 }, seen);
            Assert.AreEqual(0, channel.DispatchQueued());
        }

        [Test]
        public void DispatchQueued_IsNotReentrant()
        {
            var channel = new EventChannel<HitEvent>();
            Exception inner = null;
            EventChannelHandler<HitEvent> handler = (in HitEvent e) =>
            {
                try
                {
                    channel.DispatchQueued();
                }
                catch (FrameworkException ex)
                {
                    inner = ex;
                }
            };
            channel.Subscribe(handler);
            channel.Fire(new HitEvent());

            Assert.AreEqual(1, channel.DispatchQueued());
            Assert.IsNotNull(inner, "处理器内再次 DispatchQueued 应抛出");
            channel.Fire(new HitEvent());
            Assert.AreEqual(1, channel.DispatchQueued(), "抛出后仍可正常派发");
        }

        [Test]
        public void OwnerThread_BindsOnFirstUse_OtherThreadsThrow_FireAllowed()
        {
            var channel = new EventChannel<HitEvent>();
            Assert.AreEqual(0, channel.OwnerThreadId);
            int hits = 0;
            EventChannelHandler<HitEvent> handler = (in HitEvent e) => hits += e.Target;
            channel.Subscribe(handler);
            Assert.AreEqual(Thread.CurrentThread.ManagedThreadId, channel.OwnerThreadId);

            Exception subscribeError = null;
            Exception fireNowError = null;
            RunOnWorker(() =>
            {
                try { channel.Subscribe((in HitEvent e) => { }); }
                catch (Exception ex) { subscribeError = ex; }
                try { channel.FireNow(new HitEvent()); }
                catch (Exception ex) { fireNowError = ex; }
                channel.Fire(new HitEvent { Target = 5 });
            });

            Assert.IsInstanceOf<FrameworkException>(subscribeError);
            Assert.IsInstanceOf<FrameworkException>(fireNowError);
            Assert.AreEqual(1, channel.DispatchQueued());
            Assert.AreEqual(5, hits, "其他线程 Fire 的事件在所属线程派发");
        }

        [Test]
        public void OwnerThread_BoundToWorker_RejectsDispatchFromHere()
        {
            var channel = new EventChannel<HitEvent>();
            RunOnWorker(() => channel.Subscribe((in HitEvent e) => { }));

            Assert.AreNotEqual(Thread.CurrentThread.ManagedThreadId, channel.OwnerThreadId);
            Assert.Throws<FrameworkException>(() => channel.DispatchQueued());
            Assert.Throws<FrameworkException>(() => channel.Shutdown());
        }

        [Test]
        public void Fire_FromManyThreadsWhileDispatching_DeliversEveryEventOnce()
        {
            const int ThreadCount = 8;
            const int PerThread = 2000;
            var channel = new EventChannel<HitEvent>();
            long sum = 0;
            int count = 0;
            EventChannelHandler<HitEvent> handler = (in HitEvent e) =>
            {
                count++;
                sum += e.Target;
            };
            channel.Subscribe(handler);

            var errors = new List<Exception>();
            var threads = new Thread[ThreadCount];
            using (var startGate = new ManualResetEventSlim(false))
            {
                for (int t = 0; t < ThreadCount; t++)
                {
                    int threadIndex = t;
                    threads[t] = new Thread(() =>
                    {
                        startGate.Wait();
                        try
                        {
                            for (int i = 0; i < PerThread; i++) channel.Fire(new HitEvent { Target = threadIndex * PerThread + i });
                        }
                        catch (Exception ex)
                        {
                            lock (errors) errors.Add(ex);
                        }
                    });
                    threads[t].IsBackground = true;
                    threads[t].Start();
                }

                startGate.Set();
                // 生产者 Fire 的同时在本线程派发，覆盖双缓冲交换与写入并发的窗口。
                bool running = true;
                while (running)
                {
                    channel.DispatchQueued();
                    running = false;
                    foreach (Thread th in threads) running |= th.IsAlive;
                }

                foreach (Thread th in threads) th.Join();
            }

            channel.DispatchQueued();
            CollectionAssert.IsEmpty(errors);
            long n = ThreadCount * PerThread;
            Assert.AreEqual(n, (long)count);
            Assert.AreEqual(n * (n - 1) / 2, sum);
        }

        [Test]
        public void Shutdown_DropsQueue_IgnoresSubscribe_UnsubscribeDoesNotThrow()
        {
            var log = InstallCaptureLog();
            var channel = new EventChannel<HitEvent>();
            int hits = 0;
            EventChannelHandler<HitEvent> handler = (in HitEvent e) => hits++;
            channel.Subscribe(handler);
            channel.Fire(new HitEvent());

            channel.Shutdown();
            Assert.IsTrue(channel.IsShutdown);
            Assert.AreEqual(0, channel.QueuedCount);
            Assert.AreEqual(0, channel.HandlerCount);

            channel.Fire(new HitEvent());
            Assert.AreEqual(0, channel.QueuedCount, "关停后 Fire 被丢弃");
            channel.Subscribe(OnHit);
            Assert.AreEqual(0, channel.HandlerCount, "关停后订阅被忽略");
            Assert.AreEqual(1, log.Warnings);
            Assert.DoesNotThrow(() => channel.Unsubscribe(handler), "关停后退订不抛出（OnDestroy 晚于关停）");
            Assert.AreEqual(0, channel.DispatchQueued());
            Assert.DoesNotThrow(() => channel.Shutdown(), "可重复关停");
            Assert.AreEqual(0, hits);
        }

        [Test]
        public void SteadyState_SubscribeFireDispatch_ZeroAlloc()
        {
            var channel = new EventChannel<HitEvent>();
            long sink = 0;
            EventChannelHandler<HitEvent> a = (in HitEvent e) => sink += e.Target;
            EventChannelHandler<HitEvent> b = (in HitEvent e) => sink++;
            channel.Subscribe(a);
            channel.Subscribe(b);

            ZeroAlloc.Assert(() =>
            {
                channel.Unsubscribe(b);
                channel.Subscribe(b);
                for (int i = 0; i < 16; i++) channel.FireNow(new HitEvent { Target = i });
                for (int i = 0; i < 64; i++) channel.Fire(new HitEvent { Target = i });
                channel.DispatchQueued();
            });
            Assert.Greater(sink, 0L);
        }

        private static void RunOnWorker(Action body)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.IsBackground = true;
            thread.Start();
            Assert.IsTrue(thread.Join(10000), "工作线程超时");
            if (error != null) throw new AssertionException("工作线程抛出：" + error);
        }
    }
}
