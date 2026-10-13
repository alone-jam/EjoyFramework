using System;
using System.Collections.Generic;

namespace EjoyFramework.GamePlay.Units
{
    /// <summary>
    /// Reusable, engine-independent first-hit query against equal-radius unit circles.
    /// Uses the world's spatial broad phase and live faction/targetability filters.
    /// Call SyncSpatial after movement. Single-threaded; capacity growth may allocate,
    /// subsequent queries reuse the candidate buffer. Does not apply damage or game rules.
    /// </summary>
    public sealed class UnitRaycastQuery
    {
        private readonly UnitWorld m_World;
        private readonly List<UnitModel> m_Candidates;
        private TargetingQuery m_Targeting;
        private EjoyFramework.GamePlay.Factions.FactionRelations m_Relations;
        public int LastCandidateCount { get; private set; }

        public UnitRaycastQuery(UnitWorld world, int capacity = 64)
        {
            m_World = world ?? throw new ArgumentNullException(nameof(world));
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            m_Candidates = new List<UnitModel>(capacity);
        }

        /// <summary>
        /// Direction is normalized internally; zero direction misses. Length and radius
        /// are finite, nonnegative. Hits starting inside a circle have distance zero.
        /// Equal entry distances resolve by lowest unit ID, independent of grid order.
        /// Filter.MaxRange, when positive, additionally limits segment length.
        /// </summary>
        public bool Cast(UnitModel source, float directionX, float directionY,
            float length, float radius, in TargetFilter filter, out UnitModel hit, out float distance)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!Finite(length) || length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            if (!Finite(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
            if (!Finite(directionX) || !Finite(directionY)) throw new ArgumentException("Direction must be finite.");
            if (!Finite(source.PositionX) || !Finite(source.PositionY)) throw new ArgumentException("Source position must be finite.");
            hit = null; distance = length; LastCandidateCount = 0;
            double magnitude = Math.Sqrt((double)directionX * directionX + (double)directionY * directionY);
            if (magnitude == 0) return false;
            float dx = (float)(directionX / magnitude), dy = (float)(directionY / magnitude);
            if (filter.MaxRange > 0) length = Math.Min(length, filter.MaxRange);
            distance = length;
            m_World.QueryNearby(source.PositionX + dx * length * .5f,
                source.PositionY + dy * length * .5f, length * .5f + radius, m_Candidates);
            LastCandidateCount = m_Candidates.Count;
            if (!ReferenceEquals(m_Relations, m_World.Relations))
            { m_Relations = m_World.Relations; m_Targeting = new TargetingQuery(m_Relations); }
            for (int i = 0; i < m_Candidates.Count; i++)
            {
                var candidate = m_Candidates[i];
                if (candidate.Id == source.Id || !m_Targeting.Passes(candidate, source.FactionId, filter)) continue;
                double x = candidate.PositionX - source.PositionX, y = candidate.PositionY - source.PositionY;
                double projection = x * dx + y * dy;
                double perpendicularSquared = Math.Max(0, x * x + y * y - projection * projection);
                double radiusSquared = (double)radius * radius;
                if (perpendicularSquared > radiusSquared) continue;
                double halfChord = Math.Sqrt(radiusSquared - perpendicularSquared);
                if (projection + halfChord < 0) continue;
                float entry = (float)Math.Max(0, projection - halfChord);
                if (entry > distance) continue;
                if (hit != null && entry == distance && candidate.Id > hit.Id) continue;
                hit = candidate; distance = entry;
            }
            return hit != null;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
