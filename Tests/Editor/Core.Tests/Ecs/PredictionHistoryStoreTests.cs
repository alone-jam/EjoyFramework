using System;
using System.Threading;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;

namespace EjoyFramework.Tests.Ecs
{
    public class PredictionHistoryStoreTests
    {
        private struct State : IPredicted<State>
        {
            public int Value;
            public int Tolerance;
            public bool ThrowOnVerify;

            public bool VerifyPrediction(in State predicted)
            {
                bool matches = Math.Abs((long)Value - predicted.Value) <= Tolerance;
                Value = -999; // Deliberately mutate the receiver, even on the throwing path.
                if (ThrowOnVerify) throw new InvalidOperationException("Verification failed.");
                return matches;
            }
        }

        private World m_World;
        private PredictionHistoryStore<State> m_Store;
        private AuthoritativeBaselineStore<State> m_Authority;
        private PredictionHistoryStore<State>.Binding m_Binding;
        private AuthoritativeBaselineStore<State>.Binding m_AuthorityBinding;

        [SetUp]
        public void SetUp()
        {
            m_World = new World();
            m_Store = new PredictionHistoryStore<State>(m_World, entityCapacity: 0, historyLength: 4);
            m_Authority = new AuthoritativeBaselineStore<State>(m_World);
            m_Binding = m_Store.Bind(Spawn(m_World));
            m_AuthorityBinding = m_Authority.Bind(m_Binding.Entity);
        }

        [TearDown]
        public void TearDown()
        {
            m_Store.Dispose();
            m_Authority.Dispose();
            m_World.Dispose();
        }

        private static Entity Spawn(World world)
        {
            var entity = world.CreateEntity();
            world.Set(entity, new State { Value = 123 });
            return entity;
        }

        private void Record(uint tick, int value)
        {
            var state = new State { Value = value };
            Assert.That(m_Store.TryRecord(in m_Binding, tick, in state), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
        }

        private void At(uint tick, int expected)
        {
            Assert.That(m_Store.TryGet(in m_Binding, tick, out var state), Is.True);
            Assert.That(state.Value, Is.EqualTo(expected));
        }

        private void Missing(uint tick)
        {
            Assert.That(m_Store.TryGet(in m_Binding, tick, out var state), Is.False);
            Assert.That(state, Is.EqualTo(default(State)));
        }

        private PredictionVerificationResult Verify()
            => m_Store.VerifyPrediction(m_Authority, in m_AuthorityBinding, in m_Binding);

        [Test]
        public void EmptyHistoryAndDefaultTokenHaveDistinctIdentityFromMissingValues()
        {
            Missing(1);
            Assert.That(m_Binding.Entity, Is.EqualTo(m_AuthorityBinding.Entity));
            Assert.That(m_Store.HistoryLength, Is.EqualTo(4));
            Assert.That(Verify(), Is.EqualTo(PredictionVerificationResult.MissingAuthority));
            var state = new State();
            Assert.That(m_Store.TryRecord(default, 1, in state), Is.EqualTo(PredictionHistoryWriteResult.StaleBinding));
            Assert.That(m_Store.TruncateAfter(default, 1), Is.EqualTo(PredictionHistoryWriteResult.StaleBinding));
            Assert.That(m_Store.TryGet(default, 1, out var copy), Is.False);
            Assert.That(copy, Is.EqualTo(default(State)));
            Assert.That(m_Store.Unbind(default), Is.False);
            Record(1, 1); // Private identity baseline has no authoritative value, but membership is valid.
            At(1, 1);
        }

        [Test]
        public void RecordAndRetrievalCopyValuesAndNeverWriteWorld()
        {
            var input = new State { Value = int.MinValue };
            Assert.That(m_Store.TryRecord(in m_Binding, 10, in input), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            input.Value = int.MaxValue;
            Assert.That(m_Store.TryGet(in m_Binding, 10, out var output), Is.True);
            output.Value = 77;
            At(10, int.MinValue);
            Assert.That(m_World.Get<State>(m_Binding.Entity).Value, Is.EqualTo(123));
        }

        [Test]
        public void ExactGapsAreNeverClampedAndLatestMayBeRecomputedButOlderWritesFail()
        {
            Record(10, 10);
            Record(12, 12);
            Record(12, 120);
            foreach (uint tick in new uint[] { 0, 9, 11, 13, uint.MaxValue }) Missing(tick);
            var state = new State { Value = 999 };
            Assert.That(m_Store.TryRecord(in m_Binding, 10, in state), Is.EqualTo(PredictionHistoryWriteResult.OlderTick));
            Assert.That(m_Store.TryRecord(in m_Binding, 11, in state), Is.EqualTo(PredictionHistoryWriteResult.OlderTick));
            At(10, 10);
            At(12, 120);
        }

        [Test]
        public void WindowIsMeasuredInTicksAndTagsDisambiguateModuloCollisions()
        {
            Record(1, 1);
            Record(4, 4);
            At(1, 1); // Inclusive lower boundary for a four-tick window.
            Record(5, 5); // Collides with tick 1.
            Missing(1);
            At(4, 4);
            At(5, 5);
            Record(10, 10); // Sparse jump evicts 4/5 even though fewer than four samples exist.
            Missing(4);
            Missing(5);
            Missing(6);
            At(10, 10);
        }

        [Test]
        public void TruncateDiscardsFutureAndAllowsAnchorOverwriteAndRebuild()
        {
            Record(10, 10);
            Record(11, 11);
            Record(12, 12);
            Assert.That(m_Store.TruncateAfter(in m_Binding, 10), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            At(10, 10);
            Missing(11);
            Missing(12);
            Record(10, 100);
            Record(11, 110);
            Missing(12); // Advancing the head must not expose the discarded branch.
            Record(13, 130);
            Missing(12);
            At(10, 100);
            At(11, 110);
        }

        [Test]
        public void RewindingToGapNeverResurrectsEvictedTagsAndPreservesRetentionFloor()
        {
            Record(1, 1);
            Record(2, 2);
            Record(6, 6); // Floor is 3; tick 1's physical tag has not been overwritten.
            Assert.That(m_Store.TruncateAfter(in m_Binding, 3), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            Missing(1);
            Missing(2);
            Missing(3);
            Missing(6);
            Assert.That(m_Store.TruncateAfter(in m_Binding, 2), Is.EqualTo(PredictionHistoryWriteResult.OutsideWindow));
            var state = new State();
            Assert.That(m_Store.TryRecord(in m_Binding, 2, in state), Is.EqualTo(PredictionHistoryWriteResult.OlderTick));
            Record(3, 30);
            Record(5, 50);
            Missing(1);
            Missing(6);
            At(3, 30);
        }

        [Test]
        public void EmptyEqualAndFutureTruncationAreNoOpsButTooOldTruncationPreservesData()
        {
            Assert.That(m_Store.TruncateAfter(in m_Binding, 100), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            Record(1, 1); // Empty truncation did not introduce a head or floor.
            Record(10, 10);
            foreach (uint tick in new uint[] { 10, uint.MaxValue })
                Assert.That(m_Store.TruncateAfter(in m_Binding, tick), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            Assert.That(m_Store.TruncateAfter(in m_Binding, 6), Is.EqualTo(PredictionHistoryWriteResult.OutsideWindow));
            Record(10, 100);
            At(10, 100);
        }

        [Test]
        public void ZeroIsNotARecordOrTruncateTickAndMaxTickDoesNotWrap()
        {
            var state = new State();
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Store.TryRecord(in m_Binding, 0, in state));
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Store.TruncateAfter(in m_Binding, 0));
            Record(uint.MaxValue - 1, 1);
            Record(uint.MaxValue, 2);
            Assert.That(m_Store.TryRecord(in m_Binding, 1, in state), Is.EqualTo(PredictionHistoryWriteResult.OlderTick));
            Assert.That(m_Store.TruncateAfter(in m_Binding, 1), Is.EqualTo(PredictionHistoryWriteResult.OutsideWindow));
            Assert.That(m_Store.TruncateAfter(in m_Binding, uint.MaxValue - 1), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            Missing(uint.MaxValue);
            Record(uint.MaxValue, 3);
            At(uint.MaxValue - 1, 1);
            At(uint.MaxValue, 3);
            Missing(0);
        }

        [Test]
        public void LengthOneRetainsOnlyLatestAndStillAllowsReplacingIt()
        {
            using var store = new PredictionHistoryStore<State>(m_World, historyLength: 1);
            var binding = store.Bind(m_Binding.Entity);
            var value = new State { Value = 1 };
            Assert.That(store.TryRecord(in binding, 1, in value), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            Assert.That(store.TryRecord(in binding, 2, in value), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            value.Value = 2;
            Assert.That(store.TryRecord(in binding, 2, in value), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            Assert.That(store.TruncateAfter(in binding, 1), Is.EqualTo(PredictionHistoryWriteResult.OutsideWindow));
            Assert.That(store.TryGet(in binding, 1, out _), Is.False);
            Assert.That(store.TryGet(in binding, 2, out var copy), Is.True);
            Assert.That(copy.Value, Is.EqualTo(2));
        }

        [TestCase(9, PredictionVerificationResult.Match)]
        [TestCase(12, PredictionVerificationResult.Match)]
        [TestCase(13, PredictionVerificationResult.Mismatch)]
        public void VerificationUsesAuthorityToleranceOnCopiesAtExactAuthorityTick(int prediction, PredictionVerificationResult expected)
        {
            var authority = new State { Value = 10, Tolerance = 2 };
            Assert.That(m_Authority.TrySet(in m_AuthorityBinding, 10, in authority), Is.EqualTo(BaselineWriteResult.Accepted));
            authority.Value = -88;
            var predicted = new State { Value = prediction, ThrowOnVerify = true };
            Assert.That(m_Store.TryRecord(in m_Binding, 10, in predicted), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            Record(11, 999); // Comparing the latest prediction instead would be wrong.
            for (int i = 0; i < 3; i++) Assert.That(Verify(), Is.EqualTo(expected));
            Assert.That(m_Authority.TryGet(in m_AuthorityBinding, out uint tick, out var copy), Is.True);
            Assert.That(tick, Is.EqualTo(10));
            Assert.That(copy.Value, Is.EqualTo(10));
            At(10, prediction);
            Assert.That(m_World.Get<State>(m_Binding.Entity).Value, Is.EqualTo(123));
        }

        [Test]
        public void MissingAuthorityAndExactHistoryAreDistinctAndDoNotCallVerifier()
        {
            Record(10, 10);
            Assert.That(Verify(), Is.EqualTo(PredictionVerificationResult.MissingAuthority));
            var state = new State { Value = 10, ThrowOnVerify = true };
            Assert.That(m_Authority.TrySet(in m_AuthorityBinding, 11, in state), Is.EqualTo(BaselineWriteResult.Accepted));
            Record(12, 12);
            Assert.That(Verify(), Is.EqualTo(PredictionVerificationResult.MissingHistory));
            Record(20, 20);
            Assert.That(Verify(), Is.EqualTo(PredictionVerificationResult.MissingHistory));
        }

        [Test]
        public void ThrowingVerifierPreservesAuthorityAndPrediction()
        {
            var state = new State { Value = 10, ThrowOnVerify = true };
            Assert.That(m_Authority.TrySet(in m_AuthorityBinding, 10, in state), Is.EqualTo(BaselineWriteResult.Accepted));
            Record(10, 10);
            Assert.Throws<InvalidOperationException>(() => Verify());
            Assert.That(m_Authority.TryGet(in m_AuthorityBinding, out uint tick, out var copy), Is.True);
            Assert.That(tick, Is.EqualTo(10));
            Assert.That(copy.Value, Is.EqualTo(10));
            At(10, 10);
        }

        [TestCase("rebind")]
        [TestCase("reset")]
        [TestCase("unbind")]
        public void OldTokensCannotReadRecordTruncateOrUnbindReplacement(string operation)
        {
            Record(10, 10);
            var old = m_Binding;
            if (operation == "reset") m_Store.ResetTimeline();
            if (operation == "unbind") Assert.That(m_Store.Unbind(in old), Is.True);
            m_Binding = m_Store.Bind(old.Entity);
            Missing(10);
            Record(1, 1);
            var value = new State();
            Assert.That(m_Store.TryGet(in old, 1, out _), Is.False);
            Assert.That(m_Store.TryRecord(in old, 2, in value), Is.EqualTo(PredictionHistoryWriteResult.StaleBinding));
            Assert.That(m_Store.TruncateAfter(in old, 1), Is.EqualTo(PredictionHistoryWriteResult.StaleBinding));
            Assert.That(m_Store.Unbind(in old), Is.False);
            Assert.That(m_Store.VerifyPrediction(m_Authority, in m_AuthorityBinding, in old),
                Is.EqualTo(PredictionVerificationResult.InvalidBinding));
            At(1, 1);
        }

        [TestCase("remove")]
        [TestCase("remove-readd")]
        [TestCase("destroy-reuse")]
        [TestCase("despawn")]
        public void UnavailableMembershipRejectsAccessWithoutExplicitUnbind(string operation)
        {
            Record(10, 10);
            var entity = m_Binding.Entity;
            if (operation.StartsWith("remove")) m_World.Remove<State>(entity);
            if (operation == "remove-readd") m_World.Set(entity, new State());
            if (operation == "destroy-reuse") { m_World.DestroyEntity(entity); Assert.That(Spawn(m_World).Index, Is.EqualTo(entity.Index)); }
            if (operation == "despawn") m_World.RequestDespawn(entity);
            Missing(10);
            var value = new State();
            Assert.That(m_Store.TryRecord(in m_Binding, 11, in value), Is.EqualTo(PredictionHistoryWriteResult.TargetUnavailable));
            Assert.That(m_Store.TruncateAfter(in m_Binding, 10), Is.EqualTo(PredictionHistoryWriteResult.TargetUnavailable));
            Assert.That(Verify(), Is.EqualTo(PredictionVerificationResult.InvalidBinding));
            Assert.That(m_Store.Unbind(in m_Binding), Is.True);
        }

        [Test]
        public void FreshBindRequiredAfterMembershipChangeAndForeignTokensAreInvalid()
        {
            Record(10, 10);
            m_World.Remove<State>(m_Binding.Entity);
            m_World.Set(m_Binding.Entity, new State());
            m_Binding = m_Store.Bind(m_Binding.Entity);
            Missing(10);
            Record(1, 1);
            Assert.That(Verify(), Is.EqualTo(PredictionVerificationResult.InvalidBinding)); // Authority membership still old.
            using var other = new PredictionHistoryStore<State>(m_World);
            var foreign = other.Bind(m_Binding.Entity);
            var state = new State();
            Assert.That(m_Store.TryRecord(in foreign, 1, in state), Is.EqualTo(PredictionHistoryWriteResult.StaleBinding));
            Assert.That(m_Store.TruncateAfter(in foreign, 1), Is.EqualTo(PredictionHistoryWriteResult.StaleBinding));
            Assert.That(m_Store.TryGet(in foreign, 1, out _), Is.False);
            Assert.That(m_Store.Unbind(in foreign), Is.False);
            At(1, 1);
        }

        [Test]
        public void VerificationValidatesBothBindingsAndFullEntityBeforeMissingAuthority()
        {
            Assert.Throws<ArgumentNullException>(() => m_Store.VerifyPrediction(null, in m_AuthorityBinding, in m_Binding));
            var different = m_Authority.Bind(Spawn(m_World));
            Assert.That(m_Store.VerifyPrediction(m_Authority, in different, in m_Binding), Is.EqualTo(PredictionVerificationResult.InvalidBinding));
            using var world = new World();
            using var authority = new AuthoritativeBaselineStore<State>(world);
            var foreign = authority.Bind(Spawn(world)); // Same index/version, different World.
            Assert.That(m_Store.VerifyPrediction(authority, in foreign, in m_Binding), Is.EqualTo(PredictionVerificationResult.InvalidBinding));
            using var sameWorld = new AuthoritativeBaselineStore<State>(m_World);
            foreign = sameWorld.Bind(m_Binding.Entity);
            Assert.That(m_Store.VerifyPrediction(m_Authority, in foreign, in m_Binding), Is.EqualTo(PredictionVerificationResult.InvalidBinding));
            Assert.That(m_Store.VerifyPrediction(m_Authority, default, in m_Binding), Is.EqualTo(PredictionVerificationResult.InvalidBinding));
            m_Authority.ResetTimeline();
            Assert.That(Verify(), Is.EqualTo(PredictionVerificationResult.InvalidBinding));
        }

        [Test]
        public void BindRejectsDefaultForeignMissingAndDespawningTargetsWithoutChangingHistory()
        {
            Record(10, 10);
            using var world = new World();
            Assert.Throws<InvalidOperationException>(() => m_Store.Bind(default));
            Assert.Throws<InvalidOperationException>(() => m_Store.Bind(Spawn(world)));
            Assert.Throws<InvalidOperationException>(() => m_Store.Bind(m_World.CreateEntity()));
            var target = Spawn(m_World);
            m_World.RequestDespawn(target);
            Assert.Throws<InvalidOperationException>(() => m_Store.Bind(target));
            At(10, 10);
        }

        [Test]
        public void CapacityValidationRejectsWideProductsBeforeAllocationAndPreservesExistingHistory()
        {
            Assert.Throws<ArgumentNullException>(() => new PredictionHistoryStore<State>(null));
            foreach (int capacity in new[] { -1, int.MaxValue, 1500000000 })
                Assert.Throws<ArgumentOutOfRangeException>(() => new PredictionHistoryStore<State>(m_World, capacity, 2));
            foreach (int length in new[] { -1, 0, int.MaxValue })
                Assert.Throws<ArgumentOutOfRangeException>(() => new PredictionHistoryStore<State>(m_World, 2, length));
            Record(10, 10);
            int previous = m_Store.Capacity;
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Store.EnsureCapacity(1000000000));
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Store.EnsureCapacity(-1));
            Assert.That(m_Store.Capacity, Is.EqualTo(previous));
            At(10, 10);
            using var hugeLength = new PredictionHistoryStore<State>(m_World, 0, 0x7FEFFFFF);
            Assert.Throws<ArgumentOutOfRangeException>(() => hugeLength.Bind(Spawn(m_World)));
            Assert.That(hugeLength.Capacity, Is.Zero);
        }

        [Test]
        public void GrowthAndReservePreserveTenThousandIndependentEntityRangesAndTokens()
        {
            Record(10, 10);
            const int count = 10000;
            m_World.EnsureEntityCapacity(count + 1);
            m_World.EnsureCapacity<State>(count + 1);
            var bindings = new PredictionHistoryStore<State>.Binding[count];
            for (int i = 0; i < count; i++)
            {
                bindings[i] = m_Store.Bind(Spawn(m_World));
                var state = new State { Value = i };
                Assert.That(m_Store.TryRecord(in bindings[i], 5, in state), Is.EqualTo(PredictionHistoryWriteResult.Accepted));
            }
            m_Store.EnsureCapacity(20000);
            m_Store.EnsureCapacity(0);
            Assert.That(m_Store.Capacity, Is.EqualTo(20000));
            for (int i = 0; i < count; i++)
            {
                Assert.That(m_Store.TryGet(in bindings[i], 5, out var copy), Is.True);
                Assert.That(copy.Value, Is.EqualTo(i));
            }
            At(10, 10);
            m_Store.ResetTimeline();
            Assert.That(m_Store.Capacity, Is.EqualTo(20000));
            foreach (var binding in bindings) Assert.That(m_Store.TryGet(in binding, 5, out _), Is.False);
        }

        private Action[] Mutations() => new Action[]
        {
            () => m_Store.Bind(m_Binding.Entity), () => m_Store.Unbind(default),
            () => m_Store.TryRecord(in m_Binding, 1, default), () => m_Store.TruncateAfter(in m_Binding, uint.MaxValue),
            () => m_Store.EnsureCapacity(0), () => m_Store.ResetTimeline()
        };

        private Action[] Reads() => new Action[]
        {
            () => { _ = m_Store.Capacity; }, () => { _ = m_Store.HistoryLength; },
            () => m_Store.TryGet(in m_Binding, 1, out _), () => Verify()
        };

        [Test]
        public void IterationPermitsReadsAndVerificationButRejectsEvenNoOpMutations()
        {
            Record(1, 1);
            var state = new State { Value = 1 };
            m_Authority.TrySet(in m_AuthorityBinding, 1, in state);
            m_World.Query().ForEach(entity =>
            {
                foreach (var action in Mutations()) Assert.Throws<InvalidOperationException>(() => action());
                Assert.Throws<InvalidOperationException>(() => m_Store.Dispose());
                Assert.Throws<InvalidOperationException>(() => new PredictionHistoryStore<State>(m_World));
                At(1, 1);
                Assert.That(Verify(), Is.EqualTo(PredictionVerificationResult.Match));
            });
            At(1, 1);
        }

        [Test]
        public void ForeignThreadAccessAndDisposeAreRejectedBeforeChangingHistory()
        {
            Record(1, 1);
            var actions = new System.Collections.Generic.List<Action>(Mutations());
            actions.AddRange(Reads());
            actions.Add(() => m_Store.Dispose());
            var errors = new Exception[actions.Count];
            var worker = new Thread(() =>
            {
                for (int i = 0; i < actions.Count; i++)
                    try { actions[i](); } catch (Exception ex) { errors[i] = ex; }
            }) { IsBackground = true };
            worker.Start();
            Assert.That(worker.Join(10000), Is.True);
            foreach (var error in errors) Assert.That(error, Is.TypeOf<InvalidOperationException>());
            At(1, 1);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DisposalGuardsAllAccessAndStoreDisposalDoesNotOwnWorld(bool disposeWorld)
        {
            if (disposeWorld) m_World.Dispose(); else m_Store.Dispose();
            foreach (var action in Mutations()) Assert.Throws<ObjectDisposedException>(() => action());
            foreach (var action in Reads()) Assert.Throws<ObjectDisposedException>(() => action());
            Assert.That(m_World.IsDisposed, Is.EqualTo(disposeWorld));
            Assert.DoesNotThrow(() => m_Store.Dispose());
            if (disposeWorld) Assert.Throws<ObjectDisposedException>(() => new PredictionHistoryStore<State>(m_World));
        }

        [Test]
        public void DisposedAuthorityThrowsRatherThanLookingLikeMissingAuthority()
        {
            m_Authority.Dispose();
            Assert.Throws<ObjectDisposedException>(() => Verify());
        }

        [Test]
        public void WarmRecordOverwriteReadVerifyTruncateRebuildAndLifecycleAllocateNothing()
        {
            var state = new State { Value = 10 };
            m_Authority.TrySet(in m_AuthorityBinding, 10, in state);
            int successes = 0;
            TestDelegate body = () =>
            {
                successes = 0;
                for (int i = 0; i < 1000; i++)
                {
                    var binding = m_Store.Bind(m_Binding.Entity);
                    if (m_Store.TryRecord(in binding, 10, in state) == PredictionHistoryWriteResult.Accepted) successes++;
                    if (m_Store.TryRecord(in binding, 11, in state) == PredictionHistoryWriteResult.Accepted) successes++;
                    if (m_Store.TryRecord(in binding, 11, in state) == PredictionHistoryWriteResult.Accepted) successes++;
                    if (m_Store.VerifyPrediction(m_Authority, in m_AuthorityBinding, in binding) == PredictionVerificationResult.Match) successes++;
                    if (m_Store.TruncateAfter(in binding, 10) == PredictionHistoryWriteResult.Accepted) successes++;
                    if (!m_Store.TryGet(in binding, 11, out _)) successes++;
                    if (m_Store.TryRecord(in binding, 14, in state) == PredictionHistoryWriteResult.Accepted) successes++;
                    if (!m_Store.TryGet(in binding, 10, out _)) successes++;
                    if (m_Store.TryGet(in binding, 14, out var copy) && copy.Value == 10) successes++;
                    if (m_Store.Unbind(in binding)) successes++;
                }
                m_Store.EnsureCapacity(0);
                m_Store.ResetTimeline();
            };
            for (int i = 0; i < 8; i++) body();
            EcsAllocationTests.AssertZero(body);
            Assert.That(successes, Is.EqualTo(10000));
        }
    }
}
