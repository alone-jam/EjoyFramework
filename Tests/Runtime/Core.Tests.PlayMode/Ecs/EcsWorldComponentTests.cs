using System;
using System.Collections;
using EjoyFramework.Core.Ecs;
using EjoyFramework.Core.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace EjoyFramework.Tests.PlayMode.Ecs
{
    public class EcsWorldComponentTests
    {
        private sealed class CountSystem : ISystem, IDisposable
        {
            public int Count;
            public double Elapsed;
            public EcsTime LastTime;
            public bool Disposed;
            public void Update(World world, CommandBuffer commands, in EcsTime time)
            {
                Count++;
                Elapsed += time.DeltaTime;
                LastTime = time;
            }
            public void Dispose() => Disposed = true;
        }

        private sealed class ThrowSystem : ISystem
        {
            public void Update(World world, CommandBuffer commands, in EcsTime time) =>
                throw new InvalidOperationException("simulation failure");
        }

        private sealed class ShutdownSystem : ISystem
        {
            private readonly EcsWorldComponent m_Driver;
            public ShutdownSystem(EcsWorldComponent driver) => m_Driver = driver;
            public void Update(World world, CommandBuffer commands, in EcsTime time) =>
                Assert.Throws<InvalidOperationException>(() => m_Driver.Shutdown());
        }

        [Test]
        public void Driver_StepAccumulatesFixedTicks_AndPresentsZeroTickFrames()
        {
            var go = new GameObject("EcsTickTest");
            try
            {
                var driver = go.AddComponent<EcsWorldComponent>();
                driver.UpdateMode = EcsUpdateMode.Manual;
                var simulation = new CountSystem();
                var presentation = new CountSystem();
                driver.Loop.Add(EcsPhase.TickSimulation, simulation);
                driver.Loop.Add(EcsPhase.Presentation, presentation);
                double halfTick = driver.Clock.TickInterval / 2;

                Assert.That(driver.Step(halfTick), Is.Zero);
                Assert.That(simulation.Count, Is.Zero);
                Assert.That(presentation.Count, Is.EqualTo(1));
                Assert.That(presentation.LastTime.Alpha, Is.EqualTo(0.5));
                Assert.That(driver.Step(halfTick), Is.EqualTo(1));
                Assert.That(simulation.LastTime.Tick, Is.EqualTo(1u));
                Assert.That(simulation.LastTime.DeltaTime, Is.EqualTo(1.0 / 30));
                Assert.That(simulation.LastTime.Alpha, Is.Zero);
                Assert.That(presentation.LastTime.Tick, Is.EqualTo(1u));
                Assert.That(presentation.LastTime.Alpha, Is.Zero);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Driver_CatchUpIsBounded()
        {
            var go = new GameObject("EcsCatchUpTest");
            try
            {
                var driver = go.AddComponent<EcsWorldComponent>();
                driver.UpdateMode = EcsUpdateMode.Manual;
                var simulation = new CountSystem();
                driver.Loop.Add(EcsPhase.TickSimulation, simulation);
                Assert.That(driver.Step(10.0), Is.EqualTo(4));
                Assert.That(driver.Clock.Tick, Is.EqualTo(4u));
                Assert.That(driver.Clock.DroppedTicks, Is.EqualTo(3));
                Assert.That(driver.Clock.Alpha, Is.EqualTo(0.5));
                Assert.That(simulation.Count, Is.EqualTo(4));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void RejectedShutdown_DoesNotPartiallyDisposeDriver()
        {
            var go = new GameObject("EcsShutdownTest");
            try
            {
                var driver = go.AddComponent<EcsWorldComponent>();
                driver.UpdateMode = EcsUpdateMode.Manual;
                driver.World.CreateEntity();
                driver.World.Query().ForEach(e =>
                    Assert.Throws<InvalidOperationException>(() => driver.Shutdown()));
                driver.Loop.Add(EcsPhase.TickSimulation, new ShutdownSystem(driver));
                Assert.DoesNotThrow(() => driver.Step(driver.Clock.TickInterval));
                Assert.That(driver.IsInitialized, Is.True);
                Assert.That(driver.Loop.IsFaulted, Is.False);
                var world = driver.World;
                var oldLoop = driver.Loop;
                driver.Shutdown();
                Assert.That(world.IsDisposed, Is.True);
                Assert.That(driver.IsInitialized, Is.False);
                Assert.Throws<ObjectDisposedException>(() => oldLoop.RunFrame(0));
                driver.Initialize();
                Assert.That(driver.Clock.Tick, Is.Zero);
                Assert.AreNotSame(world, driver.World);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Driver_FailureDisablesAutomaticUpdates_ExplicitRecoveryRequired()
        {
            var go = new GameObject("EcsFailureTest");
            try
            {
                var driver = go.AddComponent<EcsWorldComponent>();
                var failure = new ThrowSystem();
                driver.Loop.Add(EcsPhase.TickSimulation, failure);
                Assert.Throws<InvalidOperationException>(() => driver.Step(driver.Clock.TickInterval));
                Assert.That(driver.enabled, Is.False);
                Assert.That(driver.IsInitialized, Is.True);
                Assert.That(driver.Loop.IsFaulted, Is.True);
                Assert.Throws<InvalidOperationException>(() => driver.Step(0.0));
                driver.Loop.Remove(EcsPhase.TickSimulation, failure);
                driver.ResetClock(10);
                Assert.That(driver.enabled, Is.False, "重置不擅自恢复自动推进");
                Assert.That(driver.Step(driver.Clock.TickInterval), Is.EqualTo(1));
                Assert.That(driver.Clock.Tick, Is.EqualTo(11u));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Driver_InvalidFrameStepDisablesAutomaticUpdates()
        {
            var go = new GameObject("EcsInvalidTimeTest");
            try
            {
                var driver = go.AddComponent<EcsWorldComponent>();
                Assert.Throws<ArgumentOutOfRangeException>(() => driver.Step(double.NaN));
                Assert.That(driver.enabled, Is.False);
                Assert.That(driver.IsInitialized, Is.True);
                Assert.That(driver.Clock.Tick, Is.Zero);
                Assert.That(driver.Loop.IsFaulted, Is.False);
                Assert.Throws<ArgumentOutOfRangeException>(() => driver.UpdateMode = (EcsUpdateMode)99);
                Assert.Throws<ArgumentOutOfRangeException>(() => driver.TimeSource = (EcsTimeSource)99);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Driver_LegacyFixedUpdateMapsToUpdate_WithoutDoubleDispatch()
        {
            var go = new GameObject("EcsLegacyModeTest");
            try
            {
                var driver = go.AddComponent<EcsWorldComponent>();
                driver.UpdateMode = (EcsUpdateMode)2; // 保留的序列化值，不调用 Unity FixedUpdate。
                Assert.That(driver.UpdateMode, Is.EqualTo(EcsUpdateMode.Update));
                var presentation = new CountSystem();
                driver.Loop.Add(EcsPhase.Presentation, presentation);
                go.SendMessage("FixedUpdate", SendMessageOptions.DontRequireReceiver);
                Assert.That(presentation.Count, Is.Zero);
                go.SendMessage("Update");
                Assert.That(presentation.Count, Is.EqualTo(1));
                driver.enabled = false;
                go.SendMessage("Update");
                Assert.That(presentation.Count, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [UnityTest]
        public IEnumerator Driver_ManualAutomaticPauseAndDestructionRespectOwnership()
        {
            var go = new GameObject("EcsWorldTest");
            try
            {
                var driver = go.AddComponent<EcsWorldComponent>();
                driver.UpdateMode = EcsUpdateMode.Manual;
                var world = driver.World;
                var system = new CountSystem();
                driver.Loop.Add(EcsPhase.Presentation, system);
                yield return null;
                Assert.That(system.Count, Is.Zero);
                driver.Step(0.25f);
                Assert.That(driver.Clock.Tick, Is.EqualTo(4u));
                driver.UpdateMode = EcsUpdateMode.Update;
                yield return null;
                yield return null;
                Assert.That(system.Count, Is.GreaterThan(1));
                driver.enabled = false;
                int paused = system.Count;
                uint pausedTick = driver.Clock.Tick;
                yield return null;
                Assert.That(system.Count, Is.EqualTo(paused));
                Assert.That(driver.Clock.Tick, Is.EqualTo(pausedTick));
                Assert.That(world.IsDisposed, Is.False);
                Object.Destroy(go);
                yield return null;
                Assert.That(world.IsDisposed, Is.True);
                Assert.That(system.Disposed, Is.False, "系统实例归调用方所有");
            }
            finally { if (go != null) Object.DestroyImmediate(go); }
        }

        [UnityTest]
        public IEnumerator Driver_UnscaledTimeContinuesWhileGameTimeIsPaused()
        {
            var go = new GameObject("EcsTimeSourceTest");
            float previousScale = Time.timeScale;
            try
            {
                var driver = go.AddComponent<EcsWorldComponent>();
                driver.UpdateMode = EcsUpdateMode.Manual;
                var presentation = new CountSystem();
                driver.Loop.Add(EcsPhase.Presentation, presentation);
                Time.timeScale = 0;
                yield return null;
                driver.UpdateMode = EcsUpdateMode.Update;
                driver.TimeSource = EcsTimeSource.Scaled;
                go.SendMessage("Update");
                Assert.That(presentation.LastTime.DeltaTime, Is.Zero);
                driver.TimeSource = EcsTimeSource.Unscaled;
                go.SendMessage("Update");
                Assert.That(presentation.LastTime.DeltaTime, Is.GreaterThan(0));
            }
            finally
            {
                Time.timeScale = previousScale;
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void View_InvalidatesOnEntityReuseAndUnbindDoesNotDestroySimulation()
        {
            using var world = new World();
            var go = new GameObject("EcsViewTest");
            try
            {
                var view = go.AddComponent<EcsEntityView>();
                var original = world.CreateEntity();
                view.Bind(world, original);
                Assert.That(view.IsBound, Is.True);
                view.Unbind();
                Assert.That(world.IsAlive(original), Is.True);
                view.Bind(world, original);
                world.DestroyEntity(original);
                var recycled = world.CreateEntity();
                Assert.That(recycled.Index, Is.EqualTo(original.Index));
                Assert.That(view.IsBound, Is.False);
                view.Bind(world, recycled);
                Object.DestroyImmediate(go);
                Assert.That(world.IsAlive(recycled), Is.True);
            }
            finally { if (go != null) Object.DestroyImmediate(go); }
        }
    }
}
