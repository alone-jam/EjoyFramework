using System;
using System.Collections.Generic;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// 按 order 升序串行执行，同 order 保持注册顺序，每个系统结束后回放命令。
    /// 持有自己的命令缓冲，不拥有 World 或注册的系统实例。不执行多线程调度。
    /// 独立组全部系统成功后释放待销毁实体；失败保留请求。需要反初始化阶段时使用 EcsLoop。
    /// </summary>
    public sealed class SystemGroup : IDisposable
    {
        private sealed class Entry
        {
            public ISystem System;
            public int Order;
            public bool Enabled = true;
            public bool Replayable;
        }

        private readonly World m_World;
        private readonly bool m_FlushDespawns;
        private readonly CommandBuffer m_Commands;
        private readonly List<Entry> m_Systems = new List<Entry>();
        private bool m_Updating;
        private bool m_Disposed;

        public SystemGroup(World world) : this(world, flushDespawns: true) { }

        internal SystemGroup(World world, bool flushDespawns)
        {
            m_World = world ?? throw new ArgumentNullException(nameof(world));
            m_FlushDespawns = flushDespawns;
            m_Commands = new CommandBuffer(world, ownedByGroup: true);
        }

        public void Add(ISystem system, int order = 0) => Add(system, order, replayable: false);

        internal void Add(ISystem system, int order, bool replayable)
        {
            RequireEditable();
            if (system == null) throw new ArgumentNullException(nameof(system));
            if (Find(system) >= 0) throw new InvalidOperationException("System instance already registered.");
            int index = m_Systems.Count;
            while (index > 0 && m_Systems[index - 1].Order > order) index--;
            m_Systems.Insert(index, new Entry { System = system, Order = order, Replayable = replayable });
        }

        public bool Remove(ISystem system)
        {
            RequireEditable();
            int index = Find(system);
            if (index < 0) return false;
            m_Systems.RemoveAt(index);
            return true;
        }

        public void SetEnabled(ISystem system, bool enabled)
        {
            RequireEditable();
            int index = Find(system);
            if (index < 0) throw new InvalidOperationException("System instance is not registered.");
            m_Systems[index].Enabled = enabled;
        }

        /// <summary>Convenience for an unscheduled frame; equivalent to passing EcsTime.ForFrame(deltaTime).</summary>
        public void Update(float deltaTime)
        {
            RequireEditable();
            m_World.RequireStructuralChange();
            var time = EcsTime.ForFrame(deltaTime);
            UpdateCore(in time);
        }

        /// <summary>Passes time unchanged to every enabled system. Does not advance or retain a clock.</summary>
        public void Update(in EcsTime time)
        {
            RequireEditable();
            m_World.RequireStructuralChange();
            UpdateCore(in time);
        }

        internal void ValidateReplay()
        {
            RequireEditable();
            m_World.RequireOutsideIteration();
            if (m_Commands.Count != 0)
                throw new InvalidOperationException("Clear or play back pending borrowed commands before replay.");
        }

        internal void UpdateReplay(in EcsTime time)
        {
            RequireEditable();
            m_World.RequireOutsideIteration();
            if (!m_World.IsReplaying) throw new InvalidOperationException("Replay dispatch requires an active World replay.");
            UpdateCore(in time, replay: true);
        }

        // Replay preflight excludes old commands; replay recording excludes new reservations.
        // Clearing these value-only buffers cannot attempt topology changes while unwinding.
        internal void DiscardReplayCommands() => m_Commands.Clear();

        private void UpdateCore(in EcsTime time, bool replay = false)
        {
            m_Updating = true;
            try
            {
                for (int i = 0; i < m_Systems.Count; i++)
                {
                    var entry = m_Systems[i];
                    if (!entry.Enabled || (replay && !entry.Replayable)) continue;
                    m_World.EnterIteration();
                    try { entry.System.Update(m_World, m_Commands, in time); }
                    finally { m_World.ExitIteration(); }
                    if (replay) m_Commands.PlaybackReplay();
                    else m_Commands.Playback();
                }
                // 独立组以整组成功结束为销毁同步点；循环的私有组由 Despawn 阶段统一处理。
                if (!replay && m_FlushDespawns) m_World.ProcessDespawns();
            }
            catch
            {
                m_Commands.Clear();
                throw;
            }
            finally { m_Updating = false; }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            if (m_Updating) throw new InvalidOperationException("Cannot dispose a running SystemGroup.");
            m_Commands.Release();
            m_Systems.Clear();
            m_Systems.Capacity = 0;
            m_Disposed = true;
        }

        private int Find(ISystem system)
        {
            for (int i = 0; i < m_Systems.Count; i++)
                if (ReferenceEquals(m_Systems[i].System, system)) return i;
            return -1;
        }

        private void RequireEditable()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(SystemGroup));
            if (m_Updating) throw new InvalidOperationException("Cannot modify or reenter a running SystemGroup.");
            m_World.RequireNotDisposed();
        }
    }
}
