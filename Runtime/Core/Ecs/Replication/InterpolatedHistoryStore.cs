using System;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Owner-thread, World-bound interpolation history with fixed samples per entity in flat arrays.
    /// Owns copies, not the World; never writes live components. Mutations require a structural-safe
    /// point; reads are allowed during iteration. Membership stamps invalidate old bindings after
    /// component removal/readdition; bind again before accepting new samples. Unmanaged values
    /// containing pointers or handles are not deep-copied or wire-safe.
    /// </summary>
    public sealed class InterpolatedHistoryStore<T> : IDisposable where T : unmanaged, IInterpolated<T>
    {
        public readonly struct Binding
        {
            internal AuthoritativeBaselineStore<T>.Binding Token { get; }
            public Entity Entity => Token.Entity;

            internal Binding(AuthoritativeBaselineStore<T>.Binding token) => Token = token;
        }

        private struct Sample
        {
            public uint Tick;
            public T Value;
        }

        private const int MaximumArrayLength = 0x7FEFFFFF;
        private readonly World m_World;
        private readonly AuthoritativeBaselineStore<T> m_Baseline;
        private readonly int m_HistoryLength;
        private int[] m_Counts;
        private Sample[] m_Samples;

        /// <summary>Exclusive entity-index bound [0, Capacity), not a live-entity count.</summary>
        public int Capacity { get { RequireUsable(false); return m_Counts.Length; } }
        public int HistoryLength { get { RequireUsable(false); return m_HistoryLength; } }

        public InterpolatedHistoryStore(World world, int entityCapacity = 64, int historyLength = 32)
        {
            m_World = world ?? throw new ArgumentNullException(nameof(world));
            world.RequireStructuralChange();
            if (historyLength < 1) throw new ArgumentOutOfRangeException(nameof(historyLength));
            m_HistoryLength = historyLength;
            int sampleCapacity = ValidateCapacity(entityCapacity);
            var counts = new int[entityCapacity];
            var samples = new Sample[sampleCapacity];
            m_Baseline = new AuthoritativeBaselineStore<T>(world, entityCapacity);
            m_Counts = counts;
            m_Samples = samples;
        }

        /// <summary>
        /// Reserves an exclusive entity-index bound without shrinking or changing bindings/data.
        /// The flat sample count is checked in wide arithmetic before any allocation.
        /// </summary>
        public void EnsureCapacity(int entityCapacity)
        {
            RequireUsable(true);
            EnsureCapacityCore(entityCapacity);
        }

        /// <summary>Issues a fresh token and discards that entity's old history, even on a repeated bind.</summary>
        public Binding Bind(Entity entity)
        {
            RequireUsable(true);
            // Reject invalid targets before attempting capacity growth. Baseline.Bind revalidates.
            if (!m_World.IsAlive(entity) || m_World.IsDespawning(entity) || !m_World.Has<T>(entity))
                throw new InvalidOperationException("Bind requires a live, non-despawning entity with the component in this World.");
            int required = checked(entity.Index + 1);
            ValidateCapacity(required);
            if (required > m_Counts.Length)
            {
                int maximumCapacity = MaximumArrayLength / m_HistoryLength;
                int capacity = Math.Min(ComponentPool<T>.Grow(m_Counts.Length, required), maximumCapacity);
                EnsureCapacityCore(capacity);
            }
            var token = m_Baseline.Bind(entity);
            ClearHistory(entity.Index);
            return new Binding(token);
        }

        /// <summary>Can release an unavailable target; a stale token never clears a replacement.</summary>
        public bool Unbind(in Binding binding)
        {
            var token = binding.Token;
            if (!m_Baseline.Unbind(in token)) return false;
            ClearHistory(binding.Entity.Index);
            return true;
        }

        /// <summary>
        /// Retains the newest HistoryLength distinct, nonzero ticks. First duplicate wins; older
        /// arrivals are accepted while room remains or while inside the full retained window.
        /// Ticks never wrap. Validation precedes all history mutation; values are copied.
        /// </summary>
        public HistoryWriteResult TryAdd(in Binding binding, uint tick, in T value)
        {
            var token = binding.Token;
            var result = m_Baseline.TrySet(in token, tick, in value);
            switch (result)
            {
                case BaselineWriteResult.StaleBinding: return HistoryWriteResult.StaleBinding;
                case BaselineWriteResult.TargetUnavailable: return HistoryWriteResult.TargetUnavailable;
                case BaselineWriteResult.DuplicateTick: return HistoryWriteResult.DuplicateTick;
            }
            // Accepted advances the private latest baseline; OlderTick must NOT reject history.
            // From here insertion is allocation-free and cannot fail for these validated ranges.
            int entityIndex = binding.Entity.Index;
            int start = entityIndex * m_HistoryLength;
            int count = m_Counts[entityIndex];
            int position = LowerBound(start, count, tick);
            if (position < count && m_Samples[start + position].Tick == tick)
                return HistoryWriteResult.DuplicateTick;
            if (count == m_HistoryLength)
            {
                if (position == 0) return HistoryWriteResult.OutsideWindow;
                // Drop oldest; shift only the prefix before the insertion. The suffix stays put.
                Array.Copy(m_Samples, start + 1, m_Samples, start, position - 1);
                position--;
            }
            else
            {
                Array.Copy(m_Samples, start + position, m_Samples, start + position + 1, count - position);
                m_Counts[entityIndex] = count + 1;
            }
            m_Samples[start + position] = new Sample { Tick = tick, Value = value };
            return HistoryWriteResult.Accepted;
        }

        /// <summary>
        /// Samples remote time targetTick + fraction, where fraction must be finite in [0,1).
        /// Tick zero is allowed here. Clamps outside history; uses actual tick distance between
        /// samples, not local clock Alpha. Invokes interpolation on local copies only.
        /// </summary>
        public bool TrySample(in Binding binding, uint targetTick, double fraction, out T value)
        {
            RequireUsable(false);
            if (!(fraction >= 0 && fraction < 1))
                throw new ArgumentOutOfRangeException(nameof(fraction), "Fraction must be finite and in [0,1).");
            value = default;
            var token = binding.Token;
            if (!m_Baseline.TryGet(in token, out _, out _)) return false;
            int start = binding.Entity.Index * m_HistoryLength;
            int count = m_Counts[binding.Entity.Index];
            if (count == 1 || targetTick < m_Samples[start].Tick)
            {
                value = m_Samples[start].Value;
                return true;
            }
            if (targetTick >= m_Samples[start + count - 1].Tick)
            {
                value = m_Samples[start + count - 1].Value;
                return true;
            }
            int upper = LowerBound(start, count, targetTick);
            if (m_Samples[start + upper].Tick == targetTick)
            {
                if (fraction == 0)
                {
                    value = m_Samples[start + upper].Value;
                    return true;
                }
                upper++;
            }
            var earlier = m_Samples[start + upper - 1];
            var later = m_Samples[start + upper];
            double elapsed = ((ulong)targetTick - earlier.Tick) + fraction;
            double distance = (ulong)later.Tick - earlier.Tick;
            float weight = (float)Math.Max(0d, Math.Min(1d, elapsed / distance));
            value = earlier.Value.Interpolate(in later.Value, weight);
            return true;
        }

        /// <summary>
        /// Invalidates every token and sample while retaining capacity; bind again for the new
        /// timeline. Required on tick reset/reconnect. Queued old data must keep its old token.
        /// Does not reset the World or its clock.
        /// </summary>
        public void ResetTimeline()
        {
            m_Baseline.ResetTimeline();
            Array.Clear(m_Counts, 0, m_Counts.Length);
            Array.Clear(m_Samples, 0, m_Samples.Length);
        }

        /// <summary>Owner-thread, idempotent, and permitted after World disposal; never disposes the World.</summary>
        public void Dispose()
        {
            m_Baseline.Dispose();
            m_Counts = Array.Empty<int>();
            m_Samples = Array.Empty<Sample>();
        }

        private void ClearHistory(int entityIndex)
        {
            Array.Clear(m_Samples, entityIndex * m_HistoryLength, m_HistoryLength);
            m_Counts[entityIndex] = 0;
        }

        private int LowerBound(int start, int count, uint tick)
        {
            int low = 0, high = count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (m_Samples[start + middle].Tick < tick) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private int ValidateCapacity(int entityCapacity)
        {
            ComponentPool<T>.ValidateCapacity(entityCapacity);
            long sampleCapacity = (long)entityCapacity * m_HistoryLength;
            if (sampleCapacity > MaximumArrayLength)
                throw new ArgumentOutOfRangeException(nameof(entityCapacity),
                    "Entity capacity times history length must not exceed 0x7FEFFFFF elements.");
            return (int)sampleCapacity;
        }

        private void EnsureCapacityCore(int entityCapacity)
        {
            int sampleCapacity = ValidateCapacity(entityCapacity);
            if (entityCapacity <= m_Counts.Length) return;
            var counts = new int[entityCapacity];
            var samples = new Sample[sampleCapacity];
            Array.Copy(m_Counts, counts, m_Counts.Length);
            Array.Copy(m_Samples, samples, m_Samples.Length);
            // Baseline reserve is the last allocation-capable step. If any allocation fails,
            // all published arrays, tokens and values still describe the previous storage.
            m_Baseline.EnsureCapacity(entityCapacity);
            m_Counts = counts;
            m_Samples = samples;
        }

        private void RequireUsable(bool mutation)
        {
            // Delegate owner-thread, store-disposal and World-disposal checks to the private store.
            _ = m_Baseline.Capacity;
            if (mutation) m_World.RequireStructuralChange();
        }
    }
}
