//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using EjoyFramework.Core.ObjectPool;
using EjoyFramework.Core.Resource;

namespace EjoyFramework.Core.UI
{
    /// <summary>
    /// 界面管理器（完整实现）。
    ///
    /// 核心流程：
    ///   OpenUIForm
    ///     1) 分配 SerialId
    ///     2) 实例池命中（同 assetName）→ Spawn → 直接 InternalOpenUIForm（isNewInstance=false）
    ///     3) 未命中 → ResourceManager.LoadAsset 异步 → 回调中 Instantiate + Register 进池 + InternalOpenUIForm
    ///     4) InternalOpenUIForm: CreateUIForm → OnInit → group.AddUIForm → group.Refresh → OnOpen → fire 成功事件
    ///   CloseUIForm
    ///     1) 加载中：在 m_LoadingRequests 中按 serial 命中并标记 Cancelled（晚到回调据此卸载 asset 而非打开 form）
    ///     2) 已加载：OnClose → group.RemoveUIForm + Refresh → OnRecycle → instancePool.Unspawn(instance) → fire 完成事件
    ///   Update
    ///     foreach group → 按 depth 从顶到底 OnUpdate 已加载 form（pause 时整组停更新）
    ///   UIGroup
    ///     按 (priority desc, addedSeq desc) 排序，Refresh 时 OnDepthChanged + Pause/Resume/Cover/Reveal 级联
    /// </summary>
    public sealed class UIManager : FrameworkModule, IUIManager
    {
        private const string UIFormPoolName = "UI Form Instance Pool";
        private const float DefaultInstanceAutoReleaseInterval = 60f;
        private const int DefaultInstanceCapacity = 16;
        private const float DefaultInstanceExpireTime = 60f;
        private const int DefaultInstancePriority = 0;

        private readonly Dictionary<string, UIGroup> m_UIGroups = new Dictionary<string, UIGroup>(StringComparer.Ordinal);
        private readonly Dictionary<int, UIFormState> m_UIFormsBySerial = new Dictionary<int, UIFormState>();
        // 在途加载请求按"打开 serial"索引。LoadAsset 的 userData 参数携带该 serial，
        // success/failure 回调据此 O(1) 精确回溯发起方 —— 不再按 AssetName 匹配，
        // 故 open→close→re-open 同一 asset 不会错配到别的请求、也不会留下孤立条目。
        private readonly Dictionary<int, PendingOpen> m_LoadingRequests = new Dictionary<int, PendingOpen>();
        // assetName → 已加载 form 列表，使 GetUIForm(string)/HasUIForm(string) 由 O(N) 线性扫描降为 O(1)。
        private readonly Dictionary<string, List<UIFormState>> m_FormsByAssetName = new Dictionary<string, List<UIFormState>>(StringComparer.Ordinal);
        // Update 复用的 grow-only 快照缓冲，避免每帧每组 new UIFormState[n]。
        private UIFormState[] m_UpdateSnapshotBuffer = new UIFormState[16];

        private IObjectPool<UIFormInstanceObject> m_InstancePool;
        private IUIFormHelper m_UIFormHelper;
        private IObjectPoolManager m_ObjectPoolManager;
        private IResourceManager m_ResourceManager;
        private LoadAssetCallbacks m_LoadAssetCallbacks;
        private int m_Serial;
        private bool m_ShuttingDown;

        public event EventHandler<OpenUIFormSuccessEventArgs> OpenUIFormSuccess;
        public event EventHandler<OpenUIFormFailureEventArgs> OpenUIFormFailure;
        public event EventHandler<CloseUIFormCompleteEventArgs> CloseUIFormComplete;

        public UIManager()
        {
            m_LoadAssetCallbacks = new LoadAssetCallbacks(LoadAssetSuccessCallback, LoadAssetFailureCallback);
        }

        /// <summary>测试用构造：直接注入 IResourceManager + IObjectPoolManager。</summary>
        internal UIManager(IResourceManager resourceManager, IObjectPoolManager objectPoolManager) : this()
        {
            m_ResourceManager = resourceManager;
            m_ObjectPoolManager = objectPoolManager;
        }

        // Priority 0：业务模块，依赖 Resource/ObjectPool。
        public override int Priority { get { return 0; } }

        // 必需配置自检：依赖 UIFormHelper 实例化/创建界面。
        public override bool RequiresConfiguration { get { return true; } }
        public override bool IsModuleConfigured { get { return m_UIFormHelper != null; } }
        public override string ConfigurationHint { get { return "Call SetUIFormHelper(...) before use."; } }

        public int UIGroupCount { get { return m_UIGroups.Count; } }

        public override void Update(float elapseSeconds, float realElapseSeconds)
        {
            foreach (var kv in m_UIGroups)
            {
                try { kv.Value.Update(elapseSeconds, realElapseSeconds, ref m_UpdateSnapshotBuffer); }
                catch (Exception ex) { FrameworkLog.Error("UIGroup '{0}' Update threw: {1}", kv.Key, ex); }
            }
        }

        public override void Shutdown()
        {
            m_ShuttingDown = true;
            CloseAllLoadedUIForms();
            // 取消所有在途加载：标记 Cancelled 后清空。后续到达的 success 回调凭 serial 找不到条目（已清空），
            // 且 m_ShuttingDown / helper 置空保证其为安全 no-op，不会 NRE，也不会把 asset 当成 form 打开。
            CancelAllLoadingRequests();
            foreach (var kv in m_UIGroups) kv.Value.Shutdown();
            m_UIGroups.Clear();
            m_FormsByAssetName.Clear();
        }

        public void ReleaseUnusedUIForms()
        {
            Framework.EnsureMainThread(nameof(ReleaseUnusedUIForms));
            m_InstancePool?.ReleaseAllUnused();
        }

        private void CancelAllLoadingRequests()
        {
            foreach (var kv in m_LoadingRequests) kv.Value.Cancelled = true;
            m_LoadingRequests.Clear();
        }

        // ===== Helper / 懒拉取的 Manager 依赖 =====
        public void SetUIFormHelper(IUIFormHelper helper)
        {
            Framework.EnsureMainThread(nameof(SetUIFormHelper));
            if (helper == null) throw new FrameworkException("UI form helper is invalid.");
            m_UIFormHelper = helper;
        }

        private IResourceManager Resource
        {
            get { return m_ResourceManager ?? (m_ResourceManager = Framework.GetModule<IResourceManager>()); }
        }

        private IObjectPool<UIFormInstanceObject> InstancePool
        {
            get
            {
                if (m_InstancePool != null) return m_InstancePool;
                var mgr = m_ObjectPoolManager ?? (m_ObjectPoolManager = Framework.GetModule<IObjectPoolManager>());
                // 复用同名池（多个 UIManager 共享同一 ObjectPoolManager 时不重复创建）
                foreach (var p in mgr.GetAllObjectPools())
                {
                    if (p.Name == UIFormPoolName && p is IObjectPool<UIFormInstanceObject> typed)
                    {
                        m_InstancePool = typed;
                        return m_InstancePool;
                    }
                }
                m_InstancePool = mgr.CreateSingleSpawnObjectPool<UIFormInstanceObject>(
                    UIFormPoolName,
                    DefaultInstanceAutoReleaseInterval,
                    DefaultInstanceCapacity,
                    DefaultInstanceExpireTime,
                    DefaultInstancePriority);
                return m_InstancePool;
            }
        }

        // ===== UIGroup =====
        public bool HasUIGroup(string n) { return n != null && m_UIGroups.ContainsKey(n); }

        public IUIGroup GetUIGroup(string n)
        {
            UIGroup g;
            return n != null && m_UIGroups.TryGetValue(n, out g) ? (IUIGroup)g : null;
        }

        public IUIGroup[] GetAllUIGroups()
        {
            var arr = new IUIGroup[m_UIGroups.Count];
            int i = 0;
            foreach (var kv in m_UIGroups) arr[i++] = kv.Value;
            return arr;
        }

        public bool AddUIGroup(string name, int depth, IUIGroupHelper helper)
        {
            Framework.EnsureMainThread(nameof(AddUIGroup));
            if (string.IsNullOrEmpty(name)) throw new FrameworkException("UI group name is invalid.");
            if (helper == null) throw new FrameworkException("UI group helper is invalid.");
            if (m_UIGroups.ContainsKey(name)) return false;
            m_UIGroups.Add(name, new UIGroup(name, depth, helper));
            return true;
        }

        // ===== UIForm 查询 =====
        public bool HasUIForm(int serialId) { return m_UIFormsBySerial.ContainsKey(serialId); }

        public bool HasUIForm(string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return false;
            List<UIFormState> list;
            return m_FormsByAssetName.TryGetValue(assetName, out list) && list.Count > 0;
        }

        public IUIForm GetUIForm(int serialId)
        {
            UIFormState s;
            return m_UIFormsBySerial.TryGetValue(serialId, out s) ? s.Form : null;
        }

        public IUIForm GetUIForm(string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return null;
            List<UIFormState> list;
            return m_FormsByAssetName.TryGetValue(assetName, out list) && list.Count > 0 ? list[0].Form : null;
        }

        // ===== assetName → form 索引维护（每个 add/remove 路径都必须调用，保持一致）=====
        private void IndexAdd(UIFormState state)
        {
            List<UIFormState> list;
            if (!m_FormsByAssetName.TryGetValue(state.AssetName, out list))
            {
                list = new List<UIFormState>(1);
                m_FormsByAssetName.Add(state.AssetName, list);
            }
            list.Add(state);
        }

        private void IndexRemove(UIFormState state)
        {
            List<UIFormState> list;
            if (m_FormsByAssetName.TryGetValue(state.AssetName, out list))
            {
                list.Remove(state);
                if (list.Count == 0) m_FormsByAssetName.Remove(state.AssetName);
            }
        }

        public IUIForm[] GetAllLoadedUIForms()
        {
            var arr = new IUIForm[m_UIFormsBySerial.Count];
            int i = 0;
            foreach (var kv in m_UIFormsBySerial) arr[i++] = kv.Value.Form;
            return arr;
        }

        // ===== 打开 =====
        public int PeekNextOpenUIFormSerial() { return m_Serial + 1; }

        public int OpenUIForm(string assetName, string groupName, int priority, bool pauseCoveredUIForm, object userData)
        {
            return OpenUIForm(assetName, groupName, priority, pauseCoveredUIForm, /*reusable*/ true, userData);
        }

        /// <summary>
        /// 打开界面（带 Reusable 控制）。Reusable==false 时关闭即释放实例，不回实例池。
        /// 非接口方法：UIController 凭具体类型调用以贯通 UIFormDef.Reusable。
        /// </summary>
        public int OpenUIForm(string assetName, string groupName, int priority, bool pauseCoveredUIForm, bool reusable, object userData)
        {
            Framework.EnsureMainThread(nameof(OpenUIForm));
            if (m_UIFormHelper == null) throw new FrameworkException("UI form helper is not set.");
            if (string.IsNullOrEmpty(assetName)) throw new FrameworkException("UI form asset name is invalid.");
            if (string.IsNullOrEmpty(groupName)) throw new FrameworkException("UI group name is invalid.");

            UIGroup group;
            if (!m_UIGroups.TryGetValue(groupName, out group))
            {
                throw new FrameworkException(Utility.Text.Format("UI group '{0}' is not exist.", groupName));
            }

            int serialId = ++m_Serial;
            float startTime = NowSeconds();

            // 实例池命中
            UIFormInstanceObject instanceObj = InstancePool.Spawn(assetName);
            if (instanceObj != null)
            {
                object pooledInstance = instanceObj.Target;
                bool ok = InternalOpenUIForm(serialId, assetName, group, pooledInstance, /*isNewInstance*/ false, /*duration*/ 0f, pauseCoveredUIForm, priority, reusable, userData);
                // 打开失败（CreateUIForm/OnInit 抛异常或返回 null）：把刚 Spawn 出来的实例归还实例池，
                // 否则它会以 in-use 状态滞留——auto-release 不回收 in-use 对象——misconfigured prefab 反复打开即泄漏。
                if (!ok) RecyclePooledInstance(pooledInstance, /*forceRelease*/ false);
                return serialId;
            }

            // 未命中：回调风格异步加载。把 serialId 作为 LoadAsset 的 userData 透传，
            // 回调凭 serial 在 m_LoadingRequests 中 O(1) 精确回溯本次请求（业务真实 userData 存于 PendingOpen）。
            m_LoadingRequests.Add(serialId, new PendingOpen
            {
                SerialId = serialId,
                AssetName = assetName,
                GroupName = groupName,
                Priority = priority,
                PauseCoveredUIForm = pauseCoveredUIForm,
                Reusable = reusable,
                UserData = userData,
                StartTime = startTime,
                Cancelled = false,
            });
            Resource.LoadAsset(assetName, null, priority, m_LoadAssetCallbacks, serialId);
            return serialId;
        }

        // ===== 关闭 =====
        public void CloseUIForm(int serialId) { CloseUIForm(serialId, null); }

        public void CloseUIForm(int serialId, object userData)
        {
            Framework.EnsureMainThread(nameof(CloseUIForm));
            // 加载中：O(1) 按 serial 命中在途请求，标记 Cancelled（保留条目）。
            // 晚到的 success 回调会发现 Cancelled，转而卸载 asset 并移除条目，不会把 form 打开，也不泄漏 prefab。
            PendingOpen pending;
            if (m_LoadingRequests.TryGetValue(serialId, out pending))
            {
                pending.Cancelled = true;
                FrameworkLog.Debug("Cancelled loading UI form serial={0}", serialId);
                return;
            }

            // 已加载：找不到时静默 + 警告，不抛异常。
            // 业务（如 Procedure.OnLeave）经常持有一个 serial 但实际 open 因 asset 加载失败而从未完成，
            // 关闭路径不该把不存在当致命错误 —— 否则任何 open 失败都会在 shutdown 时再炸一次（曾经的崩溃 case）。
            UIFormState state;
            if (!m_UIFormsBySerial.TryGetValue(serialId, out state))
            {
                FrameworkLog.Warning("CloseUIForm: no form with serial={0} (already closed or open failed). Ignored.", serialId);
                return;
            }

            UIGroup group = state.Group;
            IUIForm form = state.Form;

            try { form.OnClose(false, userData); }
            catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' OnClose threw: {1}", form.UIFormAssetName, ex); }

            group.RemoveUIForm(state);
            group.Refresh();

            m_UIFormsBySerial.Remove(serialId);
            IndexRemove(state);

            // 归还实例池
            if (state.Instance != null)
            {
                try { form.OnRecycle(); }
                catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' OnRecycle threw: {1}", form.UIFormAssetName, ex); }
                try { InstancePool.Unspawn(state.Instance); }
                catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' Unspawn threw: {1}", form.UIFormAssetName, ex); }
                // Reusable==false：关闭即释放，不在池中缓存。Unspawn 后实例已非 in-use，
                // ReleaseAllUnused 会立即销毁它（连同其它空闲实例），实现"一次性 form"语义。
                if (!state.Reusable)
                {
                    try { InstancePool.ReleaseAllUnused(); }
                    catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' ReleaseAllUnused threw: {1}", form.UIFormAssetName, ex); }
                }
            }

            var h = CloseUIFormComplete;
            if (h != null)
            {
                var args = CloseUIFormCompleteEventArgs.Create(serialId, form.UIFormAssetName, group, userData);
                try { h(this, args); }
                catch (Exception ex) { FrameworkLog.Error("CloseUIFormComplete threw: {0}", ex); }
                ReferencePool.Release(args);
            }
        }

        public void CloseAllLoadedUIForms()
        {
            Framework.EnsureMainThread(nameof(CloseAllLoadedUIForms));
            int[] keys = new int[m_UIFormsBySerial.Count];
            int i = 0;
            foreach (var kv in m_UIFormsBySerial) keys[i++] = kv.Key;
            for (int k = 0; k < keys.Length; k++)
            {
                try { CloseUIForm(keys[k]); }
                catch (Exception ex) { FrameworkLog.Error("CloseUIForm({0}): {1}", keys[k], ex); }
            }
        }

        public void CloseAllLoadingUIForms()
        {
            Framework.EnsureMainThread(nameof(CloseAllLoadingUIForms));
            CancelAllLoadingRequests();
        }

        public void RefocusUIForm(IUIForm uiForm)
        {
            RefocusUIForm(uiForm, null);
        }

        public void RefocusUIForm(IUIForm uiForm, object userData)
        {
            Framework.EnsureMainThread(nameof(RefocusUIForm));
            if (uiForm == null) throw new FrameworkException("UI form is invalid.");
            UIFormState state;
            if (!m_UIFormsBySerial.TryGetValue(uiForm.SerialId, out state))
            {
                throw new FrameworkException(Utility.Text.Format("Can not find UI form '{0}'.", uiForm.UIFormAssetName));
            }
            state.Group.RefocusUIForm(state);
            state.Group.Refresh();
            try { uiForm.OnRefocus(userData); }
            catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' OnRefocus threw: {1}", uiForm.UIFormAssetName, ex); }
        }

        // ===== 资源加载回调 =====
        // serial 通过 LoadAsset 的 userData 参数透传回来，凭它在 m_LoadingRequests 中 O(1) 精确回溯发起请求
        // （不再按 assetName 匹配），open→close→re-open 同一 asset 也不会错配，且不会留下孤立条目。

        private bool TryTakePending(object userDataSerial, string assetName, out PendingOpen pending)
        {
            pending = null;
            if (!(userDataSerial is int serialId))
            {
                FrameworkLog.Error("UI load callback for '{0}' carried no valid serial userData; discarded.", assetName);
                return false;
            }
            if (!m_LoadingRequests.TryGetValue(serialId, out pending)) return false;
            m_LoadingRequests.Remove(serialId);
            return true;
        }

        private void LoadAssetSuccessCallback(string assetName, object asset, float duration, object userDataSerial)
        {
            PendingOpen matched;
            bool found = TryTakePending(userDataSerial, assetName, out matched);

            // 已取消（CloseUIForm 标记 Cancelled）/ shutdown 后到达 / 找不到条目 / helper 已释放：
            // 一律丢弃并卸载 asset，避免 prefab 永驻内存。null-guard 保证 shutdown 后晚到回调为安全 no-op，不得 NRE。
            if (!found || matched.Cancelled || m_ShuttingDown || m_UIFormHelper == null)
            {
                FrameworkLog.Debug("Discarding loaded asset '{0}' (request cancelled or manager shut down).", assetName);
                if (asset != null && m_ResourceManager != null) m_ResourceManager.UnloadAsset(asset);
                return;
            }

            if (asset == null)
            {
                FireOpenFailure(matched.SerialId, assetName, matched.GroupName, "Loaded asset is null.", matched.UserData);
                return;
            }

            UIGroup group;
            if (!m_UIGroups.TryGetValue(matched.GroupName, out group))
            {
                FireOpenFailure(matched.SerialId, assetName, matched.GroupName, "UI group disappeared during loading.", matched.UserData);
                Resource.UnloadAsset(asset);
                return;
            }

            object instance;
            try { instance = m_UIFormHelper.InstantiateUIForm(asset); }
            catch (Exception ex)
            {
                FireOpenFailure(matched.SerialId, assetName, matched.GroupName, "InstantiateUIForm threw: " + ex.Message, matched.UserData);
                Resource.UnloadAsset(asset);
                return;
            }

            UIFormInstanceObject instanceObj = UIFormInstanceObject.Create(assetName, asset, instance, m_UIFormHelper, Resource);
            InstancePool.Register(instanceObj, /*spawned*/ true);

            float totalDuration = NowSeconds() - matched.StartTime;
            bool ok = InternalOpenUIForm(matched.SerialId, assetName, group, instance, /*isNewInstance*/ true, totalDuration, matched.PauseCoveredUIForm, matched.Priority, matched.Reusable, matched.UserData);
            // 打开失败：刚注册（spawned:true）的实例若不归还会以 in-use 状态滞留，且其包裹的 asset 永不卸载。
            // forceRelease==true → Unspawn 后立即 ReleaseAllUnused，触发 UIFormInstanceObject.Release（销毁实例 + UnloadAsset）。
            if (!ok) RecyclePooledInstance(instance, /*forceRelease*/ true);
        }

        private void LoadAssetFailureCallback(string assetName, LoadResourceStatus status, string errorMessage, object userDataSerial)
        {
            PendingOpen matched;
            if (!TryTakePending(userDataSerial, assetName, out matched)) return;
            // 已取消 / shutdown：静默丢弃，不再 fire 失败事件（业务已不关心）。
            if (matched.Cancelled || m_ShuttingDown) return;
            FireOpenFailure(matched.SerialId, assetName, matched.GroupName, status + ": " + errorMessage, matched.UserData);
        }

        // ===== 内部打开（实例已就绪） =====
        // 返回 true 表示打开成功；返回 false 表示 CreateUIForm/OnInit 失败（已 fire 失败事件），
        // 调用方据此把已 Spawn/Register 的实例归还实例池，避免实例 + asset 泄漏。
        private bool InternalOpenUIForm(int serialId, string assetName, UIGroup group, object instance, bool isNewInstance, float duration, bool pauseCoveredUIForm, int priority, bool reusable, object userData)
        {
            IUIForm form;
            try { form = m_UIFormHelper.CreateUIForm(instance, group, userData); }
            catch (Exception ex)
            {
                FireOpenFailure(serialId, assetName, group.Name, "CreateUIForm threw: " + ex.Message, userData);
                return false;
            }
            if (form == null)
            {
                FireOpenFailure(serialId, assetName, group.Name, "CreateUIForm returned null.", userData);
                return false;
            }

            try { form.OnInit(serialId, assetName, group, pauseCoveredUIForm, isNewInstance, userData); }
            catch (Exception ex)
            {
                FireOpenFailure(serialId, assetName, group.Name, "OnInit threw: " + ex.Message, userData);
                return false;
            }

            UIFormState state = new UIFormState
            {
                Form = form,
                Group = group,
                Priority = priority,
                AssetName = assetName,
                Instance = instance,
                Reusable = reusable,
            };
            m_UIFormsBySerial.Add(serialId, state);
            IndexAdd(state);
            group.AddUIForm(state);
            group.Refresh();

            try { form.OnOpen(userData); }
            catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' OnOpen threw: {1}", assetName, ex); }

            var h = OpenUIFormSuccess;
            if (h != null)
            {
                var args = OpenUIFormSuccessEventArgs.Create(form, duration, userData);
                try { h(this, args); }
                catch (Exception ex) { FrameworkLog.Error("OpenUIFormSuccess threw: {0}", ex); }
                ReferencePool.Release(args);
            }
            return true;
        }

        // 打开失败后统一回收已 Spawn/Register 的实例池实例。
        //   forceRelease==false（实例池命中路径）：仅 Unspawn 归还，实例回到可回收状态，后续 auto-release 处理。
        //   forceRelease==true（新建实例路径）：Unspawn 后立即 ReleaseAllUnused，触发 UIFormInstanceObject.Release
        //                                       销毁实例并 UnloadAsset，避免本次新加载的 asset 永驻内存。
        private void RecyclePooledInstance(object instance, bool forceRelease)
        {
            if (instance == null) return;
            try { InstancePool.Unspawn(instance); }
            catch (Exception ex) { FrameworkLog.Error("OpenUIForm failure recycle: Unspawn threw: {0}", ex); }
            if (forceRelease)
            {
                try { InstancePool.ReleaseAllUnused(); }
                catch (Exception ex) { FrameworkLog.Error("OpenUIForm failure recycle: ReleaseAllUnused threw: {0}", ex); }
            }
        }

        private void FireOpenFailure(int serialId, string assetName, string groupName, string error, object userData)
        {
            FrameworkLog.Error("OpenUIForm failed: serial={0} asset='{1}' group='{2}' error={3}", serialId, assetName, groupName, error);
            var h = OpenUIFormFailure;
            if (h != null)
            {
                var args = OpenUIFormFailureEventArgs.Create(serialId, assetName, groupName, error, userData);
                try { h(this, args); }
                catch (Exception ex) { FrameworkLog.Error("OpenUIFormFailure threw: {0}", ex); }
                ReferencePool.Release(args);
            }
        }

        private static float NowSeconds()
        {
            // 单调时钟，避免系统校时跳变导致 duration 为负/巨跳（见 Utility.Timestamp）。
            return Utility.Timestamp.SecondsF;
        }

        // ===== 内部数据结构 =====
        internal sealed class UIFormState
        {
            public IUIForm Form;
            public UIGroup Group;
            public int Priority;
            public string AssetName;
            public object Instance;
            public bool Reusable;

            // UIGroup 内部维护的状态（只允许 UIGroup.Refresh 修改）
            internal long Seq;
            internal int LastDepthInGroup;
            internal bool FirstRefresh = true;
            internal bool WasCovered;
            internal bool WasPaused;
        }

        // 在途 open 请求快照，按 serial 索引。Cancelled 标记由 CloseUIForm / Shutdown 置位，
        // 使晚到的 load 回调把已加载 asset 卸载而非误打开 form。
        private sealed class PendingOpen
        {
            public int SerialId;
            public string AssetName;
            public string GroupName;
            public int Priority;
            public bool PauseCoveredUIForm;
            public bool Reusable;
            public object UserData;
            public float StartTime;
            public bool Cancelled;
        }

        // ===== UIGroup（完整实现） =====
        internal sealed class UIGroup : IUIGroup
        {
            private readonly string m_Name;
            private int m_Depth;
            private bool m_Pause;
            private readonly IUIGroupHelper m_Helper;
            // 顶层在 First；按 (Priority desc, Seq desc) 排序。
            private readonly LinkedList<UIFormState> m_Forms = new LinkedList<UIFormState>();
            private long m_AddedSeq = 0;

            public UIGroup(string name, int depth, IUIGroupHelper helper)
            {
                m_Name = name;
                m_Depth = depth;
                m_Helper = helper;
            }

            public string Name { get { return m_Name; } }

            public int Depth
            {
                get { return m_Depth; }
                set { m_Depth = value; Refresh(); }
            }

            public bool Pause
            {
                get { return m_Pause; }
                set { if (m_Pause != value) { m_Pause = value; Refresh(); } }
            }

            public int UIFormCount { get { return m_Forms.Count; } }

            public IUIForm CurrentUIForm
            {
                get { return m_Forms.First != null ? m_Forms.First.Value.Form : null; }
            }

            public IUIGroupHelper Helper { get { return m_Helper; } }

            internal void AddUIForm(UIFormState state)
            {
                state.Seq = ++m_AddedSeq;

                var node = m_Forms.First;
                while (node != null)
                {
                    if (state.Priority > node.Value.Priority) break;
                    if (state.Priority == node.Value.Priority && state.Seq > node.Value.Seq) break;
                    node = node.Next;
                }
                if (node != null) m_Forms.AddBefore(node, state);
                else m_Forms.AddLast(state);

                // Reparenting is a group invariant: as soon as a form belongs to this group,
                // the helper-owned container holds it. No engine-side post-hook, no name lookup.
                if (m_Helper != null && state.Form != null)
                {
                    try { m_Helper.AttachUIForm(state.Form); }
                    catch (Exception ex)
                    {
                        FrameworkLog.Error("UIGroupHelper.AttachUIForm threw for group '{0}' form '{1}': {2}",
                            m_Name, state.AssetName, ex);
                    }
                }
            }

            internal void RemoveUIForm(UIFormState state)
            {
                m_Forms.Remove(state);
            }

            internal void RefocusUIForm(UIFormState state)
            {
                m_Forms.Remove(state);
                state.Seq = ++m_AddedSeq;
                AddUIForm(state);
            }

            internal void Refresh()
            {
                int depthInGroup = 0;
                bool topReached = false;
                bool pauseRest = m_Pause;

                for (var node = m_Forms.First; node != null; node = node.Next)
                {
                    var s = node.Value;
                    var f = s.Form;

                    int newDepth = depthInGroup;
                    if (s.LastDepthInGroup != newDepth || s.FirstRefresh)
                    {
                        try { f.OnDepthChanged(m_Depth, newDepth); }
                        catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' OnDepthChanged threw: {1}", f.UIFormAssetName, ex); }
                        s.LastDepthInGroup = newDepth;
                    }

                    if (!topReached)
                    {
                        // 顶层：恢复
                        if (s.WasCovered)
                        {
                            try { f.OnReveal(); } catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' OnReveal: {1}", f.UIFormAssetName, ex); }
                            s.WasCovered = false;
                        }
                        if (s.WasPaused)
                        {
                            try { f.OnResume(); } catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' OnResume: {1}", f.UIFormAssetName, ex); }
                            s.WasPaused = false;
                        }
                        if (f.PauseCoveredUIForm) pauseRest = true;
                        topReached = true;
                    }
                    else
                    {
                        if (pauseRest)
                        {
                            if (!s.WasPaused)
                            {
                                try { f.OnPause(); } catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' OnPause: {1}", f.UIFormAssetName, ex); }
                                s.WasPaused = true;
                            }
                        }
                        else
                        {
                            if (!s.WasCovered)
                            {
                                try { f.OnCover(); } catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' OnCover: {1}", f.UIFormAssetName, ex); }
                                s.WasCovered = true;
                            }
                        }
                    }

                    s.FirstRefresh = false;
                    depthInGroup++;
                }
            }

            internal void Update(float elapseSeconds, float realElapseSeconds, ref UIFormState[] snapshotBuffer)
            {
                if (m_Pause) return;
                // 拷贝快照避免 OnUpdate 中调用 CloseUIForm 改链表。
                // 复用 manager 持有的 grow-only 缓冲，避免每帧每组分配；用完清空引用以免悬挂。
                int n = m_Forms.Count;
                if (n == 0) return;
                if (snapshotBuffer.Length < n)
                {
                    int newSize = snapshotBuffer.Length;
                    while (newSize < n) newSize *= 2;
                    snapshotBuffer = new UIFormState[newSize];
                }
                int i = 0;
                for (var node = m_Forms.First; node != null; node = node.Next) snapshotBuffer[i++] = node.Value;
                try
                {
                    for (int j = 0; j < n; j++)
                    {
                        if (snapshotBuffer[j].WasPaused) continue;
                        try { snapshotBuffer[j].Form.OnUpdate(elapseSeconds, realElapseSeconds); }
                        catch (Exception ex) { FrameworkLog.Error("UIForm '{0}' OnUpdate threw: {1}", snapshotBuffer[j].Form.UIFormAssetName, ex); }
                    }
                }
                finally
                {
                    for (int j = 0; j < n; j++) snapshotBuffer[j] = null;
                }
            }

            internal void Shutdown()
            {
                m_Forms.Clear();
            }
        }
    }
}
