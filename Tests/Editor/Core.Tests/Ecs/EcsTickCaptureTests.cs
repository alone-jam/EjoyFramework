using System;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;

namespace EjoyFramework.Tests.Ecs
{
    public class EcsTickCaptureTests
    {
        private struct State { public int Value; }
        private struct Next { public Entity Entity; }

        private sealed class CallbackSystem : ISystem
        {
            public Action<World, CommandBuffer, EcsTime> Callback;
            public void Update(World world, CommandBuffer commands, in EcsTime time) => Callback(world, commands, time);
        }

        private sealed class Sink : IEcsTickCompletedSink, IDisposable
        {
            public Action<World, EcsTime> Callback;
            public bool Disposed;
            public void OnTickCompleted(World world, in EcsTime time) => Callback(world, time);
            public void Dispose() => Disposed = true;
        }

        private static Entity Spawn(World world, int value = 0)
        {
            var entity = world.CreateEntity();
            world.Set(entity, new State { Value = value });
            return entity;
        }

        [Test]
        public void MultiTickFrame_CapturesEachResolvedValueAsIndependentCopy()
        {
            using var world = new World();
            var clock = new TickClock(8, 4, 1);
            using var loop = new EcsLoop(world, clock);
            var entity = Spawn(world);
            var values = new State[3];
            var ticks = new uint[3];
            var lifetimes = new ulong[3];
            int count = 0;
            loop.Add(EcsPhase.TickSimulation, new CallbackSystem { Callback = (w, c, t) => w.Get<State>(entity).Value++ });
            loop.Add(EcsPhase.TickResolve, new CallbackSystem { Callback = (w, c, t) => w.Get<State>(entity).Value += 10 });
            loop.TickCompletedSink = new Sink { Callback = (w, t) =>
            {
                Assert.That(ComponentStateTransfer.TryCapture(w, entity, out lifetimes[count], out values[count]), Is.True);
                ticks[count] = t.Tick;
                Assert.That(t.Alpha, Is.Zero);
                Assert.That(t.DeltaTime, Is.EqualTo(0.125));
                count++; // Publish only after the row was filled.
            } };
            Assert.That(loop.RunFrame(0.375), Is.EqualTo(3));
            Assert.That(count, Is.EqualTo(3));
            Assert.That(ticks, Is.EqualTo(new uint[] { 1, 2, 3 }));
            Assert.That(values[0].Value, Is.EqualTo(11));
            Assert.That(values[1].Value, Is.EqualTo(22));
            Assert.That(values[2].Value, Is.EqualTo(33));
            world.Set(entity, new State { Value = 999 });
            Assert.That(values[0].Value, Is.EqualTo(11));
            Assert.That(lifetimes[0], Is.Not.Zero);
            Assert.That(lifetimes[2], Is.EqualTo(lifetimes[0]));
        }

        [Test]
        public void CaptureSeesCleanupWrites_AndExcludesStillPendingCascadeMembers()
        {
            using var world = new World();
            using var loop = new EcsLoop(world, new TickClock(8));
            var survivor = Spawn(world);
            var chain = new Entity[6];
            for (int i = 0; i < chain.Length; i++) chain[i] = Spawn(world);
            for (int i = 0; i < chain.Length - 1; i++) world.Set(chain[i], new Next { Entity = chain[i + 1] });
            var cleanup = world.Query().OnlyDespawning();
            loop.Add(EcsPhase.TickResolve, new CallbackSystem { Callback = (w, c, t) => w.RequestDespawn(chain[0]) });
            loop.Add(EcsPhase.Despawn, new CallbackSystem { Callback = (w, c, t) =>
            {
                cleanup.ForEach(e => { if (w.TryGet(e, out Next next)) w.RequestDespawn(next.Entity); });
                c.Set(survivor, new State { Value = 42 });
            } });
            int captured = 0;
            loop.TickCompletedSink = new Sink { Callback = (w, t) =>
            {
                Assert.That(w.IsAlive(chain[0]), Is.False);
                Assert.That(w.IsAlive(chain[4]), Is.True);
                Assert.That(w.IsDespawning(chain[4]), Is.True);
                Assert.That(ComponentStateTransfer.TryCapture<State>(w, chain[4], out _, out _), Is.False);
                Assert.That(ComponentStateTransfer.TryCapture<State>(w, survivor, out _, out var value), Is.True);
                Assert.That(value.Value, Is.EqualTo(42));
                captured++;
            } };
            loop.RunFrame(0.125);
            Assert.That(captured, Is.EqualTo(1));
        }

        [Test]
        public void ZeroTickFrame_DoesNotCaptureEvenWhenFrameEndDestroysEntities()
        {
            using var world = new World();
            using var loop = new EcsLoop(world, new TickClock());
            var entity = Spawn(world);
            world.RequestDespawn(entity);
            int calls = 0;
            loop.TickCompletedSink = new Sink { Callback = (w, t) => calls++ };
            Assert.That(loop.RunFrame(0), Is.Zero);
            Assert.That(calls, Is.Zero);
            Assert.That(world.IsAlive(entity), Is.False);
        }

        [Test]
        public void SimulationFailureBeforeBoundary_DoesNotCapture()
        {
            using var world = new World();
            using var loop = new EcsLoop(world, new TickClock());
            int calls = 0;
            loop.TickCompletedSink = new Sink { Callback = (w, t) => calls++ };
            loop.Add(EcsPhase.TickResolve, new CallbackSystem { Callback = (w, c, t) => throw new ApplicationException() });
            Assert.Throws<ApplicationException>(() => loop.RunFrame(0.1));
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public void SinkFailure_FaultsWithoutRewindingConsumedTickOrRunningPresentation()
        {
            using var world = new World();
            var clock = new TickClock(8, 4, 1);
            using var loop = new EcsLoop(world, clock);
            int captures = 0, presentations = 0;
            loop.TickCompletedSink = new Sink { Callback = (w, t) => { captures++; throw new ApplicationException(); } };
            loop.Add(EcsPhase.Presentation, new CallbackSystem { Callback = (w, c, t) => presentations++ });
            Assert.Throws<ApplicationException>(() => loop.RunFrame(0.375));
            Assert.That(captures, Is.EqualTo(1));
            Assert.That(presentations, Is.Zero);
            Assert.That(loop.IsFaulted, Is.True);
            Assert.That(clock.Tick, Is.EqualTo(1));
            Assert.That(clock.PendingTicks, Is.EqualTo(2));
            loop.TickCompletedSink = null;
            loop.ResetClock(1);
            Assert.That(loop.RunFrame(0.125), Is.EqualTo(1));
            Assert.That(captures, Is.EqualTo(1));
        }

        [Test]
        public void CaptureCannotReenterChangeSinkOrDisposeLoop_AndSinkIsBorrowed()
        {
            using var world = new World();
            var loop = new EcsLoop(world, new TickClock());
            var sink = new Sink();
            sink.Callback = (w, t) =>
            {
                Assert.That(loop.IsRunning, Is.True);
                Assert.AreSame(sink, loop.TickCompletedSink);
                Assert.Throws<InvalidOperationException>(() => loop.RunFrame(0));
                Assert.Throws<InvalidOperationException>(() => loop.TickCompletedSink = null);
                Assert.Throws<InvalidOperationException>(() => loop.ResetClock(0));
                Assert.Throws<InvalidOperationException>(() => loop.Dispose());
            };
            loop.TickCompletedSink = sink;
            loop.RunFrame(1.0 / 30);
            loop.Dispose();
            Assert.That(sink.Disposed, Is.False);
            Assert.Throws<ObjectDisposedException>(() => loop.TickCompletedSink = null);
        }

        [Test]
        public void WarmCaptureBoundaryAndValueCopy_AllocateNothing()
        {
            using var world = new World();
            var clock = new TickClock(8);
            using var loop = new EcsLoop(world, clock);
            var entity = Spawn(world, 7);
            int count = 0;
            long total = 0;
            loop.TickCompletedSink = new Sink { Callback = (w, t) =>
            {
                if (ComponentStateTransfer.TryCapture<State>(w, entity, out _, out var copy)) { total += copy.Value; count++; }
            } };
            int iterations = 1;
            TestDelegate body = () => { for (int i = 0; i < iterations; i++) loop.RunFrame(0.125); };
            for (int i = 0; i < 32; i++) body();
            count = 0; total = 0; iterations = 10000;
            EcsAllocationTests.AssertZero(body);
            Assert.That(count, Is.EqualTo(10000));
            Assert.That(total, Is.EqualTo(70000));
        }
    }
}
