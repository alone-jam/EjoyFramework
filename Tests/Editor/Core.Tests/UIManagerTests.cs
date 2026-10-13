//------------------------------------------------------------
// EjoyGame Framework Tests
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using NUnit.Framework;
using EjoyFramework.Core.ObjectPool;
using EjoyFramework.Core.UI;
using EjoyFramework.Tests.TestSupport;

using EjoyFramework.Core;
namespace EjoyFramework.Tests
{
    public class UIManagerTests
    {
        // ===== Test doubles =====
        private class FakeInstance { public string Name; }

        private class FakeUIForm : IUIForm
        {
            public int SerialId { get; private set; }
            public string UIFormAssetName { get; private set; }
            public object Handle { get; private set; }
            public IUIGroup UIGroup { get; private set; }
            public int DepthInUIGroup { get; private set; }
            public bool PauseCoveredUIForm { get; private set; }

            public bool InitCalled, OpenCalled, CloseCalled, RecycleCalled;
            public bool PauseCalled, ResumeCalled, CoverCalled, RevealCalled, RefocusCalled;
            public int UpdateCalls;
            public int LastDepth;

            public void OnInit(int serialId, string uiFormAssetName, IUIGroup uiGroup, bool pauseCoveredUIForm, bool isNewInstance, object userData)
            {
                SerialId = serialId; UIFormAssetName = uiFormAssetName;
                UIGroup = uiGroup; PauseCoveredUIForm = pauseCoveredUIForm;
                Handle = new FakeInstance { Name = uiFormAssetName };
                InitCalled = true;
            }
            public void OnRecycle() { RecycleCalled = true; }
            public void OnOpen(object userData) { OpenCalled = true; }
            public void OnClose(bool isShutdown, object userData) { CloseCalled = true; }
            public void OnPause() { PauseCalled = true; }
            public void OnResume() { ResumeCalled = true; }
            public void OnCover() { CoverCalled = true; }
            public void OnReveal() { RevealCalled = true; }
            public void OnRefocus(object userData) { RefocusCalled = true; }
            public void OnUpdate(float a, float b) { UpdateCalls++; }
            public void OnDepthChanged(int gd, int dig) { LastDepth = dig; DepthInUIGroup = dig; }
        }

        private class FakeUIFormHelper : IUIFormHelper
        {
            public int InstantiateCount, ReleaseCount;
            public Func<object, FakeUIForm> CreateFn = _ => new FakeUIForm();
            public object InstantiateUIForm(object asset) { InstantiateCount++; return new FakeInstance { Name = (asset as MockAsset)?.Name }; }
            public IUIForm CreateUIForm(object instance, IUIGroup group, object userData) { return CreateFn(instance); }
            public void ReleaseUIForm(object asset, object instance) { ReleaseCount++; }
        }

        private class FakeUIGroupHelper : IUIGroupHelper
        {
            // Test fixture: no container side effects. Real Unity helper reparents Handle.
            public void AttachUIForm(IUIForm form) { }
        }

        // ===== Setup =====
        private UIManager m_UI;
        private FakeUIFormHelper m_FormHelper;
        private MockResourceManager m_Res;
        private ObjectPoolManager m_ObjectPool;

        [SetUp]
        public void Setup()
        {
            Framework.MarkMainThread();
            m_FormHelper = new FakeUIFormHelper();
            m_Res = new MockResourceManager { ThrowOnHandleApi = true };
            m_ObjectPool = new ObjectPoolManager();

            m_UI = new UIManager(m_Res, m_ObjectPool);
            m_UI.SetUIFormHelper(m_FormHelper);
            m_UI.AddUIGroup("Default", 0, new FakeUIGroupHelper());
        }

        [TearDown]
        public void Teardown()
        {
            m_UI.Shutdown();
            m_ObjectPool.Shutdown();
        }

        // UIManager 以具名池（UIFormPoolName）创建实例池，因此按类型从所有池中检索，
        // 而非用无名 key 的 GetObjectPool<T>()（那会返回 null）。
        private IObjectPool<UIFormInstanceObject> GetInstancePool()
        {
            foreach (var p in m_ObjectPool.GetAllObjectPools())
            {
                if (p is IObjectPool<UIFormInstanceObject> typed) return typed;
            }
            return null;
        }

        [Test] public void ConcurrentSameAssetDoesNotBorrowActiveInstance()
        {
            int first=m_UI.OpenUIForm("UI/Same","Default",0,false,null);
            int second=m_UI.OpenUIForm("UI/Same","Default",0,false,null);
            Assert.That(m_FormHelper.InstantiateCount,Is.EqualTo(2));
            m_UI.CloseUIForm(first);m_UI.ReleaseUnusedUIForms();
            Assert.That(m_FormHelper.ReleaseCount,Is.EqualTo(1));Assert.That(m_UI.HasUIForm(second),Is.True);
            m_UI.CloseUIForm(second);m_UI.ReleaseUnusedUIForms();
            Assert.That(m_FormHelper.ReleaseCount,Is.EqualTo(2));Assert.That(GetInstancePool().Count,Is.Zero);
        }
        // ===== Tests =====

        [Test]
        public void OpenUIForm_FullLifecycle_FiresInit_Open_DepthChanged()
        {
            FakeUIForm capturedForm = null;
            m_UI.OpenUIFormSuccess += (s, e) => capturedForm = (FakeUIForm)e.UIForm;

            int serial = m_UI.OpenUIForm("UI/Main", "Default", 0, false, null);

            Assert.Greater(serial, 0);
            Assert.NotNull(capturedForm);
            Assert.IsTrue(capturedForm.InitCalled);
            Assert.IsTrue(capturedForm.OpenCalled);
            Assert.AreEqual(0, capturedForm.LastDepth, "First form should be at depth 0");
            Assert.AreSame(capturedForm, m_UI.GetUIForm(serial));
        }

        [Test]
        public void OpenUIForm_LoadFailure_FiresFailureEvent()
        {
            m_Res.SyncSucceed = false;
            string capturedError = null;
            m_UI.OpenUIFormFailure += (s, e) => capturedError = e.ErrorMessage;

            int serial = m_UI.OpenUIForm("UI/Bad", "Default", 0, false, null);

            Assert.Greater(serial, 0);
            Assert.IsNotNull(capturedError);
            StringAssert.Contains("fake load failed", capturedError);
            Assert.IsFalse(m_UI.HasUIForm(serial));
        }

        [Test]
        public void TwoForms_SecondOnTopWithCover_CoversFirst()
        {
            FakeUIForm a = null, b = null;
            int aOpens = 0;
            m_UI.OpenUIFormSuccess += (s, e) =>
            {
                if (a == null) { a = (FakeUIForm)e.UIForm; aOpens++; }
                else b = (FakeUIForm)e.UIForm;
            };

            m_UI.OpenUIForm("UI/A", "Default", 0, false, null);
            m_UI.OpenUIForm("UI/B", "Default", 0, false, null);

            // B 比 A 后入，priority 相同 → B 在顶
            Assert.AreSame(b, m_UI.GetUIGroup("Default").CurrentUIForm);
            Assert.IsTrue(a.CoverCalled, "A should be covered when B opens");
            Assert.AreEqual(0, b.LastDepth);
            Assert.AreEqual(1, a.LastDepth);
        }

        [Test]
        public void TwoForms_TopWithPauseCovered_PausesInsteadOfCovers()
        {
            FakeUIForm a = null, b = null;
            int idx = 0;
            m_UI.OpenUIFormSuccess += (s, e) =>
            {
                if (idx++ == 0) a = (FakeUIForm)e.UIForm;
                else b = (FakeUIForm)e.UIForm;
            };

            m_UI.OpenUIForm("UI/A", "Default", 0, false, null);
            m_UI.OpenUIForm("UI/B", "Default", 0, /*pauseCovered*/ true, null);

            Assert.IsTrue(a.PauseCalled, "A should be paused (not covered) when B has PauseCoveredUIForm=true");
            Assert.IsFalse(a.CoverCalled);
        }

        [Test]
        public void CloseUIForm_TriggersOnCloseAndRecycle_ReleasesInstance()
        {
            FakeUIForm form = null;
            m_UI.OpenUIFormSuccess += (s, e) => form = (FakeUIForm)e.UIForm;

            int serial = m_UI.OpenUIForm("UI/X", "Default", 0, false, null);
            m_UI.CloseUIForm(serial);

            Assert.IsTrue(form.CloseCalled);
            Assert.IsTrue(form.RecycleCalled);
            Assert.IsFalse(m_UI.HasUIForm(serial));
        }

        [Test]
        public void ReopenSameAsset_ReusesPooledInstance_NoExtraInstantiate()
        {
            int s1 = m_UI.OpenUIForm("UI/Pool", "Default", 0, false, null);
            m_UI.CloseUIForm(s1);

            int instantiateCountAfterClose = m_FormHelper.InstantiateCount;

            // 让 ObjectPool Update 一次以推进自动释放计时（默认 60s 间隔，立即不会触发释放）
            m_ObjectPool.Update(0.016f, 0.016f);

            int s2 = m_UI.OpenUIForm("UI/Pool", "Default", 0, false, null);

            Assert.AreEqual(instantiateCountAfterClose, m_FormHelper.InstantiateCount,
                "Reopening same asset should reuse pooled instance, not instantiate again");
            Assert.IsTrue(m_UI.HasUIForm(s2));
        }

        [Test]
        public void Update_CallsOnUpdateOnAllForms_RespectingPause()
        {
            FakeUIForm a = null, b = null;
            int idx = 0;
            m_UI.OpenUIFormSuccess += (s, e) =>
            {
                if (idx++ == 0) a = (FakeUIForm)e.UIForm;
                else b = (FakeUIForm)e.UIForm;
            };
            m_UI.OpenUIForm("UI/A", "Default", 0, false, null);
            m_UI.OpenUIForm("UI/B", "Default", 0, /*pauseCovered*/ true, null);

            m_UI.Update(0.016f, 0.016f);

            Assert.AreEqual(1, b.UpdateCalls, "B (top) should update");
            Assert.AreEqual(0, a.UpdateCalls, "A should not update while paused");
        }

        [Test]
        public void RefocusUIForm_BringsToTop_AndFiresRefocus()
        {
            FakeUIForm a = null, b = null;
            int idx = 0;
            m_UI.OpenUIFormSuccess += (s, e) => { if (idx++ == 0) a = (FakeUIForm)e.UIForm; else b = (FakeUIForm)e.UIForm; };

            m_UI.OpenUIForm("UI/A", "Default", 0, false, null);
            m_UI.OpenUIForm("UI/B", "Default", 0, false, null);

            // 此时 b 在顶
            Assert.AreSame(b, m_UI.GetUIGroup("Default").CurrentUIForm);

            m_UI.RefocusUIForm(a);
            Assert.AreSame(a, m_UI.GetUIGroup("Default").CurrentUIForm);
            Assert.IsTrue(a.RefocusCalled);
        }

        [Test]
        public void OpenUIForm_CreateUIFormThrows_NewInstance_RecyclesInstanceAndUnloadsAsset()
        {
            // 新建实例路径（实例池未命中）：CreateUIForm 抛异常应回收实例 + 卸载 asset，绝不泄漏。
            m_FormHelper.CreateFn = _ => throw new InvalidOperationException("boom");
            string capturedError = null;
            m_UI.OpenUIFormFailure += (s, e) => capturedError = e.ErrorMessage;

            int serial = m_UI.OpenUIForm("UI/Broken", "Default", 0, false, null);

            Assert.IsNotNull(capturedError, "Failure event should fire when CreateUIForm throws");
            Assert.IsFalse(m_UI.HasUIForm(serial), "No form should be registered on failure");

            // 关键：实例池里不能残留 in-use 的泄漏实例；新建路径应强制释放（Release → ReleaseUIForm + UnloadAsset）。
            var pool = GetInstancePool();
            Assert.AreEqual(0, pool.Count, "Failed new-instance open must not leave a leaked instance in the pool");
            Assert.GreaterOrEqual(m_FormHelper.ReleaseCount, 1, "ReleaseUIForm should run on the recycled instance");
            Assert.GreaterOrEqual(m_Res.UnloadCalls, 1, "Loaded asset must be unloaded on failure (no asset leak)");
        }

        [Test]
        public void OpenUIForm_CreateUIFormReturnsNull_NewInstance_RecyclesInstanceAndUnloadsAsset()
        {
            // 同上，但走 CreateUIForm 返回 null 的失败分支。
            m_FormHelper.CreateFn = _ => null;
            string capturedError = null;
            m_UI.OpenUIFormFailure += (s, e) => capturedError = e.ErrorMessage;

            int serial = m_UI.OpenUIForm("UI/Null", "Default", 0, false, null);

            Assert.IsNotNull(capturedError);
            Assert.IsFalse(m_UI.HasUIForm(serial));

            var pool = GetInstancePool();
            Assert.AreEqual(0, pool.Count, "Failed new-instance open (null form) must not leak an instance");
            Assert.GreaterOrEqual(m_Res.UnloadCalls, 1, "Loaded asset must be unloaded when CreateUIForm returns null");
        }

        [Test]
        public void OpenUIForm_CreateUIFormThrows_PooledInstance_ReturnsInstanceToPool_NotLeftSpawned()
        {
            // 先成功 open+close 一次，让实例进入实例池（Count==1、非 in-use）。
            int s1 = m_UI.OpenUIForm("UI/Pool", "Default", 0, false, null);
            m_UI.CloseUIForm(s1);

            var pool = GetInstancePool();
            Assert.AreEqual(1, pool.Count, "Pooled instance should be cached after close");
            Assert.AreEqual(1, pool.CanReleaseCount, "Cached instance should be releasable (not in-use) before reopen");

            // 第二次 open 命中实例池（Spawn），但本次 CreateUIForm 抛异常。
            m_FormHelper.CreateFn = _ => throw new InvalidOperationException("boom");
            string capturedError = null;
            m_UI.OpenUIFormFailure += (s, e) => capturedError = e.ErrorMessage;

            int s2 = m_UI.OpenUIForm("UI/Pool", "Default", 0, false, null);

            Assert.IsNotNull(capturedError);
            Assert.IsFalse(m_UI.HasUIForm(s2));
            // 关键：Spawn 出来的实例必须 Unspawn 归还——不能以 in-use 状态滞留。
            Assert.AreEqual(1, pool.Count, "Pool-hit failure should keep the instance cached, not destroy or duplicate it");
            Assert.AreEqual(1, pool.CanReleaseCount, "Spawned instance must be returned to the pool (not left spawned) on failure");
        }

        [Test]
        public void CancelLoading_BeforeAssetReady_DiscardsRequest()
        {
            // 异步模式：LoadAsset 不立即触发回调，等手动 Flush
            var asyncRes = new MockResourceManager { Async = true, ThrowOnHandleApi = true };
            var ui = new UIManager(asyncRes, m_ObjectPool);
            ui.SetUIFormHelper(m_FormHelper);
            ui.AddUIGroup("Default", 0, new FakeUIGroupHelper());

            int serial = ui.OpenUIForm("UI/Slow", "Default", 0, false, null);

            // 还没回调资源，立即关闭
            ui.CloseUIForm(serial);

            // 现在让资源回调到达
            asyncRes.FlushAllPending();

            // 应该不会触发 OpenUIFormSuccess（被丢弃）
            Assert.IsFalse(ui.HasUIForm(serial));
            ui.Shutdown();
        }
    }
}
