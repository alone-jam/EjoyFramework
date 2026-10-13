//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace EjoyFramework.Core.ObjectPool
{
    /// <summary>
    /// 对象池管理器。
    /// 修复：
    ///   - Update 前对池字典做数组快照，允许 Update 中创建/销毁 ObjectPool。
    ///   - Release / ReleaseAllUnused 也走快照，避免回调中改池字典损坏迭代器。
    ///   - 主线程断言入口。
    /// </summary>
    internal sealed partial class ObjectPoolManager : FrameworkModule, IObjectPoolManager
    {
        private const int DefaultCapacity = int.MaxValue;
        private const float DefaultExpireTime = float.MaxValue;
        private const int DefaultPriority = 0;

        private readonly Dictionary<TypeNamePair, ObjectPoolBase> m_ObjectPools;
        private ObjectPoolBase[] m_UpdateScratch = Array.Empty<ObjectPoolBase>();
        private readonly List<ObjectPoolBase> m_ReleaseSnapshot = new List<ObjectPoolBase>();
        private bool m_Releasing;

        /// <summary>按优先级升序释放。排序走 NoAllocSort（List.Sort 在 Mono 下会分配委托/包装对象）。</summary>
        private static readonly IComparer<ObjectPoolBase> s_ByPriorityAscending = new PriorityComparer();

        public ObjectPoolManager()
        {
            m_ObjectPools = new Dictionary<TypeNamePair, ObjectPoolBase>();
        }

        // Priority 50：对象池释放/spawn 早于使用方（Entity/UI 等）。
        public override int Priority
        {
            get { return 50; }
        }

        public int Count
        {
            get { return m_ObjectPools.Count; }
        }

        public override void Update(float elapseSeconds, float realElapseSeconds)
        {
            int n = m_ObjectPools.Count;
            if (n == 0) return;

            if (m_UpdateScratch.Length < n)
            {
                m_UpdateScratch = new ObjectPoolBase[Math.Max(8, n * 2)];
            }
            else if (m_UpdateScratch.Length > 16 && m_UpdateScratch.Length > n * 4)
            {
                // 一次性峰值后池数量回落，scratch 远大于所需：收缩回 max(16, n*2)，回收多余内存。
                m_UpdateScratch = new ObjectPoolBase[Math.Max(16, n * 2)];
            }

            int idx = 0;
            foreach (KeyValuePair<TypeNamePair, ObjectPoolBase> kvp in m_ObjectPools)
            {
                m_UpdateScratch[idx++] = kvp.Value;
            }

            for (int i = 0; i < idx; i++)
            {
                try { m_UpdateScratch[i].Update(elapseSeconds, realElapseSeconds); }
                catch (Exception ex) { FrameworkLog.Error("ObjectPool '{0}' Update threw: {1}", m_UpdateScratch[i].FullName, ex); }
                m_UpdateScratch[i] = null;
            }
        }

        public override void Shutdown()
        {
            ObjectPoolBase[] toShutdown = new ObjectPoolBase[m_ObjectPools.Count];
            int idx = 0;
            foreach (KeyValuePair<TypeNamePair, ObjectPoolBase> kvp in m_ObjectPools)
            {
                toShutdown[idx++] = kvp.Value;
            }
            m_ObjectPools.Clear();

            for (int i = 0; i < toShutdown.Length; i++)
            {
                try { toShutdown[i].Shutdown(); }
                catch (Exception ex) { FrameworkLog.Error("ObjectPool '{0}' Shutdown threw: {1}", toShutdown[i].FullName, ex); }
            }
        }

        public bool HasObjectPool<T>() where T : ObjectBase
        {
            return HasObjectPool<T>(string.Empty);
        }

        public bool HasObjectPool<T>(string name) where T : ObjectBase
        {
            return m_ObjectPools.ContainsKey(new TypeNamePair(typeof(T), name));
        }

        public ObjectPoolBase[] GetAllObjectPools()
        {
            var arr = new ObjectPoolBase[m_ObjectPools.Count];
            int i = 0;
            foreach (var kv in m_ObjectPools) arr[i++] = kv.Value;
            return arr;
        }

        public void GetAllObjectPools(List<ObjectPoolBase> results)
        {
            if (results == null)
            {
                throw new FrameworkException("Results is invalid.");
            }

            results.Clear();
            foreach (KeyValuePair<TypeNamePair, ObjectPoolBase> kv in m_ObjectPools)
            {
                results.Add(kv.Value);
            }
        }

        public IObjectPool<T> GetObjectPool<T>() where T : ObjectBase
        {
            return GetObjectPool<T>(string.Empty);
        }

        public IObjectPool<T> GetObjectPool<T>(string name) where T : ObjectBase
        {
            ObjectPoolBase objectPool = null;
            if (m_ObjectPools.TryGetValue(new TypeNamePair(typeof(T), name), out objectPool))
            {
                return (IObjectPool<T>)objectPool;
            }

            return null;
        }

        public IObjectPool<T> CreateSingleSpawnObjectPool<T>(string name, float autoReleaseInterval, int capacity, float expireTime, int priority) where T : ObjectBase
        {
            return InternalCreateObjectPool<T>(name, false, autoReleaseInterval, capacity, expireTime, priority);
        }

        public IObjectPool<T> CreateMultiSpawnObjectPool<T>(string name, float autoReleaseInterval, int capacity, float expireTime, int priority) where T : ObjectBase
        {
            return InternalCreateObjectPool<T>(name, true, autoReleaseInterval, capacity, expireTime, priority);
        }

        public bool DestroyObjectPool<T>() where T : ObjectBase
        {
            return DestroyObjectPool<T>(string.Empty);
        }

        public bool DestroyObjectPool<T>(string name) where T : ObjectBase
        {
            return InternalDestroyObjectPool(new TypeNamePair(typeof(T), name));
        }

        public void Release()
        {
            Framework.EnsureMainThread("ObjectPoolManager.Release");
            ReleaseAll(/*allUnused*/ false);
        }

        public void ReleaseAllUnused()
        {
            Framework.EnsureMainThread("ObjectPoolManager.ReleaseAllUnused");
            ReleaseAll(/*allUnused*/ true);
        }

        /// <summary>
        /// 按优先级升序遍历所有池执行释放。遍历基于快照：用户 Release 回调里创建/销毁池不会破坏迭代。
        /// 稳态复用 m_ReleaseSnapshot（零分配）；释放过程中被重入时退化为分配一份新数组。
        /// </summary>
        private void ReleaseAll(bool allUnused)
        {
            if (m_Releasing)
            {
                ObjectPoolBase[] reentrant = SnapshotPoolsSorted();
                for (int i = 0; i < reentrant.Length; i++)
                {
                    ReleaseOne(reentrant[i], allUnused);
                }

                return;
            }

            m_Releasing = true;
            try
            {
                m_ReleaseSnapshot.Clear();
                foreach (KeyValuePair<TypeNamePair, ObjectPoolBase> kvp in m_ObjectPools)
                {
                    m_ReleaseSnapshot.Add(kvp.Value);
                }

                NoAllocSort.Sort(m_ReleaseSnapshot, s_ByPriorityAscending);
                for (int i = 0; i < m_ReleaseSnapshot.Count; i++)
                {
                    ReleaseOne(m_ReleaseSnapshot[i], allUnused);
                }
            }
            finally
            {
                m_ReleaseSnapshot.Clear();
                m_Releasing = false;
            }
        }

        private static void ReleaseOne(ObjectPoolBase pool, bool allUnused)
        {
            try
            {
                if (allUnused) pool.ReleaseAllUnused();
                else pool.Release();
            }
            catch (Exception ex)
            {
                FrameworkLog.Error("ObjectPool '{0}' {1} threw: {2}", pool.FullName, allUnused ? "ReleaseAllUnused" : "Release", ex);
            }
        }

        private ObjectPoolBase[] SnapshotPoolsSorted()
        {
            ObjectPoolBase[] snapshot = new ObjectPoolBase[m_ObjectPools.Count];
            int idx = 0;
            foreach (KeyValuePair<TypeNamePair, ObjectPoolBase> kvp in m_ObjectPools)
            {
                snapshot[idx++] = kvp.Value;
            }

            Array.Sort(snapshot, s_ByPriorityAscending);
            return snapshot;
        }

        private sealed class PriorityComparer : IComparer<ObjectPoolBase>
        {
            public int Compare(ObjectPoolBase a, ObjectPoolBase b)
            {
                return a.Priority.CompareTo(b.Priority);
            }
        }

        private IObjectPool<T> InternalCreateObjectPool<T>(string name, bool allowMultiSpawn, float autoReleaseInterval, int capacity, float expireTime, int priority) where T : ObjectBase
        {
            Framework.EnsureMainThread("ObjectPoolManager.CreateObjectPool");
            TypeNamePair typeNamePair = new TypeNamePair(typeof(T), name);
            if (m_ObjectPools.ContainsKey(typeNamePair))
            {
                throw new FrameworkException(Utility.Text.Format("Already exist object pool '{0}'.", typeNamePair));
            }

            ObjectPool<T> objectPool = new ObjectPool<T>(name, allowMultiSpawn, autoReleaseInterval, capacity, expireTime, priority);
            m_ObjectPools.Add(typeNamePair, objectPool);
            return objectPool;
        }

        private bool InternalDestroyObjectPool(TypeNamePair typeNamePair)
        {
            Framework.EnsureMainThread("ObjectPoolManager.DestroyObjectPool");
            ObjectPoolBase objectPool = null;
            if (m_ObjectPools.TryGetValue(typeNamePair, out objectPool))
            {
                m_ObjectPools.Remove(typeNamePair);
                try { objectPool.Shutdown(); }
                catch (Exception ex) { FrameworkLog.Error("ObjectPool '{0}' Shutdown(destroy) threw: {1}", objectPool.FullName, ex); }
                return true;
            }

            return false;
        }
    }
}
