using System;
using System.Collections.Generic;

namespace EjoyFramework.Core.Ecs
{
    public sealed partial class World
    {
        private readonly List<Entity> m_DespawnQueue = new List<Entity>();
        private int m_DespawnBatchCount;
        private bool m_DespawnBatchActive;

        /// <summary>Live requested entities, including the selected cleanup batch; EntityCount still includes them.</summary>
        public int PendingDespawnCount { get; private set; }

        /// <summary>
        /// Nonstructural and idempotent: immediately excluded from default queries, but still
        /// alive and readable until processing. Returns false for duplicates, stale, foreign,
        /// default or unactivated reserved handles. Disposed worlds and active replay reject all requests.
        /// </summary>
        public bool RequestDespawn(Entity entity)
        {
            RequireNotReplaying();
            if (!IsAlive(entity) || m_Slots[entity.Index].DespawnState != 0) return false;
            m_DespawnQueue.Add(entity); // Store generation/world identity before publishing pending state.
            m_Slots[entity.Index].DespawnState = 1;
            PendingDespawnCount++;
            return true;
        }

        public bool IsDespawning(Entity entity)
        {
            RequireNotDisposed();
            return IsAlive(entity) && IsDespawningAt(entity.Index);
        }

        /// <summary>
        /// Immediately processes the current request batch at a structural-safe point, without
        /// running cleanup systems. Standalone groups call this after successful dispatch;
        /// loop hosts instead use the Despawn phase. Returns the number actually destroyed.
        /// </summary>
        public int ProcessDespawns()
        {
            if (!BeginDespawnBatch()) return 0;
            try { return CompleteDespawnBatch(); }
            catch { CancelDespawnBatch(); throw; }
        }

        internal bool IsDespawningAt(int index) => m_Slots[index].DespawnState != 0;

        internal bool MatchesOnlyDespawning(int index)
            => m_Slots[index].DespawnState == 2 || (!m_DespawnBatchActive && IsDespawningAt(index));

        internal bool BeginDespawnBatch()
        {
            RequireStructuralChange();
            if (m_DespawnBatchActive) throw new InvalidOperationException("A despawn cleanup batch is already active.");
            if (PendingDespawnCount == 0)
            {
                m_DespawnQueue.Clear(); // Discard stale entries left by explicit immediate destruction.
                return false;
            }
            m_DespawnBatchCount = m_DespawnQueue.Count;
            for (int i = 0; i < m_DespawnBatchCount; i++)
            {
                var entity = m_DespawnQueue[i];
                if (IsAlive(entity) && m_Slots[entity.Index].DespawnState == 1)
                    m_Slots[entity.Index].DespawnState = 2;
            }
            m_DespawnBatchActive = true;
            return true;
        }

        internal int CompleteDespawnBatch()
        {
            RequireStructuralChange();
            if (!m_DespawnBatchActive) throw new InvalidOperationException("No despawn cleanup batch is active.");
            int destroyed = 0;
            for (int i = 0; i < m_DespawnBatchCount; i++)
            {
                var entity = m_DespawnQueue[i];
                if (!IsAlive(entity) || m_Slots[entity.Index].DespawnState != 2) continue;
                DestroyEntity(entity);
                destroyed++;
            }
            m_DespawnQueue.RemoveRange(0, m_DespawnBatchCount);
            m_DespawnBatchCount = 0;
            m_DespawnBatchActive = false;
            return destroyed;
        }

        internal void CancelDespawnBatch()
        {
            RequireNotReplaying();
            if (!m_DespawnBatchActive) return;
            // Keep original entries in place, ahead of requests appended during cleanup.
            // Generation checks prevent restoring a dead/reused slot or duplicating a request.
            for (int i = 0; i < m_DespawnBatchCount; i++)
            {
                var entity = m_DespawnQueue[i];
                if (IsAlive(entity) && m_Slots[entity.Index].DespawnState == 2)
                    m_Slots[entity.Index].DespawnState = 1;
            }
            m_DespawnBatchCount = 0;
            m_DespawnBatchActive = false;
        }

        private void ClearDespawnState()
        {
            m_DespawnQueue.Clear();
            m_DespawnQueue.Capacity = 0;
            m_DespawnBatchCount = 0;
            m_DespawnBatchActive = false;
            PendingDespawnCount = 0;
        }
    }
}
