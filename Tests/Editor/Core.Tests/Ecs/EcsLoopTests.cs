using System;
using System.Collections.Generic;
using System.Threading;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;

namespace EjoyFramework.Tests.Ecs
{
    public class EcsLoopTests
    {
        private sealed class CallbackSystem : ISystem, IDisposable
        {
            public Action<World, CommandBuffer, EcsTime> Callback;
            public bool Disposed;
            public void Update(World world, CommandBuffer commands, in EcsTime time) => Callback(world, commands, time);
            public void Dispose() => Disposed = true;
        }

        [Test]
        public void Phases_RunPerTickThenPerFrame_WithStableOrderAndPreciseTime()
        {
            using var world = new World();
            var clock = new TickClock();
            using var loop = new EcsLoop(world, clock);
            var order = new List<string>();
            var ticks = new List<EcsTime>();
            var frames = new List<EcsTime>();
            loop.Add(EcsPhase.Presentation, new CallbackSystem
                { Callback = (w, c, t) => { order.Add("present"); frames.Add(t); } });
            loop.Add(EcsPhase.TickResolve, new CallbackSystem { Callback = (w, c, t) => order.Add("resolve") });
            loop.Add(EcsPhase.TickSimulation, new CallbackSystem { Callback = (w, c, t) => order.Add("sim-a") }, 10);
            loop.Add(EcsPhase.TickSimulation, new CallbackSystem { Callback = (w, c, t) => order.Add("sim-b") }, 10);
            loop.Add(EcsPhase.TickSimulation, new CallbackSystem { Callback = (w, c, t) => order.Add("sim-first") }, -1);
            loop.Add(EcsPhase.TickInput, new CallbackSystem
                { Callback = (w, c, t) => { order.Add("input"); ticks.Add(t); Assert.That(clock.Tick, Is.EqualTo(t.Tick)); } });
            loop.Add(EcsPhase.Interpolation, new CallbackSystem
                { Callback = (w, c, t) => { order.Add("interpolate"); frames.Add(t); } });

            double delta = 2.5 / 30;
            Assert.That(loop.RunFrame(delta), Is.EqualTo(2));
            Assert.That(order, Is.EqualTo(new[] { "input", "sim-first", "sim-a", "sim-b", "resolve",
                "input", "sim-first", "sim-a", "sim-b", "resolve", "interpolate", "present" }));
            Assert.That(ticks.Count, Is.EqualTo(2));
            for (int i = 0; i < ticks.Count; i++)
            {
                Assert.That(ticks[i].Tick, Is.EqualTo((uint)(i + 1)));
                Assert.That(ticks[i].DeltaTime, Is.EqualTo(clock.TickInterval));
                Assert.That(ticks[i].TickInterval, Is.EqualTo(clock.TickInterval));
                Assert.That(ticks[i].ElapsedTime, Is.EqualTo((i + 1) * clock.TickInterval));
                Assert.That(ticks[i].Alpha, Is.Zero);
                Assert.That(ticks[i].IsReplay, Is.False);
            }
            Assert.That(frames.Count, Is.EqualTo(2));
            foreach (var frame in frames)
            {
                Assert.That(frame.Tick, Is.EqualTo(2));
                Assert.That(frame.DeltaTime, Is.EqualTo(delta));
                Assert.That(frame.TickInterval, Is.EqualTo(clock.TickInterval));
                Assert.That(frame.ElapsedTime, Is.EqualTo(2 * clock.TickInterval));
                Assert.That(frame.Alpha, Is.EqualTo(0.5).Within(1e-12));
                Assert.That(frame.IsReplay, Is.False);
            }
        }

        [TestCase(0.0)]
        [TestCase(0.0125)]
        public void ZeroTickFrame_StillInterpolatesAndPresents(double delta)
        {
            using var world = new World();
            var clock = new TickClock();
            using var loop = new EcsLoop(world, clock);
            int simulations = 0;
            var frames = new List<EcsTime>();
            loop.Add(EcsPhase.TickSimulation, new CallbackSystem { Callback = (w, c, t) => simulations++ });
            loop.Add(EcsPhase.Interpolation, new CallbackSystem { Callback = (w, c, t) => frames.Add(t) });
            loop.Add(EcsPhase.Presentation, new CallbackSystem { Callback = (w, c, t) => frames.Add(t) });
            Assert.That(loop.RunFrame(delta), Is.Zero);
            Assert.That(simulations, Is.Zero);
            Assert.That(frames.Count, Is.EqualTo(2));
            foreach (var frame in frames)
            {
                Assert.That(frame.Tick, Is.Zero);
                Assert.That(frame.DeltaTime, Is.EqualTo(delta));
                Assert.That(frame.Alpha, Is.EqualTo(delta * 30));
            }
        }

        [Test]
        public void CatchUpAndFrameClamp_BoundWorkAndPreserveFraction()
        {
            using var world = new World();
            var clock = new TickClock(8, 2, 0.9375);
            using var loop = new EcsLoop(world, clock);
            EcsTime frame = default;
            loop.Add(EcsPhase.Presentation, new CallbackSystem { Callback = (w, c, t) => frame = t });
            Assert.That(loop.RunFrame(double.MaxValue), Is.EqualTo(2));
            Assert.That(clock.Tick, Is.EqualTo(2));
            Assert.That(clock.PendingTicks, Is.Zero);
            Assert.That(clock.DroppedTicks, Is.EqualTo(5));
            Assert.That(frame.DeltaTime, Is.EqualTo(0.9375));
            Assert.That(frame.Alpha, Is.EqualTo(0.5));
            Assert.That(loop.RunFrame(0.0625), Is.EqualTo(1));
            Assert.That(clock.Tick, Is.EqualTo(3));
            Assert.That(clock.DroppedTicks, Is.EqualTo(5));
        }

        [Test]
        public void CommandPlayback_IsVisibleWithinAndBetweenPhases_AndDestroyWaitsForSync()
        {
            using var world = new World();
            using var loop = new EcsLoop(world, new TickClock(8));
            Entity created = default;
            loop.Add(EcsPhase.TickInput, new CallbackSystem
                { Callback = (w, c, t) => { created = c.CreateEntity(); c.Set(created, new Health { Value = 7 }); } });
            loop.Add(EcsPhase.TickInput, new CallbackSystem
                { Callback = (w, c, t) => Assert.That(w.Get<Health>(created).Value, Is.EqualTo(7)) });
            loop.Add(EcsPhase.TickSimulation, new CallbackSystem { Callback = (w, c, t) => c.DestroyEntity(created) });
            loop.Add(EcsPhase.TickResolve, new CallbackSystem
                { Callback = (w, c, t) =>
                {
                    Assert.That(w.IsAlive(created), Is.True);
                    Assert.That(w.Query().Count, Is.Zero);
                    Assert.That(w.Get<Health>(created).Value, Is.EqualTo(7));
                } });
            loop.Add(EcsPhase.Despawn, new CallbackSystem
                { Callback = (w, c, t) => Assert.That(w.Query().OnlyDespawning().Count, Is.EqualTo(1)) });
            loop.Add(EcsPhase.Presentation, new CallbackSystem
                { Callback = (w, c, t) => Assert.That(w.IsAlive(created), Is.False) });
            Assert.That(loop.RunFrame(0.125), Is.EqualTo(1));
            Assert.That(world.EntityCount, Is.Zero);
        }

        [Test]
        public void Execution_RejectsReentryRegistrationResetDisposalAndDirectStructuralChanges()
        {
            using var world = new World();
            var clock = new TickClock();
            using var loop = new EcsLoop(world, clock);
            var system = new CallbackSystem();
            system.Callback = (w, c, t) =>
            {
                Assert.Throws<InvalidOperationException>(() => loop.RunFrame(0));
                Assert.Throws<InvalidOperationException>(() => loop.Add(EcsPhase.Presentation, system));
                Assert.Throws<InvalidOperationException>(() => loop.Remove(EcsPhase.TickSimulation, system));
                Assert.Throws<InvalidOperationException>(() => loop.SetEnabled(EcsPhase.TickSimulation, system, false));
                Assert.Throws<InvalidOperationException>(() => loop.ResetClock(0));
                Assert.Throws<InvalidOperationException>(() => loop.Dispose());
                Assert.Throws<InvalidOperationException>(() => w.CreateEntity());
                Assert.Throws<InvalidOperationException>(() => c.Playback());
            };
            loop.Add(EcsPhase.TickSimulation, system);
            Assert.That(loop.RunFrame(clock.TickInterval), Is.EqualTo(1));
            Assert.That(loop.IsFaulted, Is.False);
        }

        [TestCase(EcsPhase.TickSimulation)]
        [TestCase(EcsPhase.Interpolation)]
        public void Failure_ClearsUnplayedCommandsAndRequiresExplicitClockRecovery(EcsPhase failingPhase)
        {
            using var world = new World();
            var clock = new TickClock(8, 4, 1);
            using var loop = new EcsLoop(world, clock);
            int presented = 0;
            var fail = new CallbackSystem { Callback = (w, c, t) => { c.CreateEntity(); throw new ApplicationException(); } };
            loop.Add(EcsPhase.TickInput, new CallbackSystem { Callback = (w, c, t) => c.CreateEntity() });
            loop.Add(failingPhase, fail);
            loop.Add(EcsPhase.Presentation, new CallbackSystem { Callback = (w, c, t) => presented++ });
            Assert.Throws<ApplicationException>(() => loop.RunFrame(0.25));
            Assert.That(loop.IsFaulted, Is.True);
            Assert.That(presented, Is.Zero);
            int completedInputs = failingPhase == EcsPhase.TickSimulation ? 1 : 2;
            Assert.That(world.EntityCount, Is.EqualTo(completedInputs));
            Assert.That(clock.Tick, Is.EqualTo((uint)completedInputs));
            Assert.That(clock.PendingTicks, Is.EqualTo(2 - completedInputs));
            Assert.Throws<InvalidOperationException>(() => loop.RunFrame(0));
            Assert.That(loop.Remove(failingPhase, fail), Is.True);
            loop.ResetClock(clock.Tick);
            Assert.That(loop.IsFaulted, Is.False);
            Assert.That(clock.PendingTicks, Is.Zero);
            Assert.That(loop.RunFrame(0), Is.Zero);
            Assert.That(presented, Is.EqualTo(1));
            Assert.That(world.EntityCount, Is.EqualTo(completedInputs));
        }

        [Test]
        public void PlaybackFailure_ReleasesUnplayedReservationsAndFaultsLoop()
        {
            using var world = new World();
            var clock = new TickClock();
            using var loop = new EcsLoop(world, clock);
            var target = world.CreateEntity();
            CommandBuffer retained = null;
            var capture = new CallbackSystem { Callback = (w, c, t) => retained = c };
            loop.Add(EcsPhase.TickSimulation, capture);
            loop.RunFrame(clock.TickInterval);
            // 借用的缓冲在帧间可记录；失效的 Set 在回放而非记录时触发失败。
            retained.Set(target, new Health());
            Entity reserved = retained.CreateEntity();
            world.DestroyEntity(target);
            Assert.Throws<InvalidOperationException>(() => loop.RunFrame(clock.TickInterval));
            Assert.That(loop.IsFaulted, Is.True);
            Assert.That(world.EntityCount, Is.Zero);
            Assert.That(world.IsAlive(reserved), Is.False);
            loop.Remove(EcsPhase.TickSimulation, capture);
            loop.ResetClock(clock.Tick);
            Assert.That(loop.RunFrame(clock.TickInterval), Is.EqualTo(1));
            Assert.That(world.EntityCount, Is.Zero);
        }

        [Test]
        public void TickOverflow_FaultsWithoutWrapping_AndCanBeExplicitlyReset()
        {
            using var world = new World();
            var clock = new TickClock();
            clock.Reset(uint.MaxValue);
            using var loop = new EcsLoop(world, clock);
            Assert.Throws<OverflowException>(() => loop.RunFrame(clock.TickInterval));
            Assert.That(loop.IsFaulted, Is.True);
            Assert.That(clock.Tick, Is.EqualTo(uint.MaxValue));
            loop.ResetClock(0);
            Assert.That(loop.RunFrame(clock.TickInterval), Is.EqualTo(1));
        }

        [Test]
        public void Registration_IsPerPhaseAndSupportsEnableRemove()
        {
            using var world = new World();
            using var loop = new EcsLoop(world, new TickClock());
            int calls = 0;
            var system = new CallbackSystem { Callback = (w, c, t) => calls++ };
            loop.Add(EcsPhase.Interpolation, system);
            loop.Add(EcsPhase.Presentation, system);
            Assert.Throws<InvalidOperationException>(() => loop.Add(EcsPhase.Interpolation, system));
            loop.SetEnabled(EcsPhase.Interpolation, system, false);
            loop.RunFrame(0);
            Assert.That(calls, Is.EqualTo(1));
            loop.SetEnabled(EcsPhase.Interpolation, system, true);
            Assert.That(loop.Remove(EcsPhase.Presentation, system), Is.True);
            Assert.That(loop.Remove(EcsPhase.Presentation, system), Is.False);
            loop.RunFrame(0);
            Assert.That(calls, Is.EqualTo(2));
        }

        [TestCase(-1.0)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void InvalidFrame_LeavesClockAndLoopUsable(double delta)
        {
            using var world = new World();
            var clock = new TickClock();
            using var loop = new EcsLoop(world, clock);
            Assert.Throws<ArgumentOutOfRangeException>(() => loop.RunFrame(delta));
            Assert.That(clock.Tick, Is.Zero);
            Assert.That(clock.Alpha, Is.Zero);
            Assert.That(loop.IsFaulted, Is.False);
            Assert.That(loop.RunFrame(clock.TickInterval), Is.EqualTo(1));
        }

        [Test]
        public void InvalidConstructionAndPhase_AreRejected()
        {
            using var world = new World();
            var clock = new TickClock();
            Assert.Throws<ArgumentNullException>(() => new EcsLoop(null, clock));
            Assert.Throws<ArgumentNullException>(() => new EcsLoop(world, null));
            using var loop = new EcsLoop(world, clock);
            var system = new CallbackSystem { Callback = (w, c, t) => { } };
            Assert.Throws<ArgumentNullException>(() => loop.Add(EcsPhase.TickInput, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => loop.Add((EcsPhase)(-1), system));
            Assert.Throws<ArgumentOutOfRangeException>(() => loop.Remove((EcsPhase)999, system));
            Assert.Throws<ArgumentOutOfRangeException>(() => loop.SetEnabled((EcsPhase)999, system, true));
        }

        [Test]
        public void PendingClockAndDisposedWorld_AreRejectedWithoutConsumption()
        {
            using var world = new World();
            var clock = new TickClock();
            clock.Accumulate(clock.TickInterval);
            Assert.Throws<InvalidOperationException>(() => new EcsLoop(world, clock));
            Assert.That(clock.PendingTicks, Is.EqualTo(1));
            Assert.That(clock.Tick, Is.Zero);
            clock.Reset();
            using var loop = new EcsLoop(world, clock);
            world.Dispose();
            Assert.Throws<ObjectDisposedException>(() => new EcsLoop(world, clock));
            Assert.Throws<ObjectDisposedException>(() => loop.RunFrame(clock.TickInterval));
            Assert.That(clock.PendingTicks, Is.Zero);
            Assert.DoesNotThrow(() => loop.Dispose());
        }

        [Test]
        public void ActiveWorldIteration_RejectsFrameBeforeAccumulation()
        {
            using var world = new World();
            var clock = new TickClock();
            using var loop = new EcsLoop(world, clock);
            world.CreateEntity();
            world.Query().ForEach(e => Assert.Throws<InvalidOperationException>(() => loop.RunFrame(0.1)));
            Assert.That(clock.PendingTicks, Is.Zero);
            Assert.That(clock.Alpha, Is.Zero);
            Assert.That(loop.IsFaulted, Is.False);
        }

        [Test]
        public void Dispose_ReleasesOnlyGroups_NotWorldSystemsOrClock()
        {
            using var world = new World();
            var clock = new TickClock();
            var loop = new EcsLoop(world, clock);
            var system = new CallbackSystem { Callback = (w, c, t) => { } };
            loop.Add(EcsPhase.TickInput, system);
            loop.Dispose();
            loop.Dispose();
            Assert.That(world.IsDisposed, Is.False);
            Assert.That(system.Disposed, Is.False);
            Assert.DoesNotThrow(() => world.CreateEntity());
            Assert.DoesNotThrow(() => clock.Accumulate(clock.TickInterval));
            Assert.Throws<ObjectDisposedException>(() => loop.RunFrame(0));
            Assert.Throws<ObjectDisposedException>(() => loop.Add(EcsPhase.TickInput, system));
            Assert.Throws<ObjectDisposedException>(() => loop.ResetClock(0));
        }

        [Test]
        public void ForeignThread_CannotDriveLoop()
        {
            using var world = new World();
            using var loop = new EcsLoop(world, new TickClock());
            Exception failure = null;
            var thread = new Thread(() => { try { loop.RunFrame(0); } catch (Exception ex) { failure = ex; } });
            thread.Start();
            thread.Join();
            Assert.That(failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(loop.IsFaulted, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WarmLoop_AllocatesNothingAcrossTenThousandFrames(bool zeroTickFrames)
        {
            using var world = new World();
            var clock = new TickClock();
            using var loop = new EcsLoop(world, clock);
            int calls = 0;
            var system = new CallbackSystem { Callback = (w, c, t) => calls++ };
            loop.Add(EcsPhase.TickInput, system);
            loop.Add(EcsPhase.TickSimulation, system);
            loop.Add(EcsPhase.TickResolve, system);
            loop.Add(EcsPhase.Interpolation, system);
            loop.Add(EcsPhase.Presentation, system);
            double delta = zeroTickFrames ? 0 : clock.TickInterval;
            int executed = 0;
            int iterations = 1;
            TestDelegate body = () =>
            {
                for (int i = 0; i < iterations; i++) executed += loop.RunFrame(delta);
            };
            for (int i = 0; i < 32; i++) body();
            loop.ResetClock(0);
            calls = executed = 0;
            iterations = 10000;
            EcsAllocationTests.AssertZero(body);
            Assert.That(executed, Is.EqualTo(zeroTickFrames ? 0 : 10000));
            Assert.That(calls, Is.EqualTo(zeroTickFrames ? 20000 : 50000));
            Assert.That(clock.Tick, Is.EqualTo(zeroTickFrames ? 0u : 10000u));
        }
    }
}
