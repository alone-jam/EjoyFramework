using System;

namespace EjoyFramework.Core.Ecs
{
    public sealed partial class Query
    {
        /// <summary>
        /// Runs a concrete struct visitor without a delegate or boxing. Returns the number of
        /// completed visits. A working copy is written back on success or exception; do not
        /// mutate the original visitor through another alias while traversal is running.
        /// </summary>
        public int ForEach<TVisitor>(ref TVisitor visitor) where TVisitor : struct, IEntityVisitor
        {
            var adapter = new StructVisitor<TVisitor> { User = visitor };
            try { return Execute(ref adapter); }
            finally { visitor = adapter.User; }
        }

        /// <summary>Requires T1 in addition to the query filters; writes visitor state back even on failure.</summary>
        public int ForEach<TVisitor, T1>(ref TVisitor visitor)
            where TVisitor : struct, IEntityVisitor<T1> where T1 : struct
        {
            var adapter = new StructVisitor<TVisitor, T1> { User = visitor, Pool1 = m_World.GetPool<T1>() };
            try { return Execute(ref adapter); }
            finally { visitor = adapter.User; }
        }

        /// <summary>Requires T1 and T2; component references are valid only during each Visit.</summary>
        public int ForEach<TVisitor, T1, T2>(ref TVisitor visitor)
            where TVisitor : struct, IEntityVisitor<T1, T2> where T1 : struct where T2 : struct
        {
            var adapter = new StructVisitor<TVisitor, T1, T2>
            {
                User = visitor, Pool1 = m_World.GetPool<T1>(), Pool2 = m_World.GetPool<T2>()
            };
            try { return Execute(ref adapter); }
            finally { visitor = adapter.User; }
        }

        /// <summary>Requires T1 through T3; component references are valid only during each Visit.</summary>
        public int ForEach<TVisitor, T1, T2, T3>(ref TVisitor visitor)
            where TVisitor : struct, IEntityVisitor<T1, T2, T3>
            where T1 : struct where T2 : struct where T3 : struct
        {
            var adapter = new StructVisitor<TVisitor, T1, T2, T3>
            {
                User = visitor, Pool1 = m_World.GetPool<T1>(), Pool2 = m_World.GetPool<T2>(),
                Pool3 = m_World.GetPool<T3>()
            };
            try { return Execute(ref adapter); }
            finally { visitor = adapter.User; }
        }

        /// <summary>Requires T1 through T4; component references are valid only during each Visit.</summary>
        public int ForEach<TVisitor, T1, T2, T3, T4>(ref TVisitor visitor)
            where TVisitor : struct, IEntityVisitor<T1, T2, T3, T4>
            where T1 : struct where T2 : struct where T3 : struct where T4 : struct
        {
            var adapter = new StructVisitor<TVisitor, T1, T2, T3, T4>
            {
                User = visitor, Pool1 = m_World.GetPool<T1>(), Pool2 = m_World.GetPool<T2>(),
                Pool3 = m_World.GetPool<T3>(), Pool4 = m_World.GetPool<T4>()
            };
            try { return Execute(ref adapter); }
            finally { visitor = adapter.User; }
        }

        // Reuse Execute's smallest-pool selection, query/despawn filtering and iteration guard.
        // Neither adapter nor User is converted to an interface value: both calls are constrained.
        private struct StructVisitor<TVisitor> : IVisitor where TVisitor : struct, IEntityVisitor
        {
            public TVisitor User;
            public int PoolCount => 0;
            public IComponentPool PoolAt(int index) => throw new ArgumentOutOfRangeException(nameof(index));
            public void Invoke(Entity entity) => User.Visit(entity);
        }

        private struct StructVisitor<TVisitor, T1> : IVisitor
            where TVisitor : struct, IEntityVisitor<T1> where T1 : struct
        {
            public TVisitor User;
            public ComponentPool<T1> Pool1;
            public int PoolCount => 1;
            public IComponentPool PoolAt(int index)
            {
                switch (index)
                {
                    case 0: return Pool1;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
            }
            public void Invoke(Entity entity) => User.Visit(entity, ref Pool1.Get(entity.Index));
        }

        private struct StructVisitor<TVisitor, T1, T2> : IVisitor
            where TVisitor : struct, IEntityVisitor<T1, T2> where T1 : struct where T2 : struct
        {
            public TVisitor User;
            public ComponentPool<T1> Pool1;
            public ComponentPool<T2> Pool2;
            public int PoolCount => 2;
            public IComponentPool PoolAt(int index)
            {
                switch (index)
                {
                    case 0: return Pool1;
                    case 1: return Pool2;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
            }
            public void Invoke(Entity entity) => User.Visit(entity, ref Pool1.Get(entity.Index), ref Pool2.Get(entity.Index));
        }

        private struct StructVisitor<TVisitor, T1, T2, T3> : IVisitor
            where TVisitor : struct, IEntityVisitor<T1, T2, T3>
            where T1 : struct where T2 : struct where T3 : struct
        {
            public TVisitor User;
            public ComponentPool<T1> Pool1;
            public ComponentPool<T2> Pool2;
            public ComponentPool<T3> Pool3;
            public int PoolCount => 3;
            public IComponentPool PoolAt(int index)
            {
                switch (index)
                {
                    case 0: return Pool1;
                    case 1: return Pool2;
                    case 2: return Pool3;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
            }
            public void Invoke(Entity entity) => User.Visit(entity, ref Pool1.Get(entity.Index), ref Pool2.Get(entity.Index), ref Pool3.Get(entity.Index));
        }

        private struct StructVisitor<TVisitor, T1, T2, T3, T4> : IVisitor
            where TVisitor : struct, IEntityVisitor<T1, T2, T3, T4>
            where T1 : struct where T2 : struct where T3 : struct where T4 : struct
        {
            public TVisitor User;
            public ComponentPool<T1> Pool1;
            public ComponentPool<T2> Pool2;
            public ComponentPool<T3> Pool3;
            public ComponentPool<T4> Pool4;
            public int PoolCount => 4;
            public IComponentPool PoolAt(int index)
            {
                switch (index)
                {
                    case 0: return Pool1;
                    case 1: return Pool2;
                    case 2: return Pool3;
                    case 3: return Pool4;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
            }
            public void Invoke(Entity entity) => User.Visit(entity, ref Pool1.Get(entity.Index), ref Pool2.Get(entity.Index), ref Pool3.Get(entity.Index), ref Pool4.Get(entity.Index));
        }
    }
}
