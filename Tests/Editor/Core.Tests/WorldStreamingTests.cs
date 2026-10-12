//------------------------------------------------------------
// EjoyGame Framework Tests
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using EjoyFramework.Core;
using EjoyFramework.Core.Streaming;

namespace EjoyFramework.Tests
{
    /// <summary>
    /// WS3-M2：世界分区流送——期望集差分、最近优先、预算、滞回、取消、迟到结果、LOD、多观察者、浮动原点、同步兜底、失败重试、零分配。
    /// </summary>
    public sealed class WorldStreamingTests
    {
        private WorldStreamingManager m_Mgr;
        private RecordingHandler m_Handler;

        [SetUp]
        public void SetUp()
        {
            Framework.MarkMainThread();
            m_Mgr = new WorldStreamingManager();
            m_Handler = new RecordingHandler(m_Mgr);
            m_Mgr.SetHandler(m_Handler);
            m_Mgr.CellSize = 10f;
            m_Mgr.ConfigureLayer(0, new StreamingLayerSettings { LoadRadius = 25f, UnloadRadius = 35f });
            m_Mgr.MaxLoadStartsPerFrame = 100;
            m_Mgr.MaxLoadsInFlight = 100;
            m_Mgr.MaxUnloadsPerFrame = 100;
        }

        [TearDown]
        public void TearDown()
        {
            m_Mgr.Shutdown();
        }

        /// <summary>注册 (-n..n)² 的单层网格，内容键 = 序号。</summary>
        private void RegisterGrid(int n, int layer = 0)
        {
            int key = 0;
            for (int cx = -n; cx <= n; cx++)
                for (int cz = -n; cz <= n; cz++)
                    m_Mgr.RegisterCell(layer, cx, cz, key++);
        }

        private void Tick(int frames = 1)
        {
            for (int i = 0; i < frames; i++) m_Mgr.Update(0.016f, 0.016f);
        }

        // ================================================================
        //  基本差分
        // ================================================================

        [Test]
        public void ObserverInRadius_LoadsNearestCellsFirst_WithinRadiusOnly()
        {
            RegisterGrid(5);
            m_Handler.AutoComplete = true;
            m_Mgr.SetObserver(1, 5f, 5f);   // cell (0,0) 中心
            Tick();

            Assert.Greater(m_Mgr.LoadedCellCount, 0);
            // 半径 25 内（到 cell 中心）的全部单元都应加载，之外的不加载
            for (int cx = -5; cx <= 5; cx++)
                for (int cz = -5; cz <= 5; cz++)
                {
                    int id = m_Mgr.FindCell(0, cx, cz);
                    float dx = (cx + 0.5f) * 10f - 5f, dz = (cz + 0.5f) * 10f - 5f;
                    bool inside = dx * dx + dz * dz <= 25f * 25f;
                    Assert.AreEqual(inside ? StreamingCellState.Loaded : StreamingCellState.Unloaded, m_Mgr.GetCellState(id), "cell " + cx + "," + cz);
                }

            // 派发顺序按距离升序（首个必是观察者所在 cell）
            Assert.AreEqual(m_Mgr.FindCell(0, 0, 0), m_Handler.LoadOrder[0]);
            for (int i = 1; i < m_Handler.LoadOrder.Count; i++)
            {
                Assert.LessOrEqual(Dist(m_Handler.LoadOrder[i - 1]), Dist(m_Handler.LoadOrder[i]) + 1e-3f, "加载顺序必须按距离升序。");
            }
        }

        private float Dist(int cellId)
        {
            StreamingCellInfo info;
            m_Mgr.TryGetCell(cellId, out info);
            float dx = (info.Cx + 0.5f) * 10f - 5f, dz = (info.Cz + 0.5f) * 10f - 5f;
            return dx * dx + dz * dz;
        }

        [Test]
        public void Hysteresis_NoUnloadBetweenRadii_UnloadBeyond()
        {
            RegisterGrid(0);   // 只有 cell (0,0)，中心 (5,5)
            m_Handler.AutoComplete = true;
            int id = m_Mgr.FindCell(0, 0, 0);
            m_Mgr.SetObserver(1, 5f, 5f);
            Tick();
            Assert.AreEqual(StreamingCellState.Loaded, m_Mgr.GetCellState(id));

            m_Mgr.SetObserver(1, 5f + 30f, 5f);   // 30 > LoadRadius 25，< UnloadRadius 35
            Tick();
            Assert.AreEqual(StreamingCellState.Loaded, m_Mgr.GetCellState(id), "滞回区内不得卸载。");

            m_Mgr.SetObserver(1, 5f + 40f, 5f);   // > 35
            Tick();
            Assert.AreEqual(StreamingCellState.Unloaded, m_Mgr.GetCellState(id));
            Assert.AreEqual(1, m_Handler.Unloads.Count);
        }

        [Test]
        public void LeavingWhileLoading_Cancels_AndLateSuccessIsUnloaded()
        {
            RegisterGrid(0);
            int id = m_Mgr.FindCell(0, 0, 0);
            m_Mgr.SetObserver(1, 5f, 5f);
            Tick();
            Assert.AreEqual(StreamingCellState.Loading, m_Mgr.GetCellState(id));

            m_Mgr.SetObserver(1, 500f, 500f);
            Tick();
            Assert.AreEqual(StreamingCellState.Cancelling, m_Mgr.GetCellState(id));
            Assert.AreEqual(1, m_Handler.Cancels.Count);
            Assert.AreEqual(1, m_Mgr.TotalLoadsCancelled);

            m_Mgr.NotifyLoaded(id, true);   // 迟到的成功结果
            Assert.AreEqual(1, m_Handler.Unloads.Count, "取消后到达的结果必须被卸载。");
            m_Mgr.NotifyUnloaded(id);
            Assert.AreEqual(StreamingCellState.Unloaded, m_Mgr.GetCellState(id));
            Assert.AreEqual(0, m_Mgr.LoadedCellCount);
            Assert.AreEqual(0, m_Mgr.LoadingCellCount);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CancelledLoadFailure_RetriesOnlyWhenObserverReturned(bool returnBeforeCompletion)
        {
            RegisterGrid(0);
            int id = m_Mgr.FindCell(0, 0, 0);
            m_Mgr.SetObserver(1, 5f, 5f);
            Tick();
            m_Mgr.SetObserver(1, 500f, 500f);
            Tick();
            Assert.AreEqual(StreamingCellState.Cancelling, m_Mgr.GetCellState(id));

            if (returnBeforeCompletion) m_Mgr.SetObserver(1, 5f, 5f);
            Tick(); // 在取消完成前先消耗观察者移动引起的评估；之后不再移动。
            Assert.AreEqual(StreamingCellState.Cancelling, m_Mgr.GetCellState(id));
            m_Mgr.NotifyLoaded(id, false);
            Assert.AreEqual(StreamingCellState.Unloaded, m_Mgr.GetCellState(id));
            Assert.AreEqual(0, m_Mgr.TotalLoadFailures, "取消完成不应记为加载错误");

            Tick();
            Assert.AreEqual(returnBeforeCompletion ? StreamingCellState.Loading : StreamingCellState.Unloaded,
                m_Mgr.GetCellState(id));
            Assert.AreEqual(returnBeforeCompletion ? 2 : 1, m_Mgr.TotalLoadsStarted);
        }

        [Test]
        public void LoadFailure_ReturnsToUnloaded_AndRetriesOnNextEvaluation()
        {
            RegisterGrid(0);
            int id = m_Mgr.FindCell(0, 0, 0);
            m_Mgr.SetObserver(1, 5f, 5f);
            Tick();
            m_Mgr.NotifyLoaded(id, false);
            Assert.AreEqual(StreamingCellState.Unloaded, m_Mgr.GetCellState(id));
            Assert.AreEqual(1, m_Mgr.TotalLoadFailures);

            Tick();   // 失败置脏 → 重新评估 → 重试
            Assert.AreEqual(StreamingCellState.Loading, m_Mgr.GetCellState(id));
            Assert.AreEqual(2, m_Mgr.TotalLoadsStarted);
        }

        // ================================================================
        //  预算
        // ================================================================

        [Test]
        public void Budgets_LimitStartsPerFrame_AndInFlight()
        {
            RegisterGrid(5);
            m_Mgr.MaxLoadStartsPerFrame = 2;
            m_Mgr.MaxLoadsInFlight = 3;
            m_Mgr.SetObserver(1, 5f, 5f);

            Tick();
            Assert.AreEqual(2, m_Mgr.LoadingCellCount);
            Tick();
            Assert.AreEqual(3, m_Mgr.LoadingCellCount, "在途上限 3。");
            Tick();
            Assert.AreEqual(3, m_Mgr.LoadingCellCount);
            Assert.Greater(m_Mgr.QueuedLoadCount, 0);

            m_Mgr.NotifyLoaded(m_Handler.LoadOrder[0], true);
            Tick();
            Assert.AreEqual(3, m_Mgr.LoadingCellCount, "完成一个后补派一个。");
            Assert.AreEqual(1, m_Mgr.LoadedCellCount);
        }

        [Test]
        public void UnloadBudget_SpreadsUnloadsAcrossFrames()
        {
            RegisterGrid(3);
            m_Handler.AutoComplete = true;
            m_Mgr.SetObserver(1, 5f, 5f);
            Tick();
            int loaded = m_Mgr.LoadedCellCount;
            Assert.Greater(loaded, 2);

            m_Mgr.MaxUnloadsPerFrame = 2;
            m_Mgr.SetObserver(1, 1000f, 1000f);
            Tick();
            Assert.AreEqual(loaded - 2, m_Mgr.LoadedCellCount, "每帧最多卸载 2 个。");
            Tick(loaded);
            Assert.AreEqual(0, m_Mgr.LoadedCellCount);
        }

        // ================================================================
        //  LOD / 多观察者 / 多层
        // ================================================================

        [Test]
        public void Lod_UpdatesWithDistance_AndNotifiesHandler()
        {
            m_Mgr.ConfigureLayer(0, new StreamingLayerSettings { LoadRadius = 50f, UnloadRadius = 60f, LodDistances = new[] { 10f, 30f } });
            RegisterGrid(0);
            m_Handler.AutoComplete = true;
            int id = m_Mgr.FindCell(0, 0, 0);
            m_Mgr.SetObserver(1, 5f, 5f);
            Tick();
            Assert.AreEqual(0, m_Mgr.GetCellLod(id));

            m_Mgr.SetObserver(1, 25f, 5f);   // 距中心 20 → LOD 1
            Tick();
            Assert.AreEqual(1, m_Mgr.GetCellLod(id));
            Assert.AreEqual(1, m_Handler.LodChanges.Count);

            m_Mgr.SetObserver(1, 45f, 5f);   // 40 → LOD 2
            Tick();
            Assert.AreEqual(2, m_Mgr.GetCellLod(id));
        }

        [Test]
        public void MultipleObservers_UnionOfDesiredSets()
        {
            RegisterGrid(10);
            m_Handler.AutoComplete = true;
            m_Mgr.SetObserver(1, -80f, -80f);
            m_Mgr.SetObserver(2, 80f, 80f);
            Tick();

            Assert.AreEqual(StreamingCellState.Loaded, m_Mgr.GetCellState(m_Mgr.FindCell(0, -8, -8)));
            Assert.AreEqual(StreamingCellState.Loaded, m_Mgr.GetCellState(m_Mgr.FindCell(0, 8, 8)));
            Assert.AreEqual(StreamingCellState.Unloaded, m_Mgr.GetCellState(m_Mgr.FindCell(0, 0, 0)), "两个观察者都够不到的中间单元不应加载。");

            m_Mgr.RemoveObserver(2);
            Tick();
            Assert.AreEqual(StreamingCellState.Unloaded, m_Mgr.GetCellState(m_Mgr.FindCell(0, 8, 8)), "移除观察者后其独占的单元应卸载。");
        }

        [Test]
        public void Layers_HaveIndependentRadii_AndPriorityBias()
        {
            m_Mgr.ConfigureLayer(1, new StreamingLayerSettings { LoadRadius = 12f, UnloadRadius = 20f, PriorityBias = -100 });
            m_Mgr.RegisterCell(0, 0, 0, 1);
            m_Mgr.RegisterCell(1, 0, 0, 2);
            m_Mgr.RegisterCell(1, 2, 0, 3);   // 中心 (25,5)，距 20 > 层 1 半径 12
            m_Mgr.SetObserver(1, 5f, 5f);
            Tick();

            Assert.AreEqual(StreamingCellState.Loading, m_Mgr.GetCellState(m_Mgr.FindCell(1, 0, 0)));
            Assert.AreEqual(StreamingCellState.Unloaded, m_Mgr.GetCellState(m_Mgr.FindCell(1, 2, 0)));
            Assert.AreEqual(m_Mgr.FindCell(1, 0, 0), m_Handler.LoadOrder[0], "负偏置的层同距离下先加载。");
        }

        // ================================================================
        //  浮动原点 / 同步兜底 / 注销
        // ================================================================

        [Test]
        public void FloatingOrigin_ShiftKeepsWorldSemantics()
        {
            RegisterGrid(5);
            m_Handler.AutoComplete = true;
            m_Mgr.SetObserver(1, 45f, 5f);   // 世界 (45,5) → cell (4,0)
            Tick();
            int cell40 = m_Mgr.FindCell(0, 4, 0);
            Assert.AreEqual(StreamingCellState.Loaded, m_Mgr.GetCellState(cell40));
            int loadedBefore = m_Mgr.LoadedCellCount;

            // 原点平移 +40：本地 (5,5) 即世界 (45,5)，期望集不变
            m_Mgr.ShiftOrigin(40f, 0f);
            m_Mgr.SetObserver(1, 5f, 5f);
            Tick();
            float ox, oz;
            m_Mgr.GetOrigin(out ox, out oz);
            Assert.AreEqual(40f, ox);
            Assert.AreEqual(loadedBefore, m_Mgr.LoadedCellCount, "原点平移后世界位置不变，不应产生加载/卸载。");
            Assert.AreEqual(0, m_Handler.Unloads.Count);
            Assert.AreEqual(StreamingCellState.Loaded, m_Mgr.GetCellState(cell40));
        }

        [Test]
        public void RequireLoaded_BypassesBudget_AndPreemptsQueue()
        {
            RegisterGrid(5);
            m_Mgr.MaxLoadStartsPerFrame = 0;   // 正常路径不派发
            m_Mgr.SetObserver(1, 5f, 5f);
            Tick();
            Assert.AreEqual(0, m_Mgr.LoadingCellCount);
            Assert.Greater(m_Mgr.QueuedLoadCount, 0);

            int far = m_Mgr.FindCell(0, 2, 0);
            Assert.IsTrue(m_Mgr.RequireLoaded(far));
            Assert.AreEqual(StreamingCellState.Loading, m_Mgr.GetCellState(far));
            Assert.IsFalse(m_Mgr.RequireLoaded(far), "已在加载中不重复派发。");

            int outside = m_Mgr.FindCell(0, 5, 5);   // 期望集之外也可强制加载
            Assert.IsTrue(m_Mgr.RequireLoaded(outside));
            m_Mgr.NotifyLoaded(outside, true);
            Assert.AreEqual(StreamingCellState.Loaded, m_Mgr.GetCellState(outside));
        }

        [Test]
        public void UnregisterLoadedCell_Unloads_AndReleasesAfterNotify()
        {
            RegisterGrid(0);
            m_Handler.AutoComplete = false;
            int id = m_Mgr.FindCell(0, 0, 0);
            m_Mgr.SetObserver(1, 5f, 5f);
            Tick();
            m_Mgr.NotifyLoaded(id, true);
            Assert.IsTrue(m_Mgr.UnregisterCell(id));
            Assert.AreEqual(1, m_Handler.Unloads.Count);
            Assert.AreEqual(-1, m_Mgr.FindCell(0, 0, 0));
            Assert.AreEqual(StreamingCellState.None, m_Mgr.GetCellState(id), "注销后对外即不可见。");

            m_Mgr.NotifyUnloaded(id);
            int reused = m_Mgr.RegisterCell(0, 0, 0, 9);
            Assert.AreEqual(id, reused, "槽位应被复用（测试前提）。");
            Assert.AreEqual(StreamingCellState.Unloaded, m_Mgr.GetCellState(reused));
        }

        [Test]
        public void ConfigureLayer_InvalidHysteresis_Throws()
        {
            Assert.Throws<FrameworkException>(() => m_Mgr.ConfigureLayer(2, new StreamingLayerSettings { LoadRadius = 30f, UnloadRadius = 30f }));
            Assert.Throws<FrameworkException>(() => m_Mgr.RegisterCell(3, 0, 0, 0));
        }

        [Test]
        public void GetLoadedCells_NonAlloc_ListsLoaded()
        {
            RegisterGrid(2);
            m_Handler.AutoComplete = true;
            m_Mgr.SetObserver(1, 5f, 5f);
            Tick();
            var list = new List<int>();
            m_Mgr.GetLoadedCells(list);
            Assert.AreEqual(m_Mgr.LoadedCellCount, list.Count);
        }

        // ================================================================
        //  零分配
        // ================================================================

        [Test]
        public void PatrolAcrossWorld_SteadyState_DoesNotAllocate()
        {
            RegisterGrid(20);   // 41² = 1681 单元
            m_Handler.AutoComplete = true;
            m_Handler.Record = false;
            m_Mgr.ReevaluateMoveThreshold = 0f;
            float t = 0f;
            TestDelegate body = () =>
            {
                for (int i = 0; i < 30; i++)
                {
                    t += 7f;
                    float x = -150f + (t % 300f);
                    m_Mgr.SetObserver(1, x, 5f);
                    m_Mgr.Update(0.016f, 0.016f);
                }
            };
            body();
            body();
            Assert.That(body, Is.Not.AllocatingGCMemory());
            Assert.Greater(m_Mgr.TotalUnloads, 0, "巡逻应产生真实的加载与卸载。");
        }

        // ================================================================
        //  handler
        // ================================================================

        private sealed class RecordingHandler : IWorldStreamingHandler
        {
            private readonly WorldStreamingManager m_Mgr;
            public bool AutoComplete;
            public bool Record = true;
            public readonly List<int> LoadOrder = new List<int>();
            public readonly List<int> Cancels = new List<int>();
            public readonly List<int> Unloads = new List<int>();
            public readonly List<int> LodChanges = new List<int>();

            public RecordingHandler(WorldStreamingManager mgr) { m_Mgr = mgr; }

            public void BeginLoad(int cellId, int layer, int cx, int cz, int contentKey, int lod)
            {
                if (Record) LoadOrder.Add(cellId);
                if (AutoComplete) m_Mgr.NotifyLoaded(cellId, true);
            }

            public void CancelLoad(int cellId)
            {
                if (Record) Cancels.Add(cellId);
            }

            public void BeginUnload(int cellId, int layer, int cx, int cz, int contentKey)
            {
                if (Record) Unloads.Add(cellId);
                if (AutoComplete) m_Mgr.NotifyUnloaded(cellId);
            }

            public void OnLodChanged(int cellId, int fromLod, int toLod)
            {
                if (Record) LodChanges.Add(cellId);
            }
        }
    }
}
