using System;
using System.Reflection;
using System.Threading;
using EjoyFramework.Core.Ecs;
using EjoyFramework.Tests.Ecs;
using EjoyGame.Samples.EcsDemo;
using NUnit.Framework;

namespace EjoyFramework.EcsSampleTests
{
    public class PredictionCorrectionTests
    {
        private PredictionCorrectionScenario m_Scenario;

        [SetUp]
        public void SetUp()
        {
            m_Scenario = new PredictionCorrectionScenario(historyLength: 8, inputCapacity: 8);
            m_Scenario.Step(1);
            m_Scenario.Step(1); // Tick 3: position 3, velocity 2; both tick-1 anchors are zero.
        }

        [TearDown]
        public void TearDown() => m_Scenario.Dispose();

        private static FieldInfo Field(object target, string name)
            => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new InvalidOperationException("Missing test observation field: " + name);
        private static T Read<T>(object target, string name) => (T)Field(target, name).GetValue(target);
        private static void Replace(object target, string name, object value) => Field(target, name).SetValue(target, value);
        private World World => Read<World>(m_Scenario, "m_World");
        private EcsLoop Loop => Read<EcsLoop>(m_Scenario, "m_Loop");
        private TickClock Clock => Read<TickClock>(m_Scenario, "m_Clock");
        private Entity Entity => Read<Entity>(m_Scenario, "m_Entity");
        private PredictionHistoryStore<CorrectionPosition> Positions
            => Read<PredictionHistoryStore<CorrectionPosition>>(m_Scenario, "m_PositionHistory");
        private PredictionHistoryStore<CorrectionVelocity> Velocities
            => Read<PredictionHistoryStore<CorrectionVelocity>>(m_Scenario, "m_VelocityHistory");
        private AuthoritativeBaselineStore<CorrectionPosition> AuthorityPositions
            => Read<AuthoritativeBaselineStore<CorrectionPosition>>(m_Scenario, "m_AuthorityPositions");
        private AuthoritativeBaselineStore<CorrectionVelocity> AuthorityVelocities
            => Read<AuthoritativeBaselineStore<CorrectionVelocity>>(m_Scenario, "m_AuthorityVelocities");

        private void Publish(int position = 0, int velocity = 0, uint tick = 1)
            => m_Scenario.PublishAuthority(m_Scenario.Epoch, tick,
                new CorrectionPosition(position), new CorrectionVelocity(velocity));

        private object[] ClockState() => new object[]
        {
            Clock.Tick, Clock.PendingTicks, Clock.Alpha, Clock.DroppedTicks,
            Clock.TickRate, Clock.TickInterval, Clock.MaxTicksPerFrame, Clock.MaxFrameDelta
        };

        private static object[] Storage(object store)
        {
            if (store.GetType().GetField("m_Samples", BindingFlags.Instance | BindingFlags.NonPublic) != null)
                return new object[] { Read<Array>(store, "m_Samples").Clone(), Read<Array>(store, "m_Cursors").Clone(),
                    Storage(Read<object>(store, "m_Baseline")) };
            return new object[] { Read<Array>(store, "m_Entries").Clone(), Read<ulong>(store, "m_Timeline"),
                Read<ulong>(store, "m_NextBindingGeneration") };
        }

        private object[] Observe()
        {
            bool hasPosition = World.TryGet(Entity, out CorrectionPosition position);
            bool hasVelocity = World.TryGet(Entity, out CorrectionVelocity velocity);
            World.TryGetComponentLifetime<CorrectionPosition>(Entity, out ulong positionLifetime);
            World.TryGetComponentLifetime<CorrectionVelocity>(Entity, out ulong velocityLifetime);
            var groups = Read<Array>(Loop, "m_Groups");
            var queued = new int[groups.Length];
            for (int i = 0; i < queued.Length; i++) queued[i] = Read<CommandBuffer>(groups.GetValue(i), "m_Commands").Count;
            return new object[] { World.EntityCount, World.IsAlive(Entity), World.PendingDespawnCount,
                hasPosition, position, hasVelocity, velocity, positionLifetime, velocityLifetime,
                ClockState(), queued, Loop.IsFaulted, Loop.IsRunning, m_Scenario.RequiresRecovery,
                Read<object>(m_Scenario, "m_AuthorityPair"), Read<Array>(m_Scenario, "m_Inputs").Clone(),
                Storage(Positions), Storage(Velocities), Storage(AuthorityPositions), Storage(AuthorityVelocities) };
        }

        private void NoChange(CorrectionReason reason, uint baseline = 1, int budget = 128)
        {
            var before = Observe();
            var result = m_Scenario.TryCorrect(baseline, budget);
            Assert.That(result.Outcome, Is.EqualTo(CorrectionOutcome.ResyncRequiredNoChange));
            Assert.That(result.Reason, Is.EqualTo(reason));
            Assert.That(Observe(), Is.EqualTo(before));
        }

        private void Prediction(uint tick, int position, int velocity)
        {
            Assert.That(m_Scenario.TryGetPrediction(tick, out var p, out var v), Is.True);
            Assert.That(p.Units, Is.EqualTo(position));
            Assert.That(v.Units, Is.EqualTo(velocity));
        }

        [Test]
        public void ConcreteComponentsUseDocumentedToleranceAndWideComparison()
        {
            Assert.That(new CorrectionPosition(0).VerifyPrediction(new CorrectionPosition(1)), Is.True);
            Assert.That(new CorrectionPosition(0).VerifyPrediction(new CorrectionPosition(-1)), Is.True);
            Assert.That(new CorrectionPosition(0).VerifyPrediction(new CorrectionPosition(2)), Is.False);
            Assert.That(new CorrectionPosition(int.MaxValue).VerifyPrediction(new CorrectionPosition(int.MinValue)), Is.False);
            Assert.That(new CorrectionVelocity(1).VerifyPrediction(new CorrectionVelocity(0)), Is.False);
            Assert.That(new CorrectionVelocity(int.MinValue).VerifyPrediction(new CorrectionVelocity(int.MinValue)), Is.True);
        }

        [Test]
        public void ForwardStepsOwnTwoComponentStateAndReturnCopiesAtExactTicks()
        {
            Assert.That(m_Scenario.Epoch, Is.EqualTo(1));
            var state = m_Scenario.Current;
            Assert.That(state.Tick, Is.EqualTo(3));
            Assert.That(state.Position.Units, Is.EqualTo(3));
            Assert.That(state.Velocity.Units, Is.EqualTo(2));
            Prediction(1, 0, 0);
            Prediction(2, 1, 1);
            Prediction(3, 3, 2);
            Assert.That(m_Scenario.TryGetPrediction(0, out var p, out var v), Is.False);
            Assert.That(p.Units, Is.Zero);
            Assert.That(v.Units, Is.Zero);
            Assert.That(m_Scenario.TryGetPrediction(4, out _, out _), Is.False);
            var next = m_Scenario.Step(-2);
            Assert.That(next.Tick, Is.EqualTo(4));
            Assert.That(next.Position.Units, Is.EqualTo(3));
            Assert.That(next.Velocity.Units, Is.Zero);
            Assert.That(state.Velocity.Units, Is.EqualTo(2));
        }

        [Test]
        public void MatchingPairDoesNotRestoreTruncateReplayOrCapture()
        {
            Publish();
            // A private test-only perturbation makes accidental restore/replay observable.
            World.Set(Entity, new CorrectionPosition(777));
            World.Set(Entity, new CorrectionVelocity(999));
            var before = Observe();
            var result = m_Scenario.TryCorrect(1);
            Assert.That(result.Outcome, Is.EqualTo(CorrectionOutcome.Matched));
            Assert.That(result.Reason, Is.EqualTo(CorrectionReason.None));
            Assert.That(Observe(), Is.EqualTo(before));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AFirstMismatchCannotHideMissingSecondPredictionOrTheReverse(bool missingPosition)
        {
            Publish(missingPosition ? 0 : 100, missingPosition ? 10 : 0);
            object history = missingPosition ? (object)Positions : Velocities;
            var samples = Read<Array>(history, "m_Samples");
            object anchor = samples.GetValue(1);
            Replace(anchor, "Tick", 0u); // Missing exact history, while the membership binding stays valid.
            samples.SetValue(anchor, 1);
            NoChange(CorrectionReason.PredictionUnavailable);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OldPredictionTimelineBindingsRequireResyncWithoutRelabelling(bool position)
        {
            Publish();
            if (position) Positions.ResetTimeline(); else Velocities.ResetTimeline();
            NoChange(CorrectionReason.PredictionUnavailable);
        }

        [Test]
        public void DisposedPredictionPrerequisiteReturnsNoChangeInsteadOfStartingRestoration()
        {
            Publish(100);
            Positions.Dispose();
            NoChange(CorrectionReason.PrerequisiteUnavailable);
        }

        [Test]
        public void MissingAuthorityIsDistinctFromMissingPrediction()
        {
            NoChange(CorrectionReason.AuthorityUnavailable);
            Publish();
            var binding = Read<AuthoritativeBaselineStore<CorrectionVelocity>.Binding>(m_Scenario, "m_AuthorityVelocityBinding");
            Assert.That(AuthorityVelocities.Unbind(in binding), Is.True);
            NoChange(CorrectionReason.AuthorityUnavailable);
        }

        [Test]
        public void PartialAuthorityPublicationDoesNotPublishANewCoherentPair()
        {
            Publish();
            object before = Read<object>(m_Scenario, "m_AuthorityPair");
            var binding = Read<AuthoritativeBaselineStore<CorrectionVelocity>.Binding>(m_Scenario, "m_AuthorityVelocityBinding");
            AuthorityVelocities.Unbind(in binding);
            Assert.Throws<InvalidOperationException>(() => Publish(100, 10, 2));
            Assert.That(Read<object>(m_Scenario, "m_AuthorityPair"), Is.EqualTo(before));
            Assert.That(m_Scenario.Current.Position.Units, Is.EqualTo(3));
            AssertRecoveryGate();
            Assert.That(Loop.IsFaulted, Is.False);
        }

        [Test]
        public void EqualNumericTicksWithoutCurrentOwnerPairEpochDoNotEstablishAuthority()
        {
            Publish();
            object pair = Read<object>(m_Scenario, "m_AuthorityPair");
            Replace(pair, "Epoch", 2UL);
            Replace(m_Scenario, "m_AuthorityPair", pair);
            NoChange(CorrectionReason.AuthorityEpochMismatch);
        }

        [Test]
        public void AuthorityMustHaveRequestedExactTickAndBothPublishedValues()
        {
            Publish(tick: 2);
            NoChange(CorrectionReason.AuthorityTickMismatch);
            var binding = Read<AuthoritativeBaselineStore<CorrectionPosition>.Binding>(m_Scenario, "m_AuthorityPositionBinding");
            var value = new CorrectionPosition(100);
            Assert.That(AuthorityPositions.TrySet(in binding, 3, in value), Is.EqualTo(BaselineWriteResult.Accepted));
            NoChange(CorrectionReason.AuthorityTickMismatch, baseline: 2);
        }

        [Test]
        public void IndependentlyReboundSameTickAuthorityDoesNotImpersonatePublishedPair()
        {
            Publish();
            var binding = AuthorityPositions.Bind(Entity);
            Replace(m_Scenario, "m_AuthorityPositionBinding", binding);
            var value = new CorrectionPosition(100);
            AuthorityPositions.TrySet(in binding, 1, in value);
            NoChange(CorrectionReason.AuthorityUnavailable);
        }

        [TestCase("gap")]
        [TestCase("epoch")]
        [TestCase("collision")]
        public void MatchingPairStillRequiresEveryExactInputAndEpoch(string corruption)
        {
            Publish();
            var inputs = Read<Array>(m_Scenario, "m_Inputs");
            object sample = inputs.GetValue(2);
            if (corruption == "epoch") Replace(sample, "Epoch", 2UL);
            else Replace(sample, "Tick", corruption == "gap" ? 0u : 10u);
            inputs.SetValue(sample, 2);
            NoChange(CorrectionReason.InputUnavailable);
        }

        [Test]
        public void InputOverwriteRequiresResyncInsteadOfUsingCurrentOrZeroInput()
        {
            using var scenario = new PredictionCorrectionScenario(historyLength: 8, inputCapacity: 1);
            scenario.Step(1);
            scenario.Step(2);
            scenario.PublishAuthority(1, 1, new CorrectionPosition(), new CorrectionVelocity());
            var before = scenario.Current;
            var result = scenario.TryCorrect(1);
            Assert.That(result.Outcome, Is.EqualTo(CorrectionOutcome.ResyncRequiredNoChange));
            Assert.That(result.Reason, Is.EqualTo(CorrectionReason.InputUnavailable));
            Assert.That(scenario.Current, Is.EqualTo(before));
            Assert.That(scenario.RequiresRecovery, Is.False);
        }

        [TestCase(0u, 128)]
        [TestCase(4u, 128)]
        [TestCase(1u, 0)]
        [TestCase(1u, -1)]
        [TestCase(1u, 1)]
        public void RangeOrBudgetFailurePrecedesAllCorrectionMutation(uint baseline, int budget)
        {
            Publish(tick: baseline == 0 ? 1 : baseline);
            NoChange(baseline == 0 ? CorrectionReason.AuthorityTickMismatch : CorrectionReason.ReplayRejected, baseline, budget);
        }

        [TestCase("fault")]
        [TestCase("clock-batch")]
        [TestCase("commands")]
        public void MatchingPairStillRequiresUsableLoopAndNoPendingWork(string corruption)
        {
            Publish();
            if (corruption == "fault") Replace(Loop, "<IsFaulted>k__BackingField", true);
            if (corruption == "clock-batch") Clock.Accumulate(Clock.TickInterval);
            if (corruption == "commands")
            {
                var group = Read<Array>(Loop, "m_Groups").GetValue((int)EcsPhase.TickSimulation);
                Read<CommandBuffer>(group, "m_Commands").Set(Entity, new CorrectionPosition(999));
            }
            NoChange(CorrectionReason.ReplayRejected);
        }

        [TestCase("extra-entity")]
        [TestCase("position-membership")]
        [TestCase("velocity-membership")]
        [TestCase("despawn")]
        [TestCase("capture-null")]
        [TestCase("capture-replaced")]
        [TestCase("clock-config")]
        public void ObservableScopeAndCaptureChangesAreRejectedBeforeEvenMatchPath(string corruption)
        {
            Publish();
            switch (corruption)
            {
                case "extra-entity": World.CreateEntity(); break;
                case "position-membership": World.Remove<CorrectionPosition>(Entity); World.Set(Entity, new CorrectionPosition()); break;
                case "velocity-membership": World.Remove<CorrectionVelocity>(Entity); World.Set(Entity, new CorrectionVelocity()); break;
                case "despawn": World.RequestDespawn(Entity); break;
                case "capture-null": Loop.TickCompletedSink = null; break;
                case "capture-replaced": Loop.TickCompletedSink = new EmptySink(); break;
                case "clock-config": Replace(Clock, "<TickInterval>k__BackingField", 0.25); break;
            }
            NoChange(CorrectionReason.ScopeChanged);
        }

        private sealed class EmptySink : IEcsTickCompletedSink
        {
            public void OnTickCompleted(World world, in EcsTime time) { }
        }

        [TestCase(100, 0, 103, 2)]
        [TestCase(1, 10, 24, 12)] // Position matches within tolerance, but its authoritative anchor must still replace zero.
        public void MismatchRestoresBothComponentsAndRebuildsBothAnchorsAndHistories(
            int position, int velocity, int finalPosition, int finalVelocity)
        {
            Publish(position, velocity);
            var clock = ClockState();
            var result = m_Scenario.TryCorrect(1);
            Assert.That(result.Outcome, Is.EqualTo(CorrectionOutcome.Corrected));
            Assert.That(result.Reason, Is.EqualTo(CorrectionReason.None));
            Assert.That(m_Scenario.Current.Position.Units, Is.EqualTo(finalPosition));
            Assert.That(m_Scenario.Current.Velocity.Units, Is.EqualTo(finalVelocity));
            Prediction(1, position, velocity);
            Prediction(2, position + velocity + 1, velocity + 1);
            Prediction(3, finalPosition, finalVelocity);
            Assert.That(ClockState(), Is.EqualTo(clock));
            Assert.That(m_Scenario.RequiresRecovery, Is.False);
            Assert.That(m_Scenario.TryCorrect(1).Outcome, Is.EqualTo(CorrectionOutcome.Matched));
        }

        [TestCase(1u)]
        [TestCase(uint.MaxValue)]
        public void EmptyRangeStillRestoresAndReplacesBothCurrentTickAnchors(uint tick)
        {
            using var scenario = new PredictionCorrectionScenario(initialTick: tick);
            scenario.PublishAuthority(1, tick, new CorrectionPosition(1), new CorrectionVelocity(5));
            Assert.That(scenario.TryCorrect(tick).Outcome, Is.EqualTo(CorrectionOutcome.Corrected));
            Assert.That(scenario.Current.Tick, Is.EqualTo(tick));
            Assert.That(scenario.Current.Position.Units, Is.EqualTo(1));
            Assert.That(scenario.Current.Velocity.Units, Is.EqualTo(5));
            Assert.That(scenario.TryGetPrediction(tick, out var p, out var v), Is.True);
            Assert.That(p.Units, Is.EqualTo(1));
            Assert.That(v.Units, Is.EqualTo(5));
            if (tick == uint.MaxValue)
            {
                Assert.Throws<OverflowException>(() => scenario.Step(0));
                Assert.That(scenario.RequiresRecovery, Is.False);
            }
        }

        [Test]
        public void ReplayToUintMaxUsesExactRecordedInputWithoutWrapping()
        {
            using var scenario = new PredictionCorrectionScenario(initialTick: uint.MaxValue - 1);
            scenario.Step(1);
            scenario.PublishAuthority(1, uint.MaxValue - 1, new CorrectionPosition(10), new CorrectionVelocity());
            Assert.That(scenario.TryCorrect(uint.MaxValue - 1, 1).Outcome, Is.EqualTo(CorrectionOutcome.Corrected));
            Assert.That(scenario.Current.Tick, Is.EqualTo(uint.MaxValue));
            Assert.That(scenario.Current.Position.Units, Is.EqualTo(11));
            Assert.That(scenario.Current.Velocity.Units, Is.EqualTo(1));
        }

        private void AssertRecoveryGate()
        {
            Assert.That(m_Scenario.RequiresRecovery, Is.True);
            Assert.Throws<InvalidOperationException>(() => m_Scenario.Step(0));
            Assert.Throws<InvalidOperationException>(() => Publish());
            Assert.Throws<InvalidOperationException>(() => m_Scenario.TryCorrect(1));
            Assert.That(Loop.IsRunning, Is.False);
        }

        [Test]
        public void PostRestoreCheckedOverflowFaultsOwnerAndLoopWithoutResettingClockOrUndoingAnchors()
        {
            Publish(100, int.MaxValue);
            var clock = ClockState();
            Assert.Throws<OverflowException>(() => m_Scenario.TryCorrect(1));
            AssertRecoveryGate();
            Assert.That(Loop.IsFaulted, Is.True);
            Assert.That(m_Scenario.Current.Position.Units, Is.EqualTo(100));
            Assert.That(m_Scenario.Current.Velocity.Units, Is.EqualTo(int.MaxValue));
            Prediction(1, 100, int.MaxValue);
            Assert.That(ClockState(), Is.EqualTo(clock));
        }

        private sealed class FailingSystem : ISystem
        {
            public Exception Error;
            public void Update(World world, CommandBuffer commands, in EcsTime time) => throw Error;
        }

        [Test]
        public void ExecutionRethrowsOriginalSystemExceptionAndRequiresOwnerRecovery()
        {
            Publish(100);
            var error = new ApplicationException("Test-only private-scope failure.");
            // Deliberately bypass the sample API to model an execution failure, not a supported participant extension.
            Loop.Add(EcsPhase.TickResolve, new FailingSystem { Error = error }, replayable: true);
            Assert.That(Assert.Throws<ApplicationException>(() => m_Scenario.TryCorrect(1)), Is.SameAs(error));
            AssertRecoveryGate();
            Assert.That(m_Scenario.Current.Position.Units, Is.EqualTo(101));
            Assert.That(Loop.IsFaulted, Is.True);
        }

        private void ShortenVelocitySamples(bool makeHeadEqualAnchor)
        {
            var samples = Read<Array>(Velocities, "m_Samples");
            var shortened = Array.CreateInstance(samples.GetType().GetElementType(), 2);
            Array.Copy(samples, shortened, 2); // Tick-1 anchor is readable; later slot access will fail.
            Replace(Velocities, "m_Samples", shortened);
            if (!makeHeadEqualAnchor) return;
            var cursors = Read<Array>(Velocities, "m_Cursors");
            object cursor = cursors.GetValue(0);
            Replace(cursor, "LatestTick", 1u);
            cursors.SetValue(cursor, 0);
        }

        [Test]
        public void AnchorRebuildFailureBeforeReplayStillRequiresOwnerRecovery()
        {
            Publish(100);
            ShortenVelocitySamples(makeHeadEqualAnchor: false);
            var clock = ClockState();
            Assert.Throws<IndexOutOfRangeException>(() => m_Scenario.TryCorrect(1));
            AssertRecoveryGate();
            Assert.That(Loop.IsFaulted, Is.False); // Failure preceded Replay, so loop fault alone is insufficient.
            Assert.That(m_Scenario.Current.Position.Units, Is.EqualTo(100));
            Assert.That(m_Scenario.Current.Velocity.Units, Is.Zero);
            Assert.That(ClockState(), Is.EqualTo(clock));
        }

        [Test]
        public void SecondHistoryCaptureFailureKeepsPartialProgressAndOriginalException()
        {
            Publish(100);
            ShortenVelocitySamples(makeHeadEqualAnchor: true);
            var clock = ClockState();
            Assert.Throws<IndexOutOfRangeException>(() => m_Scenario.TryCorrect(1));
            AssertRecoveryGate();
            Assert.That(Loop.IsFaulted, Is.True);
            Assert.That(m_Scenario.Current.Position.Units, Is.EqualTo(101));
            var binding = Read<PredictionHistoryStore<CorrectionPosition>.Binding>(m_Scenario, "m_PositionBinding");
            Assert.That(Positions.TryGet(in binding, 2, out var partial), Is.True);
            Assert.That(partial.Units, Is.EqualTo(101));
            Assert.That(ClockState(), Is.EqualTo(clock));
        }

        [Test]
        public void ForwardExecutionFailureAlsoRequiresRecovery()
        {
            World.Set(Entity, new CorrectionVelocity(int.MaxValue));
            Assert.Throws<OverflowException>(() => m_Scenario.Step(1));
            AssertRecoveryGate();
            Assert.That(Loop.IsFaulted, Is.True);
        }

        [Test]
        public void InvalidPublicInputsAndAuthorityEpochRejectWithoutPublicationOrExecution()
        {
            var before = Observe();
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Scenario.Step(int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Scenario.Step(int.MinValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Scenario.PublishAuthority(2, 1,
                new CorrectionPosition(), new CorrectionVelocity()));
            Assert.Throws<ArgumentOutOfRangeException>(() => m_Scenario.PublishAuthority(1, 0,
                new CorrectionPosition(), new CorrectionVelocity()));
            Assert.That(Observe(), Is.EqualTo(before));
            Publish();
            before = Observe();
            Assert.Throws<ArgumentOutOfRangeException>(() => Publish(tick: 1));
            Assert.That(Observe(), Is.EqualTo(before));
        }

        [Test]
        public void ConstructorBoundsAndOwnerDisposalGuardsAreExplicit()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PredictionCorrectionScenario(initialTick: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PredictionCorrectionScenario(epoch: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PredictionCorrectionScenario(historyLength: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PredictionCorrectionScenario(inputCapacity: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PredictionCorrectionScenario(historyLength: int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PredictionCorrectionScenario(inputCapacity: int.MaxValue));
            var errors = new Exception[3];
            var thread = new Thread(() =>
            {
                try { m_Scenario.TryCorrect(1); } catch (Exception ex) { errors[0] = ex; }
                try { m_Scenario.Step(0); } catch (Exception ex) { errors[1] = ex; }
                try { m_Scenario.Dispose(); } catch (Exception ex) { errors[2] = ex; }
            }) { IsBackground = true };
            thread.Start();
            Assert.That(thread.Join(10000), Is.True);
            foreach (var error in errors) Assert.That(error, Is.TypeOf<InvalidOperationException>());
            m_Scenario.Dispose();
            Assert.That(World.IsDisposed, Is.True);
            Assert.Throws<ObjectDisposedException>(() => m_Scenario.TryCorrect(1));
            Assert.Throws<ObjectDisposedException>(() => m_Scenario.Step(0));
            Assert.Throws<ObjectDisposedException>(() => { _ = m_Scenario.Current; });
            Assert.DoesNotThrow(() => m_Scenario.Dispose());
        }

        [Test]
        public void PublicScopeExposesNoMutableWorldLoopOrRegistrationEscapeHatch()
        {
            foreach (var method in typeof(PredictionCorrectionScenario).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(World)));
                Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(EcsLoop)));
                foreach (var parameter in method.GetParameters())
                {
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(ISystem)));
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(IEcsTickCompletedSink)));
                }
            }
            Assert.That(typeof(PredictionCorrectionScenario).GetFields(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
        }

        [Test]
        public void WarmCorrectedAndMatchedPathsAllocateNothing()
        {
            int corrected = 0, matched = 0;
            TestDelegate correct = () =>
            {
                corrected = 0;
                for (int i = 0; i < 1000; i++)
                {
                    var state = m_Scenario.Step(0);
                    m_Scenario.PublishAuthority(m_Scenario.Epoch, state.Tick - 1,
                        new CorrectionPosition(checked((int)state.Tick * 10)), new CorrectionVelocity());
                    if (m_Scenario.TryCorrect(state.Tick - 1).Outcome == CorrectionOutcome.Corrected) corrected++;
                }
            };
            TestDelegate match = () =>
            {
                matched = 0;
                for (int i = 0; i < 1000; i++)
                    if (m_Scenario.TryCorrect(m_Scenario.Current.Tick - 1).Outcome == CorrectionOutcome.Matched) matched++;
            };
            for (int i = 0; i < 4; i++) { correct(); match(); }
            EcsAllocationTests.AssertZero(correct);
            EcsAllocationTests.AssertZero(match);
            Assert.That(corrected, Is.EqualTo(1000));
            Assert.That(matched, Is.EqualTo(1000));
            Assert.That(m_Scenario.RequiresRecovery, Is.False);
        }
    }
}
