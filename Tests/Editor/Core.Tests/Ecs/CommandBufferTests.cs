using System;
using NUnit.Framework;
using EjoyFramework.Core.Ecs;

namespace EjoyFramework.Tests.Ecs
{
    public class CommandBufferTests
    {
        [Test]
        public void Create_ReservesStableHandleUntilPlaybackAndSupportsEntityReferences()
        {
            using var world = new World(1);
            using var commands = new CommandBuffer(world);
            var parent = commands.CreateEntity();
            var child = commands.CreateEntity();
            commands.Set(child, new Parent { Entity = parent });
            Assert.That(world.EntityCount, Is.Zero);
            Assert.That(world.Query().Count, Is.Zero);
            Assert.That(world.IsAlive(parent), Is.False);
            Assert.Throws<InvalidOperationException>(() => world.Set(parent, new Health()));
            commands.Playback();
            Assert.That(world.EntityCount, Is.EqualTo(2));
            Assert.That(world.Get<Parent>(child).Entity, Is.EqualTo(parent));
            Assert.That(world.IsAlive(parent), Is.True);
            Assert.That(commands.Count, Is.Zero);
        }

        [Test]
        public void ClearAndDispose_CancelReservedEntitiesWithoutTouchingLiveOnes()
        {
            using var world = new World();
            var commands = new CommandBuffer(world);
            var live = world.CreateEntity();
            var cancelled = commands.CreateEntity();
            commands.DestroyEntity(live);
            commands.Clear();
            Assert.That(world.IsAlive(live), Is.True);
            var recycled = commands.CreateEntity();
            Assert.That(recycled.Index, Is.EqualTo(cancelled.Index));
            Assert.That(recycled.Version, Is.Not.EqualTo(cancelled.Version));
            commands.Dispose();
            commands.Dispose();
            Assert.That(world.IsAlive(recycled), Is.False);
            Assert.Throws<ObjectDisposedException>(() => commands.CreateEntity());
            Assert.That(world.CreateEntity().Index, Is.EqualTo(recycled.Index));
        }

        [Test]
        public void Commands_PlayInRecordedOrderAndCanBeReused()
        {
            using var world = new World();
            using var commands = new CommandBuffer(world);
            var entity = commands.CreateEntity();
            commands.Set(entity, new Health { Value = 1 });
            commands.Remove<Health>(entity);
            commands.Set(entity, new Health { Value = 2 });
            commands.Playback();
            Assert.That(world.Get<Health>(entity).Value, Is.EqualTo(2));
            commands.Set(entity, new Health { Value = 3 });
            commands.Playback();
            Assert.That(world.Get<Health>(entity).Value, Is.EqualTo(3));
            commands.DestroyEntity(entity);
            commands.Playback();
            Assert.That(world.IsAlive(entity), Is.True, "回放只请求销毁，组件留给清理系统");
            Assert.That(world.Query().Count, Is.Zero);
            Assert.That(world.ProcessDespawns(), Is.EqualTo(1));
            Assert.That(world.EntityCount, Is.Zero);
        }

        [Test]
        public void Buffer_RejectsForeignWorldAndOtherBuffersReservations()
        {
            using var world = new World();
            using var foreignWorld = new World();
            using var first = new CommandBuffer(world);
            using var second = new CommandBuffer(world);
            var reserved = first.CreateEntity();
            var foreign = foreignWorld.CreateEntity();
            Assert.Throws<InvalidOperationException>(() => second.Set(reserved, new Health()));
            Assert.Throws<InvalidOperationException>(() => first.DestroyEntity(foreign));
            first.Playback();
            second.Set(reserved, new Health());
            Assert.DoesNotThrow(() => second.Playback());
        }

        [Test]
        public void PlaybackFailure_DiscardsRemainingCommandsAndReleasesPendingReservations()
        {
            using var world = new World();
            using var commands = new CommandBuffer(world);
            var live = world.CreateEntity();
            var stale = world.CreateEntity();
            commands.Set(live, new Health { Value = 5 });
            commands.Set(stale, new Health()); // 有效记录；真正失效的 Set 仍应使回放失败。
            var pending = commands.CreateEntity();
            world.DestroyEntity(stale);
            Assert.Throws<InvalidOperationException>(() => commands.Playback());
            Assert.That(world.Get<Health>(live).Value, Is.EqualTo(5), "之前已完成的修改不回滚");
            Assert.That(world.IsAlive(pending), Is.False);
            Assert.That(commands.Count, Is.Zero);
            var recycled = world.CreateEntity();
            Assert.That(recycled.Index, Is.EqualTo(pending.Index));
            Assert.That(recycled.Version, Is.Not.EqualTo(pending.Version));
            Assert.DoesNotThrow(() => commands.Playback());
        }

        [Test]
        public void StaleCommand_CannotAffectRecycledEntity()
        {
            using var world = new World();
            using var commands = new CommandBuffer(world);
            var old = world.CreateEntity();
            commands.DestroyEntity(old);
            world.DestroyEntity(old);
            var next = world.CreateEntity();
            Assert.DoesNotThrow(() => commands.Playback(), "失效的销毁请求幂等忽略");
            Assert.That(world.PendingDespawnCount, Is.Zero);
            Assert.That(world.ProcessDespawns(), Is.Zero);
            Assert.That(world.IsAlive(next), Is.True);
        }

        [Test]
        public void Iteration_CanQueueSpawnsAndDeletesAndRejectedPlaybackPreservesBuffer()
        {
            using var world = new World(1);
            using var commands = new CommandBuffer(world);
            world.CreateEntity();
            int visited = 0;
            world.Query().ForEach(entity =>
            {
                visited++;
                for (int i = 0; i < 100; i++)
                {
                    var created = commands.CreateEntity();
                    commands.Set(created, new Health { Value = i });
                }
                commands.DestroyEntity(entity);
                Assert.Throws<InvalidOperationException>(() => commands.Playback());
            });
            Assert.That(visited, Is.EqualTo(1));
            Assert.That(commands.Count, Is.EqualTo(201));
            commands.Playback();
            Assert.That(world.EntityCount, Is.EqualTo(101));
            Assert.That(world.Query().Count, Is.EqualTo(100));
            Assert.That(world.ProcessDespawns(), Is.EqualTo(1));
            Assert.That(world.EntityCount, Is.EqualTo(100));
        }

        [Test]
        public void RecordedValue_IsCopiedAtRecordingTime()
        {
            using var world = new World();
            using var commands = new CommandBuffer(world);
            var entity = commands.CreateEntity();
            var value = new Health { Value = 10 };
            commands.Set(entity, value);
            value.Value = 20;
            commands.Playback();
            Assert.That(world.Get<Health>(entity).Value, Is.EqualTo(10));
        }

        private struct Parent { public Entity Entity; }
    }
}
