using System;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Owner-thread local prediction copies in one flat per-type sample array, with no per-entity
    /// objects. HistoryLength bounds the retained tick window, not just the sample count. Ticks
    /// are nonzero and never wrap; exact reads never clamp or interpolate. Does not own or write
    /// the World, capture state, or schedule replay. Unmanaged pointers/handles are not deep-copied.
    /// </summary>
    public sealed class PredictionHistoryStore<T> : IDisposable where T : unmanaged, IPredicted<T>
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

        private struct Cursor
        {
            public uint LatestTick;
            public uint LowerBound;
        }

        private const int MaximumArrayLength = 0x7FEFFFFF;
        private readonly World m_World;
        private readonly AuthoritativeBaselineStore<T> m_Baseline;
        private readonly int m_HistoryLength;
        private Cursor[] m_Cursors;
        private Sample[] m_Samples;

        /// <summary>Exclusive entity-index bound [0, Capacity), not a live-entity count.</summary>
        public int Capacity { get { RequireUsable(); return m_Cursors.Length; } }
        public int HistoryLength { get { RequireUsable(); return m_HistoryLength; } }

        public PredictionHistoryStore(World world, int entityCapacity = 64, int historyLength = 32)
        {
            m_World = world ?? throw new ArgumentNullException(nameof(world));
            world.RequireStructuralChange();
            if (historyLength < 1) throw new ArgumentOutOfRangeException(nameof(historyLength));
            m_HistoryLength = historyLength;
            int sampleCapacity = ValidateCapacity(entityCapacity);
            var cursors = new Cursor[entityCapacity];
            var samples = new Sample[sampleCapacity];
            m_Baseline = new AuthoritativeBaselineStore<T>(world, entityCapacity);
            m_Cursors = cursors;
            m_Samples = samples;
        }

        /// <summary>
        /// Reserves the exclusive entity-index bound without shrinking or changing tokens/data.
        /// Checks the flat sample product before allocating; all allocations precede publication.
        /// Requires a structural-safe point, including no-growth requests.
        /// </summary>
        public void EnsureCapacity(int entityCapacity)
        {
            RequireUsable();
            m_World.RequireStructuralChange();
            EnsureCapacityCore(entityCapacity);
        }

        /// <summary>
        /// Issues a fresh membership token and clears this entity's history. Required after
        /// remove/readd, ownership transfer or timeline reset; old queued tokens remain invalid.
        /// </summary>
        public Binding Bind(Entity entity)
        {
            RequireUsable();
            m_World.RequireStructuralChange();
            if (!m_World.IsAlive(entity) || m_World.IsDespawning(entity) || !m_World.Has<T>(entity))
                throw new InvalidOperationException("Bind requires a live, non-despawning entity with the component in this World.");
            int required = checked(entity.Index + 1);
            ValidateCapacity(required);
            if (required > m_Cursors.Length)
            {
                int maximumCapacity = MaximumArrayLength / m_HistoryLength;
                int capacity = Math.Min(ComponentPool<T>.Grow(m_Cursors.Length, required), maximumCapacity);
                EnsureCapacityCore(capacity);
            }
            var token = m_Baseline.Bind(entity);
            ClearHistory(entity.Index);
            return new Binding(token);
        }

        /// <summary>Structural-safe release, even after target removal; stale tokens cannot clear replacements.</summary>
        public bool Unbind(in Binding binding)
        {
            var token = binding.Token;
            if (!m_Baseline.Unbind(in token)) return false;
            ClearHistory(binding.Entity.Index);
            return true;
        }

        /// <summary>
        /// Copies a newer nonzero tick, or replaces the current head for local recomputation.
        /// OlderTick requires an explicit successful TruncateAfter before rebuilding. Recording
        /// advances a monotonic retention floor; evicted history cannot be recovered by rewinding.
        /// Requires owner/store/World validity and no iteration, not structural-change permission.
        /// </summary>
        public PredictionHistoryWriteResult TryRecord(in Binding binding, uint tick, in T value)
        {
            RequireOutsideIteration();
            if (tick == 0) throw new ArgumentOutOfRangeException(nameof(tick), "Prediction tick zero is invalid.");
            var result = ValidateBinding(in binding);
            if (result != PredictionHistoryWriteResult.Accepted) return result;
            int entityIndex = binding.Entity.Index;
            var cursor = m_Cursors[entityIndex];
            if (tick < cursor.LatestTick) return PredictionHistoryWriteResult.OlderTick;
            uint lowerBound = tick >= (uint)m_HistoryLength ? tick - (uint)m_HistoryLength + 1 : 1;
            m_Samples[SampleIndex(entityIndex, tick)] = new Sample { Tick = tick, Value = value };
            m_Cursors[entityIndex] = new Cursor
            {
                LatestTick = tick,
                LowerBound = Math.Max(cursor.LowerBound, lowerBound)
            };
            return PredictionHistoryWriteResult.Accepted;
        }

        /// <summary>
        /// Returns a value copy only for this exact retained tick. Zero, gaps, evictions and
        /// invalid/unavailable bindings return false/default. Reads are allowed during iteration.
        /// </summary>
        public bool TryGet(in Binding binding, uint tick, out T value)
        {
            value = default;
            if (ValidateBinding(in binding) != PredictionHistoryWriteResult.Accepted) return false;
            var cursor = m_Cursors[binding.Entity.Index];
            if (tick == 0 || tick < cursor.LowerBound || tick > cursor.LatestTick) return false;
            var sample = m_Samples[SampleIndex(binding.Entity.Index, tick)];
            if (sample.Tick != tick) return false;
            value = sample.Value;
            return true;
        }

        /// <summary>
        /// Discards all samples after anchorTick and moves the logical head to that tick, even
        /// when it is a gap (re-recording the anchor is then allowed). Zero throws. An anchor
        /// below the retained floor returns OutsideWindow without changes; empty history or an
        /// anchor at/after the head is an accepted no-op. The floor NEVER moves backward, so
        /// truncation cannot resurrect evicted tags. No iteration; no structural guard required.
        /// </summary>
        public PredictionHistoryWriteResult TruncateAfter(in Binding binding, uint anchorTick)
        {
            RequireOutsideIteration();
            if (anchorTick == 0)
                throw new ArgumentOutOfRangeException(nameof(anchorTick), "Truncation tick zero is invalid.");
            var result = ValidateBinding(in binding);
            if (result != PredictionHistoryWriteResult.Accepted) return result;
            int entityIndex = binding.Entity.Index;
            var cursor = m_Cursors[entityIndex];
            if (anchorTick < cursor.LowerBound) return PredictionHistoryWriteResult.OutsideWindow;
            if (anchorTick >= cursor.LatestTick) return PredictionHistoryWriteResult.Accepted;
            int start = entityIndex * m_HistoryLength;
            for (int offset = 0; offset < m_HistoryLength; offset++)
                if (m_Samples[start + offset].Tick > anchorTick) m_Samples[start + offset] = default;
            m_Cursors[entityIndex] = new Cursor { LatestTick = anchorTick, LowerBound = cursor.LowerBound };
            return PredictionHistoryWriteResult.Accepted;
        }

        /// <summary>
        /// Validates both bindings against the same full entity/current component membership,
        /// then compares at the authority's exact tick. Calls authority.VerifyPrediction on LOCAL
        /// value copies; a mutating or throwing verifier cannot change retained values. Exceptions
        /// propagate. Owner/disposal guards apply to both stores; no World writes or replay occur.
        /// Matching numeric ticks do not establish a shared authority origin: the caller must
        /// coordinate timelines, including resets. T defines tolerance; external verifier side effects
        /// and pointer/handle targets cannot be isolated by copying unmanaged values.
        /// </summary>
        public PredictionVerificationResult VerifyPrediction(AuthoritativeBaselineStore<T> authority,
            in AuthoritativeBaselineStore<T>.Binding authorityBinding, in Binding predictionBinding)
        {
            RequireUsable();
            if (authority == null) throw new ArgumentNullException(nameof(authority));
            if (authority.ValidateBinding(in authorityBinding) != BaselineWriteResult.Accepted
                || ValidateBinding(in predictionBinding) != PredictionHistoryWriteResult.Accepted
                || authorityBinding.Entity != predictionBinding.Entity)
                return PredictionVerificationResult.InvalidBinding;
            if (!authority.TryGet(in authorityBinding, out uint tick, out T authoritativeValue))
                return PredictionVerificationResult.MissingAuthority;
            if (!TryGet(in predictionBinding, tick, out T prediction))
                return PredictionVerificationResult.MissingHistory;
            return authoritativeValue.VerifyPrediction(in prediction)
                ? PredictionVerificationResult.Match : PredictionVerificationResult.Mismatch;
        }

        /// <summary>
        /// Structural-safe reset invalidates every token/sample and retains capacity. Bind again;
        /// never relabel queued old data with new tokens. Does not reset any World or clock.
        /// </summary>
        public void ResetTimeline()
        {
            m_Baseline.ResetTimeline();
            Array.Clear(m_Cursors, 0, m_Cursors.Length);
            Array.Clear(m_Samples, 0, m_Samples.Length);
        }

        /// <summary>Owner-thread, structural-safe and idempotent; also permitted after World disposal.</summary>
        public void Dispose()
        {
            m_Baseline.Dispose();
            m_Cursors = Array.Empty<Cursor>();
            m_Samples = Array.Empty<Sample>();
        }

        private PredictionHistoryWriteResult ValidateBinding(in Binding binding)
        {
            var token = binding.Token;
            // Read-only: the private identity store never receives authoritative values/ticks.
            var result = m_Baseline.ValidateBinding(in token);
            if (result == BaselineWriteResult.StaleBinding) return PredictionHistoryWriteResult.StaleBinding;
            if (result == BaselineWriteResult.TargetUnavailable) return PredictionHistoryWriteResult.TargetUnavailable;
            return PredictionHistoryWriteResult.Accepted;
        }

        private int SampleIndex(int entityIndex, uint tick)
            => entityIndex * m_HistoryLength + (int)(tick % (uint)m_HistoryLength);

        private void ClearHistory(int entityIndex)
        {
            Array.Clear(m_Samples, entityIndex * m_HistoryLength, m_HistoryLength);
            m_Cursors[entityIndex] = default;
        }

        private int ValidateCapacity(int entityCapacity)
        {
            ComponentPool<T>.ValidateCapacity(entityCapacity);
            long sampleCapacity = checked((long)entityCapacity * m_HistoryLength);
            if (sampleCapacity > MaximumArrayLength)
                throw new ArgumentOutOfRangeException(nameof(entityCapacity),
                    "Entity capacity times history length must not exceed 0x7FEFFFFF elements.");
            return (int)sampleCapacity;
        }

        private void EnsureCapacityCore(int entityCapacity)
        {
            int sampleCapacity = ValidateCapacity(entityCapacity);
            if (entityCapacity <= m_Cursors.Length) return;
            var cursors = new Cursor[entityCapacity];
            var samples = new Sample[sampleCapacity];
            Array.Copy(m_Cursors, cursors, m_Cursors.Length);
            Array.Copy(m_Samples, samples, m_Samples.Length);
            // Last allocation-capable step: a failure leaves all published arrays/tokens intact.
            m_Baseline.EnsureCapacity(entityCapacity);
            m_Cursors = cursors;
            m_Samples = samples;
        }

        private void RequireUsable()
        {
            // Delegate owner-thread/store/World disposal checks, without requiring structural permission.
            _ = m_Baseline.Capacity;
        }

        private void RequireOutsideIteration()
        {
            RequireUsable();
            m_World.RequireOutsideIteration();
        }
    }
}
