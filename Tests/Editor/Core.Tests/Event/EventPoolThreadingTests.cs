//------------------------------------------------------------
// EjoyGame Framework Tests
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using EjoyFramework.Core;
using EjoyFramework.Core.Event;
using NUnit.Framework;

namespace EjoyFramework.Tests.Event
{
    /// <summary>
    /// EventPool / EventManager 的线程契约（WS5-M2）：订阅表加锁、Update 单线程不可重入、关停后的 Fire 与退订。
    /// </summary>
    public class EventPoolThreadingTests
    {
        private const int PingId = 7101;

        public sealed class PingArgs : GameEventArgs
        {
            public override int Id { get { return PingId; } }
            public override void Clear() { }
        }

        public sealed class CountingArgs : GameEventArgs
        {
            public static int ClearCount;
            public override int Id { get { return PingId; } }
            public override void Clear() { Interlocked.Increment(ref ClearCount); }
        }

        [SetUp]
        public void Setup()
        {
            Framework.MarkMainThread();
        }

        [Test]
        public void SubscribeUnsubscribe_FromWorkers_WhileDispatching_TableStaysConsistent()
        {
            const int ThreadCount = 4;
            const int Iterations = 2000;
            var em = new EventManager();
            int dispatched = 0;
            EventHandler<GameEventArgs> anchor = (s, e) => Interlocked.Increment(ref dispatched);
            em.Subscribe(PingId, anchor);

            var errors = new List<Exception>();
            var threads = new Thread[ThreadCount];
            using (var startGate = new ManualResetEventSlim(false))
            {
                for (int t = 0; t < ThreadCount; t++)
                {
                    threads[t] = new Thread(() =>
                    {
                        startGate.Wait();
                        try
                        {
                            // 必须捕获本线程变量：不捕获的 lambda 会被编译器缓存成同一个静态委托，各线程就成了重复订阅。
                            int ownHits = 0;
                            EventHandler<GameEventArgs> mine = (s, e) => ownHits++;
                            for (int i = 0; i < Iterations; i++)
                            {
                                em.Subscribe(PingId, mine);
                                em.Fire(this, ReferencePool.Acquire<PingArgs>());
                                if (!em.Check(PingId, mine)) throw new InvalidOperationException("订阅后 Check 应为 true");
                                em.Unsubscribe(PingId, mine);
                            }
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
                bool running = true;
                while (running)
                {
                    em.Update(0.016f, 0.016f);
                    running = false;
                    foreach (Thread th in threads) running |= th.IsAlive;
                }

                foreach (Thread th in threads) th.Join();
            }

            em.Update(0.016f, 0.016f);
            CollectionAssert.IsEmpty(errors);
            Assert.AreEqual(1, em.Count(PingId), "工作线程的订阅全部退掉，只剩 anchor");
            Assert.AreEqual(ThreadCount * Iterations, dispatched);
            em.Shutdown();
        }

        [Test]
        public void SubscribeFromWorker_DispatchedOnUpdate()
        {
            var em = new EventManager();
            int hits = 0;
            var worker = new Thread(() => em.Subscribe(PingId, (s, e) => hits++));
            worker.Start();
            Assert.IsTrue(worker.Join(10000));

            em.Fire(this, ReferencePool.Acquire<PingArgs>());
            em.Update(0.016f, 0.016f);
            Assert.AreEqual(1, hits, "订阅不再要求主线程");
            em.Shutdown();
        }

        [Test]
        public void Update_ConcurrentFromAnotherThread_Throws()
        {
            var pool = new EventPool<GameEventArgs>(EventPoolMode.AllowNoHandler | EventPoolMode.AllowMultiHandler);
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                pool.Subscribe(PingId, (s, e) =>
                {
                    entered.Set();
                    release.Wait(10000);
                });
                pool.Fire(this, ReferencePool.Acquire<PingArgs>());

                var updater = new Thread(() => pool.Update(0f, 0f));
                updater.IsBackground = true;
                updater.Start();
                try
                {
                    Assert.IsTrue(entered.Wait(10000), "另一线程应已进入派发");
                    var ex = Assert.Throws<FrameworkException>(() => pool.Update(0f, 0f));
                    StringAssert.Contains("并发", ex.Message);
                }
                finally
                {
                    release.Set();
                    updater.Join(10000);
                }

                pool.Fire(this, ReferencePool.Acquire<PingArgs>());
                Assert.DoesNotThrow(() => pool.Update(0f, 0f), "派发结束后恢复可用");
            }

            pool.Shutdown();
        }

        [Test]
        public void Update_ReentrantFromHandler_Throws()
        {
            var pool = new EventPool<GameEventArgs>(EventPoolMode.AllowNoHandler | EventPoolMode.AllowMultiHandler);
            Exception inner = null;
            pool.Subscribe(PingId, (s, e) =>
            {
                try
                {
                    pool.Update(0f, 0f);
                }
                catch (FrameworkException ex)
                {
                    inner = ex;
                }
            });
            pool.Fire(this, ReferencePool.Acquire<PingArgs>());

            pool.Update(0f, 0f);
            Assert.IsNotNull(inner);
            StringAssert.Contains("重入", inner.Message);
            pool.Shutdown();
        }

        [Test]
        public void FireNow_OnWorker_RunsHandlersOnCallingThread()
        {
            var pool = new EventPool<GameEventArgs>(EventPoolMode.AllowNoHandler | EventPoolMode.AllowMultiHandler);
            int handlerThread = 0;
            int workerThread = 0;
            pool.Subscribe(PingId, (s, e) => handlerThread = Thread.CurrentThread.ManagedThreadId);

            var worker = new Thread(() =>
            {
                workerThread = Thread.CurrentThread.ManagedThreadId;
                pool.FireNow(null, ReferencePool.Acquire<PingArgs>());
            });
            worker.Start();
            Assert.IsTrue(worker.Join(10000));

            Assert.AreNotEqual(0, workerThread);
            Assert.AreEqual(workerThread, handlerThread);
            pool.Shutdown();
        }

        [Test]
        public void Fire_AfterShutdown_ReleasesArgs_WithoutDispatch()
        {
            var em = new EventManager();
            int hits = 0;
            em.Subscribe(PingId, (s, e) => hits++);
            em.Shutdown();

            var args = ReferencePool.Acquire<CountingArgs>();
            int before = CountingArgs.ClearCount;
            em.Fire(this, args);

            Assert.AreEqual(before + 1, CountingArgs.ClearCount, "关停后 Fire：参数立即回收");
            Assert.AreEqual(0, em.EventCount);
            em.Update(0.016f, 0.016f);
            Assert.AreEqual(0, hits);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Registration_AfterShutdown_IsIgnored(bool useDefaultHandler)
        {
            var pool = new EventPool<GameEventArgs>(EventPoolMode.AllowNoHandler);
            int hits = 0;
            EventHandler<GameEventArgs> handler = (s, e) => hits++;
            pool.Shutdown();

            if (useDefaultHandler) pool.SetDefaultHandler(handler);
            else pool.Subscribe(PingId, handler);

            Assert.AreEqual(0, pool.EventHandlerCount);
            Assert.IsFalse(pool.Check(PingId, handler));
            int before = CountingArgs.ClearCount;
            pool.FireNow(null, ReferencePool.Acquire<CountingArgs>());
            Assert.AreEqual(0, hits, "关停后不能通过重新注册恢复派发");
            Assert.AreEqual(before + 1, CountingArgs.ClearCount, "参数仍需回收");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Registration_WaitingAcrossShutdown_IsIgnored(bool useDefaultHandler)
        {
            var pool = new EventPool<GameEventArgs>(EventPoolMode.AllowNoHandler);
            int hits = 0;
            EventHandler<GameEventArgs> handler = (s, e) => hits++;
            RunRegistrationAcrossShutdown(pool, () =>
            {
                if (useDefaultHandler) pool.SetDefaultHandler(handler);
                else pool.Subscribe(PingId, handler);
            });

            Assert.IsTrue(pool.IsShutdown);
            Assert.AreEqual(0, pool.EventHandlerCount);
            Assert.IsFalse(pool.Check(PingId, handler));
            pool.FireNow(null, ReferencePool.Acquire<PingArgs>());
            Assert.AreEqual(0, hits, "等待订阅锁的工作线程不能在关停后安装处理器");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Registration_NullValidation_IsPreserved(bool shutdown)
        {
            var pool = new EventPool<GameEventArgs>(EventPoolMode.AllowNoHandler);
            int hits = 0;
            pool.SetDefaultHandler((s, e) => hits++);
            if (shutdown) pool.Shutdown();

            Assert.Throws<FrameworkException>(() => pool.Subscribe(PingId, null));
            Assert.DoesNotThrow(() => pool.SetDefaultHandler(null));
            pool.FireNow(null, ReferencePool.Acquire<PingArgs>());
            Assert.AreEqual(0, hits, "null 仍可清除默认处理器");
            pool.Shutdown();
        }

        private static void RunRegistrationAcrossShutdown(EventPool<GameEventArgs> pool, Action registration)
        {
            // 仅测试反射：控制既有锁，不给玩家代码添加测试回调或同步钩子。
            FieldInfo field = typeof(EventPool<GameEventArgs>).GetField(
                "m_HandlerLock", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            object handlerLock = field.GetValue(pool);
            Exception error = null;
            using (var started = new ManualResetEventSlim(false))
            {
                var worker = new Thread(() =>
                {
                    try
                    {
                        started.Set();
                        registration();
                    }
                    catch (Exception ex) { error = ex; }
                });
                worker.IsBackground = true;
                Monitor.Enter(handlerLock);
                try
                {
                    worker.Start();
                    Assert.IsTrue(started.Wait(10000), "工作线程应已开始注册");
                    // 当前线程可重入锁完成关停；工作线程必须等到关停清理完成才获得锁。
                    pool.Shutdown();
                }
                finally
                {
                    Monitor.Exit(handlerLock);
                    Assert.IsTrue(worker.Join(10000), "释放订阅锁后工作线程应完成");
                }
            }

            Assert.IsNull(error, "工作线程注册不应抛出");
        }

        [Test]
        public void Unsubscribe_Missing_ThrowsBeforeShutdown_NotAfter()
        {
            var em = new EventManager();
            EventHandler<GameEventArgs> handler = (s, e) => { };
            Assert.Throws<FrameworkException>(() => em.Unsubscribe(PingId, handler));

            em.Subscribe(PingId, handler);
            em.Shutdown();
            Assert.AreEqual(0, em.EventHandlerCount);
            Assert.DoesNotThrow(() => em.Unsubscribe(PingId, handler), "关停已清掉全部订阅，晚到的退订属正常时序");
        }
    }
}
