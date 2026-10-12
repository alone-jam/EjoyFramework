using System;
using System.Collections.Generic;

namespace EjoyFramework.Core.Ecs
{
    public sealed partial class World
    {
        private struct Partition
        {
            public int Head;
            public int Tail;
            public int Count;
            public bool Reserved;
        }

        // Sparse keys: even int.MaxValue costs one record, not an array indexed by the key.
        private Dictionary<int, Partition> m_Partitions = new Dictionary<int, Partition>();
        private long m_NextPartitionKey;

        /// <summary>
        /// Reserves the next unused key, skipping existing manual and reserved records. Keys
        /// issued by this allocator are never automatically reused. Ownership is cooperative:
        /// only the reserving owner or its delegates should assign/release this key.
        /// </summary>
        public int ReservePartitionKey()
        {
            RequireStructuralChange();
            long candidate = m_NextPartitionKey;
            while (candidate <= int.MaxValue && m_Partitions.ContainsKey((int)candidate)) candidate++;
            if (candidate > int.MaxValue)
                throw new OverflowException("Partition reservation keys exhausted for this world.");
            int key = (int)candidate;
            m_Partitions.Add(key, new Partition { Head = -1, Tail = -1, Reserved = true });
            // Publish the cursor only after insertion succeeds; long represents int.MaxValue + 1.
            m_NextPartitionKey = candidate + 1;
            return key;
        }

        /// <summary>
        /// Releases an empty reservation, not its allocator history. Unknown/unreserved keys
        /// return false; still-alive members (including pending despawns) prevent release. The
        /// owner must also finish its external work before releasing; integer keys are not security capabilities.
        /// </summary>
        public bool ReleasePartitionKey(int partitionKey)
        {
            RequireStructuralChange();
            ValidatePartitionKey(partitionKey);
            if (!m_Partitions.TryGetValue(partitionKey, out var partition) || !partition.Reserved) return false;
            if (partition.Count != 0)
                throw new InvalidOperationException("Cannot release a partition reservation while members are still alive.");
            m_Partitions.Remove(partitionKey);
            return true;
        }

        /// <summary>
        /// Assigns one partition to a live entity, including a pending-despawn entity. Appends to
        /// the destination's insertion order; assigning the same key preserves order. Membership
        /// changes require a structural-safe point, even if this call would be a no-op.
        /// </summary>
        public void SetPartition(Entity entity, int partitionKey)
        {
            RequireAlive(entity);
            RequireStructuralChange();
            ValidatePartitionKey(partitionKey);
            int index = entity.Index;
            if (m_Slots[index].HasPartition && m_Slots[index].PartitionKey == partitionKey) return;
            if (!m_Partitions.TryGetValue(partitionKey, out var partition))
            {
                partition = new Partition { Head = -1, Tail = -1 };
                // Allocate the destination record before detaching: allocation failure leaves
                // existing membership intact. All remaining dictionary writes update existing keys.
                m_Partitions.Add(partitionKey, partition);
            }
            DetachPartition(index);
            m_Slots[index].HasPartition = true;
            m_Slots[index].PartitionKey = partitionKey;
            m_Slots[index].PartitionPrevious = partition.Tail;
            m_Slots[index].PartitionNext = -1;
            if (partition.Tail >= 0) m_Slots[partition.Tail].PartitionNext = index;
            else partition.Head = index;
            partition.Tail = index;
            partition.Count++;
            m_Partitions[partitionKey] = partition;
        }

        /// <summary>Removes live membership, returning false when unassigned. Does not cancel despawn.</summary>
        public bool ClearPartition(Entity entity)
        {
            RequireAlive(entity);
            RequireStructuralChange();
            if (!m_Slots[entity.Index].HasPartition) return false;
            DetachPartition(entity.Index);
            return true;
        }

        /// <summary>Counts all still-alive members, including pending despawns. Unknown keys return zero.</summary>
        public int GetPartitionCount(int partitionKey)
        {
            RequireNotDisposed();
            ValidatePartitionKey(partitionKey);
            return m_Partitions.TryGetValue(partitionKey, out var partition) ? partition.Count : 0;
        }

        /// <summary>
        /// Visits all members in insertion order, INCLUDING pending despawns, even during cleanup.
        /// Unlike OnlyDespawning queries this is not limited to a selected cleanup batch. Uses a
        /// working visitor copy, written back in finally on success or failure. Nested traversal
        /// with distinct visitors and nonstructural despawn requests are allowed; membership,
        /// physical destruction and other structural mutations are forbidden while traversing.
        /// </summary>
        public int ForEachInPartition<TVisitor>(int partitionKey, ref TVisitor visitor)
            where TVisitor : struct, IEntityVisitor
        {
            RequireNotDisposed();
            ValidatePartitionKey(partitionKey);
            if (!m_Partitions.TryGetValue(partitionKey, out var partition)) return 0;
            var working = visitor;
            EnterIteration();
            try
            {
                int visited = 0;
                for (int index = partition.Head; index >= 0; index = m_Slots[index].PartitionNext)
                {
                    working.Visit(EntityAt(index));
                    visited++;
                }
                return visited;
            }
            finally { visitor = working; ExitIteration(); }
        }

        /// <summary>Requests all currently unrequested members in insertion order; does not physically destroy them.</summary>
        public int RequestDespawnPartition(int partitionKey) => RequestDespawnPartition(partitionKey, int.MaxValue);

        /// <summary>
        /// Marks at most maxRequests NEW requests, skipping members already pending. Zero requests
        /// does nothing; negative budgets are rejected. One forward traversal per call, stopping at
        /// the request limit. This is a request budget, not a scan, cleanup-pass or destruction budget.
        /// May be called during forward queries, systems or partition traversal; replay rejects even zero requests.
        /// </summary>
        public int RequestDespawnPartition(int partitionKey, int maxRequests)
        {
            RequireNotReplaying();
            ValidatePartitionKey(partitionKey);
            if (maxRequests < 0) throw new ArgumentOutOfRangeException(nameof(maxRequests));
            if (maxRequests == 0 || !m_Partitions.TryGetValue(partitionKey, out var partition)) return 0;
            EnterIteration();
            try
            {
                int requested = 0;
                for (int index = partition.Head; index >= 0 && requested < maxRequests; index = m_Slots[index].PartitionNext)
                    if (RequestDespawn(EntityAt(index))) requested++;
                return requested;
            }
            finally { ExitIteration(); }
        }

        private void DetachPartition(int index)
        {
            if (!m_Slots[index].HasPartition) return;
            int key = m_Slots[index].PartitionKey;
            int previous = m_Slots[index].PartitionPrevious;
            int next = m_Slots[index].PartitionNext;
            var partition = m_Partitions[key];
            if (previous >= 0) m_Slots[previous].PartitionNext = next;
            else partition.Head = next;
            if (next >= 0) m_Slots[next].PartitionPrevious = previous;
            else partition.Tail = previous;
            partition.Count--;
            if (partition.Count == 0 && !partition.Reserved) m_Partitions.Remove(key);
            else m_Partitions[key] = partition;
            m_Slots[index].HasPartition = false;
            m_Slots[index].PartitionKey = 0;
            m_Slots[index].PartitionPrevious = -1;
            m_Slots[index].PartitionNext = -1;
        }

        private static void ValidatePartitionKey(int partitionKey)
        {
            if (partitionKey < 0) throw new ArgumentOutOfRangeException(nameof(partitionKey));
        }
    }
}
