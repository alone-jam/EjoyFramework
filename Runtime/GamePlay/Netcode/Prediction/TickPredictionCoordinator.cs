using System;
using EjoyFramework.Core;
using EjoyFramework.Core.Ecs;

namespace EjoyFramework.GamePlay.Netcode.Prediction
{
    public enum PredictionCorrectionOutcome { Matched, Corrected, IgnoredOld, WrongEpoch, ResyncRequired }
    public enum PredictionResyncReason { None, AuthorityAhead, HistoryUnavailable, ReplayBudget }
    public readonly struct PredictionCorrectionResult
    {
        public PredictionCorrectionOutcome Outcome { get; }
        public PredictionResyncReason Reason { get; }
        public int ReplayedTicks { get; }
        public PredictionCorrectionResult(PredictionCorrectionOutcome outcome, PredictionResyncReason reason = PredictionResyncReason.None, int replayedTicks = 0)
        { Outcome = outcome; Reason = reason; ReplayedTicks = replayedTicks; }
    }

    /// <summary>
    /// Fixed-tick prediction for one locally owned entity with one complete, unmanaged value-state
    /// component. Borrows World/entity/systems; owns loop, exact input history and state histories.
    /// Systems must derive all mutable simulation state from TState and recorded TCommand; native
    /// adapters must restore their pose from value state on each step. Not a multi-entity or physics
    /// world rollback service. Authority must be a fully decoded, authenticated-by-caller snapshot.
    /// No IO is performed; forward-only effects never run during correction replay.
    /// </summary>
    public sealed class TickPredictionCoordinator<TCommand, TState> : IDisposable
        where TCommand : unmanaged where TState : unmanaged, IPredicted<TState>
    {
        private readonly int owner = Environment.CurrentManagedThreadId;
        private readonly World world;
        private readonly Entity entity;
        private readonly ulong stateLifetime, commandLifetime;
        private readonly TickClock clock;
        private readonly EcsLoop loop;
        private readonly RingBuffer<TCommand> inputs;
        private readonly PredictionHistoryStore<TState> predictions;
        private readonly AuthoritativeBaselineStore<TState> authority;
        private PredictionHistoryStore<TState>.Binding predictionBinding;
        private AuthoritativeBaselineStore<TState>.Binding authorityBinding;
        private readonly int replayBudget;
        private bool busy, disposed, started, faulted;
        private ulong epoch;
        private uint lastAuthorityTick, lastAcknowledgedTick;
        public ulong Epoch { get { Check(); return epoch; } }
        public uint Tick { get { Check(); return clock.Tick; } }
        public uint AcknowledgedTick { get { Check(); return lastAcknowledgedTick; } }
        public bool RequiresRecovery { get { Check(); return faulted; } }
        public TState Current { get { Check(); Scope(); return world.Get<TState>(entity); } }
        public long ForwardTicks { get; private set; }
        public long ReplayTicks { get; private set; }
        public long Corrections { get; private set; }
        public long Matches { get; private set; }
        public long ResyncRequests { get; private set; }

        public TickPredictionCoordinator(World world, Entity entity, int tickRate = 60, int historyCapacity = 128, int maxReplayTicks = 128)
        {
            if (typeof(TCommand) == typeof(TState)) throw new ArgumentException("Command and state must be different component types.");
            this.world = world ?? throw new ArgumentNullException(nameof(world)); this.entity = entity;
            if (historyCapacity < 2 || historyCapacity > 4096) throw new ArgumentOutOfRangeException(nameof(historyCapacity));
            if (maxReplayTicks < 1 || maxReplayTicks > 4096) throw new ArgumentOutOfRangeException(nameof(maxReplayTicks));
            if (tickRate < 1 || tickRate > 1000) throw new ArgumentOutOfRangeException(nameof(tickRate));
            if (!world.TryGetComponentLifetime<TState>(entity, out stateLifetime) || !world.TryGetComponentLifetime<TCommand>(entity, out commandLifetime))
                throw new ArgumentException("Prediction requires existing state and command components.");
            Scope(); replayBudget = maxReplayTicks;
            clock = new TickClock(tickRate, 1, 1d / tickRate); loop = new EcsLoop(world, clock);
            inputs = new RingBuffer<TCommand>(historyCapacity, RingBufferOverflow.Overwrite);
            predictions = new PredictionHistoryStore<TState>(world, entity.Index + 1, historyCapacity);
            authority = new AuthoritativeBaselineStore<TState>(world, entity.Index + 1);
            loop.Add(EcsPhase.TickInput, new InputSystem(this), replayable: true);
            loop.TickCompletedSink = new Capture(this);
        }
        /// <summary>Configure before first Reset. Replay eligibility is an explicit purity contract.</summary>
        public void AddSimulation(ISystem system, int order = 0)
        { Configure(); loop.Add(EcsPhase.TickSimulation, system, order, replayable: true); }
        /// <summary>Notification/IO/FX consumers run once on forward ticks, never on replay.</summary>
        public void AddForwardOnly(ISystem system, int order = 0)
        { Configure(); loop.Add(EcsPhase.TickResolve, system, order, replayable: false); }
        private void Configure() { Idle(); if (started) throw new InvalidOperationException("Prediction configuration is sealed."); }

        /// <summary>
        /// Explicit full-state resync/lifecycle admission, not an implicit fallback to a nearby tick.
        /// Clears inputs/history and clock fraction. Caller must restore any external native state not
        /// restored by its adapter. A new session must retain its original epoch on queued messages.
        /// Recreate this coordinator after component membership/ownership scope changes.
        /// </summary>
        public void Reset(ulong sessionEpoch, uint tick, in TState state)
        {
            Idle(allowFault: true); Scope();
            if (sessionEpoch == 0) throw new ArgumentOutOfRangeException(nameof(sessionEpoch));
            if (tick == 0 || tick == uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(tick));
            busy = true;
            try
            {
                predictions.ResetTimeline(); authority.ResetTimeline();
                predictionBinding = predictions.Bind(entity); authorityBinding = authority.Bind(entity);
                world.Get<TState>(entity) = state; world.Get<TCommand>(entity) = default;
                inputs.Clear(unchecked((int)(tick + 1))); loop.ResetClock(tick);
                Record(tick); Require(authority.TrySet(in authorityBinding, tick, in state));
                epoch = sessionEpoch; lastAuthorityTick = lastAcknowledgedTick = tick; started = true; faulted = false;
            }
            catch { faulted = true; throw; }
            finally { busy = false; }
        }
        /// <summary>Stores one immutable input and completes exactly one next tick; no render delta.</summary>
        public void Predict(in TCommand command)
        {
            Idle(); Scope(); if (!started) throw new InvalidOperationException("Reset from a complete authority state first.");
            uint next = checked(clock.Tick + 1);
            loop.ValidateReplay(clock.Tick, clock.Tick, replayBudget);
            if (inputs.NextSequence != unchecked((int)next)) throw new InvalidOperationException("Input timeline diverged.");
            busy = true;
            try
            {
                inputs.Push(command);
                if (loop.RunFrame(clock.TickInterval) != 1 || clock.Tick != next) throw new InvalidOperationException("Prediction must complete one tick.");
                ForwardTicks++;
            }
            catch { faulted = true; throw; }
            finally { busy = false; }
        }
        public bool TryGetCommand(uint tick, out TCommand command)
        { Check(); return inputs.TryGetAtSequence(unchecked((int)tick), out command); }
        /// <summary>All new commands plus a bounded redundant tail, clipped to retained/unacknowledged input.
        /// A catch-up batch must not skip its early new commands merely because the current head advanced.</summary>
        public bool TryGetSendRange(uint lastSentTick, int redundancyTicks, out uint first, out uint last)
        {
            Check(); Scope(); first = last = 0;
            if (!started || lastSentTick > clock.Tick || redundancyTicks < 0 || redundancyTicks > 4096)
                throw new ArgumentOutOfRangeException();
            if (inputs.IsEmpty) return false;
            ulong candidate = (ulong)lastSentTick + 1 - Math.Min((ulong)lastSentTick, (ulong)redundancyTicks);
            candidate = Math.Max(candidate, (ulong)lastAcknowledgedTick + 1);
            candidate = Math.Max(candidate, unchecked((uint)inputs.HeadSequence));
            if (candidate > clock.Tick) return false;
            first = (uint)candidate; last = clock.Tick; return true;
        }
        public bool TryGetPrediction(uint tick, out TState state)
        { Check(); if (!started) { state = default; return false; } return predictions.TryGet(in predictionBinding, tick, out state); }

        /// <summary>
        /// Ignores old/duplicate snapshots and rejects other epochs without mutation. Ack is the
        /// highest resolved input tick represented by this snapshot (never a transport ACK), <= its
        /// state tick and monotonic. Missing exact input/state history returns ResyncRequired without
        /// changing state/clock/history/ack. No current input substitutes for a missing historical tick.
        /// Once mutation begins, exceptions fault the coordinator; explicit Reset is then mandatory.
        /// </summary>
        public PredictionCorrectionResult Reconcile(ulong sessionEpoch, uint tick, uint acknowledgedTick, in TState state)
        {
            Idle(); Scope(); if (!started) throw new InvalidOperationException("Prediction is not initialized.");
            if (sessionEpoch != epoch) return new PredictionCorrectionResult(PredictionCorrectionOutcome.WrongEpoch);
            if (tick == 0 || acknowledgedTick > tick) throw new ArgumentOutOfRangeException(nameof(acknowledgedTick));
            if (tick <= lastAuthorityTick) return new PredictionCorrectionResult(PredictionCorrectionOutcome.IgnoredOld);
            if (acknowledgedTick < lastAcknowledgedTick) throw new ArgumentOutOfRangeException(nameof(acknowledgedTick));
            if (tick > clock.Tick) return Resync(PredictionResyncReason.AuthorityAhead);
            if (!predictions.TryGet(in predictionBinding, tick, out var predicted)) return Resync(PredictionResyncReason.HistoryUnavailable);
            if (clock.Tick - tick > replayBudget) return Resync(PredictionResyncReason.ReplayBudget);
            for (uint t = tick; t < clock.Tick;)
                if (!inputs.ContainsSequence(unchecked((int)++t))) return Resync(PredictionResyncReason.HistoryUnavailable);
            loop.ValidateReplay(tick, clock.Tick, replayBudget);
            busy = true;
            try
            {
                var authoritativeCopy = state;
                bool matched = authoritativeCopy.VerifyPrediction(in predicted);
                Require(authority.TrySet(in authorityBinding, tick, in state));
                int replayed = 0;
                if (!matched)
                {
                    if (!ComponentStateTransfer.TryRestoreExact(world, authority, in authorityBinding, tick))
                        throw new InvalidOperationException("Prechecked authority restoration failed.");
                    Require(predictions.TruncateAfter(in predictionBinding, tick)); Record(tick);
                    replayed = loop.Replay(tick, clock.Tick, replayBudget);
                    ReplayTicks += replayed; Corrections++;
                }
                else Matches++;
                lastAuthorityTick = tick; lastAcknowledgedTick = acknowledgedTick;
                inputs.DiscardBefore(unchecked((int)(acknowledgedTick + 1)));
                return new PredictionCorrectionResult(matched ? PredictionCorrectionOutcome.Matched : PredictionCorrectionOutcome.Corrected,
                    replayedTicks: replayed);
            }
            catch { faulted = true; throw; }
            finally { busy = false; }
        }
        private PredictionCorrectionResult Resync(PredictionResyncReason reason)
        { ResyncRequests++; return new PredictionCorrectionResult(PredictionCorrectionOutcome.ResyncRequired, reason); }
        private void Record(uint tick)
        { var value = world.Get<TState>(entity); Require(predictions.TryRecord(in predictionBinding, tick, in value)); }
        private static void Require(PredictionHistoryWriteResult result)
        { if (result != PredictionHistoryWriteResult.Accepted) throw new InvalidOperationException("Prediction history rejected: " + result); }
        private static void Require(BaselineWriteResult result)
        { if (result != BaselineWriteResult.Accepted) throw new InvalidOperationException("Authority history rejected: " + result); }
        private void Scope()
        {
            if (world.IsDisposed || world.EntityCount != 1 || world.PendingDespawnCount != 0 || !world.IsAlive(entity) || world.IsDespawning(entity) ||
                !world.TryGetComponentLifetime<TState>(entity, out ulong s) || s != stateLifetime ||
                !world.TryGetComponentLifetime<TCommand>(entity, out ulong c) || c != commandLifetime)
                throw new InvalidOperationException("Prediction scope changed; recreate from complete authority state.");
        }
        private void Check()
        {
            if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Use prediction on its owner thread.");
            if (disposed) throw new ObjectDisposedException(nameof(TickPredictionCoordinator<TCommand, TState>));
        }
        private void Idle(bool allowFault = false)
        { Check(); if (busy || (!allowFault && faulted)) throw new InvalidOperationException("Prediction is busy or requires explicit recovery."); }
        public void Dispose()
        {
            if (disposed) return; Idle(allowFault: true);
            loop.Dispose(); predictions.Dispose(); authority.Dispose(); inputs.Clear(); disposed = true;
        }
        private sealed class InputSystem : ISystem
        {
            private readonly TickPredictionCoordinator<TCommand, TState> host;
            public InputSystem(TickPredictionCoordinator<TCommand, TState> host) { this.host = host; }
            public void Update(World world, CommandBuffer buffer, in EcsTime time)
            {
                if (!host.inputs.TryGetAtSequence(unchecked((int)time.Tick), out var command)) throw new InvalidOperationException("Exact recorded input missing.");
                world.Get<TCommand>(host.entity) = command;
            }
        }
        private sealed class Capture : IEcsTickCompletedSink
        {
            private readonly TickPredictionCoordinator<TCommand, TState> host;
            public Capture(TickPredictionCoordinator<TCommand, TState> host) { this.host = host; }
            public void OnTickCompleted(World world, in EcsTime time) => host.Record(time.Tick);
        }
    }
}
