using System;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>不可变查询描述；在初始化时缓存，WithAny 调用累积为 OR，WithAll/WithNone 累积为 AND。</summary>
    public sealed partial class Query
    {
        private enum DespawnFilter : byte { Exclude, Include, Only }

        private readonly World m_World;
        private readonly DespawnFilter m_DespawnFilter;
        private readonly IComponentPool[] m_All;
        private readonly IComponentPool[] m_Any;
        private readonly IComponentPool[] m_None;

        internal Query(World world) : this(world, Array.Empty<IComponentPool>(), Array.Empty<IComponentPool>(), Array.Empty<IComponentPool>()) { }

        private Query(World world, IComponentPool[] all, IComponentPool[] any, IComponentPool[] none,
            DespawnFilter despawnFilter = DespawnFilter.Exclude)
        {
            m_World = world;
            m_DespawnFilter = despawnFilter;
            m_All = all; m_Any = any; m_None = none;
        }

        public Query WithAll<T>() where T : struct
            => new Query(m_World, Append(m_All, m_World.GetPool<T>()), m_Any, m_None, m_DespawnFilter);
        public Query WithAny<T>() where T : struct
            => new Query(m_World, m_All, Append(m_Any, m_World.GetPool<T>()), m_None, m_DespawnFilter);
        public Query WithNone<T>() where T : struct
            => new Query(m_World, m_All, m_Any, Append(m_None, m_World.GetPool<T>()), m_DespawnFilter);

        /// <summary>Immutable variant including both ordinary and requested entities.</summary>
        public Query IncludeDespawning() => new Query(m_World, m_All, m_Any, m_None, DespawnFilter.Include);

        /// <summary>
        /// Immutable variant for requested entities. During a cleanup pass it selects only the
        /// captured batch, not new requests made by that pass. Outside cleanup it selects all pending.
        /// </summary>
        public Query OnlyDespawning() => new Query(m_World, m_All, m_Any, m_None, DespawnFilter.Only);

        public int Count
        {
            get { var visitor = new EntityVisitor(); return Execute(ref visitor); }
        }

        public void ForEach(Action<Entity> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var visitor = new EntityVisitor { Action = action };
            Execute(ref visitor);
        }

        private static IComponentPool[] Append(IComponentPool[] source, IComponentPool pool)
        {
            for (int i = 0; i < source.Length; i++)
                if (ReferenceEquals(source[i], pool)) return source;
            var result = new IComponentPool[source.Length + 1];
            Array.Copy(source, result, source.Length);
            result[source.Length] = pool;
            return result;
        }

        private interface IVisitor
        {
            int PoolCount { get; }
            IComponentPool PoolAt(int index);
            void Invoke(Entity entity);
        }

        private struct EntityVisitor : IVisitor
        {
            public Action<Entity> Action;
            public int PoolCount => 0;
            public IComponentPool PoolAt(int index) => throw new ArgumentOutOfRangeException(nameof(index));
            public void Invoke(Entity entity) => Action?.Invoke(entity);
        }

        private int Execute<TVisitor>(ref TVisitor visitor) where TVisitor : struct, IVisitor
        {
            m_World.EnterIteration();
            try
            {
                IComponentPool smallest = null;
                for (int i = 0; i < m_All.Length; i++)
                    if (smallest == null || m_All[i].Count < smallest.Count) smallest = m_All[i];
                for (int i = 0; i < visitor.PoolCount; i++)
                {
                    var pool = visitor.PoolAt(i);
                    if (smallest == null || pool.Count < smallest.Count) smallest = pool;
                }
                int count = smallest == null ? m_World.EntityCount : smallest.Count;
                int matched = 0;
                for (int i = 0; i < count; i++)
                {
                    int index = smallest == null ? m_World.AliveIndexAt(i) : smallest.EntityAt(i);
                    if (!Matches(index)) continue;
                    bool hasRequired = true;
                    for (int p = 0; p < visitor.PoolCount; p++)
                        if (!visitor.PoolAt(p).Has(index)) { hasRequired = false; break; }
                    if (!hasRequired) continue;
                    visitor.Invoke(m_World.EntityAt(index));
                    matched++;
                }
                return matched;
            }
            finally { m_World.ExitIteration(); }
        }

        private bool Matches(int index)
        {
            if (m_DespawnFilter == DespawnFilter.Exclude && m_World.IsDespawningAt(index)) return false;
            if (m_DespawnFilter == DespawnFilter.Only && !m_World.MatchesOnlyDespawning(index)) return false;
            for (int i = 0; i < m_All.Length; i++)
                if (!m_All[i].Has(index)) return false;
            for (int i = 0; i < m_None.Length; i++)
                if (m_None[i].Has(index)) return false;
            if (m_Any.Length == 0) return true;
            for (int i = 0; i < m_Any.Length; i++)
                if (m_Any[i].Has(index)) return true;
            return false;
        }
    }
}
