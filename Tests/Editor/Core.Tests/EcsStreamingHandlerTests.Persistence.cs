using EjoyFramework.Core;
using EjoyFramework.Core.Ecs;
using EjoyFramework.Core.Serialization;
using EjoyFramework.Core.Streaming;
using NUnit.Framework;

namespace EjoyFramework.Tests
{
    public partial class EcsStreamingHandlerTests
    {
        private sealed class Serializer : ICellStateSerializer
        {
            public EcsStreamingHandler Bridge;
            public World World;
            public int Captures, Restores, CapturedMembers, RestoredMembers;
            public void Capture(int id, int layer, int cx, int cz, int content, ByteBuffer buffer)
            {
                Captures++;
                Assert.That(Bridge.TryGetContext(id, out var context), Is.True);
                CapturedMembers = World.GetPartitionCount(context.PartitionKey);
                buffer.WriteInt(CapturedMembers);
            }
            public void Restore(int id, int layer, int cx, int cz, int content, ByteBuffer buffer)
            {
                Restores++;
                Assert.That(Bridge.TryGetContext(id, out var context), Is.True);
                RestoredMembers = World.GetPartitionCount(context.PartitionKey);
            }
        }

        [Test]
        public void Persistence_RestoresOnlyCompletePopulation_AndCapturesBeforeUnregisteredCellCleanup()
        {
            Framework.MarkMainThread();
            var manager = new WorldStreamingManager();
            using var world = new World(); using var loop = new EcsLoop(world, new TickClock());
            var store = new WorldStateStore(); store.SetCell(0, -3, 7, new byte[] { 1 }, 0, 1);
            var serializer = new Serializer { World = world };
            var persistence = new PersistentStreamingHandler(manager, manager, store, serializer);
            var pop = new Populator();
            var bridge = new EcsStreamingHandler(persistence, loop, pop);
            var io = new Io { Replies = bridge }; bridge.Inner = io; persistence.Inner = bridge;
            serializer.Bridge = bridge;
            manager.ConfigureLayer(0, new StreamingLayerSettings()); manager.SetHandler(persistence);
            try
            {
                int id = manager.RegisterCell(0, -3, 7, 91);
                Assert.That(manager.RequireLoaded(id), Is.True);
                Pump(bridge, 1, 0);
                Assert.That(serializer.Restores, Is.Zero);
                Assert.That(manager.GetCellState(id), Is.EqualTo(StreamingCellState.Loading));
                Pump(bridge, 1, 0); Pump(bridge, 1, 0);
                Assert.That(serializer.Restores, Is.EqualTo(1));
                Assert.That(serializer.RestoredMembers, Is.EqualTo(3));
                Assert.That(manager.GetCellState(id), Is.EqualTo(StreamingCellState.Loaded));
                Assert.That(bridge.TryGetContext(id, out var context), Is.True);
                Assert.That(manager.UnregisterCell(id), Is.True);
                Assert.That(manager.TryGetCell(id, out _), Is.False);
                Assert.That(serializer.Captures, Is.EqualTo(1));
                Assert.That(serializer.CapturedMembers, Is.EqualTo(3));
                Assert.That(world.PendingDespawnCount, Is.Zero);
                Pump(bridge, 0, 3);
                Assert.That(world.GetPartitionCount(context.PartitionKey), Is.EqualTo(3));
                Assert.That(io.Unloads, Is.Zero);
                loop.RunFrame(0); Pump(bridge, 0, 0);
                Assert.That(io.UnloadCx, Is.EqualTo(-3)); Assert.That(io.UnloadContent, Is.EqualTo(91));
                Assert.That(bridge.ActiveOperationCount, Is.Zero);
                bridge.Dispose();
            }
            finally { manager.Shutdown(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Persistence_NeverRestoresOrCapturesCancelledOrFailedPartialPopulation(bool fail)
        {
            Framework.MarkMainThread();
            var manager = new WorldStreamingManager();
            using var world = new World(); using var loop = new EcsLoop(world, new TickClock());
            var store = new WorldStateStore(); store.SetCell(0, 0, 0, new byte[] { 1 }, 0, 1);
            var serializer = new Serializer { World = world };
            var persistence = new PersistentStreamingHandler(manager, manager, store, serializer);
            var pop = new Populator { ThrowAfterCreate = fail };
            var bridge = new EcsStreamingHandler(persistence, loop, pop);
            bridge.Inner = new Io { Replies = bridge }; persistence.Inner = bridge; serializer.Bridge = bridge;
            manager.ConfigureLayer(0, new StreamingLayerSettings()); manager.SetHandler(persistence);
            try
            {
                int id = manager.RegisterCell(0, 0, 0, 0);
                manager.RequireLoaded(id); Pump(bridge, 1, 0);
                if (!fail) manager.UnregisterCell(id);
                Pump(bridge, 0, 10); loop.RunFrame(0); Pump(bridge, 0, 0);
                Assert.That(serializer.Restores, Is.Zero); Assert.That(serializer.Captures, Is.Zero);
                Assert.That(world.EntityCount, Is.Zero); Assert.That(bridge.ActiveOperationCount, Is.Zero);
                bridge.Dispose();
            }
            finally { manager.Shutdown(); }
        }

        private static void Pump(EcsStreamingHandler bridge, int create, int despawn)
            => bridge.Pump(ref create, ref despawn);
    }
}
