using System;
using System.Collections.Generic;
using System.Threading;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Setup-only component/schema catalog, not replicated state storage. Register on the
    /// constructing thread, then Freeze before any lookup. Frozen snapshots may be shared
    /// across threads and allocate nothing on warmed lookup/enumeration paths.
    /// Internal component IDs index local metadata only; descriptors expose explicit schema IDs.
    /// </summary>
    public sealed class ReplicationSchema
    {
        private readonly struct Entry
        {
            public readonly int TypeId;
            public readonly ReplicationDescriptor Descriptor;
            public Entry(int typeId, ushort schemaId, ReplicationMode mode)
            {
                TypeId = typeId;
                Descriptor = new ReplicationDescriptor(schemaId, mode);
            }
        }

        private readonly int m_OwnerThreadId = Environment.CurrentManagedThreadId;
        private List<Entry> m_Entries = new List<Entry>();
        private ReplicationDescriptor[] m_Descriptors;
        private ReplicationDescriptor[] m_ByType;
        private bool m_Frozen;

        public bool IsFrozen => Volatile.Read(ref m_Frozen);
        public int Count { get { RequireFrozen(); return m_Descriptors.Length; } }

        /// <summary>Readonly descriptors in ascending schema-ID order; no local type IDs are exposed.</summary>
        public ReadOnlySpan<ReplicationDescriptor> Descriptors
        {
            get { RequireFrozen(); return m_Descriptors; }
        }

        public void RegisterReplicated<T>(ushort schemaId) where T : unmanaged
            => Register<T>(schemaId, ReplicationMode.Replicated);

        public void RegisterPredicted<T>(ushort schemaId) where T : unmanaged, IPredicted<T>
            => Register<T>(schemaId, ReplicationMode.Predicted);

        public void RegisterInterpolated<T>(ushort schemaId) where T : unmanaged, IInterpolated<T>
            => Register<T>(schemaId, ReplicationMode.Interpolated);

        /// <summary>Idempotent on the setup thread. Allocation failure leaves the catalog unfrozen and retryable.</summary>
        public void Freeze()
        {
            RequireOwnerThread();
            if (IsFrozen) return;
            Entry[] entries = m_Entries.ToArray();
            Array.Sort(entries, (left, right) => left.Descriptor.SchemaId.CompareTo(right.Descriptor.SchemaId));
            int maximumTypeId = -1;
            for (int i = 0; i < entries.Length; i++) maximumTypeId = Math.Max(maximumTypeId, entries[i].TypeId);
            var descriptors = new ReplicationDescriptor[entries.Length];
            var byType = new ReplicationDescriptor[checked(maximumTypeId + 1)];
            for (int i = 0; i < entries.Length; i++)
            {
                descriptors[i] = entries[i].Descriptor;
                byType[entries[i].TypeId] = entries[i].Descriptor;
            }
            m_Descriptors = descriptors;
            m_ByType = byType;
            m_Entries = null;
            // Publish fully initialized immutable arrays to readers; no mutation after this point.
            Volatile.Write(ref m_Frozen, true);
        }

        public bool TryGet<T>(out ReplicationDescriptor descriptor) where T : unmanaged
        {
            RequireFrozen();
            int typeId = ComponentType<T>.Id;
            descriptor = typeId < m_ByType.Length ? m_ByType[typeId] : default;
            return descriptor.SchemaId != 0;
        }

        public bool TryGet(ushort schemaId, out ReplicationDescriptor descriptor)
        {
            RequireFrozen();
            int low = 0;
            int high = m_Descriptors.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                var candidate = m_Descriptors[middle];
                if (candidate.SchemaId == schemaId) { descriptor = candidate; return true; }
                if (candidate.SchemaId < schemaId) low = middle + 1;
                else high = middle - 1;
            }
            descriptor = default;
            return false;
        }

        public ReplicationDescriptor GetAt(int index)
        {
            RequireFrozen();
            if ((uint)index >= (uint)m_Descriptors.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return m_Descriptors[index];
        }

        private void Register<T>(ushort schemaId, ReplicationMode mode) where T : unmanaged
        {
            RequireOwnerThread();
            if (IsFrozen) throw new InvalidOperationException("Replication schema is frozen.");
            if (schemaId == 0) throw new ArgumentOutOfRangeException(nameof(schemaId), "Schema ID zero is reserved.");
            int typeId = ComponentType<T>.Id;
            // Registration is a cold setup path. A single list avoids partial multi-map publication.
            for (int i = 0; i < m_Entries.Count; i++)
            {
                var entry = m_Entries[i];
                if (entry.TypeId == typeId || entry.Descriptor.SchemaId == schemaId)
                    throw new InvalidOperationException("A component type and schema ID may each be registered only once, across all modes.");
            }
            m_Entries.Add(new Entry(typeId, schemaId, mode));
        }

        private void RequireFrozen()
        {
            if (!IsFrozen) throw new InvalidOperationException("Freeze the replication schema before reading it.");
        }

        private void RequireOwnerThread()
        {
            if (Environment.CurrentManagedThreadId != m_OwnerThreadId)
                throw new InvalidOperationException("Configure the replication schema only on its constructing thread.");
        }
    }
}
