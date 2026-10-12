using System;
using EjoyFramework.GamePlay.Netcode.Session;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests.Netcode.Session
{
    public class TickCommandAdmissionFlowTests
    {
        // Test-only host policy. The inbox owns no clock or simulation callback. Missing input
        // explicitly means zero axis here; another host can choose a different documented policy.
        private sealed class FixedStepOwner
        {
            public readonly TickCommandInbox<InboxTestCommand, InboxTestCodec> Inbox;
            public int Position { get; private set; }
            public int SimulationCount { get; private set; }
            public double ElapsedSeconds { get; private set; }
            private const double StepSeconds = 0.125;

            public FixedStepOwner(TickInputFrameContext context)
                => Inbox = new TickCommandInbox<InboxTestCommand, InboxTestCodec>(in context, default, windowSize: 4);

            public void Step(bool failAfterValueWrite = false)
            {
                var selection = Inbox.BeginTick(checked(Inbox.LastSimulatedTick + 1));
                try
                {
                    Position = checked(Position + (selection.HasCommand ? selection.Command.Axis : 0));
                    if (failAfterValueWrite) throw new ApplicationException("Simulation failed after a value write.");
                    SimulationCount++;
                    ElapsedSeconds += StepSeconds;
                }
                catch
                {
                    Inbox.FailTick(selection.Tick);
                    throw;
                }
                Inbox.CompleteTick(selection.Tick);
            }
        }

        [Test]
        public void RealFrameBurstDoesNotAdvanceSimulationTimeOrProcessedCommandTick()
        {
            var owner = new FixedStepOwner(InboxTestFrames.Context());
            foreach (uint tick in new uint[] { 3, 1, 2 })
                Assert.That(owner.Inbox.TryAdmit(InboxTestFrames.Frame(tick, (int)tick)).Status,
                    Is.EqualTo(TickCommandAdmissionStatus.Accepted));
            Assert.That(owner.SimulationCount, Is.Zero);
            Assert.That(owner.ElapsedSeconds, Is.Zero);
            Assert.That(owner.Position, Is.Zero);
            Assert.That(owner.Inbox.LastSimulatedTick, Is.Zero);
            Assert.That(owner.Inbox.LastProcessedCommandTick, Is.Zero);
            Assert.That(owner.Inbox.PendingCommandCount, Is.EqualTo(3));

            owner.Step();
            Assert.That(owner.SimulationCount, Is.EqualTo(1));
            Assert.That(owner.ElapsedSeconds, Is.EqualTo(0.125));
            Assert.That(owner.Position, Is.EqualTo(1));
            Assert.That(owner.Inbox.LastSimulatedTick, Is.EqualTo(1));
            Assert.That(owner.Inbox.LastProcessedCommandTick, Is.EqualTo(1));
            Assert.That(owner.Inbox.PendingCommandCount, Is.EqualTo(2));
            owner.Step();
            owner.Step();
            Assert.That(owner.SimulationCount, Is.EqualTo(3));
            Assert.That(owner.ElapsedSeconds, Is.EqualTo(0.375));
            Assert.That(owner.Position, Is.EqualTo(6));
            owner.Inbox.Close();
        }

        [Test]
        public void MissingTickIsExplicitHostPolicyAndHighestProcessedTickDoesNotProveEarlierInput()
        {
            var owner = new FixedStepOwner(InboxTestFrames.Context());
            owner.Inbox.TryAdmit(InboxTestFrames.Frame(2, 4));
            owner.Step(); // Missing tick 1: this host chooses zero, not the inbox.
            Assert.That(owner.Position, Is.Zero);
            Assert.That(owner.Inbox.LastSimulatedTick, Is.EqualTo(1));
            Assert.That(owner.Inbox.LastProcessedCommandTick, Is.Zero);
            Assert.That(owner.Inbox.TryAdmit(InboxTestFrames.Frame(1, 9)).Status, Is.EqualTo(TickCommandAdmissionStatus.StaleTick));
            owner.Step();
            Assert.That(owner.Position, Is.EqualTo(4));
            Assert.That(owner.Inbox.LastProcessedCommandTick, Is.EqualTo(2)); // Does not acknowledge a tick-1 command.
            Assert.That(owner.ElapsedSeconds, Is.EqualTo(0.25));
            owner.Inbox.Close();
        }

        [Test]
        public void FailedSimulationDoesNotAcknowledgeInputAndInboxDoesNotRollbackHostValues()
        {
            var owner = new FixedStepOwner(InboxTestFrames.Context());
            owner.Inbox.TryAdmit(InboxTestFrames.Frame(1, 7));
            Assert.Throws<ApplicationException>(() => owner.Step(failAfterValueWrite: true));
            Assert.That(owner.Position, Is.EqualTo(7)); // A partial simulation write remains; there is no abort/retry rollback.
            Assert.That(owner.Inbox.LastSimulatedTick, Is.Zero);
            Assert.That(owner.Inbox.LastProcessedCommandTick, Is.Zero);
            Assert.That(owner.Inbox.IsFaulted, Is.True);
            Assert.That(owner.SimulationCount, Is.Zero);
            Assert.That(owner.ElapsedSeconds, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => owner.Step());
            Assert.That(owner.Position, Is.EqualTo(7));
            owner.Inbox.Close();
        }

        [Test]
        public void WorldResetUsesNewContextAndOldCallbacksStayOnClosedInbox()
        {
            var old = new FixedStepOwner(InboxTestFrames.Context());
            var oldFrame = InboxTestFrames.Frame(1, 5);
            old.Inbox.TryAdmit(oldFrame);
            old.Inbox.Close();
            var nextContext = InboxTestFrames.Context(world: 23);
            var next = new FixedStepOwner(nextContext);
            Assert.That(old.Inbox.TryAdmit(oldFrame).Status, Is.EqualTo(TickCommandAdmissionStatus.Closed));
            var rejected = next.Inbox.TryAdmit(oldFrame);
            Assert.That(rejected.Status, Is.EqualTo(TickCommandAdmissionStatus.FrameRejected));
            Assert.That(rejected.FrameError, Is.EqualTo(TickInputFrameError.WorldEpochMismatch));
            Assert.That(next.Inbox.TryAdmit(InboxTestFrames.Frame(1, 9, context: nextContext)).Status,
                Is.EqualTo(TickCommandAdmissionStatus.Accepted));
            Assert.That(next.ElapsedSeconds, Is.Zero);
            next.Step();
            Assert.That(next.Position, Is.EqualTo(9));
            Assert.That(next.ElapsedSeconds, Is.EqualTo(0.125));
            Assert.That(old.ElapsedSeconds, Is.Zero);
            next.Inbox.Close();
        }
    }
}
