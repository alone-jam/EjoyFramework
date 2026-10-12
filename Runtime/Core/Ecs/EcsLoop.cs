using System;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Single-thread phased loop; construct and use on the clock's owning thread. Mutating
    /// entry points enforce loop thread ownership. Owns its private groups, not the caller's
    /// World, Clock or systems. Do not share/modify the clock while this loop is in use.
    /// Steady-state dispatch allocates nothing; systems remain responsible for their own work.
    /// </summary>
    public sealed class EcsLoop : IDisposable
    {
        /// <summary>Bounds cleanup cascades at each tick-end or frame-end synchronization point.</summary>
        public const int MaxDespawnPassesPerSync = 4;

        private readonly World m_World;
        private readonly TickClock m_Clock;
        private readonly SystemGroup[] m_Groups;
        private readonly int m_OwnerThreadId = Environment.CurrentManagedThreadId;
        private bool m_Running;
        private bool m_Disposed;
        private IEcsTickCompletedSink m_TickCompletedSink;

        /// <summary>A dispatch/overflow failure requires explicit state recovery and ResetClock before another frame/replay.</summary>
        public bool IsFaulted { get; private set; }

        /// <summary>True while dispatching phases; an owning host must not shut down the World during dispatch.</summary>
        public bool IsRunning => m_Running;

        /// <summary>Borrowed world used by this loop; ownership remains with its caller.</summary>
        public World World
        {
            get
            {
                RequireOwnerThread();
                if (m_Disposed) throw new ObjectDisposedException(nameof(EcsLoop));
                return m_World;
            }
        }

        /// <summary>
        /// Borrowed capture sink, replaceable only between frames. Null disables capture.
        /// Runs outside system iteration but inside loop dispatch, so reentry and replacement
        /// are rejected. Also runs after each explicit replay tick, inside the value-only guard.
        /// This boundary does not enforce read-only World access; sinks must not mutate World values.
        /// </summary>
        public IEcsTickCompletedSink TickCompletedSink
        {
            get
            {
                RequireOwnerThread();
                if (m_Disposed) throw new ObjectDisposedException(nameof(EcsLoop));
                return m_TickCompletedSink;
            }
            set { RequireEditable(); m_TickCompletedSink = value; }
        }

        public EcsLoop(World world, TickClock clock)
        {
            m_World = world ?? throw new ArgumentNullException(nameof(world));
            m_Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            world.RequireNotDisposed();
            if (clock.PendingTicks != 0)
                throw new InvalidOperationException("Drain or reset the clock before assigning it to an EcsLoop.");
            m_Groups = new SystemGroup[(int)EcsPhase.Presentation + 1];
            for (int i = 0; i < m_Groups.Length; i++) m_Groups[i] = new SystemGroup(world, flushDespawns: false);
        }

        /// <summary>
        /// Stable order within a phase; an instance may be registered in different phases.
        /// Replay is opt-in for tick Input/Simulation/Resolve only. Eligibility is a caller
        /// contract for recorded inputs and absence of IO, not a side-effect sandbox.
        /// </summary>
        public void Add(EcsPhase phase, ISystem system, int order = 0, bool replayable = false)
        {
            RequireEditable();
            var group = GetGroup(phase);
            if (replayable && (phase < EcsPhase.TickInput || phase > EcsPhase.TickResolve))
                throw new ArgumentException("Only tick Input/Simulation/Resolve systems may opt into replay.", nameof(replayable));
            group.Add(system, order, replayable);
        }

        public bool Remove(EcsPhase phase, ISystem system)
        {
            RequireEditable();
            return GetGroup(phase).Remove(system);
        }

        public void SetEnabled(EcsPhase phase, ISystem system, bool enabled)
        {
            RequireEditable();
            GetGroup(phase).SetEnabled(system, enabled);
        }

        /// <summary>
        /// Accumulates once, consumes each tick through Input/Simulation/Resolve, then runs
        /// Interpolation/Presentation once. Returns completed ticks. Tick phases receive the
        /// fixed interval and zero alpha; frame phases receive the clamped frame delta, latest
        /// consumed tick and fractional alpha. Captured Despawn batches run at tick end and frame
        /// end, up to MaxDespawnPassesPerSync each; remaining requests wait for the next sync point.
        /// No automatic interpolation or replay is performed. TickCompletedSink observes each
        /// tick after its despawn sync; a sink failure faults the loop without rewinding time.
        /// Invalid input/entry conditions do not fault the loop or advance the clock.
        /// </summary>
        public int RunFrame(double realDeltaSeconds)
        {
            RequireEditable();
            m_World.RequireStructuralChange();
            if (IsFaulted)
                throw new InvalidOperationException("Recover simulation state and call ResetClock with an explicit baseline before RunFrame.");
            m_Clock.Accumulate(realDeltaSeconds);
            m_Running = true;
            try
            {
                int completed = 0;
                while (m_Clock.TryAdvance(out var tickTime))
                {
                    for (int i = (int)EcsPhase.TickInput; i <= (int)EcsPhase.TickResolve; i++)
                        m_Groups[i].Update(in tickTime);
                    ProcessDespawnSync(in tickTime);
                    m_TickCompletedSink?.OnTickCompleted(m_World, in tickTime);
                    completed++;
                }
                var frameTime = new EcsTime(m_Clock.Tick, Math.Min(realDeltaSeconds, m_Clock.MaxFrameDelta),
                    m_Clock.TickInterval, m_Clock.Alpha);
                m_Groups[(int)EcsPhase.Interpolation].Update(in frameTime);
                m_Groups[(int)EcsPhase.Presentation].Update(in frameTime);
                ProcessDespawnSync(in frameTime);
                return completed;
            }
            catch
            {
                // SystemGroup already cleared its unplayed commands. Prior world changes and
                // consumed ticks remain; pending ticks are preserved for diagnostics, not replay.
                IsFaulted = true;
                throw;
            }
            finally { m_Running = false; }
        }

        /// <summary>
        /// Read-only preflight for explicit value-only replay. Call BEFORE restoring a baseline;
        /// Replay rechecks it. Requires idle/nonfaulted loop, no World iteration/replay, no pending
        /// clock batch or commands in ANY owned group (including disabled/non-replay groups).
        /// Range is nonwrapping (baselineTick, targetTick], baseline is nonzero, target is the
        /// current real clock tick, and count must fit a positive budget. Does not validate caller
        /// input history, authority origin, baseline completeness or application state.
        /// </summary>
        public void ValidateReplay(uint baselineTick, uint targetTick, int maxReplayTicks = 128)
        {
            RequireEditable();
            m_World.RequireStructuralChange();
            if (IsFaulted) throw new InvalidOperationException("Recover simulation state and ResetClock before replay.");
            if (m_Clock.PendingTicks != 0)
                throw new InvalidOperationException("Drain or reset the real clock's pending batch before replay.");
            if (baselineTick == 0) throw new ArgumentOutOfRangeException(nameof(baselineTick));
            if (targetTick < baselineTick || targetTick != m_Clock.Tick)
                throw new ArgumentOutOfRangeException(nameof(targetTick), "Replay must end at the current tick without wrapping.");
            if (maxReplayTicks <= 0 || (ulong)targetTick - baselineTick > (ulong)maxReplayTicks)
                throw new ArgumentOutOfRangeException(nameof(maxReplayTicks), "Replay requires a positive sufficient tick budget.");
            RequireEmptyReplayBuffers();
        }

        /// <summary>
        /// Re-simulates (baselineTick, targetTick] with fixed delta, alpha zero and IsReplay=true.
        /// Executes eligible tick systems only, then the borrowed sink outside iteration but
        /// inside the value-only World guard. All owned command buffers must be empty before
        /// and after capture; cross-group/sink leakage faults replay rather than escaping to a
        /// later tick/frame. No despawn/presentation/IO phase or clock mutation.
        /// Empty valid ranges do nothing. Caller restores values/inputs and truncates prediction
        /// history first; sink remains nonmutating by contract. A failure faults the loop and
        /// discards unplayed owned commands, but does not undo values or rewind the real clock.
        /// Eligibility cannot enforce IO purity, native pointee safety or deterministic results.
        /// </summary>
        public int Replay(uint baselineTick, uint targetTick, int maxReplayTicks = 128)
        {
            ValidateReplay(baselineTick, targetTick, maxReplayTicks);
            if (baselineTick == targetTick) return 0;
            m_World.BeginReplay();
            m_Running = true;
            try
            {
                uint tick = baselineTick;
                while (tick < targetTick)
                {
                    tick++; // Comparison before increment permits uint.MaxValue without wrapping.
                    var time = new EcsTime(tick, m_Clock.TickInterval, m_Clock.TickInterval, 0, isReplay: true);
                    for (int i = (int)EcsPhase.TickInput; i <= (int)EcsPhase.TickResolve; i++)
                        m_Groups[i].UpdateReplay(in time);
                    // Catch writes queued into skipped or already-executed groups before capture.
                    RequireEmptyReplayBuffers();
                    m_TickCompletedSink?.OnTickCompleted(m_World, in time);
                    // A sink may also retain a borrowed buffer; never carry its writes forward.
                    RequireEmptyReplayBuffers();
                }
                return (int)(targetTick - baselineTick);
            }
            catch
            {
                IsFaulted = true;
                // Includes commands queued by a failing sink into another borrowed group buffer.
                for (int i = 0; i < m_Groups.Length; i++) m_Groups[i].DiscardReplayCommands();
                throw;
            }
            finally
            {
                m_World.EndReplay();
                m_Running = false;
            }
        }

        /// <summary>
        /// Clears the clock's pending batch, fraction and drop counters and clears the loop fault.
        /// Does not restore world state, replay work, or undo a partially executed tick. The caller
        /// must reconcile simulation state and select the baseline explicitly before continuing.
        /// </summary>
        public void ResetClock(uint tick)
        {
            RequireEditable();
            m_World.RequireStructuralChange();
            m_Clock.Reset(tick);
            IsFaulted = false;
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            RequireOwnerThread();
            if (m_Running) throw new InvalidOperationException("Cannot dispose a running EcsLoop.");
            if (!m_World.IsDisposed) m_World.RequireNotReplaying();
            for (int i = 0; i < m_Groups.Length; i++) m_Groups[i].Dispose();
            m_TickCompletedSink = null;
            m_Disposed = true;
        }

        private void RequireEmptyReplayBuffers()
        {
            for (int i = 0; i < m_Groups.Length; i++) m_Groups[i].ValidateReplay();
        }

        private void ProcessDespawnSync(in EcsTime time)
        {
            for (int pass = 0; pass < MaxDespawnPassesPerSync; pass++)
            {
                if (!m_World.BeginDespawnBatch()) return;
                try
                {
                    // OnlyDespawning sees the captured batch; new requests get a later cleanup pass.
                    m_Groups[(int)EcsPhase.Despawn].Update(in time);
                    m_World.CompleteDespawnBatch();
                }
                catch
                {
                    m_World.CancelDespawnBatch();
                    throw;
                }
            }
        }

        private SystemGroup GetGroup(EcsPhase phase)
        {
            int index = (int)phase;
            if (index < 0 || index >= m_Groups.Length) throw new ArgumentOutOfRangeException(nameof(phase));
            return m_Groups[index];
        }

        private void RequireEditable()
        {
            RequireOwnerThread();
            if (m_Disposed) throw new ObjectDisposedException(nameof(EcsLoop));
            if (m_Running) throw new InvalidOperationException("Cannot modify or reenter a running EcsLoop.");
            m_World.RequireNotDisposed();
        }

        private void RequireOwnerThread()
        {
            if (Environment.CurrentManagedThreadId != m_OwnerThreadId)
                throw new InvalidOperationException("Use EcsLoop only on its constructing thread.");
        }
    }
}
