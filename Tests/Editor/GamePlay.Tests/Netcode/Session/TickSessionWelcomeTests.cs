using EjoyFramework.GamePlay.Netcode.Session;
using NUnit.Framework;
namespace EjoyFramework.GamePlay.Tests.Netcode
{
    public sealed class TickSessionWelcomeTests
    {
        [Test] public void RoundTripRejectsWrongSchemaReservedBytesAndTrailingData()
        {
            var welcome = new TickSessionWelcome(new TickInputFrameContext(99, 2, 7, 1), 123, 60, 4);
            var bytes = new byte[40]; Assert.That(TickSessionWelcomeCodec.TryEncode(in welcome, bytes), Is.True);
            Assert.That(TickSessionWelcomeCodec.TryDecode(bytes, 7, 1, out var decoded), Is.True);
            Assert.That(decoded.ServerTick, Is.EqualTo(123)); Assert.That(decoded.TickRate, Is.EqualTo(60)); Assert.That(decoded.ControlledEntityId, Is.EqualTo(4));
            Assert.That(decoded.Context.SessionEpoch, Is.EqualTo(99));
            Assert.That(TickSessionWelcomeCodec.TryDecode(bytes, 8, 1, out _), Is.False);
            bytes[39] = 1; Assert.That(TickSessionWelcomeCodec.TryDecode(bytes, 7, 1, out _), Is.False);
            Assert.That(TickSessionWelcomeCodec.TryDecode(new byte[41], 7, 1, out _), Is.False);
        }
        [Test] public void InvalidEncodeLeavesDestinationUntouched()
        {
            var welcome = default(TickSessionWelcome); var bytes = new byte[40]; bytes[0] = 123;
            Assert.That(TickSessionWelcomeCodec.TryEncode(in welcome, bytes), Is.False); Assert.That(bytes[0], Is.EqualTo(123));
        }
    }
}
