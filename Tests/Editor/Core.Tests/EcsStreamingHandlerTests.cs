using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using EjoyFramework.Core;
using EjoyFramework.Core.Ecs;
using EjoyFramework.Core.Streaming;
using NUnit.Framework;
using Is = NUnit.Framework.Is;
#if UNITY_5_3_OR_NEWER
using UnityEngine.TestTools.Constraints;
#endif

namespace EjoyFramework.Tests
{
    public partial class EcsStreamingHandlerTests
    {
        private struct Marker { public int Value; }
        private sealed class Replies : IWorldStreamingNotifier
        {
            public int Success, Failure, Unloaded;
            public Action<int, bool> OnLoaded;
            public Action<int> OnUnloaded;
            public void NotifyLoaded(int id, bool success)
            { if (success) Success++; else Failure++; OnLoaded?.Invoke(id, success); }
            public void NotifyUnloaded(int id) { Unloaded++; OnUnloaded?.Invoke(id); }
        }
        private sealed class Io : IWorldStreamingHandler
        {
            public IWorldStreamingNotifier Replies;
            public bool SyncLoad = true, SyncUnload = true, CancelCompletes, ThrowLoad, ThrowUnload;
            public int Loads, Unloads, Cancels, Lods, UnloadCx, UnloadContent;
            public void BeginLoad(int id, int layer, int cx, int cz, int content, int lod)
            { Loads++; if (SyncLoad) Replies.NotifyLoaded(id, true); if (ThrowLoad) throw new ApplicationException(); }
            public void CancelLoad(int id) { Cancels++; if (CancelCompletes) Replies.NotifyLoaded(id, false); }
            public void BeginUnload(int id, int layer, int cx, int cz, int content)
            { Unloads++; UnloadCx = cx; UnloadContent = content; if (SyncUnload) Replies.NotifyUnloaded(id); if (ThrowUnload) throw new ApplicationException(); }
            public void OnLodChanged(int id, int from, int to) => Lods++;
        }
        private sealed class Populator : ICellEntityPopulator
        {
            public int Target = 3, PerCall = 1, Calls;
            public bool ThrowAfterCreate, InvalidBudget, InvalidCursor, IncreaseBudget;
            public Action During;
            public CellPopulationContext Last;
            public bool Populate(World world, in CellPopulationContext context, ref int budget, ref int cursor)
            {
                Calls++; Last = context;
                int count = Math.Min(PerCall, budget);
                for (int i = 0; i < count && cursor < Target; i++)
                {
                    var e = world.CreateEntity();
                    world.SetPartition(e, context.PartitionKey); // Own it BEFORE initialization can throw.
                    budget--; cursor++;
                    world.Set(e, new Marker { Value = cursor });
                    if (ThrowAfterCreate) throw new ApplicationException();
                }
                During?.Invoke();
                if (InvalidBudget) budget = -1;
                if (InvalidCursor) cursor = -1;
                if (IncreaseBudget) budget = int.MaxValue;
                return cursor == Target;
            }
        }
        private sealed class Setup : IDisposable
        {
            public readonly World World = new World();
            public readonly EcsLoop Loop;
            public readonly Replies Replies = new Replies();
            public readonly Io Io = new Io();
            public readonly Populator Pop = new Populator();
            public readonly EcsStreamingHandler Bridge;
            public Setup()
            {
                Loop = new EcsLoop(World, new TickClock());
                Bridge = new EcsStreamingHandler(Replies, Loop, Pop);
                Bridge.Inner = Io; Io.Replies = Bridge;
            }
            public void Load(int id = 1) => Bridge.BeginLoad(id, 2, -3, 4, 99, 1);
            public void Pump(int create = 10, int despawn = 10) => Bridge.Pump(ref create, ref despawn);
            public void Dispose() { Loop.Dispose(); World.Dispose(); } // IO is a fake; active bridge faults are intentional tests.
        }

        [Test]
        public void ConstructorAndMetadata_ValidateBeforeIo_AndIdleDisposePreservesBorrowedObjects()
        {
            using var f = new Setup();
            Assert.Throws<ArgumentNullException>(() => new EcsStreamingHandler(null, f.Loop, f.Pop));
            Assert.Throws<ArgumentNullException>(() => new EcsStreamingHandler(f.Replies, null, f.Pop));
            Assert.Throws<ArgumentNullException>(() => new EcsStreamingHandler(f.Replies, f.Loop, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => f.Bridge.BeginLoad(-1, 0, 0, 0, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => f.Bridge.BeginLoad(1, 16, 0, 0, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => f.Bridge.BeginLoad(1, 0, 0, 0, 0, -1));
            Assert.That(f.Io.Loads, Is.Zero);
            Assert.That(f.Bridge.ActiveOperationCount, Is.Zero);
            var context = new CellPopulationContext(1, 2, -3, 4, 99, 1, 7, 8);
            Assert.That(context.CellId, Is.EqualTo(1)); Assert.That(context.Layer, Is.EqualTo(2));
            Assert.That(context.Cx, Is.EqualTo(-3)); Assert.That(context.Cz, Is.EqualTo(4));
            Assert.That(context.ContentKey, Is.EqualTo(99)); Assert.That(context.InitialLod, Is.EqualTo(1));
            Assert.That(context.Generation, Is.EqualTo(7)); Assert.That(context.PartitionKey, Is.EqualTo(8));
            f.Bridge.Dispose(); f.Bridge.Dispose();
            Assert.That(f.World.IsDisposed, Is.False);
            Assert.DoesNotThrow(() => f.Loop.RunFrame(0));
            Assert.Throws<ObjectDisposedException>(() => f.Pump());
        }

        [Test]
        public void Bridge_RequiresLiveLoopOnItsOwningThread()
        {
            using var f = new Setup();
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { new EcsStreamingHandler(f.Replies, f.Loop, f.Pop); }
                catch (Exception ex) { failure = ex; }
            });
            thread.Start(); thread.Join();
            Assert.That(failure, Is.TypeOf<InvalidOperationException>());
            f.Loop.Dispose();
            Assert.Throws<ObjectDisposedException>(() => f.Pump());
        }

        [Test]
        public void SynchronousIo_OnlyQueues_PumpSharesBudgetsAndProcessesEachOperationOnce()
        {
            using var f = new Setup();
            f.Load(1); f.Load(2);
            Assert.That(f.Pop.Calls, Is.Zero); Assert.That(f.World.EntityCount, Is.Zero);
            Assert.That(f.Replies.Success, Is.Zero);
            int create = 1, despawn = 0;
            f.Bridge.Pump(ref create, ref despawn);
            Assert.That(create, Is.Zero); Assert.That(f.Pop.Calls, Is.EqualTo(1));
            Assert.That(f.World.EntityCount, Is.EqualTo(1));
            Assert.That(f.World.Query().Count, Is.EqualTo(1)); // Readiness gating belongs to the populator/gameplay queries.
            f.Pump(0, 0);
            Assert.That(f.Pop.Calls, Is.EqualTo(1));
            f.Pump(10, 0);
            Assert.That(f.Pop.Calls, Is.EqualTo(3)); // One call per operation, not repeated until budget exhaustion.
            Assert.That(f.Replies.Success, Is.Zero);
            f.Pump(); f.Pump();
            Assert.That(f.Replies.Success, Is.EqualTo(2));
            Assert.That(f.World.EntityCount, Is.EqualTo(6));
        }

        [Test]
        public void DespawnBudget_IsSharedAcrossUnloadingOperations()
        {
            using var f = new Setup(); f.Pop.Target = 1;
            f.Load(1); f.Load(2); f.Pump(2, 0);
            f.Bridge.BeginUnload(1, 0, 0, 0, 0); f.Bridge.BeginUnload(2, 0, 0, 0, 0);
            int create = 0, despawn = 1;
            f.Bridge.Pump(ref create, ref despawn);
            Assert.That(despawn, Is.Zero);
            Assert.That(f.World.PendingDespawnCount, Is.EqualTo(1));
            Assert.That(f.Io.Unloads, Is.Zero);
            f.Pump(0, 0);
            Assert.That(f.World.PendingDespawnCount, Is.EqualTo(1));
            f.Loop.RunFrame(0); f.Pump(0, 1);
            Assert.That(f.Replies.Unloaded, Is.EqualTo(1));
            Assert.That(f.World.PendingDespawnCount, Is.EqualTo(1));
            f.Loop.RunFrame(0); f.Pump(0, 0);
            Assert.That(f.Replies.Unloaded, Is.EqualTo(2));
        }

        [Test]
        public void PartialDespawnFailure_ExhaustsBudgetBeforeOtherOperations()
        {
            using var f = new Setup();
            f.Pop.Target = f.Pop.PerCall = 2;
            f.Load(1); f.Pump(2, 0);
            var members = new BudgetMembers { Items = new List<Entity>() };
            f.World.ForEachInPartition(f.Pop.Last.PartitionKey, ref members);
            f.Load(2); f.Pump(2, 0);
            var other = new BudgetMembers { Items = new List<Entity>() };
            f.World.ForEachInPartition(f.Pop.Last.PartitionKey, ref other);
            f.Bridge.BeginUnload(1, 0, 0, 0, 0);
            f.Bridge.BeginUnload(2, 0, 0, 0, 0);
            int create = 0, despawn = 2;
            // 测试内损坏后继索引，在第一条请求已成功后确定性触发异常。
            // 不模拟真实 OOM，不添加生产环境分配器/回调注入钩子；finally 恢复结构。
            int previous = ReplacePartitionNext(f.World, members.Items[0].Index, int.MaxValue);
            try
            {
                Assert.DoesNotThrow(() => f.Bridge.Pump(ref create, ref despawn));
                Assert.That(despawn, Is.Zero, "异常后无法确定已消费数量，剩余预算必须作废");
                Assert.That(f.World.PendingDespawnCount, Is.EqualTo(1));
                Assert.That(f.World.IsDespawning(members.Items[0]), Is.True);
                foreach (var entity in other.Items) Assert.That(f.World.IsDespawning(entity), Is.False);
                Assert.That(f.Io.Unloads, Is.Zero);
            }
            finally { ReplacePartitionNext(f.World, members.Items[0].Index, previous); }
        }

        private struct BudgetMembers : IEntityVisitor
        {
            public List<Entity> Items;
            public void Visit(Entity entity) => Items.Add(entity);
        }

        private static int ReplacePartitionNext(World world, int index, int next)
        {
            var field = typeof(World).GetField("m_Slots", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            var slots = (Array)field.GetValue(world);
            object slot = slots.GetValue(index);
            var link = slot.GetType().GetField("PartitionNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(link, Is.Not.Null);
            int previous = (int)link.GetValue(slot);
            link.SetValue(slot, next);
            slots.SetValue(slot, index);
            return previous;
        }

        [Test]
        public void CancelBeforeIo_LateSuccessIsUnloadedBeforeFailureReply()
        {
            using var f = new Setup();
            f.Io.SyncLoad = false; f.Io.SyncUnload = false;
            f.Load(); f.Bridge.CancelLoad(1); f.Pump();
            Assert.That(f.Io.Cancels, Is.EqualTo(1));
            Assert.That(f.Replies.Failure, Is.Zero);
            f.Bridge.NotifyLoaded(1, true);
            Assert.That(f.Io.Unloads, Is.Zero);
            f.Pump(0, 0);
            Assert.That(f.Io.Unloads, Is.EqualTo(1));
            Assert.That(f.Replies.Failure, Is.Zero); Assert.That(f.Pop.Calls, Is.Zero);
            f.Bridge.NotifyUnloaded(1); f.Pump(0, 0);
            Assert.That(f.Replies.Failure, Is.EqualTo(1)); Assert.That(f.Replies.Success, Is.Zero);
            Assert.That(f.Bridge.ActiveOperationCount, Is.Zero);
        }

        [Test]
        public void CancelDuringPopulation_DrainsPhysicalEntitiesBeforeIoAndFailureReply()
        {
            using var f = new Setup();
            f.Pop.During = () => f.Bridge.CancelLoad(1);
            f.Load(); f.Pump(1, 0);
            var context = f.Pop.Last;
            Assert.That(f.World.GetPartitionCount(context.PartitionKey), Is.EqualTo(1));
            f.Pump(0, 1);
            Assert.That(f.World.PendingDespawnCount, Is.EqualTo(1));
            Assert.That(f.World.GetPartitionCount(context.PartitionKey), Is.EqualTo(1));
            Assert.That(f.Io.Unloads, Is.Zero); Assert.That(f.Replies.Success, Is.Zero);
            f.Loop.RunFrame(0); f.Pump(0, 0);
            Assert.That(f.Io.Unloads, Is.EqualTo(1)); Assert.That(f.Replies.Failure, Is.EqualTo(1));
            Assert.That(f.World.ReleasePartitionKey(context.PartitionKey), Is.False);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void PopulatorFailureOrInvalidOutputs_CancelAndDrainOwnedEntities(int failure)
        {
            using var f = new Setup();
            f.Pop.ThrowAfterCreate = failure == 0;
            f.Pop.InvalidBudget = failure == 1;
            f.Pop.InvalidCursor = failure == 2;
            f.Pop.IncreaseBudget = failure == 3;
            f.Load();
            int create = 3, despawn = 0;
            Assert.DoesNotThrow(() => f.Bridge.Pump(ref create, ref despawn));
            Assert.That(create, Is.Zero); // Unknown/invalid consumption cannot be reused by another operation.
            Assert.That(f.Bridge.TryGetFault(1, out var error), Is.True);
            Assert.That(error, Is.Not.Null);
            f.Pump(0, 10); f.Loop.RunFrame(0); f.Pump(0, 0);
            Assert.That(f.World.EntityCount, Is.Zero);
            Assert.That(f.Replies.Success, Is.Zero); Assert.That(f.Replies.Failure, Is.EqualTo(1));
        }

        [Test]
        public void NormalUnload_WaitsForCleanup_UsesCapturedMetadata_AndAcknowledgesOnce()
        {
            using var f = new Setup(); f.Pop.Target = 1;
            f.Load(); f.Pump();
            Assert.That(f.Bridge.TryGetContext(1, out var context), Is.True);
            f.Bridge.BeginUnload(1, 9, 999, 999, 999); // Never consult mutable manager metadata for cleanup.
            int create = 0, despawn = 1;
            f.Bridge.Pump(ref create, ref despawn);
            Assert.That(despawn, Is.Zero); Assert.That(f.Io.Unloads, Is.Zero);
            Assert.That(f.World.GetPartitionCount(context.PartitionKey), Is.EqualTo(1));
            f.Pump(0, 0); Assert.That(f.Replies.Unloaded, Is.Zero);
            f.Loop.RunFrame(0); f.Pump(0, 0);
            Assert.That(f.Io.UnloadCx, Is.EqualTo(-3)); Assert.That(f.Io.UnloadContent, Is.EqualTo(99));
            Assert.That(f.Replies.Unloaded, Is.EqualTo(1)); Assert.That(f.Replies.Failure, Is.Zero);
            Assert.That(f.Bridge.ActiveOperationCount, Is.Zero);
            Assert.DoesNotThrow(() => f.Bridge.Dispose());
        }

        [Test]
        public void ReentrantReplies_CanUnloadAndStartNextGenerationWithoutOldWorkOverwritingIt()
        {
            using var f = new Setup(); f.Pop.Target = 1;
            f.Replies.OnLoaded = (id, success) => { if (success) f.Bridge.BeginUnload(id, 0, 0, 0, 0); };
            f.Replies.OnUnloaded = id => f.Load(id);
            f.Load(); f.Pump(); var first = f.Pop.Last;
            f.Pump(0, 10); f.Loop.RunFrame(0); f.Pump(0, 0);
            Assert.That(f.Bridge.TryGetContext(1, out var next), Is.True);
            Assert.That(next.Generation, Is.GreaterThan(first.Generation));
            Assert.That(next.PartitionKey, Is.Not.EqualTo(first.PartitionKey));
            Assert.That(f.World.GetPartitionCount(next.PartitionKey), Is.Zero);
            f.Replies.OnLoaded = null; f.Replies.OnUnloaded = null;
            f.Pump();
            Assert.That(f.Pop.Last.Generation, Is.EqualTo(next.Generation));
            Assert.That(f.World.GetPartitionCount(next.PartitionKey), Is.EqualTo(1));
        }

        [Test]
        public void SynchronousCancelCompletion_IsDeferredAndAllowsZeroBudgetRetirement()
        {
            using var f = new Setup(); f.Io.SyncLoad = false; f.Io.CancelCompletes = true;
            f.Load(); f.Bridge.CancelLoad(1);
            Assert.That(f.Replies.Failure, Is.Zero);
            f.Pump(0, 0);
            Assert.That(f.Replies.Failure, Is.EqualTo(1)); Assert.That(f.Pop.Calls, Is.Zero);
        }

        [Test]
        public void InnerThrowAfterStartingIo_IsRetainedUntilRealTerminalCallbacks()
        {
            using var f = new Setup(); f.Io.SyncLoad = false; f.Io.ThrowLoad = true;
            Assert.DoesNotThrow(() => f.Load());
            Assert.That(f.Bridge.TryGetFault(1, out _), Is.True);
            f.Pump(0, 0);
            Assert.That(f.Bridge.ActiveOperationCount, Is.EqualTo(1)); Assert.That(f.Replies.Failure, Is.Zero);
            f.Bridge.NotifyLoaded(1, true); f.Io.SyncUnload = false; f.Io.ThrowUnload = true;
            Assert.DoesNotThrow(() => f.Pump(0, 0));
            Assert.That(f.Bridge.ActiveOperationCount, Is.EqualTo(1));
            Assert.That(f.Replies.Failure, Is.Zero);
            f.Bridge.NotifyUnloaded(1); f.Pump(0, 0);
            Assert.That(f.Replies.Failure, Is.EqualTo(1));
        }

        [Test]
        public void LodOnlyForwardsWhileLoaded_AndInnerAndDisposeRequireIdle()
        {
            using var f = new Setup(); f.Pop.Target = 1;
            f.Load(); f.Bridge.OnLodChanged(1, 0, 2);
            Assert.That(f.Io.Lods, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => f.Bridge.Inner = new Io());
            Assert.Throws<InvalidOperationException>(() => f.Bridge.Dispose());
            f.Pump(); f.Bridge.OnLodChanged(1, 0, 2);
            Assert.That(f.Io.Lods, Is.EqualTo(1));
            f.Bridge.BeginUnload(1, 0, 0, 0, 0); f.Bridge.OnLodChanged(1, 2, 3);
            Assert.That(f.Io.Lods, Is.EqualTo(1));
        }

        private sealed class PumpingSystem : ISystem
        {
            public EcsStreamingHandler Bridge;
            public void Update(World w, CommandBuffer c, in EcsTime t)
            { int create = 1, despawn = 1; Bridge.Pump(ref create, ref despawn); }
        }

        [Test]
        public void Pump_RejectsWrongThreadReentryRunningFaultedLoopAndInvalidBudgets()
        {
            using var f = new Setup();
            Exception wrongThread = null;
            var thread = new Thread(() => { try { f.Pump(); } catch (Exception ex) { wrongThread = ex; } });
            thread.Start(); thread.Join();
            Assert.That(wrongThread, Is.TypeOf<InvalidOperationException>());
            Assert.Throws<ArgumentOutOfRangeException>(() => f.Pump(-1, 0));
            f.Pop.During = () => Assert.Throws<InvalidOperationException>(() => f.Pump());
            f.Load(); f.Pump(1, 0);
            f.Loop.Add(EcsPhase.Presentation, new PumpingSystem { Bridge = f.Bridge });
            Assert.Throws<InvalidOperationException>(() => f.Loop.RunFrame(0));
            Assert.That(f.Loop.IsFaulted, Is.True);
            Assert.Throws<InvalidOperationException>(() => f.Pump());
        }

        [Test]
        public void WarmPendingPopulationPump_HasNoManagedAllocations()
        {
            using var f = new Setup(); f.Pop.Target = 10000;
            f.World.EnsureEntityCapacity(10000); f.World.EnsureCapacity<Marker>(10000);
            f.Load();
            TestDelegate body = () => { int create = 1, despawn = 0; f.Bridge.Pump(ref create, ref despawn); };
            for (int i = 0; i < 8; i++) body();
#if UNITY_5_3_OR_NEWER
            Assert.That(body, Is.Not.AllocatingGCMemory());
#else
            long before = GC.GetAllocatedBytesForCurrentThread(); body();
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
#endif
            Assert.That(f.Pop.Calls, Is.EqualTo(9));
        }

        [Test]
        public void AllocationDetector_SeesKnownAllocation()
        {
            byte[] retained = null; TestDelegate body = () => retained = new byte[1024]; body();
#if UNITY_5_3_OR_NEWER
            Assert.That(body, new AllocatingGCMemoryConstraint());
#else
            long before = GC.GetAllocatedBytesForCurrentThread(); body();
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.GreaterThanOrEqualTo(1024));
#endif
            GC.KeepAlive(retained);
        }
    }
}
