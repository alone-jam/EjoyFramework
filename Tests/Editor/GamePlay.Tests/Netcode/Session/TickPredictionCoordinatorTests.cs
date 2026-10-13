using System;
using EjoyFramework.Core.Ecs;
using EjoyFramework.GamePlay.Netcode.Prediction;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests
{
    public sealed class TickPredictionCoordinatorTests
    {
        private struct Input { public int Acceleration; }
        private struct State : IPredicted<State>
        {
            public int Position, Velocity;
            public bool VerifyPrediction(in State predicted) => Math.Abs(Position - predicted.Position) <= 1 && Velocity == predicted.Velocity;
        }
        private sealed class Simulation : ISystem
        {
            public Entity Entity; public int Forward, Replayed; public bool ThrowReplay; public Action Reenter;
            public void Update(World world, CommandBuffer buffer, in EcsTime time)
            {
                Reenter?.Invoke();
                if (time.IsReplay) { Replayed++; if (ThrowReplay) throw new ApplicationException("replay marker"); } else Forward++;
                ref var state = ref world.Get<State>(Entity); state.Velocity += world.Get<Input>(Entity).Acceleration; state.Position += state.Velocity;
            }
        }
        private sealed class Effect : ISystem
        {
            public int Count;
            public void Update(World world, CommandBuffer buffer, in EcsTime time) { if (time.IsReplay) throw new InvalidOperationException("Forward effect replayed"); Count++; }
        }
        private World world; private Entity entity; private TickPredictionCoordinator<Input, State> client; private Simulation simulation; private Effect effects;
        [SetUp] public void Setup() => Create(16);
        private void Create(int capacity, int budget = 16)
        {
            client?.Dispose(); world?.Dispose(); world = new World(); entity = world.CreateEntity(); world.Set(entity, new State()); world.Set(entity, new Input());
            client = new TickPredictionCoordinator<Input, State>(world, entity, 60, capacity, budget);
            simulation = new Simulation { Entity = entity }; effects = new Effect(); client.AddSimulation(simulation); client.AddForwardOnly(effects); client.Reset(1, 1, new State());
        }
        [TearDown] public void TearDown() { client?.Dispose(); world?.Dispose(); }
        private void Steps(int count) { for (int i = 0; i < count; i++) client.Predict(new Input { Acceleration = 1 }); }
        [Test] public void CorrectsExactAnchorAndReplaysInputsWithoutDuplicatingForwardEffects()
        {
            Steps(5); Assert.That(client.Current.Position, Is.EqualTo(15));
            var result = client.Reconcile(1, 3, 3, new State { Position = 10, Velocity = 2 });
            Assert.That(result.Outcome, Is.EqualTo(PredictionCorrectionOutcome.Corrected)); Assert.That(result.ReplayedTicks, Is.EqualTo(3));
            Assert.That(client.Current.Position, Is.EqualTo(22)); Assert.That(client.Current.Velocity, Is.EqualTo(5)); Assert.That(client.Tick, Is.EqualTo(6));
            Assert.That(simulation.Replayed, Is.EqualTo(3)); Assert.That(effects.Count, Is.EqualTo(5));
            Assert.That(client.TryGetPrediction(3, out var anchor), Is.True); Assert.That(anchor.Position, Is.EqualTo(10));
            Assert.That(client.TryGetPrediction(6, out var head), Is.True); Assert.That(head.Position, Is.EqualTo(22));
            Assert.That(client.TryGetCommand(3, out _), Is.False); Assert.That(client.TryGetCommand(4, out _), Is.True);
            client.Predict(new Input { Acceleration = 1 }); Assert.That(client.Current.Position, Is.EqualTo(28)); Assert.That(effects.Count, Is.EqualTo(6));
        }
        [Test] public void ComponentToleranceMatchesWithoutChangingState()
        {
            Steps(5); var result = client.Reconcile(1, 3, 2, new State { Position = 4, Velocity = 2 });
            Assert.That(result.Outcome, Is.EqualTo(PredictionCorrectionOutcome.Matched)); Assert.That(client.Current.Position, Is.EqualTo(15));
            Assert.That(client.AcknowledgedTick, Is.EqualTo(2)); Assert.That(client.TryGetCommand(3, out _), Is.True);
        }
        [Test] public void DuplicateAndReorderedSnapshotsCannotOverwriteAcceptedAuthority()
        {
            Steps(5); client.Reconcile(1, 4, 4, new State { Position = 6, Velocity = 3 });
            Assert.That(client.Reconcile(1, 4, 4, new State { Position = 999 }).Outcome, Is.EqualTo(PredictionCorrectionOutcome.IgnoredOld));
            Assert.That(client.Reconcile(1, 3, 3, new State { Position = 999 }).Outcome, Is.EqualTo(PredictionCorrectionOutcome.IgnoredOld));
            Assert.That(client.Current.Position, Is.EqualTo(15)); Assert.That(client.AcknowledgedTick, Is.EqualTo(4));
        }
        [Test] public void WrongEpochIsRejectedBeforeStateOrAcknowledgementMutation()
        {
            Steps(3); Assert.That(client.Reconcile(2, 3, 3, new State { Position = 99 }).Outcome, Is.EqualTo(PredictionCorrectionOutcome.WrongEpoch));
            Assert.That(client.Current.Position, Is.EqualTo(6)); Assert.That(client.AcknowledgedTick, Is.EqualTo(1));
        }
        [Test] public void HistoryOverflowRequiresExplicitResyncAndDoesNotInventInputs()
        {
            Create(4); Steps(9); var before = client.Current;
            var result = client.Reconcile(1, 3, 3, new State { Position = 30 });
            Assert.That(result.Outcome, Is.EqualTo(PredictionCorrectionOutcome.ResyncRequired)); Assert.That(result.Reason, Is.EqualTo(PredictionResyncReason.HistoryUnavailable));
            Assert.That(client.Current.Position, Is.EqualTo(before.Position)); Assert.That(client.Tick, Is.EqualTo(10)); Assert.That(client.AcknowledgedTick, Is.EqualTo(1));
            client.Reset(1, 10, new State { Position = 100, Velocity = 2 }); client.Predict(new Input { Acceleration = 1 });
            Assert.That(client.Current.Position, Is.EqualTo(103)); Assert.That(client.TryGetCommand(9, out _), Is.False);
        }
        [Test] public void ReplayBudgetRejectionLeavesStateAndClockUntouched()
        {
            Create(16, 2); Steps(7); var result = client.Reconcile(1, 3, 3, new State());
            Assert.That(result.Reason, Is.EqualTo(PredictionResyncReason.ReplayBudget)); Assert.That(client.Current.Position, Is.EqualTo(28)); Assert.That(client.Tick, Is.EqualTo(8));
        }
        [Test] public void AuthorityAheadRequiresExplicitBaselineAdmission()
        {
            Assert.That(client.Reconcile(1, 5, 5, new State()).Reason, Is.EqualTo(PredictionResyncReason.AuthorityAhead)); Assert.That(client.Tick, Is.EqualTo(1));
        }
        [Test] public void ReconnectClearsEveryHistoryAndRejectsRetiredEpoch()
        {
            Steps(4); client.Reset(8, 20, new State { Position = 200 });
            Assert.That(client.TryGetPrediction(3, out _), Is.False); Assert.That(client.TryGetCommand(3, out _), Is.False);
            Assert.That(client.Reconcile(1, 21, 21, new State()).Outcome, Is.EqualTo(PredictionCorrectionOutcome.WrongEpoch));
            client.Predict(new Input { Acceleration = 2 }); Assert.That(client.Current.Position, Is.EqualTo(202));
        }
        [Test] public void ReplayExceptionFaultsUntilExplicitRecoveryAndRetainsOriginalException()
        {
            Steps(5); simulation.ThrowReplay = true;
            var error = Assert.Throws<ApplicationException>(() => client.Reconcile(1, 3, 3, new State { Position = 99 })); Assert.That(error.Message, Is.EqualTo("replay marker"));
            Assert.That(client.RequiresRecovery, Is.True); Assert.Throws<InvalidOperationException>(() => client.Predict(new Input()));
            simulation.ThrowReplay = false; client.Reset(1, 6, new State { Position = 60 }); client.Predict(new Input { Acceleration = 1 });
            Assert.That(client.RequiresRecovery, Is.False); Assert.That(client.Current.Position, Is.EqualTo(61));
        }
        [Test] public void ReentryCannotMutateRunningPrediction()
        {
            simulation.Reenter = () => client.Reset(2, 1, new State());
            Assert.Throws<InvalidOperationException>(() => client.Predict(new Input())); Assert.That(client.RequiresRecovery, Is.True);
        }
        [Test] public void ChangedComponentMembershipCannotReuseOldTimeline()
        {
            world.Remove<State>(entity); world.Set(entity, new State()); Assert.Throws<InvalidOperationException>(() => client.Predict(new Input()));
        }
        [Test] public void ConfigurationIsSealedAfterAdmission() => Assert.Throws<InvalidOperationException>(() => client.AddSimulation(new Simulation()));
        [Test] public void AcknowledgementCannotClaimUnsimulatedTicks()
        { Steps(4); Assert.Throws<ArgumentOutOfRangeException>(() => client.Reconcile(1, 3, 4, new State())); Assert.That(client.AcknowledgedTick, Is.EqualTo(1)); }
        [Test] public void AcknowledgementCannotRegressAcrossNewerState()
        { Steps(5); client.Reconcile(1, 3, 3, new State { Position = 3, Velocity = 2 }); Assert.Throws<ArgumentOutOfRangeException>(() => client.Reconcile(1, 4, 2, new State())); }
        [Test] public void WarmSteadyPredictionAndCorrectionAllocateNoManagedBytes()
        {
            Steps(10);
            for (int i = 0; i < 16; i++)
            {
                client.Predict(new Input { Acceleration = 1 });
                client.TryGetPrediction(client.Tick - 1, out var state); state.Position += 2;
                client.Reconcile(1, client.Tick - 1, client.Tick - 1, state);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                client.Predict(new Input { Acceleration = 1 });
                client.TryGetPrediction(client.Tick - 1, out var state); state.Position += 2;
                client.Reconcile(1, client.Tick - 1, client.Tick - 1, state);
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(bytes, Is.Zero);
        }
        [Test] public void PresentationRemainsContinuousThenDecaysWithoutChangingSimulation()
        {
            var smoothing = new PredictionPresentationOffset(1); var old = new float[] { 10 }; var corrected = new float[] { 20 }; var rendered = new float[1];
            smoothing.Correct(old, corrected); smoothing.Apply(corrected, rendered); Assert.That(rendered[0], Is.EqualTo(10));
            smoothing.Advance(.1, .1); smoothing.Apply(corrected, rendered); Assert.That(rendered[0], Is.EqualTo(15).Within(.0001));
            Assert.That(corrected[0], Is.EqualTo(20)); smoothing.Clear(); smoothing.Apply(corrected, rendered); Assert.That(rendered[0], Is.EqualTo(20));
        }
        [Test] public void PresentationRejectsNonfiniteInputsBeforeChangingOffset()
        {
            var s = new PredictionPresentationOffset(1); Assert.Throws<ArgumentOutOfRangeException>(() => s.Correct(new float[] { float.NaN }, new float[] { 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => s.Advance(double.NaN, .1)); var output = new float[1]; s.Apply(new float[] { 2 }, output); Assert.That(output[0], Is.EqualTo(2));
        }
    }
}
