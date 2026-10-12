using System;
using System.Collections.Generic;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// 单线程、按记录顺序回放。创建立即预留句柄，Playback 后才参与查询。
    /// 回放失败保留已完成的操作，丢弃剩余命令并释放未激活的预留；不提供事务回滚。
    /// During EcsLoop replay only existing-component Set recording is allowed. Ordinary Playback
    /// remains structural-safe; the loop uses internal value-only playback after each system.
    /// </summary>
    public sealed class CommandBuffer : IDisposable
    {
        private enum Kind : byte { Create, Destroy, Set, Remove }
        private struct Command
        {
            public Kind Kind;
            public Entity Entity;
            public IValues Values;
            public int ValueIndex;
        }

        private interface IValues
        {
            void Set(World world, Entity entity, int index);
            void Remove(World world, Entity entity);
            void Clear();
        }

        // One typed value buffer per component type; no per-command boxing or closures.
        private sealed class Values<T> : IValues where T : struct
        {
            private readonly List<T> m_Values = new List<T>();
            public int Add(T value) { int index = m_Values.Count; m_Values.Add(value); return index; }
            public void Set(World world, Entity entity, int index) => world.Set(entity, m_Values[index]);
            public void Remove(World world, Entity entity) => world.Remove<T>(entity);
            public void Clear() => m_Values.Clear();
        }

        private readonly World m_World;
        private readonly bool m_OwnedByGroup;
        private readonly List<Command> m_Commands = new List<Command>();
        private readonly Dictionary<Type, IValues> m_Values = new Dictionary<Type, IValues>();
        private bool m_Disposed;

        public int Count => m_Commands.Count;

        public CommandBuffer(World world) : this(world, ownedByGroup: false) { }

        internal CommandBuffer(World world, bool ownedByGroup)
        {
            m_World = world ?? throw new ArgumentNullException(nameof(world));
            world.RequireNotDisposed();
            m_OwnedByGroup = ownedByGroup;
        }

        public Entity CreateEntity()
        {
            RequireUsable();
            var entity = m_World.ReserveEntity(this);
            m_Commands.Add(new Command { Kind = Kind.Create, Entity = entity });
            return entity;
        }

        /// <summary>Records a despawn request; playback does not immediately destroy components.</summary>
        public void DestroyEntity(Entity entity)
        {
            Validate(entity);
            m_World.RequireNotReplaying();
            m_Commands.Add(new Command { Kind = Kind.Destroy, Entity = entity });
        }

        public void Set<T>(Entity entity, T value) where T : struct
        {
            Validate(entity);
            if (m_World.IsReplaying && !m_World.Has<T>(entity))
                throw new InvalidOperationException("Replay commands may only set existing components.");
            var values = GetValues<T>();
            int index = values.Add(value);
            m_Commands.Add(new Command { Kind = Kind.Set, Entity = entity, Values = values, ValueIndex = index });
        }

        public void Remove<T>(Entity entity) where T : struct
        {
            Validate(entity);
            m_World.RequireNotReplaying();
            m_Commands.Add(new Command { Kind = Kind.Remove, Entity = entity, Values = GetValues<T>() });
        }

        public void Playback()
        {
            RequireUsable();
            // A rejected synchronization point preserves the pending buffer for later playback.
            m_World.RequireStructuralChange();
            PlaybackCore(replay: false);
        }

        internal void PlaybackReplay()
        {
            RequireUsable();
            m_World.RequireOutsideIteration();
            if (!m_World.IsReplaying) throw new InvalidOperationException("Value-only playback requires an active replay.");
            PlaybackCore(replay: true);
        }

        private void PlaybackCore(bool replay)
        {
            try
            {
                for (int i = 0; i < m_Commands.Count; i++)
                {
                    var command = m_Commands[i];
                    if (replay && command.Kind != Kind.Set)
                        throw new InvalidOperationException("Structural commands cannot execute during replay.");
                    switch (command.Kind)
                    {
                        case Kind.Create: m_World.Activate(command.Entity, this); break;
                        case Kind.Destroy: m_World.RequestDespawn(command.Entity); break;
                        case Kind.Set: command.Values.Set(m_World, command.Entity, command.ValueIndex); break;
                        case Kind.Remove: command.Values.Remove(m_World, command.Entity); break;
                    }
                }
            }
            finally { Clear(); }
        }

        /// <summary>丢弃待执行命令、取消预留句柄；保留容量以便复用。</summary>
        public void Clear()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(CommandBuffer));
            for (int i = 0; i < m_Commands.Count; i++)
                if (m_Commands[i].Kind == Kind.Create) m_World.CancelReservation(m_Commands[i].Entity, this);
            m_Commands.Clear();
            foreach (var values in m_Values.Values) values.Clear();
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            if (m_OwnedByGroup) throw new InvalidOperationException("This CommandBuffer is owned by its SystemGroup and cannot be disposed by a borrower.");
            Release();
        }

        internal void Release()
        {
            if (m_Disposed) return;
            if (!m_World.IsDisposed) m_World.RequireNotReplaying();
            Clear();
            m_Commands.Capacity = 0;
            m_Values.Clear();
            m_Values.TrimExcess();
            m_Disposed = true;
        }

        private void Validate(Entity entity)
        {
            RequireUsable();
            m_World.ValidateCommandTarget(entity, this);
        }

        private void RequireUsable()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(CommandBuffer));
            m_World.RequireNotDisposed();
        }

        private Values<T> GetValues<T>() where T : struct
        {
            if (!m_Values.TryGetValue(typeof(T), out var values))
            {
                values = new Values<T>();
                m_Values.Add(typeof(T), values);
            }
            return (Values<T>)values;
        }
    }
}
