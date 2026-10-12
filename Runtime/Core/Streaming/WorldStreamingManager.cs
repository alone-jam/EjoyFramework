//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace EjoyFramework.Core.Streaming
{
    /// <summary>
    /// <see cref="IWorldStreamingManager"/> 实现。
    ///
    /// 数据结构：
    ///   • 单元存槽位数组（id = 下标），(layer, cx, cz) → id 用 long 键字典；
    ///   • 每层一张 <c>Dictionary&lt;long, int&gt;</c>（(cx,cz) → id）；评估时按观察者位置枚举半径覆盖的整数 cell 范围，
    ///     不遍历全部单元——复杂度与半径内 cell 数成正比，与世界总大小无关；
    ///   • 期望集用"代数戳"标记（每次评估 +1），活动单元戳过期即为候选卸载（再按 UnloadRadius 滞回确认）；
    ///   • 加载队列是 <see cref="BinaryHeap{T}"/>（键 = 距离 + 层偏置），每帧按预算出队派发。
    /// 稳态零分配。
    /// </summary>
    internal sealed class WorldStreamingManager : FrameworkModule, IWorldStreamingManager
    {
        private const int MaxLayers = 16;

        private struct Cell
        {
            public int Layer;
            public int Cx;
            public int Cz;
            public int ContentKey;
            public StreamingCellState State;
            public int Lod;
            public int DesiredStamp;     // 最近一次被期望集命中的评估代数
            public int QueueStamp;       // 最近一次入堆的评估代数：堆中旧项据此丢弃（重排优先级）
            public int Version;          // 槽位复用计数：堆中指向旧单元的项据此丢弃
            public float NearestDistance;
            public bool InUse;
        }

        private struct Observer
        {
            public int Id;
            public float LocalX;
            public float LocalZ;
            public bool InUse;
        }

        private struct LoadEntry
        {
            public int CellId;
            public int Version;
            public int Stamp;
            public float Key;
        }

        private sealed class LoadEntryComparer : IComparer<LoadEntry>
        {
            public static readonly LoadEntryComparer Instance = new LoadEntryComparer();
            public int Compare(LoadEntry a, LoadEntry b) { return a.Key.CompareTo(b.Key); }
        }

        private readonly StreamingLayerSettings[] m_Layers = new StreamingLayerSettings[MaxLayers];
        private readonly Dictionary<long, int>[] m_LayerIndex = new Dictionary<long, int>[MaxLayers];
        private Cell[] m_Cells = new Cell[256];
        private int m_CellCount;
        private readonly Stack<int> m_FreeCells = new Stack<int>();
        private Observer[] m_Observers = new Observer[4];
        private int m_ObserverCount;
        private readonly BinaryHeap<LoadEntry> m_LoadQueue = new BinaryHeap<LoadEntry>(LoadEntryComparer.Instance, 64);
        private readonly List<int> m_ActiveCells = new List<int>();     // Queued/Loading/Loaded/Unloading/Cancelling 的单元
        private readonly List<int> m_Scratch = new List<int>();

        private IWorldStreamingHandler m_Handler;
        private float m_CellSize = 64f;
        private float m_RadiusScale = 1f;
        private float m_MoveThreshold = 16f;
        private bool m_MoveThresholdExplicit;
        private int m_MaxLoadStartsPerFrame = 4;
        private int m_MaxLoadsInFlight = 8;
        private int m_MaxUnloadsPerFrame = 4;
        private float m_OriginX;
        private float m_OriginZ;
        private float m_LastEvalX = float.NaN;
        private float m_LastEvalZ = float.NaN;
        private int m_Stamp;
        private bool m_Dirty = true;
        private bool m_Evaluating;

        private int m_LoadedCount;
        private int m_LoadingCount;
        private long m_TotalLoadsStarted;
        private long m_TotalLoadsCompleted;
        private long m_TotalLoadsCancelled;
        private long m_TotalLoadFailures;
        private long m_TotalUnloads;

        public override int Priority { get { return 5; } }   // 早于业务模块

        // ================================================================
        //  配置
        // ================================================================

        public float CellSize
        {
            get { return m_CellSize; }
            set
            {
                if (!(value > 0f)) throw new FrameworkException("WorldStreaming：CellSize 必须为正数。");
                m_CellSize = value;
                if (!m_MoveThresholdExplicit) m_MoveThreshold = value * 0.25f;
                m_Dirty = true;
            }
        }

        public void ConfigureLayer(int layer, StreamingLayerSettings settings)
        {
            Framework.EnsureMainThread(nameof(ConfigureLayer));
            ValidateLayer(layer);
            if (settings == null) throw new FrameworkException("WorldStreaming：settings 不能为 null。");
            if (!(settings.LoadRadius >= 0f)) throw new FrameworkException("WorldStreaming：LoadRadius 必须 >= 0。");
            if (!(settings.UnloadRadius > settings.LoadRadius)) throw new FrameworkException("WorldStreaming：UnloadRadius 必须大于 LoadRadius（滞回）。");
            m_Layers[layer] = settings;
            if (m_LayerIndex[layer] == null) m_LayerIndex[layer] = new Dictionary<long, int>();
            m_Dirty = true;
        }

        public int MaxLoadStartsPerFrame { get { return m_MaxLoadStartsPerFrame; } set { m_MaxLoadStartsPerFrame = value < 0 ? 0 : value; } }
        public int MaxLoadsInFlight { get { return m_MaxLoadsInFlight; } set { m_MaxLoadsInFlight = value < 1 ? 1 : value; } }
        public int MaxUnloadsPerFrame { get { return m_MaxUnloadsPerFrame; } set { m_MaxUnloadsPerFrame = value < 0 ? 0 : value; } }

        public float ReevaluateMoveThreshold
        {
            get { return m_MoveThreshold; }
            set { m_MoveThreshold = value < 0f ? 0f : value; m_MoveThresholdExplicit = true; }
        }

        public float RadiusScale
        {
            get { return m_RadiusScale; }
            set
            {
                if (!(value > 0f)) throw new FrameworkException("WorldStreaming：RadiusScale 必须为正数。");
                if (value == m_RadiusScale) return;
                m_RadiusScale = value;
                m_Dirty = true;
            }
        }

        public void SetHandler(IWorldStreamingHandler handler)
        {
            m_Handler = handler;
            m_Dirty = true;
        }

        // ================================================================
        //  单元登记
        // ================================================================

        public int RegisterCell(int layer, int cx, int cz, int contentKey)
        {
            Framework.EnsureMainThread(nameof(RegisterCell));
            ValidateLayer(layer);
            if (m_Layers[layer] == null) throw new FrameworkException(Utility.Text.Format("WorldStreaming：层 {0} 未配置，先调用 ConfigureLayer。", layer));

            long key = Pack(cx, cz);
            int id;
            if (m_LayerIndex[layer].TryGetValue(key, out id))
            {
                m_Cells[id].ContentKey = contentKey;
                return id;
            }

            id = AcquireCell();
            ref Cell c = ref m_Cells[id];
            c.Layer = layer;
            c.Cx = cx;
            c.Cz = cz;
            c.ContentKey = contentKey;
            c.State = StreamingCellState.Unloaded;
            c.Lod = -1;
            c.DesiredStamp = -1;
            c.InUse = true;
            m_LayerIndex[layer].Add(key, id);
            m_Dirty = true;
            return id;
        }

        public bool UnregisterCell(int cellId)
        {
            Framework.EnsureMainThread(nameof(UnregisterCell));
            if (!IsValid(cellId)) return false;
            ref Cell c = ref m_Cells[cellId];
            switch (c.State)
            {
                case StreamingCellState.Loaded:
                    BeginUnload(cellId);
                    break;
                case StreamingCellState.Loading:
                    CancelLoading(cellId);
                    break;
                case StreamingCellState.Queued:
                    c.State = StreamingCellState.Unloaded;   // 堆里的过期项出队时被丢弃
                    m_ActiveCells.Remove(cellId);
                    break;
            }

            // 仍在 Unloading/Cancelling 的单元：等回报到达再真正释放槽位（Notify 里检查 InUse=false 的"待注销"标记）。
            m_LayerIndex[c.Layer].Remove(Pack(c.Cx, c.Cz));
            if (c.State == StreamingCellState.Unloaded)
            {
                ReleaseCell(cellId);
            }
            else
            {
                c.InUse = false;   // 待注销：回报到达时释放
            }

            return true;
        }

        public int FindCell(int layer, int cx, int cz)
        {
            if (layer < 0 || layer >= MaxLayers || m_LayerIndex[layer] == null) return -1;
            int id;
            return m_LayerIndex[layer].TryGetValue(Pack(cx, cz), out id) ? id : -1;
        }

        public bool TryGetCell(int cellId, out StreamingCellInfo info)
        {
            if (!IsValid(cellId))
            {
                info = default(StreamingCellInfo);
                return false;
            }

            ref Cell c = ref m_Cells[cellId];
            info = new StreamingCellInfo(cellId, c.Layer, c.Cx, c.Cz, c.ContentKey, c.State, c.Lod);
            return true;
        }

        public void WorldToCell(float x, float z, out int cx, out int cz)
        {
            cx = (int)Math.Floor(x / m_CellSize);
            cz = (int)Math.Floor(z / m_CellSize);
        }

        // ================================================================
        //  观察者 / 原点
        // ================================================================

        public void SetObserver(int observerId, float x, float z)
        {
            Framework.EnsureMainThread(nameof(SetObserver));
            for (int i = 0; i < m_ObserverCount; i++)
            {
                if (m_Observers[i].Id == observerId)
                {
                    m_Observers[i].LocalX = x;
                    m_Observers[i].LocalZ = z;
                    MarkMoved(x + m_OriginX, z + m_OriginZ);
                    return;
                }
            }

            if (m_ObserverCount == m_Observers.Length) Array.Resize(ref m_Observers, m_Observers.Length * 2);
            m_Observers[m_ObserverCount].Id = observerId;
            m_Observers[m_ObserverCount].LocalX = x;
            m_Observers[m_ObserverCount].LocalZ = z;
            m_Observers[m_ObserverCount].InUse = true;
            m_ObserverCount++;
            m_Dirty = true;
        }

        public bool RemoveObserver(int observerId)
        {
            Framework.EnsureMainThread(nameof(RemoveObserver));
            for (int i = 0; i < m_ObserverCount; i++)
            {
                if (m_Observers[i].Id != observerId) continue;
                m_Observers[i] = m_Observers[m_ObserverCount - 1];
                m_ObserverCount--;
                m_Dirty = true;
                return true;
            }

            return false;
        }

        public void ShiftOrigin(float dx, float dz)
        {
            Framework.EnsureMainThread(nameof(ShiftOrigin));
            m_OriginX += dx;
            m_OriginZ += dz;
            // 观察者本地坐标未变，但业务通常紧接着会按新原点重设；这里不强制评估，世界坐标不变时期望集也不变。
        }

        public void GetOrigin(out float x, out float z)
        {
            x = m_OriginX;
            z = m_OriginZ;
        }

        private void MarkMoved(float worldX, float worldZ)
        {
            if (m_ObserverCount > 1 || float.IsNaN(m_LastEvalX))
            {
                m_Dirty = true;
                return;
            }

            float dx = worldX - m_LastEvalX, dz = worldZ - m_LastEvalZ;
            if (dx * dx + dz * dz >= m_MoveThreshold * m_MoveThreshold) m_Dirty = true;
        }

        // ================================================================
        //  驱动
        // ================================================================

        public override void Update(float elapseSeconds, float realElapseSeconds)
        {
            if (m_Dirty && !m_Evaluating)
            {
                m_Dirty = false;
                Evaluate();
            }

            PumpLoads(m_MaxLoadStartsPerFrame);
        }

        public void ForceReevaluate()
        {
            Framework.EnsureMainThread(nameof(ForceReevaluate));
            if (m_Evaluating) { m_Dirty = true; return; }
            m_Dirty = false;
            Evaluate();
            PumpLoads(m_MaxLoadStartsPerFrame);
        }

        /// <summary>
        /// 一次评估：1) 期望集标记（每个观察者、每层，枚举 LoadRadius 覆盖的 cell 范围）；
        /// 2) 活动单元：未被标记且到最近观察者距离 &gt; UnloadRadius → 卸载/取消；被标记 → LOD 更新；
        /// 3) 未活动且被标记 → 入加载队列。
        /// </summary>
        private void Evaluate()
        {
            m_Evaluating = true;
            try
            {
                m_Stamp++;
                if (m_ObserverCount > 0)
                {
                    m_LastEvalX = m_Observers[0].LocalX + m_OriginX;
                    m_LastEvalZ = m_Observers[0].LocalZ + m_OriginZ;
                }

                // 1) 标记期望集 + 记录最近距离
                for (int o = 0; o < m_ObserverCount; o++)
                {
                    float ox = m_Observers[o].LocalX + m_OriginX;
                    float oz = m_Observers[o].LocalZ + m_OriginZ;
                    for (int layer = 0; layer < MaxLayers; layer++)
                    {
                        StreamingLayerSettings ls = m_Layers[layer];
                        if (ls == null || m_LayerIndex[layer].Count == 0) continue;
                        MarkLayer(layer, ls, ox, oz);
                    }
                }

                // 2) 活动单元差分（倒序遍历，允许移除）
                int unloadBudget = m_MaxUnloadsPerFrame > 0 ? m_MaxUnloadsPerFrame : int.MaxValue;
                for (int i = m_ActiveCells.Count - 1; i >= 0; i--)
                {
                    int id = m_ActiveCells[i];
                    ref Cell c = ref m_Cells[id];
                    bool desired = c.DesiredStamp == m_Stamp;
                    if (desired)
                    {
                        if (c.State == StreamingCellState.Loaded) UpdateLod(id, ref c);
                        continue;
                    }

                    // 未在期望集：按 UnloadRadius 滞回确认（距离取本代最近观察者；无观察者视为无限远）
                    float unloadRadius = m_Layers[c.Layer].UnloadRadius * m_RadiusScale;
                    float dist = m_ObserverCount == 0 ? float.MaxValue : NearestObserverDistance(c.Cx, c.Cz);
                    if (dist <= unloadRadius) continue;

                    switch (c.State)
                    {
                        case StreamingCellState.Loaded:
                            if (unloadBudget <= 0) { m_Dirty = true; continue; }
                            unloadBudget--;
                            BeginUnload(id);
                            break;
                        case StreamingCellState.Loading:
                            CancelLoading(id);
                            break;
                        case StreamingCellState.Queued:
                            c.State = StreamingCellState.Unloaded;
                            m_ActiveCells.RemoveAt(i);
                            break;
                    }
                }

                // 3) 期望但未活动 → 入队；已排队的按新距离重新入堆（旧项凭 QueueStamp 出队时丢弃）
                for (int i = 0; i < m_Scratch.Count; i++)
                {
                    int id = m_Scratch[i];
                    ref Cell c = ref m_Cells[id];
                    if (c.State == StreamingCellState.Unloaded)
                    {
                        c.State = StreamingCellState.Queued;
                        m_ActiveCells.Add(id);
                    }
                    else if (c.State != StreamingCellState.Queued)
                    {
                        continue;
                    }

                    Enqueue(id, ref c);
                }

                m_Scratch.Clear();
            }
            finally
            {
                m_Evaluating = false;
            }
        }

        private void MarkLayer(int layer, StreamingLayerSettings ls, float ox, float oz)
        {
            Dictionary<long, int> index = m_LayerIndex[layer];
            float r = ls.LoadRadius * m_RadiusScale;
            int minCx = (int)Math.Floor((ox - r) / m_CellSize), maxCx = (int)Math.Floor((ox + r) / m_CellSize);
            int minCz = (int)Math.Floor((oz - r) / m_CellSize), maxCz = (int)Math.Floor((oz + r) / m_CellSize);
            float rSq = r * r;
            for (int cx = minCx; cx <= maxCx; cx++)
            {
                for (int cz = minCz; cz <= maxCz; cz++)
                {
                    int id;
                    if (!index.TryGetValue(Pack(cx, cz), out id)) continue;
                    float dx = CellCenter(cx) - ox, dz = CellCenter(cz) - oz;
                    float dSq = dx * dx + dz * dz;
                    if (dSq > rSq) continue;

                    ref Cell c = ref m_Cells[id];
                    float d = (float)Math.Sqrt(dSq);
                    if (c.DesiredStamp != m_Stamp)
                    {
                        c.DesiredStamp = m_Stamp;
                        c.NearestDistance = d;
                        m_Scratch.Add(id);
                    }
                    else if (d < c.NearestDistance)
                    {
                        c.NearestDistance = d;
                    }
                }
            }
        }

        private float NearestObserverDistance(int cx, int cz)
        {
            float best = float.MaxValue;
            float x = CellCenter(cx), z = CellCenter(cz);
            for (int o = 0; o < m_ObserverCount; o++)
            {
                float dx = m_Observers[o].LocalX + m_OriginX - x, dz = m_Observers[o].LocalZ + m_OriginZ - z;
                float d = dx * dx + dz * dz;
                if (d < best) best = d;
            }

            return (float)Math.Sqrt(best);
        }

        private float CellCenter(int c)
        {
            return (c + 0.5f) * m_CellSize;
        }

        private void UpdateLod(int id, ref Cell c)
        {
            int lod = ComputeLod(m_Layers[c.Layer], c.NearestDistance / m_RadiusScale);   // 距离按缩放折回表空间 = 表整体乘缩放
            if (lod == c.Lod) return;
            int from = c.Lod;
            c.Lod = lod;
            if (m_Handler == null) return;
            try { m_Handler.OnLodChanged(id, from, lod); }
            catch (Exception ex) { FrameworkLog.Error("WorldStreaming：OnLodChanged 抛出异常：{0}", ex); }
        }

        private static int ComputeLod(StreamingLayerSettings ls, float distance)
        {
            float[] table = ls.LodDistances;
            if (table == null || table.Length == 0) return 0;
            for (int i = 0; i < table.Length; i++)
            {
                if (distance < table[i]) return i;
            }

            return table.Length;
        }

        private void Enqueue(int id, ref Cell c)
        {
            c.QueueStamp = m_Stamp;
            LoadEntry e;
            e.CellId = id;
            e.Version = c.Version;
            e.Stamp = m_Stamp;
            e.Key = c.NearestDistance + m_Layers[c.Layer].PriorityBias * m_CellSize;
            m_LoadQueue.Push(e);
        }

        private void PumpLoads(int budget)
        {
            if (m_Handler == null) return;
            while (budget > 0 && m_LoadingCount < m_MaxLoadsInFlight && m_LoadQueue.Count > 0)
            {
                LoadEntry e = m_LoadQueue.Pop();
                if (!IsValid(e.CellId)) continue;
                ref Cell c = ref m_Cells[e.CellId];
                if (c.Version != e.Version || c.State != StreamingCellState.Queued || c.QueueStamp != e.Stamp) continue;   // 过期项
                budget--;
                BeginLoad(e.CellId);
            }
        }

        // ================================================================
        //  动作
        // ================================================================

        private void BeginLoad(int id)
        {
            ref Cell c = ref m_Cells[id];
            c.State = StreamingCellState.Loading;
            c.Lod = ComputeLod(m_Layers[c.Layer], c.NearestDistance / m_RadiusScale);
            m_LoadingCount++;
            m_TotalLoadsStarted++;
            try { m_Handler.BeginLoad(id, c.Layer, c.Cx, c.Cz, c.ContentKey, c.Lod); }
            catch (Exception ex)
            {
                FrameworkLog.Error("WorldStreaming：BeginLoad 抛出异常，单元按失败处理：{0}", ex);
                NotifyLoaded(id, false);
            }
        }

        private void CancelLoading(int id)
        {
            ref Cell c = ref m_Cells[id];
            c.State = StreamingCellState.Cancelling;
            m_TotalLoadsCancelled++;
            if (m_Handler == null) return;
            try { m_Handler.CancelLoad(id); }
            catch (Exception ex) { FrameworkLog.Error("WorldStreaming：CancelLoad 抛出异常：{0}", ex); }
        }

        private void BeginUnload(int id)
        {
            ref Cell c = ref m_Cells[id];
            c.State = StreamingCellState.Unloading;
            m_LoadedCount--;
            m_TotalUnloads++;
            if (m_Handler == null) { NotifyUnloaded(id); return; }
            try { m_Handler.BeginUnload(id, c.Layer, c.Cx, c.Cz, c.ContentKey); }
            catch (Exception ex)
            {
                FrameworkLog.Error("WorldStreaming：BeginUnload 抛出异常，视为已卸载：{0}", ex);
                NotifyUnloaded(id);
            }
        }

        public void NotifyLoaded(int cellId, bool success)
        {
            Framework.EnsureMainThread(nameof(NotifyLoaded));
            if (cellId < 0 || cellId >= m_CellCount) return;
            ref Cell c = ref m_Cells[cellId];
            if (c.State != StreamingCellState.Loading && c.State != StreamingCellState.Cancelling)
            {
                FrameworkLog.Warning("WorldStreaming：单元 {0} 处于 {1} 状态时收到 NotifyLoaded，已忽略（重复回报？）。", cellId, c.State);
                return;
            }

            m_LoadingCount--;
            bool wanted = c.State == StreamingCellState.Loading && c.InUse;
            if (success)
            {
                m_TotalLoadsCompleted++;
                if (wanted)
                {
                    c.State = StreamingCellState.Loaded;
                    m_LoadedCount++;
                    return;
                }

                // 取消/注销后才到达：结果作废，让 handler 卸载
                c.State = StreamingCellState.Unloading;
                m_LoadedCount++;   // BeginUnload 会 -1，保持计数对称
                BeginUnload(cellId);
                return;
            }

            if (wanted) m_TotalLoadFailures++;
            c.State = StreamingCellState.Unloaded;
            c.Lod = -1;
            m_ActiveCells.Remove(cellId);
            if (!c.InUse) ReleaseCell(cellId);
            else if (wanted || c.DesiredStamp == m_Stamp) m_Dirty = true; // 失败重试，或取消期间观察者已返回。
        }

        public void NotifyUnloaded(int cellId)
        {
            Framework.EnsureMainThread(nameof(NotifyUnloaded));
            if (cellId < 0 || cellId >= m_CellCount) return;
            ref Cell c = ref m_Cells[cellId];
            if (c.State != StreamingCellState.Unloading)
            {
                FrameworkLog.Warning("WorldStreaming：单元 {0} 处于 {1} 状态时收到 NotifyUnloaded，已忽略。", cellId, c.State);
                return;
            }

            c.State = StreamingCellState.Unloaded;
            c.Lod = -1;
            m_ActiveCells.Remove(cellId);
            if (!c.InUse) ReleaseCell(cellId);
            else if (c.DesiredStamp == m_Stamp) m_Dirty = true;   // 卸载期间又被期望：重新入队
        }

        public bool RequireLoaded(int cellId)
        {
            Framework.EnsureMainThread(nameof(RequireLoaded));
            if (!IsValid(cellId) || m_Handler == null) return false;
            ref Cell c = ref m_Cells[cellId];
            switch (c.State)
            {
                case StreamingCellState.Unloaded:
                    c.NearestDistance = 0f;
                    c.DesiredStamp = m_Stamp;
                    m_ActiveCells.Add(cellId);
                    BeginLoad(cellId);
                    return true;
                case StreamingCellState.Queued:
                    c.NearestDistance = 0f;
                    BeginLoad(cellId);   // 堆里的项出队时发现状态非 Queued 会被丢弃
                    return true;
                default:
                    return false;
            }
        }

        // ================================================================
        //  状态
        // ================================================================

        public int RegisteredCellCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < MaxLayers; i++) if (m_LayerIndex[i] != null) n += m_LayerIndex[i].Count;
                return n;
            }
        }

        public int LoadedCellCount { get { return m_LoadedCount; } }
        public int LoadingCellCount { get { return m_LoadingCount; } }
        public int QueuedLoadCount { get { return m_LoadQueue.Count; } }
        public int ObserverCount { get { return m_ObserverCount; } }
        public long TotalLoadsStarted { get { return m_TotalLoadsStarted; } }
        public long TotalLoadsCompleted { get { return m_TotalLoadsCompleted; } }
        public long TotalLoadsCancelled { get { return m_TotalLoadsCancelled; } }
        public long TotalLoadFailures { get { return m_TotalLoadFailures; } }
        public long TotalUnloads { get { return m_TotalUnloads; } }

        public StreamingCellState GetCellState(int cellId)
        {
            return IsValid(cellId) ? m_Cells[cellId].State : StreamingCellState.None;
        }

        public int GetCellLod(int cellId)
        {
            return IsValid(cellId) && m_Cells[cellId].State == StreamingCellState.Loaded ? m_Cells[cellId].Lod : -1;
        }

        public void GetLoadedCells(List<int> results)
        {
            if (results == null) throw new FrameworkException("results 不能为 null。");
            results.Clear();
            for (int i = 0; i < m_ActiveCells.Count; i++)
            {
                if (m_Cells[m_ActiveCells[i]].State == StreamingCellState.Loaded) results.Add(m_ActiveCells[i]);
            }
        }

        public override void Shutdown()
        {
            m_Handler = null;
            m_LoadQueue.Clear();
            m_ActiveCells.Clear();
            m_Scratch.Clear();
            m_FreeCells.Clear();
            for (int i = 0; i < MaxLayers; i++)
            {
                m_Layers[i] = null;
                if (m_LayerIndex[i] != null) m_LayerIndex[i].Clear();
            }

            Array.Clear(m_Cells, 0, m_CellCount);
            m_CellCount = 0;
            m_ObserverCount = 0;
            m_LoadedCount = 0;
            m_LoadingCount = 0;
            m_OriginX = 0f;
            m_OriginZ = 0f;
            m_LastEvalX = float.NaN;
            m_Dirty = true;
        }

        // ================================================================
        //  内部
        // ================================================================

        private static void ValidateLayer(int layer)
        {
            if (layer < 0 || layer >= MaxLayers) throw new FrameworkException(Utility.Text.Format("WorldStreaming：层 id {0} 越界（0..{1}）。", layer, MaxLayers - 1));
        }

        private bool IsValid(int cellId)
        {
            return cellId >= 0 && cellId < m_CellCount && m_Cells[cellId].InUse;
        }

        private static long Pack(int cx, int cz)
        {
            return ((long)cx << 32) | (uint)cz;
        }

        private int AcquireCell()
        {
            if (m_FreeCells.Count > 0) return m_FreeCells.Pop();
            if (m_CellCount == m_Cells.Length) Array.Resize(ref m_Cells, m_Cells.Length * 2);
            return m_CellCount++;
        }

        private void ReleaseCell(int id)
        {
            int version = m_Cells[id].Version + 1;
            m_Cells[id] = default(Cell);
            m_Cells[id].Version = version;
            m_FreeCells.Push(id);
        }
    }
}
