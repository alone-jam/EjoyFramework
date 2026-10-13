//------------------------------------------------------------
// EjoyGame Framework Tests
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using EjoyFramework.Core;
using EjoyFramework.Core.ObjectPool;

namespace EjoyFramework.Tests
{
    /// <summary>
    /// WS1-M1 对象池专业化：预热 / 收缩 / 指标 / 重复归还诊断 / 重入释放 / 热路径零分配。
    /// </summary>
    public sealed class ObjectPoolProfessionalTests
    {
        private ObjectPoolManager m_Manager;

        [SetUp]
        public void SetUp()
        {
            Framework.MarkMainThread();
            m_Manager = new ObjectPoolManager();
        }

        [TearDown]
        public void TearDown()
        {
            m_Manager.Shutdown();
        }

        // ================================================================
        //  Prewarm
        // ================================================================

        [Test]
        public void Prewarm_CreatesIdleObjects_AndSpawnHits()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 16, 60f, 0);
            int created = pool.Prewarm(4, () => PoolObj.Make(string.Empty));

            Assert.AreEqual(4, created);
            Assert.AreEqual(4, pool.Count);
            Assert.AreEqual(0, pool.SpawnedCount);

            PoolObj a = pool.Spawn();
            Assert.IsNotNull(a, "预热后 Spawn 必须命中。");
            Assert.AreEqual(1, pool.SpawnedCount);

            // 已达目标数量时不再创建。
            Assert.AreEqual(0, pool.Prewarm(4, () => PoolObj.Make(string.Empty)));
        }

        [Test]
        public void Prewarm_BeyondCapacity_ThrowsWithActionableMessage()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 2, 60f, 0);
            FrameworkException ex = Assert.Throws<FrameworkException>(() => pool.Prewarm(3, () => PoolObj.Make(string.Empty)));
            StringAssert.Contains("Capacity", ex.Message);
            Assert.AreEqual(0, pool.Count, "抛错前不应创建任何对象。");
        }

        // ================================================================
        //  Trim
        // ================================================================

        [Test]
        public void Trim_ReleasesOldestIdleFirst_AndRespectsLocked()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 16, 60f, 0);
            PoolObj oldest = PoolObj.Make("oldest");
            PoolObj middle = PoolObj.Make("middle");
            PoolObj newest = PoolObj.Make("newest");
            pool.Register(oldest, false);
            pool.Register(middle, false);
            pool.Register(newest, false);

            // 具名对象在单实例池里按名精确取用，依次使用一遍制造可区分的 LastUseTime。
            Touch(pool, "oldest");
            Thread.Sleep(20);   // 大于 Windows 计时器 15.6ms 粒度，保证 LastUseTime 严格递增
            Touch(pool, "middle");
            Thread.Sleep(20);
            Touch(pool, "newest");

            middle.Locked = true;

            // 保留 2：需释放 1 个，候选 [oldest, newest]（middle 被锁），最久未用的 oldest 先走。
            Assert.AreEqual(1, pool.Trim(2));
            Assert.IsTrue(oldest.Released, "最久未用的应先被释放。");
            Assert.IsFalse(middle.Released);
            Assert.IsFalse(newest.Released);
            Assert.AreEqual(2, pool.Count);

            // 保留 0：只能再释放 newest，Locked 的 middle 必须留下。
            Assert.AreEqual(1, pool.Trim(0));
            Assert.IsTrue(newest.Released);
            Assert.IsFalse(middle.Released, "Locked 对象不得被收缩释放。");
            Assert.AreEqual(1, pool.Count);
        }

        [Test]
        public void Trim_KeepCountAboveCount_ReleasesNothing()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 16, 60f, 0);
            pool.Prewarm(2, () => PoolObj.Make(string.Empty));
            Assert.AreEqual(0, pool.Trim(5));
            Assert.AreEqual(2, pool.Count);
        }

        // ================================================================
        //  Metrics
        // ================================================================

        [Test]
        public void Metrics_TrackSpawnUnspawnReleaseMissAndPeak()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 16, 60f, 0);

            Assert.IsNull(pool.Spawn(), "空池 Spawn 应未命中。");

            pool.Prewarm(3, () => PoolObj.Make(string.Empty));
            PoolObj a = pool.Spawn();
            PoolObj b = pool.Spawn();
            pool.Unspawn(a.Target);
            pool.Trim(2);

            ObjectPoolMetrics m;
            pool.GetMetrics(out m);
            Assert.AreEqual(2, m.Count);
            Assert.AreEqual(1, m.SpawnedCount);
            Assert.AreEqual(1, m.IdleCount);
            Assert.AreEqual(3, m.PeakCount);
            Assert.AreEqual(2, m.TotalSpawnCount);
            Assert.AreEqual(1, m.TotalUnspawnCount);
            Assert.AreEqual(1, m.TotalReleaseCount);
            Assert.AreEqual(1, m.SpawnMissCount);
            Assert.AreEqual(16, m.Capacity);
            Assert.AreEqual(2f / 3f, m.HitRate, 1e-6f);

            pool.Unspawn(b.Target);
        }

        [Test]
        public void GetAllObjectInfos_NonAllocOverload_MatchesArrayOverload()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 16, 60f, 0);
            pool.Prewarm(3, () => PoolObj.Make("x"));
            pool.Spawn("x");

            var list = new List<ObjectInfo>();
            pool.GetAllObjectInfos(list);
            ObjectInfo[] array = ((ObjectPoolBase)pool).GetAllObjectInfos();

            Assert.AreEqual(array.Length, list.Count);
            int spawned = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Assert.AreEqual("x", list[i].Name);
                if (list[i].SpawnCount > 0) spawned++;
            }

            Assert.AreEqual(1, spawned);
        }

        // ================================================================
        //  Robustness
        // ================================================================

        [Test]
        public void DoubleUnspawn_ThrowsBeforeRunningOnUnspawnTwice()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 16, 60f, 0);
            PoolObj obj = PoolObj.Make(string.Empty);
            pool.Register(obj, false);
            pool.Spawn();
            pool.Unspawn(obj.Target);

            FrameworkException ex = Assert.Throws<FrameworkException>(() => pool.Unspawn(obj.Target));
            StringAssert.Contains("重复 Unspawn", ex.Message);
            Assert.AreEqual(1, obj.UnspawnCalls, "重复归还必须在触碰用户 OnUnspawn 之前被拦下。");
            Assert.AreEqual(0, pool.SpawnedCount);
        }

        [Test]
        public void Unspawn_UnknownTarget_Throws()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 16, 60f, 0);
            Assert.Throws<FrameworkException>(() => pool.Unspawn(new object()));
        }

        [Test]
        public void ReentrantRelease_FromUserReleaseCallback_IsSafe()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 16, 60f, 0);
            var objs = new List<PoolObj>();
            for (int i = 0; i < 6; i++)
            {
                PoolObj o = PoolObj.Make(string.Empty);
                o.OnRelease = () => pool.ReleaseAllUnused();   // 释放回调里再次触发整池释放
                objs.Add(o);
                pool.Register(o, false);
            }

            Assert.DoesNotThrow(() => pool.ReleaseAllUnused());

            for (int i = 0; i < objs.Count; i++)
            {
                Assert.IsTrue(objs[i].Released, "对象 " + i + " 应被释放。");
                Assert.AreEqual(1, objs[i].ReleaseCalls, "对象 " + i + " 不得被重复释放。");
            }

            Assert.AreEqual(0, pool.Count);
        }

        [Test]
        public void ManagerRelease_ReentrantFromPoolCallback_IsSafe()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 1, 0f, 0);
            IObjectPool<PoolObj> other = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("q", 60f, 1, 0f, 5);
            PoolObj a = PoolObj.Make(string.Empty);
            a.OnRelease = () => m_Manager.Release();
            pool.Register(a, false);
            pool.Register(PoolObj.Make(string.Empty), false);
            other.Register(PoolObj.Make(string.Empty), false);
            other.Register(PoolObj.Make(string.Empty), false);

            Thread.Sleep(2);   // ExpireTime = 0 → 所有空闲对象立即过期
            Assert.DoesNotThrow(() => m_Manager.Release());
            Assert.LessOrEqual(pool.Count, 1);
            Assert.LessOrEqual(other.Count, 1);
        }

        // ================================================================
        //  Zero GC
        // ================================================================

        [Test]
        public void SpawnUnspawnCycle_SteadyState_DoesNotAllocate()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 16, 60f, 0);
            pool.Prewarm(4, () => PoolObj.Make(string.Empty));
            for (int i = 0; i < 8; i++) Cycle(pool);   // JIT 预热

            Assert.That(() =>
            {
                for (int i = 0; i < 64; i++) Cycle(pool);
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void ReleaseAndTrim_SteadyState_DoNotAllocate()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 16, 60f, 0);
            Func<PoolObj> factory = () => PoolObj.Make(string.Empty);
            pool.Prewarm(8, factory);
            pool.Trim(4);
            pool.Prewarm(8, factory);
            pool.Release();

            Assert.That(() =>
            {
                pool.Release();           // 无过期对象：走完整判定路径但不释放
                pool.Trim(4);             // 释放 4 个：快照 + 排序 + 释放 + 内部对象回收
            }, Is.Not.AllocatingGCMemory());

            Assert.AreEqual(4, pool.Count);
        }

        [Test]
        public void MetricsAndInfos_DoNotAllocate()
        {
            IObjectPool<PoolObj> pool = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("p", 60f, 16, 60f, 0);
            pool.Prewarm(4, () => PoolObj.Make(string.Empty));
            var infos = new List<ObjectInfo>(16);
            ObjectPoolMetrics warm;
            pool.GetMetrics(out warm);
            pool.GetAllObjectInfos(infos);

            Assert.That(() =>
            {
                ObjectPoolMetrics m;
                pool.GetMetrics(out m);
                pool.GetAllObjectInfos(infos);
                int idle = m.IdleCount + pool.SpawnedCount + pool.PeakCount;
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void ManagerReleaseAll_SteadyState_DoesNotAllocate()
        {
            IObjectPool<PoolObj> a = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("a", 60f, 16, 60f, 2);
            IObjectPool<PoolObj> b = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("b", 60f, 16, 60f, 1);
            a.Prewarm(2, () => PoolObj.Make(string.Empty));
            b.Prewarm(2, () => PoolObj.Make(string.Empty));
            m_Manager.Release();
            var pools = new List<ObjectPoolBase>(4);
            m_Manager.GetAllObjectPools(pools);

            TestDelegate steadyState = () =>
            {
                m_Manager.Release();
                m_Manager.GetAllObjectPools(pools);
            };
            // Warm the exact delegate as well as the APIs: Mono instruments/JITs it
            // on first invocation. The measured path still includes both operations.
            steadyState();
            Assert.That(steadyState, Is.Not.AllocatingGCMemory());

            Assert.AreEqual(2, pools.Count);
        }

        [Test]
        public void NamedConsumerPools_CanBeFoundAndDestroyedIndependently()
        {
            IObjectPoolManager manager = m_Manager;
            var enemy = manager.CreateSingleSpawnObjectPool<PoolObj>("arena.enemies", 60, 16, 60, 0);
            var other = manager.CreateSingleSpawnObjectPool<PoolObj>("preview.enemies", 60, 16, 60, 0);
            var resource = PoolObj.Make("enemy"); enemy.Register(resource, false);
            Assert.That(manager.HasObjectPool<PoolObj>("arena.enemies"), Is.True);
            Assert.That(manager.GetObjectPool<PoolObj>("arena.enemies"), Is.SameAs(enemy));
            Assert.That(manager.DestroyObjectPool<PoolObj>("arena.enemies"), Is.True);
            Assert.That(resource.Released, Is.True);
            Assert.That(manager.GetObjectPool<PoolObj>("arena.enemies"), Is.Null);
            Assert.That(manager.GetObjectPool<PoolObj>("preview.enemies"), Is.SameAs(other));
            Assert.That(manager.DestroyObjectPool<PoolObj>("arena.enemies"), Is.False);
            Assert.That(manager.Count, Is.EqualTo(1));
        }

        [TestCase("pool")]
        [TestCase("manager")]
        [TestCase("enumeration")]
        public void ReleaseOperations_WarmedDelegate_DoesNotAllocate(string operation)
        {
            var a = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("a", 60f, 16, 60f, 2);
            var b = m_Manager.CreateSingleSpawnObjectPool<PoolObj>("b", 60f, 16, 60f, 1);
            a.Prewarm(2, () => PoolObj.Make(string.Empty));
            b.Prewarm(2, () => PoolObj.Make(string.Empty));
            var pools = new List<ObjectPoolBase>(4);
            TestDelegate body = operation == "pool" ? (TestDelegate)(() => a.Release())
                : operation == "manager" ? () => m_Manager.Release()
                : () => m_Manager.GetAllObjectPools(pools);
            for (int i = 0; i < 8; i++) body();
            Assert.That(body, Is.Not.AllocatingGCMemory());
        }

        // ================================================================
        //  helpers
        // ================================================================

        private static void Cycle(IObjectPool<PoolObj> pool)
        {
            PoolObj o = pool.Spawn();
            pool.Unspawn(o.Target);
        }

        private static void Touch(IObjectPool<PoolObj> pool, string name)
        {
            PoolObj got = pool.Spawn(name);
            Assert.IsNotNull(got, "具名对象应可取到：" + name);
            pool.Unspawn(got.Target);
        }

        private sealed class PoolObj : ObjectBase
        {
            public bool Released;
            public int ReleaseCalls;
            public int UnspawnCalls;
            public Action OnRelease;

            public static PoolObj Make(string name)
            {
                var o = ReferencePool.Acquire<PoolObj>();
                o.Initialize(name, new object());
                o.Released = false;
                o.ReleaseCalls = 0;
                o.UnspawnCalls = 0;
                o.OnRelease = null;
                return o;
            }

            protected internal override void OnUnspawn()
            {
                UnspawnCalls++;
            }

            protected internal override void Release(bool isShutdown)
            {
                Released = true;
                ReleaseCalls++;
                Action cb = OnRelease;
                OnRelease = null;
                if (cb != null) cb();
            }
        }
    }
}
