using EjoyGame.Samples.EcsDemo;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests.Netcode.Session
{
    public class TickAuthorityScenarioTests
    {
        [Test]
        public void EncodedOutOfOrderAndMissingCommands_DriveOnlyServerTicks()
        {
            using var scenario = new TickAuthorityScenario();
            scenario.Send(0, 2, 2);
            scenario.Send(0, 1, 1);
            scenario.Send(1, 2, 3);
            Assert.That(scenario.RunFrame(0), Is.Zero);
            Assert.That(scenario.SimulationCalls, Is.Zero);
            Assert.That(scenario.Tick, Is.Zero);
            Assert.That(scenario.RunFrame(0.125), Is.EqualTo(1));
            Assert.That(scenario.GetPlayerTotal(0), Is.EqualTo(1));
            Assert.That(scenario.GetPlayerTotal(1), Is.Zero);
            Assert.That(scenario.GetPeer(1).LastProcessedCommandTick, Is.Zero);
            Assert.That(scenario.RunFrame(0.125), Is.EqualTo(1));
            Assert.That(scenario.SimulationCalls, Is.EqualTo(2));
            Assert.That(scenario.GetPlayerTotal(0), Is.EqualTo(3));
            Assert.That(scenario.GetPlayerTotal(1), Is.EqualTo(3));
            Assert.That(scenario.GetPeer(1).LastProcessedCommandTick, Is.EqualTo(2));
        }
    }
}
