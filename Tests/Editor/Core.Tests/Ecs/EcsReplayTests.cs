using System;
using System.Collections.Generic;
using System.Threading;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;

namespace EjoyFramework.Tests.Ecs
{
    public class EcsReplayTests
    {
        private struct Position : IPredicted<Position>
        {
            public int Value;
            public bool VerifyPrediction(in Position predicted) => Value == predicted.Value;
        }
        private struct Velocity { public int Value; }
        private struct Unknown { }
        private sealed class System : ISystem, IDisposable
        {
            public Action<World, CommandBuffer, EcsTime> Callback;
            public bool Disposed;
            public void Update(World world, CommandBuffer commands, in EcsTime time) => Callback(world, commands, time);
            public void Dispose() => Disposed = true;
        }
        private sealed class Sink : IEcsTickCompletedSink, IDisposable
        {
            public Action<World, EcsTime> Callback;
            public bool Disposed;
            public void OnTickCompleted(World world, in EcsTime time) => Callback(world, time);
            public void Dispose() => Disposed = true;
        }

        private World m_World;
        private TickClock m_Clock;
        private EcsLoop m_Loop;
        private Entity m_Entity;

        [SetUp]
        public void SetUp()
        {
            m_World = new World(1);
            m_Entity = m_World.CreateEntity();
            m_World.Set(m_Entity, new Position { Value = 10 });
            m_World.Set(m_Entity, new Velocity { Value = 1 });
            m_World.SetPartition(m_Entity, 7);
            Assert.That(m_World.ReservePartitionKey(), Is.Zero);
            m_Clock = new TickClock(8, 2, 0.9375);
            m_Clock.Reset(10);
            m_Clock.Accumulate(0.9375);
            while (m_Clock.TryAdvance(out _)) { }
            m_Loop = new EcsLoop(m_World, m_Clock); // Tick 12, Alpha .5, DroppedTicks 5.
        }

        [TearDown]
        public void TearDown() { m_Loop.Dispose(); m_World.Dispose(); }

        private object[] ClockState() => new object[]
        {
            m_Clock.Tick, m_Clock.PendingTicks, m_Clock.Alpha, m_Clock.DroppedTicks,
            m_Clock.TickRate, m_Clock.TickInterval, m_Clock.MaxTicksPerFrame, m_Clock.MaxFrameDelta
        };

        private System Add(EcsPhase phase, Action<World, CommandBuffer, EcsTime> callback,
            int order = 0, bool replayable = true)
        {
            var system = new System { Callback = callback };
            m_Loop.Add(phase, system, order, replayable);
            return system;
        }

        [Test]
        public void ReplayUsesOnlyEligibleEnabledTickSystemsInStableOrderAndPreservesEveryClockField()
        {
            var before = ClockState();
            var order = new List<string>();
            var times = new List<EcsTime>();
            Add(EcsPhase.TickInput, (w, c, t) => { order.Add("input"); times.Add(t); });
            Add(EcsPhase.TickSimulation, (w, c, t) => order.Add("late-a"), 10);
            Add(EcsPhase.TickSimulation, (w, c, t) => order.Add("first"), -1);
            Add(EcsPhase.TickSimulation, (w, c, t) => order.Add("late-b"), 10);
            Add(EcsPhase.TickResolve, (w, c, t) => order.Add("resolve"));
            var disabled = Add(EcsPhase.TickInput, (w, c, t) => Assert.Fail("Disabled system ran."));
            m_Loop.SetEnabled(EcsPhase.TickInput, disabled, false);
            var removed = Add(EcsPhase.TickResolve, (w, c, t) => Assert.Fail("Removed system ran."));
            Assert.That(m_Loop.Remove(EcsPhase.TickResolve, removed), Is.True);
            foreach (var phase in new[] { EcsPhase.TickInput, EcsPhase.TickSimulation, EcsPhase.TickResolve,
                EcsPhase.Interpolation, EcsPhase.Presentation, EcsPhase.Despawn })
                m_Loop.Add(phase, new System { Callback = (w, c, t) => Assert.Fail("Non-replay system ran.") });
            m_Loop.TickCompletedSink = new Sink { Callback = (w, t) => order.Add("capture") };
            m_World.RequestDespawn(m_Entity);
            m_Loop.ValidateReplay(10, 12);
            Assert.That(m_Loop.Replay(10, 12), Is.EqualTo(2));
            Assert.That(order, Is.EqualTo(new[] { "input", "first", "late-a", "late-b", "resolve", "capture",
                "input", "first", "late-a", "late-b", "resolve", "capture" }));
            for (int i = 0; i < times.Count; i++)
            {
                Assert.That(times[i].Tick, Is.EqualTo((uint)(11 + i)));
                Assert.That(times[i].DeltaTime, Is.EqualTo(0.125));
                Assert.That(times[i].TickInterval, Is.EqualTo(0.125));
                Assert.That(times[i].Alpha, Is.Zero);
                Assert.That(times[i].ElapsedTime, Is.EqualTo((11 + i) * 0.125));
                Assert.That(times[i].IsReplay, Is.True);
            }
            Assert.That(ClockState(), Is.EqualTo(before));
            Assert.That(m_World.IsAlive(m_Entity), Is.True);
            Assert.That(m_World.PendingDespawnCount, Is.EqualTo(1));
            Assert.That(m_Loop.IsFaulted || m_Loop.IsRunning, Is.False);
        }

        [TestCase(EcsPhase.Interpolation)]
        [TestCase(EcsPhase.Presentation)]
        [TestCase(EcsPhase.Despawn)]
        public void NonTickPhasesCannotOptIn(EcsPhase phase)
        {
            var system = new System { Callback = (w, c, t) => Assert.Fail("Invalid registration survived.") };
            Assert.Throws<ArgumentException>(() => m_Loop.Add(phase, system, replayable: true));
            Assert.That(m_Loop.Replay(10, 12), Is.EqualTo(2));
        }

        [Test]
        public void EmptyValidRangeDoesNotDispatchOrCapture()
        {
            var before = ClockState();
            Add(EcsPhase.TickSimulation, (w, c, t) => Assert.Fail("Empty replay ran a system."));
            m_Loop.TickCompletedSink = new Sink { Callback = (w, t) => Assert.Fail("Empty replay captured.") };
            m_Loop.ValidateReplay(12, 12);
            Assert.That(m_Loop.Replay(12, 12), Is.Zero);
            Assert.That(ClockState(), Is.EqualTo(before));
        }

        [TestCase(0u, 12u, 128)]
        [TestCase(10u, 11u, 128)]
        [TestCase(10u, 13u, 128)]
        [TestCase(13u, 12u, 128)]
        [TestCase(uint.MaxValue, 12u, 128)]
        [TestCase(10u, 12u, 0)]
        [TestCase(10u, 12u, -1)]
        [TestCase(10u, 12u, 1)]
        public void InvalidRangesAndBudgetsRejectBeforeWorldClockOrFaultMutation(uint baseline, uint target, int budget)
        {
            var before = ClockState();
            Add(EcsPhase.TickInput, (w, c, t) => Assert.Fail("Invalid replay dispatched."));
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Loop.ValidateReplay(baseline, target, budget));
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Loop.Replay(baseline, target, budget));
            Assert.That(ClockState(), Is.EqualTo(before));
            Assert.That(m_World.Get<Position>(m_Entity).Value, Is.EqualTo(10));
            Assert.That(m_Loop.IsFaulted || m_Loop.IsRunning, Is.False);
        }

        [Test]
        public void PendingRealClockBatchRejectsEvenEmptyReplayWithoutConsumingIt()
        {
            m_Clock.Accumulate(0.125);
            var before = ClockState();
            Assert.Throws<InvalidOperationException>(() => m_Loop.ValidateReplay(12, 12));
            Assert.Throws<InvalidOperationException>(() => m_Loop.Replay(10, 12));
            Assert.That(ClockState(), Is.EqualTo(before));
            Assert.That(m_Loop.IsFaulted, Is.False);
        }

        [Test]
        public void MaxTickIsInclusiveWithoutWrapAndExplicitBudgetCanPermitLargeReplay()
        {
            m_Loop.ResetClock(uint.MaxValue);
            var ticks = new List<uint>();
            var system = Add(EcsPhase.TickInput, (w, c, t) => ticks.Add(t.Tick));
            Assert.That(m_Loop.Replay(uint.MaxValue - 2, uint.MaxValue, 2), Is.EqualTo(2));
            Assert.That(ticks, Is.EqualTo(new[] { uint.MaxValue - 1, uint.MaxValue }));
            m_Loop.Remove(EcsPhase.TickInput, system);
            m_Loop.ResetClock(10001);
            int calls = 0;
            Add(EcsPhase.TickInput, (w, c, t) => calls++);
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Loop.ValidateReplay(1, 10001));
            Assert.That(m_Loop.Replay(1, 10001, 10000), Is.EqualTo(10000));
            Assert.That(calls, Is.EqualTo(10000));
            Assert.That(m_Clock.Tick, Is.EqualTo(10001));
        }

        [Test]
        public void ExistingValueWritesAndPerSystemDeferredPlaybackAreAllowedWithoutChangingMembership()
        {
            m_World.TryGetComponentLifetime<Position>(m_Entity, out ulong lifetime);
            Add(EcsPhase.TickInput, (w, c, t) => w.Set(m_Entity, new Position { Value = (int)t.Tick }));
            Add(EcsPhase.TickSimulation, (w, c, t) =>
            {
                w.Get<Position>(m_Entity).Value += 10;
                c.Set(m_Entity, new Velocity { Value = 7 });
                Assert.That(w.Get<Velocity>(m_Entity).Value, Is.EqualTo(t.Tick == 11 ? 1 : 7));
            });
            Add(EcsPhase.TickSimulation, (w, c, t) => Assert.That(w.Get<Velocity>(m_Entity).Value, Is.EqualTo(7)));
            m_Loop.Replay(10, 12);
            Assert.That(m_World.Get<Position>(m_Entity).Value, Is.EqualTo(22));
            m_World.TryGetComponentLifetime<Position>(m_Entity, out ulong after);
            Assert.That(after, Is.EqualTo(lifetime));
        }

        private static IEnumerable<TestCaseData> StructuralCases()
        {
            var paths = new[] { "create", "destroy", "set-missing", "remove", "remove-missing",
                "reserve-entities", "reserve-components", "reserve-zero", "dispose-world",
                "partition-set", "partition-clear", "partition-reserve", "partition-release",
                "despawn", "despawn-default", "despawn-partition", "despawn-zero", "despawn-process",
                "command-create", "command-destroy", "command-remove", "command-set-missing", "command-playback",
                "command-dispose", "query-all", "query-any", "query-none" };
            foreach (var path in paths)
                foreach (bool inSystem in new[] { false, true }) yield return new TestCaseData(path, inSystem);
        }

        private void StructuralOperation(string path, CommandBuffer commands)
        {
            switch (path)
            {
                case "create": m_World.CreateEntity(); break;
                case "destroy": m_World.DestroyEntity(m_Entity); break;
                case "set-missing": m_World.Set(m_Entity, new Unknown()); break;
                case "remove": m_World.Remove<Position>(m_Entity); break;
                case "remove-missing": m_World.Remove<Unknown>(m_Entity); break;
                case "reserve-entities": m_World.EnsureEntityCapacity(100); break;
                case "reserve-components": m_World.EnsureCapacity<Unknown>(100); break;
                case "reserve-zero": m_World.EnsureCapacity<Position>(0); break;
                case "dispose-world": m_World.Dispose(); break;
                case "partition-set": m_World.SetPartition(m_Entity, 8); break;
                case "partition-clear": m_World.ClearPartition(m_Entity); break;
                case "partition-reserve": m_World.ReservePartitionKey(); break;
                case "partition-release": m_World.ReleasePartitionKey(0); break;
                case "despawn": m_World.RequestDespawn(m_Entity); break;
                case "despawn-default": m_World.RequestDespawn(default); break;
                case "despawn-partition": m_World.RequestDespawnPartition(7); break;
                case "despawn-zero": m_World.RequestDespawnPartition(7, 0); break;
                case "despawn-process": m_World.ProcessDespawns(); break;
                case "command-create": commands.CreateEntity(); break;
                case "command-destroy": commands.DestroyEntity(m_Entity); break;
                case "command-remove": commands.Remove<Position>(m_Entity); break;
                case "command-set-missing": commands.Set(m_Entity, new Unknown()); break;
                case "command-playback": commands.Playback(); break;
                case "command-dispose": commands.Dispose(); break;
                case "query-all": m_World.Query().WithAll<Unknown>(); break;
                case "query-any": m_World.Query().WithAny<Unknown>(); break;
                case "query-none": m_World.Query().WithNone<Unknown>(); break;
                default: throw new ArgumentOutOfRangeException(nameof(path));
            }
        }

        [TestCaseSource(nameof(StructuralCases))]
        public void EveryStructuralPathRejectsBeforeSideEffectsInSystemsAndOutsideIterationSink(string path, bool inSystem)
        {
            using var commands = new CommandBuffer(m_World);
            m_World.TryGetComponentLifetime<Position>(m_Entity, out ulong lifetime);
            Action attempt = () =>
            {
                Assert.Throws<InvalidOperationException>(() => StructuralOperation(path, commands));
                Assert.That(commands.Count, Is.Zero);
                // If failed Set/reserve/query had created an empty pool, this would stop throwing.
                Assert.Throws<InvalidOperationException>(() => m_World.Query().WithAll<Unknown>());
            };
            if (inSystem) Add(EcsPhase.TickSimulation, (w, c, t) => attempt());
            else m_Loop.TickCompletedSink = new Sink { Callback = (w, t) => attempt() };
            m_Loop.Replay(10, 12);
            Assert.That(m_World.EntityCount, Is.EqualTo(1));
            Assert.That(m_World.IsAlive(m_Entity), Is.True);
            Assert.That(m_World.Get<Position>(m_Entity).Value, Is.EqualTo(10));
            Assert.That(m_World.PendingDespawnCount, Is.Zero);
            Assert.That(m_World.GetPartitionCount(7), Is.EqualTo(1));
            Assert.That(m_World.GetPartitionCount(8), Is.Zero);
            Assert.That(m_World.ReleasePartitionKey(0), Is.True);
            Assert.That(m_World.ReservePartitionKey(), Is.EqualTo(1));
            m_World.TryGetComponentLifetime<Position>(m_Entity, out ulong after);
            Assert.That(after, Is.EqualTo(lifetime));
            var next = m_World.CreateEntity();
            Assert.That(next.Index, Is.EqualTo(1));
            Assert.That(next.Version, Is.EqualTo(1)); // Failed deferred create did not consume a reservation generation.
        }

        [Test]
        public void ClearingOldExternalReservationsIsRejectedDuringReplayButWorksAfterGuardUnwinds()
        {
            using var commands = new CommandBuffer(m_World);
            var reserved = commands.CreateEntity();
            m_Loop.TickCompletedSink = new Sink { Callback = (w, t) =>
            {
                Assert.Throws<InvalidOperationException>(() => commands.Clear());
                Assert.That(commands.Count, Is.EqualTo(1));
                Assert.That(w.IsAlive(reserved), Is.False);
            } };
            m_Loop.Replay(10, 12);
            commands.Playback();
            Assert.That(m_World.IsAlive(reserved), Is.True);
        }

        [TestCase(EcsPhase.TickInput)]
        [TestCase(EcsPhase.TickSimulation)]
        [TestCase(EcsPhase.TickResolve)]
        [TestCase(EcsPhase.Interpolation)]
        [TestCase(EcsPhase.Presentation)]
        [TestCase(EcsPhase.Despawn)]
        public void PendingBorrowedCommandsInAnyGroupRejectPreflightWithoutClearingOrExecuting(EcsPhase phase)
        {
            CommandBuffer borrowed = null;
            var system = Add(phase, (w, c, t) => borrowed = c, replayable: false);
            if (phase == EcsPhase.Despawn) m_World.RequestDespawn(m_World.CreateEntity());
            m_Loop.RunFrame(0.125);
            borrowed.Set(m_Entity, new Position { Value = 999 });
            var reserved = borrowed.CreateEntity();
            borrowed.Set(reserved, new Position { Value = 777 });
            m_Loop.SetEnabled(phase, system, false);
            var before = ClockState();
            Assert.Throws<InvalidOperationException>(() => m_Loop.ValidateReplay(12, 13));
            Assert.Throws<InvalidOperationException>(() => m_Loop.Replay(12, 13));
            Assert.That(borrowed.Count, Is.EqualTo(3));
            Assert.That(m_World.Get<Position>(m_Entity).Value, Is.EqualTo(10));
            Assert.That(m_World.IsAlive(reserved), Is.False);
            Assert.That(ClockState(), Is.EqualTo(before));
            Assert.That(m_Loop.IsFaulted, Is.False);
            borrowed.Clear();
            m_Loop.ValidateReplay(12, 13);
        }

        [Test]
        public void ReplayRechecksPreflightAfterCallerValidationEvenForValueOnlyPendingCommands()
        {
            CommandBuffer borrowed = null;
            Add(EcsPhase.Presentation, (w, c, t) => borrowed = c, replayable: false);
            m_Loop.RunFrame(0);
            m_Loop.ValidateReplay(10, 12);
            borrowed.Set(m_Entity, new Position { Value = 999 });
            Assert.Throws<InvalidOperationException>(() => m_Loop.Replay(10, 12));
            Assert.That(m_World.Get<Position>(m_Entity).Value, Is.EqualTo(10));
            Assert.That(borrowed.Count, Is.EqualTo(1));
            borrowed.Clear();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReplayRejectsReentryAndLoopChangesFromSystemsAndSink(bool fromSink)
        {
            using var other = new EcsLoop(m_World, m_Clock);
            Action attempt = () =>
            {
                Assert.That(m_Loop.IsRunning, Is.True);
                Assert.Throws<InvalidOperationException>(() => m_Loop.ValidateReplay(10, 12));
                Assert.Throws<InvalidOperationException>(() => m_Loop.Replay(10, 12));
                Assert.Throws<InvalidOperationException>(() => m_Loop.RunFrame(0));
                Assert.Throws<InvalidOperationException>(() => m_Loop.ResetClock(1));
                Assert.Throws<InvalidOperationException>(() => m_Loop.TickCompletedSink = null);
                Assert.Throws<InvalidOperationException>(() => m_Loop.Add(EcsPhase.TickInput, new System()));
                Assert.Throws<InvalidOperationException>(() => m_Loop.Remove(EcsPhase.TickInput, null));
                Assert.Throws<InvalidOperationException>(() => m_Loop.SetEnabled(EcsPhase.TickInput, null, false));
                Assert.Throws<InvalidOperationException>(() => m_Loop.Dispose());
                Assert.Throws<InvalidOperationException>(() => other.Replay(10, 12));
                Assert.Throws<InvalidOperationException>(() => other.RunFrame(0));
            };
            if (fromSink) m_Loop.TickCompletedSink = new Sink { Callback = (w, t) => attempt() };
            else Add(EcsPhase.TickInput, (w, c, t) => attempt());
            m_Loop.Replay(10, 12);
            Assert.That(m_Loop.IsRunning || m_Loop.IsFaulted, Is.False);
        }

        [Test]
        public void SystemFailureKeepsPriorValuesClearsUnplayedCommandsFaultsAndUnwindsForExplicitRecovery()
        {
            var before = ClockState();
            CommandBuffer borrowed = null;
            int later = 0, captures = 0;
            Add(EcsPhase.TickInput, (w, c, t) => c.Set(m_Entity, new Position { Value = 20 }));
            var failing = Add(EcsPhase.TickSimulation, (w, c, t) =>
            {
                borrowed = c;
                w.Get<Position>(m_Entity).Value++;
                c.Set(m_Entity, new Position { Value = 999 });
                throw new ApplicationException("Replay failed.");
            });
            Add(EcsPhase.TickResolve, (w, c, t) => later++);
            m_Loop.TickCompletedSink = new Sink { Callback = (w, t) => captures++ };
            Assert.Throws<ApplicationException>(() => m_Loop.Replay(10, 12));
            Assert.That(borrowed.Count, Is.Zero);
            Assert.That(m_World.Get<Position>(m_Entity).Value, Is.EqualTo(21));
            Assert.That(later + captures, Is.Zero);
            Assert.That(ClockState(), Is.EqualTo(before));
            Assert.That(m_Loop.IsFaulted, Is.True);
            Assert.That(m_Loop.IsRunning, Is.False);
            Assert.Throws<InvalidOperationException>(() => m_Loop.ValidateReplay(10, 12));
            Assert.Throws<InvalidOperationException>(() => m_Loop.Replay(10, 12));
            Assert.Throws<InvalidOperationException>(() => m_Loop.RunFrame(0));
            Assert.That(m_World.IsAlive(m_World.CreateEntity()), Is.True); // Both guards unwound.
            m_Loop.Remove(EcsPhase.TickSimulation, failing);
            m_Loop.ResetClock(12);
            Assert.That(m_Loop.Replay(11, 12), Is.EqualTo(1));
            Assert.That(later, Is.EqualTo(1));
            Assert.That(captures, Is.EqualTo(1));
            Assert.That(m_World.Get<Position>(m_Entity).Value, Is.EqualTo(20));
        }

        [TestCase(EcsPhase.Presentation, false)]
        [TestCase(EcsPhase.TickInput, false)]
        [TestCase(EcsPhase.Presentation, true)]
        [TestCase(EcsPhase.TickInput, true)]
        public void ReplayBoundaryFaultsOnCrossGroupQueuedWritesAndPreventsLaterForwardOverwrite(
            EcsPhase retainedPhase, bool leakFromSink)
        {
            CommandBuffer retained = null, current = null;
            Add(retainedPhase, (w, c, t) => retained = c, replayable: retainedPhase == EcsPhase.TickInput);
            m_Loop.RunFrame(0.125); // Retain a skipped Presentation or earlier TickInput buffer.
            var before = ClockState();
            int captures = 0, updates = 0;
            var producer = Add(EcsPhase.TickResolve, (w, c, t) =>
            {
                current = c;
                updates++;
                c.Set(m_Entity, new Velocity { Value = 7 }); // Normal current-group deferral remains valid.
                if (!leakFromSink) retained.Set(m_Entity, new Position { Value = 999 });
            });
            m_Loop.TickCompletedSink = new Sink { Callback = (w, t) =>
            {
                captures++;
                if (leakFromSink) retained.Set(m_Entity, new Position { Value = 999 });
            } };
            Assert.Throws<InvalidOperationException>(() => m_Loop.Replay(11, 13));
            Assert.That(updates, Is.EqualTo(1));
            Assert.That(captures, Is.EqualTo(leakFromSink ? 1 : 0));
            Assert.That(retained.Count, Is.Zero);
            Assert.That(current.Count, Is.Zero);
            Assert.That(m_World.Get<Position>(m_Entity).Value, Is.EqualTo(10));
            Assert.That(m_World.Get<Velocity>(m_Entity).Value, Is.EqualTo(7));
            Assert.That(m_Loop.IsFaulted, Is.True);
            Assert.That(m_Loop.IsRunning, Is.False);
            Assert.That(ClockState(), Is.EqualTo(before));
            m_Loop.SetEnabled(EcsPhase.TickResolve, producer, false);
            m_Loop.TickCompletedSink = null;
            m_Loop.ResetClock(13);
            Assert.DoesNotThrow(() => m_Loop.ValidateReplay(12, 13)); // Checks every owned buffer after cleanup.
            Assert.That(m_Loop.RunFrame(0.125), Is.EqualTo(1));
            Assert.That(m_World.Get<Position>(m_Entity).Value, Is.EqualTo(10));
            Assert.That(m_World.Get<Velocity>(m_Entity).Value, Is.EqualTo(7));
            Assert.That(retained.Count, Is.Zero);
            Assert.That(current.Count, Is.Zero);
        }

        [Test]
        public void SinkFailureStopsLaterTicksAndDiscardsCommandsQueuedIntoAnotherBorrowedGroup()
        {
            CommandBuffer borrowed = null;
            Add(EcsPhase.Presentation, (w, c, t) => borrowed = c, replayable: false);
            m_Loop.RunFrame(0);
            var before = ClockState();
            int calls = 0;
            Add(EcsPhase.TickSimulation, (w, c, t) => { w.Get<Position>(m_Entity).Value++; calls++; });
            m_Loop.TickCompletedSink = new Sink { Callback = (w, t) =>
            {
                borrowed.Set(m_Entity, new Position { Value = 999 });
                throw new ApplicationException("Capture failed.");
            } };
            Assert.Throws<ApplicationException>(() => m_Loop.Replay(10, 12));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(borrowed.Count, Is.Zero);
            Assert.That(m_World.Get<Position>(m_Entity).Value, Is.EqualTo(11));
            Assert.That(m_Loop.IsRunning, Is.False);
            Assert.That(m_Loop.IsFaulted, Is.True);
            Assert.That(ClockState(), Is.EqualTo(before));
            Assert.DoesNotThrow(() => m_World.SetPartition(m_Entity, 8));
        }

        [Test]
        public void PredictionCaptureRunsOutsideIterationInsideReplayGuardWithReplayMetadata()
        {
            using var history = new PredictionHistoryStore<Position>(m_World, 1, 4);
            var binding = history.Bind(m_Entity);
            int captures = 0;
            Add(EcsPhase.TickSimulation, (w, c, t) => w.Get<Position>(m_Entity).Value++);
            m_Loop.TickCompletedSink = new Sink { Callback = (w, t) =>
            {
                Assert.That(t.IsReplay, Is.True);
                Assert.That(t.Alpha, Is.Zero);
                var value = w.Get<Position>(m_Entity);
                Assert.That(history.TryRecord(in binding, t.Tick, in value), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
                Assert.That(history.TruncateAfter(in binding, t.Tick), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
                Assert.Throws<InvalidOperationException>(() => history.EnsureCapacity(0));
                Assert.Throws<InvalidOperationException>(() => history.Bind(m_Entity));
                Assert.Throws<InvalidOperationException>(() => history.Unbind(in binding));
                Assert.Throws<InvalidOperationException>(() => history.ResetTimeline());
                Assert.Throws<InvalidOperationException>(() => history.Dispose());
                captures++;
            } };
            m_Loop.Replay(10, 12);
            Assert.That(captures, Is.EqualTo(2));
            Assert.That(history.TryGet(in binding, 11, out var first), Is.True);
            Assert.That(history.TryGet(in binding, 12, out var last), Is.True);
            Assert.That(first.Value, Is.EqualTo(11));
            Assert.That(last.Value, Is.EqualTo(12));
        }

        [Test]
        public void TwoComponentCorrectionTruncationAndReplayRebuildHistoryWithoutChangingRealClock()
        {
            using var history = new PredictionHistoryStore<Position>(m_World, 1, 8);
            using var positions = new AuthoritativeBaselineStore<Position>(m_World);
            using var velocities = new AuthoritativeBaselineStore<Velocity>(m_World);
            var predicted = history.Bind(m_Entity);
            var positionBinding = positions.Bind(m_Entity);
            var velocityBinding = velocities.Bind(m_Entity);
            var initial = new Position { Value = 10 };
            history.TryRecord(in predicted, 10, in initial);
            Add(EcsPhase.TickSimulation, (w, c, t) => w.Get<Position>(m_Entity).Value += w.Get<Velocity>(m_Entity).Value);
            m_Loop.TickCompletedSink = new Sink { Callback = (w, t) =>
            {
                var value = w.Get<Position>(m_Entity);
                Assert.That(history.TryRecord(in predicted, t.Tick, in value), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            } };
            m_Loop.ResetClock(10);
            m_Loop.RunFrame(0.25);
            var before = ClockState();
            var corrected = new Position { Value = 100 };
            var velocity = new Velocity { Value = 3 };
            positions.TrySet(in positionBinding, 10, in corrected);
            velocities.TrySet(in velocityBinding, 10, in velocity);
            Assert.That(history.VerifyPrediction(positions, in positionBinding, in predicted), Is.EqualTo(PredictionVerificationResult.Mismatch));
            m_Loop.ValidateReplay(10, 12); // Check dispatch before restoring any World values.
            Assert.That(ComponentStateTransfer.TryRestoreExact(m_World, positions, in positionBinding,
                velocities, in velocityBinding, 10), Is.True);
            Assert.That(history.TruncateAfter(in predicted, 10), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            history.TryRecord(in predicted, 10, in corrected);
            m_Loop.Replay(10, 12);
            Assert.That(m_World.Get<Position>(m_Entity).Value, Is.EqualTo(106));
            Assert.That(m_World.Get<Velocity>(m_Entity).Value, Is.EqualTo(3));
            Assert.That(history.TryGet(in predicted, 11, out var rebuilt), Is.True);
            Assert.That(rebuilt.Value, Is.EqualTo(103));
            Assert.That(history.TryGet(in predicted, 12, out rebuilt), Is.True);
            Assert.That(rebuilt.Value, Is.EqualTo(106));
            Assert.That(history.VerifyPrediction(positions, in positionBinding, in predicted), Is.EqualTo(PredictionVerificationResult.Match));
            Assert.That(ClockState(), Is.EqualTo(before));
        }

        [Test]
        public void PreflightRejectsIterationForeignThreadAndDisposalAndLoopOwnsNoBorrowedObjects()
        {
            m_World.Query().ForEach(e =>
            {
                Assert.Throws<InvalidOperationException>(() => m_Loop.ValidateReplay(10, 12));
                Assert.Throws<InvalidOperationException>(() => m_Loop.Replay(10, 12));
            });
            var errors = new Exception[2];
            var worker = new Thread(() =>
            {
                try { m_Loop.ValidateReplay(10, 12); } catch (Exception ex) { errors[0] = ex; }
                try { m_Loop.Replay(10, 12); } catch (Exception ex) { errors[1] = ex; }
            });
            worker.Start();
            Assert.That(worker.Join(10000), Is.True);
            foreach (var error in errors) Assert.That(error, Is.TypeOf<InvalidOperationException>());
            var system = Add(EcsPhase.TickInput, (w, c, t) => { });
            var sink = new Sink { Callback = (w, t) => { } };
            m_Loop.TickCompletedSink = sink;
            m_Loop.Dispose();
            Assert.That(system.Disposed || sink.Disposed || m_World.IsDisposed, Is.False);
            Assert.Throws<ObjectDisposedException>(() => m_Loop.ValidateReplay(10, 12));
            Assert.Throws<ObjectDisposedException>(() => m_Loop.Replay(10, 12));
            using var another = new EcsLoop(m_World, m_Clock);
            m_World.Dispose();
            Assert.Throws<ObjectDisposedException>(() => another.ValidateReplay(10, 12));
            Assert.Throws<ObjectDisposedException>(() => another.Replay(10, 12));
        }

        [Test]
        public void WarmValuePlaybackReplayAndRealPredictionCaptureAllocateNothing()
        {
            using var history = new PredictionHistoryStore<Position>(m_World, 1, 4);
            var binding = history.Bind(m_Entity);
            var baseline = new Position();
            history.TryRecord(in binding, 10, in baseline);
            Add(EcsPhase.TickSimulation, (w, c, t) =>
            {
                w.Get<Position>(m_Entity).Value++;
                c.Set(m_Entity, new Velocity { Value = 1 });
            });
            int captures = 0, ticks = 0;
            m_Loop.TickCompletedSink = new Sink { Callback = (w, t) =>
            {
                var value = w.Get<Position>(m_Entity);
                if (t.IsReplay && history.TryRecord(in binding, t.Tick, in value) == PredictionHistoryWriteResult.Accepted) captures++;
            } };
            TestDelegate body = () =>
            {
                captures = 0; ticks = 0;
                for (int i = 0; i < 1000; i++)
                {
                    history.TruncateAfter(in binding, 10);
                    m_World.Set(m_Entity, baseline);
                    ticks += m_Loop.Replay(10, 12);
                }
            };
            for (int i = 0; i < 8; i++) body();
            var before = ClockState();
            EcsAllocationTests.AssertZero(body);
            Assert.That(captures, Is.EqualTo(2000));
            Assert.That(ticks, Is.EqualTo(2000));
            Assert.That(ClockState(), Is.EqualTo(before));
            Assert.That(history.TryGet(in binding, 12, out var value), Is.True);
            Assert.That(value.Value, Is.EqualTo(2));
        }
    }
}
