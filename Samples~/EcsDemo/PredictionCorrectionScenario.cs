using System;
using EjoyFramework.Core.Ecs;

namespace EjoyGame.Samples.EcsDemo
{
    /// <summary>
    /// Closed typed example, NOT a rollback coordinator. Owns one entity, two components covering
    /// all mutable simulation state, one stateless system, fixed configuration and both histories.
    /// No World/Loop/registration handles escape. Authority publication is one trusted producer;
    /// authenticating transport/session origins remains outside this example. Recreate to resync.
    /// </summary>
    public sealed class PredictionCorrectionScenario : IDisposable
    {
        private const int TickRate = 8;
        private const double TickSeconds = 1d / TickRate;
        private const int MaximumCapacity = 1024;
        private const int MaximumAcceleration = 1024;
        private readonly int m_OwnerThread = Environment.CurrentManagedThreadId;
        private readonly ulong m_Epoch;
        private readonly World m_World;
        private readonly TickClock m_Clock;
        private readonly EcsLoop m_Loop;
        private readonly Entity m_Entity;
        private readonly ulong m_PositionLifetime;
        private readonly ulong m_VelocityLifetime;
        private readonly InputSample[] m_Inputs;
        private readonly PredictionHistoryStore<CorrectionPosition> m_PositionHistory;
        private readonly PredictionHistoryStore<CorrectionVelocity> m_VelocityHistory;
        private readonly PredictionHistoryStore<CorrectionPosition>.Binding m_PositionBinding;
        private readonly PredictionHistoryStore<CorrectionVelocity>.Binding m_VelocityBinding;
        private readonly AuthoritativeBaselineStore<CorrectionPosition> m_AuthorityPositions;
        private readonly AuthoritativeBaselineStore<CorrectionVelocity> m_AuthorityVelocities;
        private readonly AuthoritativeBaselineStore<CorrectionPosition>.Binding m_AuthorityPositionBinding;
        private readonly AuthoritativeBaselineStore<CorrectionVelocity>.Binding m_AuthorityVelocityBinding;
        private readonly CaptureSink m_Capture;
        private AuthorityPair m_AuthorityPair;
        private bool m_Busy;
        private bool m_RequiresRecovery;
        private bool m_Disposed;

        private struct InputSample
        {
            public uint Tick;
            public ulong Epoch;
            public int Acceleration;
        }

        // Published only after both baseline stores accept the same producer's packet. Keeping
        // its copied values also detects accidental independent replacement at the same tick.
        private struct AuthorityPair
        {
            public uint Tick;
            public ulong Epoch;
            public CorrectionPosition Position;
            public CorrectionVelocity Velocity;
        }

        public ulong Epoch { get { RequireUsable(); return m_Epoch; } }
        public bool RequiresRecovery { get { RequireUsable(); return m_RequiresRecovery; } }
        public CorrectionSnapshot Current
        {
            get
            {
                RequireUsable();
                return new CorrectionSnapshot(m_Clock.Tick, m_World.Get<CorrectionPosition>(m_Entity),
                    m_World.Get<CorrectionVelocity>(m_Entity));
            }
        }

        /// <summary>Starts at a nonzero tick with zero position/velocity. Capacities are sample-limited to 1..1024.</summary>
        public PredictionCorrectionScenario(uint initialTick = 1, ulong epoch = 1,
            int historyLength = 32, int inputCapacity = 32)
        {
            if (initialTick == 0) throw new ArgumentOutOfRangeException(nameof(initialTick));
            if (epoch == 0) throw new ArgumentOutOfRangeException(nameof(epoch));
            ValidateCapacity(historyLength, nameof(historyLength));
            ValidateCapacity(inputCapacity, nameof(inputCapacity));
            m_Epoch = epoch;
            m_Inputs = new InputSample[inputCapacity];
            m_World = new World(1);
            m_Entity = m_World.CreateEntity();
            m_World.Set(m_Entity, new CorrectionPosition());
            m_World.Set(m_Entity, new CorrectionVelocity());
            if (!m_World.TryGetComponentLifetime<CorrectionPosition>(m_Entity, out m_PositionLifetime)
                || !m_World.TryGetComponentLifetime<CorrectionVelocity>(m_Entity, out m_VelocityLifetime))
                throw new InvalidOperationException("Initial component memberships are missing.");
            m_Clock = new TickClock(TickRate, 1, TickSeconds);
            m_Clock.Reset(initialTick);
            m_Loop = new EcsLoop(m_World, m_Clock);
            m_PositionHistory = new PredictionHistoryStore<CorrectionPosition>(m_World, 1, historyLength);
            m_VelocityHistory = new PredictionHistoryStore<CorrectionVelocity>(m_World, 1, historyLength);
            m_PositionBinding = m_PositionHistory.Bind(m_Entity);
            m_VelocityBinding = m_VelocityHistory.Bind(m_Entity);
            m_AuthorityPositions = new AuthoritativeBaselineStore<CorrectionPosition>(m_World, 1);
            m_AuthorityVelocities = new AuthoritativeBaselineStore<CorrectionVelocity>(m_World, 1);
            m_AuthorityPositionBinding = m_AuthorityPositions.Bind(m_Entity);
            m_AuthorityVelocityBinding = m_AuthorityVelocities.Bind(m_Entity);
            m_Capture = new CaptureSink(this);
            m_Loop.Add(EcsPhase.TickSimulation, new SimulationSystem(this), replayable: true);
            m_Loop.TickCompletedSink = m_Capture;
            RecordBoth(initialTick);
        }

        /// <summary>
        /// Records one bounded acceleration input, then advances exactly one fixed tick. Arithmetic
        /// overflow during execution requires recovery; uint exhaustion/invalid input rejects before recording.
        /// </summary>
        public CorrectionSnapshot Step(int acceleration)
        {
            RequireIdle();
            if (acceleration < -MaximumAcceleration || acceleration > MaximumAcceleration)
                throw new ArgumentOutOfRangeException(nameof(acceleration));
            if (!ScopeMatches()) throw new InvalidOperationException("The closed sample scope or capture changed.");
            m_Loop.ValidateReplay(m_Clock.Tick, m_Clock.Tick, 1); // Pure idle/pending-work guard, even for forward steps.
            uint tick = checked(m_Clock.Tick + 1);
            m_Inputs[InputIndex(tick)] = new InputSample { Tick = tick, Epoch = m_Epoch, Acceleration = acceleration };
            m_Busy = true;
            try
            {
                if (m_Loop.RunFrame(TickSeconds) != 1 || m_Clock.Tick != tick)
                    throw new InvalidOperationException("The sample requires exactly one completed forward tick.");
                return Current;
            }
            catch { m_RequiresRecovery = true; throw; }
            finally { m_Busy = false; }
        }

        /// <summary>
        /// One explicit authority producer publishes a complete copied pair for this owner's epoch.
        /// Ticks must increase; no independently supplied per-component/session sources are combined.
        /// Future pairs may be staged, but cannot be corrected until ValidateReplay accepts their range.
        /// </summary>
        public void PublishAuthority(ulong epoch, uint tick, CorrectionPosition position, CorrectionVelocity velocity)
        {
            RequireIdle();
            if (epoch != m_Epoch) throw new ArgumentOutOfRangeException(nameof(epoch));
            if (tick == 0 || tick <= m_AuthorityPair.Tick) throw new ArgumentOutOfRangeException(nameof(tick));
            m_Busy = true;
            try
            {
                RequireAccepted(m_AuthorityPositions.TrySet(in m_AuthorityPositionBinding, tick, in position));
                RequireAccepted(m_AuthorityVelocities.TrySet(in m_AuthorityVelocityBinding, tick, in velocity));
                m_AuthorityPair = new AuthorityPair { Epoch = epoch, Tick = tick, Position = position, Velocity = velocity };
            }
            catch { m_RequiresRecovery = true; throw; } // Partial publication is never relabelled as coherent.
            finally { m_Busy = false; }
        }

        /// <summary>Returns copies only when BOTH histories contain this exact tick; no fallback.</summary>
        public bool TryGetPrediction(uint tick, out CorrectionPosition position, out CorrectionVelocity velocity)
        {
            RequireUsable();
            bool hasPosition = m_PositionHistory.TryGet(in m_PositionBinding, tick, out position);
            bool hasVelocity = m_VelocityHistory.TryGet(in m_VelocityBinding, tick, out velocity);
            if (hasPosition && hasVelocity) return true;
            position = default;
            velocity = default;
            return false;
        }

        /// <summary>
        /// Preflight is synchronous/read-only, including on the Matched path. Missing prerequisites
        /// return ResyncRequiredNoChange; no current input or adjacent history substitutes for a gap.
        /// Once restoration begins, every failure marks RequiresRecovery and rethrows the ORIGINAL
        /// exception. No automatic ResetClock or rollback of already changed values/history occurs.
        /// </summary>
        public CorrectionResult TryCorrect(uint baselineTick, int maxReplayTicks = 128)
        {
            RequireIdle();
            uint targetTick = m_Clock.Tick;
            CorrectionReason reason;
            CorrectionPosition position;
            CorrectionVelocity velocity;
            bool matched;
            try { reason = Precheck(baselineTick, targetTick, maxReplayTicks, out position, out velocity, out matched); }
            catch (InvalidOperationException)
            {
                // Includes disposed prerequisites. These checks have no user callbacks or writes.
                return NoChange(CorrectionReason.PrerequisiteUnavailable);
            }
            if (reason != CorrectionReason.None) return NoChange(reason);
            if (matched) return new CorrectionResult(CorrectionOutcome.Matched);
            m_Busy = true;
            try
            {
                if (!ComponentStateTransfer.TryRestoreExact(m_World, m_AuthorityPositions, in m_AuthorityPositionBinding,
                    m_AuthorityVelocities, in m_AuthorityVelocityBinding, baselineTick))
                    throw new InvalidOperationException("Prechecked paired restoration was rejected.");
                RequireAccepted(m_PositionHistory.TruncateAfter(in m_PositionBinding, baselineTick));
                RequireAccepted(m_VelocityHistory.TruncateAfter(in m_VelocityBinding, baselineTick));
                RequireAccepted(m_PositionHistory.TryRecord(in m_PositionBinding, baselineTick, in position));
                RequireAccepted(m_VelocityHistory.TryRecord(in m_VelocityBinding, baselineTick, in velocity));
                int replayed = m_Loop.Replay(baselineTick, targetTick, maxReplayTicks);
                if ((uint)replayed != targetTick - baselineTick)
                    throw new InvalidOperationException("The sample did not replay the complete prechecked interval.");
                return new CorrectionResult(CorrectionOutcome.Corrected);
            }
            catch { m_RequiresRecovery = true; throw; }
            finally { m_Busy = false; }
        }

        private CorrectionReason Precheck(uint baselineTick, uint targetTick, int budget,
            out CorrectionPosition position, out CorrectionVelocity velocity, out bool matched)
        {
            position = default;
            velocity = default;
            matched = false;
            if (!ScopeMatches()) return CorrectionReason.ScopeChanged;
            if (m_AuthorityPair.Tick == 0) return CorrectionReason.AuthorityUnavailable;
            if (m_AuthorityPair.Epoch != m_Epoch) return CorrectionReason.AuthorityEpochMismatch;
            if (m_AuthorityPair.Tick != baselineTick) return CorrectionReason.AuthorityTickMismatch;
            bool hasPosition = m_AuthorityPositions.TryGet(in m_AuthorityPositionBinding, out uint positionTick, out position);
            bool hasVelocity = m_AuthorityVelocities.TryGet(in m_AuthorityVelocityBinding, out uint velocityTick, out velocity);
            if (!hasPosition || !hasVelocity) return CorrectionReason.AuthorityUnavailable;
            if (positionTick != baselineTick || velocityTick != baselineTick) return CorrectionReason.AuthorityTickMismatch;
            if (position.Units != m_AuthorityPair.Position.Units || velocity.Units != m_AuthorityPair.Velocity.Units)
                return CorrectionReason.AuthorityUnavailable;
            // Bound the scan before visiting inputs; loop validation itself performs no mutation.
            try { m_Loop.ValidateReplay(baselineTick, targetTick, budget); }
            catch (ArgumentException) { return CorrectionReason.ReplayRejected; }
            catch (InvalidOperationException) { return CorrectionReason.ReplayRejected; }
            var positionResult = m_PositionHistory.VerifyPrediction(m_AuthorityPositions,
                in m_AuthorityPositionBinding, in m_PositionBinding);
            var velocityResult = m_VelocityHistory.VerifyPrediction(m_AuthorityVelocities,
                in m_AuthorityVelocityBinding, in m_VelocityBinding);
            // Evaluate BOTH verifications before deciding; mismatch cannot hide a missing second anchor.
            if (!Comparable(positionResult) || !Comparable(velocityResult)) return CorrectionReason.PredictionUnavailable;
            uint tick = baselineTick;
            while (tick < targetTick)
            {
                tick++;
                if (!TryReadInput(tick, out _)) return CorrectionReason.InputUnavailable;
            }
            matched = positionResult == PredictionVerificationResult.Match && velocityResult == PredictionVerificationResult.Match;
            return CorrectionReason.None;
        }

        private bool ScopeMatches()
        {
            return !m_World.IsDisposed && m_World.EntityCount == 1 && m_World.IsAlive(m_Entity)
                && !m_World.IsDespawning(m_Entity) && m_World.PendingDespawnCount == 0
                && m_World.TryGetComponentLifetime<CorrectionPosition>(m_Entity, out ulong positionLifetime)
                && positionLifetime == m_PositionLifetime
                && m_World.TryGetComponentLifetime<CorrectionVelocity>(m_Entity, out ulong velocityLifetime)
                && velocityLifetime == m_VelocityLifetime
                && ReferenceEquals(m_Loop.World, m_World) && ReferenceEquals(m_Loop.TickCompletedSink, m_Capture)
                && m_Clock.TickRate == TickRate && m_Clock.TickInterval == TickSeconds
                && m_Clock.MaxTicksPerFrame == 1 && m_Clock.MaxFrameDelta == TickSeconds;
        }

        private bool TryReadInput(uint tick, out int acceleration)
        {
            var sample = m_Inputs[InputIndex(tick)];
            acceleration = sample.Acceleration;
            return tick != 0 && sample.Tick == tick && sample.Epoch == m_Epoch
                && acceleration >= -MaximumAcceleration && acceleration <= MaximumAcceleration;
        }

        private int InputIndex(uint tick) => (int)(tick % (uint)m_Inputs.Length);

        private void RecordBoth(uint tick)
        {
            var position = m_World.Get<CorrectionPosition>(m_Entity);
            var velocity = m_World.Get<CorrectionVelocity>(m_Entity);
            RequireAccepted(m_PositionHistory.TryRecord(in m_PositionBinding, tick, in position));
            RequireAccepted(m_VelocityHistory.TryRecord(in m_VelocityBinding, tick, in velocity));
        }

        // Stateless: readonly wiring is not hidden simulation state. No random source, cached
        // input, accumulated time, participant list or mutable system configuration exists.
        private sealed class SimulationSystem : ISystem
        {
            private readonly PredictionCorrectionScenario m_Owner;
            public SimulationSystem(PredictionCorrectionScenario owner) => m_Owner = owner;
            public void Update(World world, CommandBuffer commands, in EcsTime time)
            {
                if (!ReferenceEquals(world, m_Owner.m_World) || time.DeltaTime != TickSeconds
                    || time.TickInterval != TickSeconds || time.Alpha != 0)
                    throw new InvalidOperationException("Unexpected simulation scope or fixed-step time.");
                if (!m_Owner.TryReadInput(time.Tick, out int acceleration))
                    throw new InvalidOperationException("Exact current-epoch input is unavailable during execution.");
                var position = world.Get<CorrectionPosition>(m_Owner.m_Entity);
                var velocity = world.Get<CorrectionVelocity>(m_Owner.m_Entity);
                int nextVelocity = checked(velocity.Units + acceleration);
                int nextPosition = checked(position.Units + nextVelocity);
                world.Set(m_Owner.m_Entity, new CorrectionPosition(nextPosition));
                world.Set(m_Owner.m_Entity, new CorrectionVelocity(nextVelocity));
            }
        }

        private sealed class CaptureSink : IEcsTickCompletedSink
        {
            private readonly PredictionCorrectionScenario m_Owner;
            public CaptureSink(PredictionCorrectionScenario owner) => m_Owner = owner;
            public void OnTickCompleted(World world, in EcsTime time)
            {
                if (!ReferenceEquals(world, m_Owner.m_World))
                    throw new InvalidOperationException("Unexpected capture World.");
                m_Owner.RecordBoth(time.Tick); // Reads World copies; never writes live components.
            }
        }

        private static bool Comparable(PredictionVerificationResult result)
            => result == PredictionVerificationResult.Match || result == PredictionVerificationResult.Mismatch;
        private static CorrectionResult NoChange(CorrectionReason reason)
            => new CorrectionResult(CorrectionOutcome.ResyncRequiredNoChange, reason);
        private static void RequireAccepted(PredictionHistoryWriteResult result)
        {
            if (result != PredictionHistoryWriteResult.Accepted)
                throw new InvalidOperationException("Prediction history rejected sample work: " + result);
        }
        private static void RequireAccepted(BaselineWriteResult result)
        {
            if (result != BaselineWriteResult.Accepted)
                throw new InvalidOperationException("Authority store rejected pair publication: " + result);
        }
        private static void ValidateCapacity(int capacity, string name)
        {
            if (capacity < 1 || capacity > MaximumCapacity) throw new ArgumentOutOfRangeException(name);
        }
        private void RequireUsable()
        {
            RequireOwner();
            if (m_Disposed) throw new ObjectDisposedException(nameof(PredictionCorrectionScenario));
        }
        private void RequireIdle()
        {
            RequireUsable();
            if (m_Busy) throw new InvalidOperationException("Cannot reenter the sample during execution.");
            if (m_RequiresRecovery) throw new InvalidOperationException("Recreate the sample from a coherent resync; execution failed.");
        }
        private void RequireOwner()
        {
            if (Environment.CurrentManagedThreadId != m_OwnerThread)
                throw new InvalidOperationException("Use the sample only on its constructing thread.");
        }

        public void Dispose()
        {
            RequireOwner();
            if (m_Disposed) return;
            if (m_Busy) throw new InvalidOperationException("Cannot dispose a running correction sample.");
            m_Loop.Dispose();
            m_PositionHistory.Dispose();
            m_VelocityHistory.Dispose();
            m_AuthorityPositions.Dispose();
            m_AuthorityVelocities.Dispose();
            m_World.Dispose();
            m_Disposed = true;
        }
    }
}
