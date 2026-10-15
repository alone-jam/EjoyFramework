//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using EjoyFramework.Core.ObjectPool;
using EjoyFramework.Core.Resource;

namespace EjoyFramework.Core.Entity
{
    /// <summary>
    /// 实体管理器（完整实现）。
    ///
    /// 核心流程：
    ///   ShowEntity
    ///     1) 检查 group / id 合法
    ///     2) 实例池命中（同 assetName）→ Spawn → 直接 InternalShow（isNew=false）
    ///     3) 未命中 → ResourceManager.LoadAsset 异步 → 回调中 Instantiate + Register 进池（spawned=true）+ InternalShow
    ///     4) InternalShow: CreateEntity → group.AddEntity → 触发 ShowEntitySuccess
    ///   HideEntity
    ///     1) 加载中：从 m_LoadingEntities 删除（异步回调到达时丢弃 asset）
    ///     2) 已加载：group.RemoveEntity → instancePool.Unspawn(target) → 触发 HideEntityComplete
    /// </summary>
    internal sealed class EntityManager : FrameworkModule, IEntityManager
    {
        private const string EntityPoolPrefix = "Entity Instance Pool - ";

        private readonly Dictionary<string, EntityGroup> m_Groups = new Dictionary<string, EntityGroup>(StringComparer.Ordinal);
        private readonly Dictionary<int, EntityState> m_Entities = new Dictionary<int, EntityState>();
        private readonly Dictionary<int, LoadingEntityInfo> m_LoadingEntities = new Dictionary<int, LoadingEntityInfo>();
        // assetName → 已加载实体 id 列表，使 HasEntity(string)/GetEntities(string) 由 O(N) 线性扫描降为 O(1)。
        // 在 InternalShow（add）与 HideEntityInternal（remove）两处维护，保持与 m_Entities 一致。
        private readonly Dictionary<string, List<int>> m_EntityIdsByAssetName = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        private IEntityHelper m_EntityHelper;
        private IObjectPoolManager m_ObjectPoolManager;
        private IResourceManager m_ResourceManager;

        public event EventHandler<ShowEntitySuccessEventArgs> ShowEntitySuccess;
        public event EventHandler<ShowEntityFailureEventArgs> ShowEntityFailure;
        public event EventHandler<HideEntityCompleteEventArgs> HideEntityComplete;

        public EntityManager()
        {
        }

        /// <summary>测试用构造：直接注入 IResourceManager + IObjectPoolManager（任一可为 null，懒拉取兜底）。</summary>
        internal EntityManager(IResourceManager resourceManager, IObjectPoolManager objectPoolManager)
        {
            m_ResourceManager = resourceManager;
            m_ObjectPoolManager = objectPoolManager;
        }

        // Priority 0：业务模块，依赖 Resource/ObjectPool。
        public override int Priority { get { return 0; } }

        // 必需配置自检：依赖 EntityHelper 实例化/创建实体。
        public override bool RequiresConfiguration { get { return true; } }
        public override bool IsModuleConfigured { get { return m_EntityHelper != null; } }
        public override string ConfigurationHint { get { return "Call SetEntityHelper(...) before use."; } }

        public int EntityCount { get { return m_Entities.Count; } }
        public int EntityGroupCount { get { return m_Groups.Count; } }

        // 复用的实体快照缓冲：Update 遍历快照而非字典本身，允许 OnUpdate 内 Hide/Show 实体。
        private IEntity[] m_UpdateBuffer = Array.Empty<IEntity>();

        public override void Update(float elapseSeconds, float realElapseSeconds)
        {
            if (m_EntityHelper == null || m_Entities.Count == 0) return;
            // 快照后驱动每个实体的 OnUpdate（此前从未驱动，EntityLogic.OnUpdate 是死代码）。
            if (m_UpdateBuffer.Length < m_Entities.Count) m_UpdateBuffer = new IEntity[Math.Max(8, m_Entities.Count * 2)];
            int n = 0;
            foreach (var kv in m_Entities) m_UpdateBuffer[n++] = kv.Value.Form;
            for (int i = 0; i < n; i++)
            {
                IEntity e = m_UpdateBuffer[i];
                m_UpdateBuffer[i] = null;
                try { m_EntityHelper.UpdateEntity(e, elapseSeconds, realElapseSeconds); }
                catch (Exception ex) { FrameworkLog.Error("EntityHelper.UpdateEntity threw: {0}", ex); }
            }
        }

        public override void Shutdown()
        {
            HideAllLoadedEntitiesInternal(true);
            // 加载中实体必须 Cancel handle，否则晚到资产没人 UnloadAsset → 内存泄漏。
            HideAllLoadingEntities();
            foreach (var kv in m_Groups) kv.Value.Shutdown();
            m_Groups.Clear();
            // HideAllLoadedEntitiesInternal 已逐个 IndexRemove，此处兜底清空保持与 m_Entities 同步。
            m_EntityIdsByAssetName.Clear();
        }

        // ===== 注入 =====

        private IResourceManager Resource
        {
            get { return m_ResourceManager ?? (m_ResourceManager = Framework.GetModule<IResourceManager>()); }
        }

        private IObjectPoolManager ObjectPool
        {
            get { return m_ObjectPoolManager ?? (m_ObjectPoolManager = Framework.GetModule<IObjectPoolManager>()); }
        }

        public void SetEntityHelper(IEntityHelper h)
        {
            Framework.EnsureMainThread(nameof(SetEntityHelper));
            if (h == null) throw new FrameworkException("Entity helper is invalid.");
            m_EntityHelper = h;
        }

        // ===== EntityGroup =====

        public bool HasEntityGroup(string n) { return n != null && m_Groups.ContainsKey(n); }

        public IEntityGroup GetEntityGroup(string n)
        {
            EntityGroup g;
            return n != null && m_Groups.TryGetValue(n, out g) ? (IEntityGroup)g : null;
        }

        public IEntityGroup[] GetAllEntityGroups()
        {
            var arr = new IEntityGroup[m_Groups.Count];
            int i = 0;
            foreach (var kv in m_Groups) arr[i++] = kv.Value;
            return arr;
        }

        public bool AddEntityGroup(string name, float autoReleaseInterval, int capacity, float expireTime, int priority, IEntityGroupHelper helper)
        {
            Framework.EnsureMainThread(nameof(AddEntityGroup));
            if (string.IsNullOrEmpty(name)) throw new FrameworkException("Entity group name is invalid.");
            if (m_Groups.ContainsKey(name)) return false;

            // Live instances have one owner: active entities must never borrow the same Target.
            // Idle instances with the same asset name remain reusable after HideEntity.
            var pool = ObjectPool.CreateSingleSpawnObjectPool<EntityInstanceObject>(
                EntityPoolPrefix + name, autoReleaseInterval, capacity, expireTime, priority);

            m_Groups.Add(name, new EntityGroup(name, helper, pool));
            return true;
        }

        // ===== 查询 =====

        public bool HasEntity(int id) { return m_Entities.ContainsKey(id); }
        public bool HasEntity(string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return false;
            List<int> ids;
            return m_EntityIdsByAssetName.TryGetValue(assetName, out ids) && ids.Count > 0;
        }

        public IEntity GetEntity(int id)
        {
            EntityState s;
            return m_Entities.TryGetValue(id, out s) ? s.Form : null;
        }

        public IEntity[] GetEntities(string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return Array.Empty<IEntity>();
            List<int> ids;
            if (!m_EntityIdsByAssetName.TryGetValue(assetName, out ids) || ids.Count == 0) return Array.Empty<IEntity>();

            var list = new List<IEntity>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                EntityState s;
                if (m_Entities.TryGetValue(ids[i], out s)) list.Add(s.Form);
            }
            return list.ToArray();
        }

        // ===== assetName → entityId 索引维护（InternalShow 增 / HideEntityInternal 删，保持与 m_Entities 一致）=====
        private void IndexAdd(string assetName, int entityId)
        {
            List<int> ids;
            if (!m_EntityIdsByAssetName.TryGetValue(assetName, out ids))
            {
                ids = new List<int>(1);
                m_EntityIdsByAssetName.Add(assetName, ids);
            }
            ids.Add(entityId);
        }

        private void IndexRemove(string assetName, int entityId)
        {
            List<int> ids;
            if (m_EntityIdsByAssetName.TryGetValue(assetName, out ids))
            {
                ids.Remove(entityId);
                if (ids.Count == 0) m_EntityIdsByAssetName.Remove(assetName);
            }
        }

        public IEntity[] GetAllLoadedEntities()
        {
            var arr = new IEntity[m_Entities.Count];
            int i = 0;
            foreach (var kv in m_Entities) arr[i++] = kv.Value.Form;
            return arr;
        }

        // ===== Show =====

        public void ShowEntity(int entityId, string assetName, string groupName, int priority, object userData)
        {
            Framework.EnsureMainThread(nameof(ShowEntity));
            if (m_EntityHelper == null) throw new FrameworkException("Entity helper is not set.");
            if (string.IsNullOrEmpty(assetName)) throw new FrameworkException("Entity asset name is invalid.");
            if (string.IsNullOrEmpty(groupName)) throw new FrameworkException("Entity group name is invalid.");
            if (m_Entities.ContainsKey(entityId)) throw new FrameworkException(Utility.Text.Format("Entity id {0} already exists.", entityId));
            if (m_LoadingEntities.ContainsKey(entityId)) throw new FrameworkException(Utility.Text.Format("Entity id {0} is loading.", entityId));

            EntityGroup group;
            if (!m_Groups.TryGetValue(groupName, out group))
                throw new FrameworkException(Utility.Text.Format("Entity group '{0}' does not exist.", groupName));

            float startTime = NowSeconds();

            // 实例池命中（同 assetName）
            EntityInstanceObject instanceObj = group.InstancePool.Spawn(assetName);
            if (instanceObj != null)
            {
                InternalShow(entityId, assetName, group, instanceObj.Target, /*isNew*/ false, /*duration*/ 0f, priority, userData);
                return;
            }

            // 未命中：异步加载
            var info = new LoadingEntityInfo
            {
                AssetName = assetName,
                GroupName = groupName,
                Priority = priority,
                UserData = userData,
                StartTime = startTime,
            };
            m_LoadingEntities.Add(entityId, info);
            var handle = Resource.LoadAssetWithHandle(assetName, priority, entityId);
            info.LoadHandle = handle;
            handle.Completed += OnEntityLoadCompleted;
        }

        // ===== Hide =====

        public void HideEntity(int entityId) { HideEntityInternal(entityId, null, false); }

        public void HideEntity(int entityId, object userData) { HideEntityInternal(entityId, userData, false); }

        private void HideEntityInternal(int entityId, object userData, bool isShutdown)
        {
            Framework.EnsureMainThread(nameof(HideEntity));
            // 加载中：取消句柄并移除
            if (m_LoadingEntities.TryGetValue(entityId, out var loading))
            {
                m_LoadingEntities.Remove(entityId);
                if (loading.LoadHandle != null) loading.LoadHandle.Cancel();
                FrameworkLog.Debug("Cancelled loading entity id={0}", entityId);
                return;
            }

            EntityState state;
            if (!m_Entities.TryGetValue(entityId, out state))
            {
                throw new FrameworkException(Utility.Text.Format("Can not find entity id {0}.", entityId));
            }

            EntityGroup group = state.Group;
            string assetName = state.Form.EntityAssetName;

            group.RemoveEntity(state);
            m_Entities.Remove(entityId);
            // 用 state.AssetName 维护索引——与 InternalShow 中 IndexAdd 使用的 key 完全一致，
            // 避免依赖 Form.EntityAssetName 可能与之不同步时索引泄漏。
            IndexRemove(state.AssetName, entityId);

            // 归还池前驱动 OnHide（此前从未触发，EntityLogic.OnHide 是死代码）。
            try { m_EntityHelper.HideEntity(state.Form, isShutdown, userData); }
            catch (Exception ex) { FrameworkLog.Error("Entity '{0}' HideEntity threw: {1}", assetName, ex); }

            // 归还实例池：Unspawn 接受 Target（GameObject 实例），无须 wrapper 引用
            try { group.InstancePool.Unspawn(state.Instance); }
            catch (Exception ex) { FrameworkLog.Error("Entity '{0}' Unspawn threw: {1}", assetName, ex); }

            var h = HideEntityComplete;
            if (h != null)
            {
                var args = HideEntityCompleteEventArgs.Create(entityId, assetName, group, userData);
                try { h(this, args); }
                catch (Exception ex) { FrameworkLog.Error("HideEntityComplete handler threw: {0}", ex); }
                ReferencePool.Release(args);
            }
        }

        public void HideAllLoadedEntities() { HideAllLoadedEntitiesInternal(false); }

        private void HideAllLoadedEntitiesInternal(bool isShutdown)
        {
            Framework.EnsureMainThread(nameof(HideAllLoadedEntities));
            int[] ids = new int[m_Entities.Count];
            int i = 0;
            foreach (var kv in m_Entities) ids[i++] = kv.Key;
            for (int k = 0; k < ids.Length; k++)
            {
                try { HideEntityInternal(ids[k], null, isShutdown); }
                catch (Exception ex) { FrameworkLog.Error("HideEntity({0}) threw: {1}", ids[k], ex); }
            }
        }

        public void AttachEntity(int childEntityId, int parentEntityId, object userData)
        {
            Framework.EnsureMainThread(nameof(AttachEntity));
            if (m_EntityHelper == null) throw new FrameworkException("Entity helper is not set.");
            if (!m_Entities.TryGetValue(childEntityId, out var child))
                throw new FrameworkException(Utility.Text.Format("Can not find child entity id {0}.", childEntityId));
            if (!m_Entities.TryGetValue(parentEntityId, out var parent))
                throw new FrameworkException(Utility.Text.Format("Can not find parent entity id {0}.", parentEntityId));
            m_EntityHelper.AttachEntity(child.Form, parent.Form, userData);
        }

        public void DetachEntity(int childEntityId, object userData)
        {
            Framework.EnsureMainThread(nameof(DetachEntity));
            if (m_EntityHelper == null) throw new FrameworkException("Entity helper is not set.");
            if (!m_Entities.TryGetValue(childEntityId, out var child))
                throw new FrameworkException(Utility.Text.Format("Can not find child entity id {0}.", childEntityId));
            // 父实体可选：当前实现按子实体当前父节点解除；parent 传 null，helper 把子节点归位到 InstanceRoot。
            m_EntityHelper.DetachEntity(child.Form, null, userData);
        }

        public void HideAllLoadingEntities()
        {
            Framework.EnsureMainThread(nameof(HideAllLoadingEntities));
            foreach (var kv in m_LoadingEntities)
            {
                if (kv.Value.LoadHandle != null && !kv.Value.LoadHandle.IsDone) kv.Value.LoadHandle.Cancel();
            }
            m_LoadingEntities.Clear();
        }

        // ===== 资源加载回调（handle.Completed） =====

        private void OnEntityLoadCompleted(IAssetLoadHandle handle)
        {
            if (!(handle.UserData is int entityId))
            {
                FrameworkLog.Error("OnEntityLoadCompleted: unexpected UserData type '{0}', expected int entityId.",
                    handle.UserData?.GetType().FullName ?? "null");
                if (handle.Asset != null && Resource != null) Resource.UnloadAsset(handle.Asset);
                return;
            }
            if (!m_LoadingEntities.TryGetValue(entityId, out var matched))
            {
                // Shutdown/Hide 竞态：HideAllLoadingEntities 已取消并移除该条目，但资产仍可能晚到。
                // 早返回分支必须释放已加载资产，否则泄漏。
                if (handle.Status != LoadAssetStatus.Cancelled && handle.Asset != null && Resource != null)
                    Resource.UnloadAsset(handle.Asset);
                return;
            }
            m_LoadingEntities.Remove(entityId);

            if (handle.Status == LoadAssetStatus.Cancelled) return;
            if (handle.Status == LoadAssetStatus.Failed)
            {
                FireShowFailure(entityId, matched.AssetName, matched.GroupName, handle.FailureStatus + ": " + handle.ErrorMessage, matched.UserData);
                return;
            }

            object asset = handle.Asset;
            EntityGroup group;
            if (!m_Groups.TryGetValue(matched.GroupName, out group))
            {
                FireShowFailure(entityId, matched.AssetName, matched.GroupName, "Entity group disappeared during loading.", matched.UserData);
                if (asset != null) Resource.UnloadAsset(asset);
                return;
            }

            object instance;
            try { instance = m_EntityHelper.InstantiateEntity(asset); }
            catch (Exception ex)
            {
                FireShowFailure(entityId, matched.AssetName, matched.GroupName, "InstantiateEntity threw: " + ex.Message, matched.UserData);
                Resource.UnloadAsset(asset);
                return;
            }

            EntityInstanceObject instanceObj = EntityInstanceObject.Create(matched.AssetName, asset, instance, m_EntityHelper, Resource);
            group.InstancePool.Register(instanceObj, /*spawned*/ true);

            float totalDuration = NowSeconds() - matched.StartTime;
            InternalShow(entityId, matched.AssetName, group, instance, /*isNew*/ true, totalDuration, matched.Priority, matched.UserData);
        }

        // ===== 内部 Show（实例就绪） =====

        private void InternalShow(int entityId, string assetName, EntityGroup group, object instance,
            bool isNew, float duration, int priority, object userData)
        {
            IEntity entity;
            try { entity = m_EntityHelper.CreateEntity(instance, group, userData); }
            catch (Exception ex)
            {
                FireShowFailure(entityId, assetName, group.Name, "CreateEntity threw: " + ex.Message, userData);
                // 实例出错也得归还池避免泄漏
                try { group.InstancePool.Unspawn(instance); } catch (Exception ue) { FrameworkLog.Error("Entity recovery Unspawn threw: {0}", ue); }
                return;
            }
            if (entity == null)
            {
                FireShowFailure(entityId, assetName, group.Name, "CreateEntity returned null.", userData);
                try { group.InstancePool.Unspawn(instance); } catch (Exception ue) { FrameworkLog.Error("Entity recovery Unspawn threw: {0}", ue); }
                return;
            }

            var state = new EntityState
            {
                Form = entity,
                Group = group,
                Priority = priority,
                AssetName = assetName,
                EntityId = entityId,
                Instance = instance,
            };
            m_Entities.Add(entityId, state);
            IndexAdd(assetName, entityId);
            group.AddEntity(state);

            var h = ShowEntitySuccess;
            if (h != null)
            {
                var args = ShowEntitySuccessEventArgs.Create(entityId, assetName, group, duration, userData, entity);
                try { h(this, args); }
                catch (Exception ex) { FrameworkLog.Error("ShowEntitySuccess threw: {0}", ex); }
                ReferencePool.Release(args);
            }
        }

        private void FireShowFailure(int entityId, string assetName, string groupName, string error, object userData)
        {
            FrameworkLog.Error("ShowEntity failed: id={0} asset='{1}' group='{2}' error={3}", entityId, assetName, groupName, error);
            var h = ShowEntityFailure;
            if (h != null)
            {
                var args = ShowEntityFailureEventArgs.Create(entityId, assetName, groupName, error, userData);
                try { h(this, args); }
                catch (Exception ex) { FrameworkLog.Error("ShowEntityFailure threw: {0}", ex); }
                ReferencePool.Release(args);
            }
        }

        private static float NowSeconds()
        {
            return Utility.Timestamp.SecondsF;
        }

        // ===== 内部状态 =====

        internal sealed class EntityState
        {
            public IEntity Form;
            public EntityGroup Group;
            public int Priority;
            public string AssetName;
            public int EntityId;
            public object Instance;
        }

        private sealed class LoadingEntityInfo
        {
            public string AssetName;
            public string GroupName;
            public int Priority;
            public object UserData;
            public float StartTime;
            public IAssetLoadHandle LoadHandle;
        }

        // ===== EntityGroup（完整实现） =====

        internal sealed class EntityGroup : IEntityGroup
        {
            private readonly string m_Name;
            private readonly IEntityGroupHelper m_Helper;
            private readonly IObjectPool<EntityInstanceObject> m_InstancePool;
            private readonly Dictionary<int, EntityState> m_GroupEntities = new Dictionary<int, EntityState>();

            public EntityGroup(string name, IEntityGroupHelper helper, IObjectPool<EntityInstanceObject> pool)
            {
                m_Name = name; m_Helper = helper; m_InstancePool = pool;
            }

            public string Name { get { return m_Name; } }
            public int EntityCount { get { return m_GroupEntities.Count; } }
            public IObjectPool<EntityInstanceObject> InstancePool { get { return m_InstancePool; } }
            public IEntityGroupHelper Helper { get { return m_Helper; } }

            public void AddEntity(EntityState s) { m_GroupEntities[s.EntityId] = s; }
            public void RemoveEntity(EntityState s) { m_GroupEntities.Remove(s.EntityId); }

            public void Shutdown() { m_GroupEntities.Clear(); }
        }
    }
}
