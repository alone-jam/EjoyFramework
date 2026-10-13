using System;
using System.Collections.Generic;

namespace EjoyFramework.Core.ObjectPool
{
    /// <summary>
    /// Owner-scoped keyed live set over a single-spawn pool. Mark referenced objects each
    /// frame using Touch, then Sweep. Unreferenced objects receive one retirement callback
    /// and stay owned until ready to return. Clear returns all objects immediately.
    /// The caller owns the pool and must clear this set before destroying that pool.
    /// No per-frame allocations after collection capacity warmup. Not thread-safe.
    /// </summary>
    public sealed class PooledObjectSet<TKey, TObject> where TObject : ObjectBase
    {
        private struct Entry { public TObject Value; public bool Seen, Retiring; }
        private readonly Dictionary<TKey, Entry> m_Entries;
        private readonly List<TKey> m_Keys;
        private readonly IObjectPool<TObject> m_Pool;
        private readonly Func<TKey, TObject> m_Create;
        private readonly string m_ObjectName;
        private readonly Action<TObject> m_Retire;
        private readonly Predicate<TObject> m_CanReturn;
        public int Count => m_Entries.Count;
        public PooledObjectSet(IObjectPool<TObject> pool, Func<TKey, TObject> create,
            Action<TObject> retire, Predicate<TObject> canReturn, int capacity = 64, string objectName = "")
        {
            m_ObjectName = objectName ?? throw new ArgumentNullException(nameof(objectName));
            m_Pool = pool ?? throw new ArgumentNullException(nameof(pool));
            m_Create = create ?? throw new ArgumentNullException(nameof(create));
            m_Retire = retire ?? throw new ArgumentNullException(nameof(retire));
            m_CanReturn = canReturn ?? throw new ArgumentNullException(nameof(canReturn));
            m_Entries = new Dictionary<TKey, Entry>(capacity);m_Keys = new List<TKey>(capacity);
        }
        public bool TryGet(TKey key, out TObject value)
        {
            if(m_Entries.TryGetValue(key,out var entry)){value=entry.Value;return true;}
            value=null;return false;
        }
        public TObject Touch(TKey key)
        {
            if(m_Entries.TryGetValue(key,out var entry))
            {
                if(entry.Retiring)throw new InvalidOperationException("Retiring keys must not be reused before return. Use a generation-aware key or clear the set.");
                entry.Seen=true;m_Entries[key]=entry;return entry.Value;
            }
            var value=m_Pool.Spawn(m_ObjectName);
            if(value==null)
            {
                value=m_Create(key) ?? throw new InvalidOperationException("Pool factory returned null.");
                if(value.Name != m_ObjectName)throw new InvalidOperationException("Factory object name must match the configured pool bucket.");
                m_Pool.Register(value,false);
                value=m_Pool.Spawn(value.Name);
                if(value==null)throw new InvalidOperationException("New pool object could not be spawned.");
            }
            m_Entries.Add(key,new Entry{Value=value,Seen=true});return value;
        }
        public void Sweep()
        {
            m_Keys.Clear();foreach(var key in m_Entries.Keys)m_Keys.Add(key);
            for(int i=0;i<m_Keys.Count;i++)
            {
                var key=m_Keys[i];var entry=m_Entries[key];
                if(!entry.Seen)
                {
                    if(!entry.Retiring){entry.Retiring=true;m_Entries[key]=entry;m_Retire(entry.Value);}
                    if(m_CanReturn(entry.Value)){m_Pool.Unspawn(entry.Value.Target);m_Entries.Remove(key);continue;}
                }
                entry.Seen=false;m_Entries[key]=entry;
            }
        }
        public void Clear()
        {
            foreach(var entry in m_Entries.Values)m_Pool.Unspawn(entry.Value.Target);
            m_Entries.Clear();m_Keys.Clear();
        }
    }
}
