using System;
using NUnit.Framework;
using Is = NUnit.Framework.Is;
#if UNITY_5_3_OR_NEWER
using UnityEngine.TestTools.Constraints;
#endif

namespace EjoyFramework.GamePlay.Tests.Netcode.Messages
{
    public class NetcodeCodecAllocationTests
    {
        // Caller warms this exact delegate/path first; no production counters or injection hooks.
        internal static void AssertZero(TestDelegate body)
        {
#if UNITY_5_3_OR_NEWER
            Assert.That(body, Is.Not.AllocatingGCMemory());
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
#endif
        }

        [Test]
        public void DetectorObservesAllocationAndAcceptsWarmedEmptyDelegate()
        {
            byte[] retained = null;
            TestDelegate allocate = () => retained = new byte[256];
            TestDelegate empty = () => { };
            for (int i = 0; i < 32; i++) { allocate(); empty(); }
#if UNITY_5_3_OR_NEWER
            Assert.That(allocate, new AllocatingGCMemoryConstraint());
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
            allocate();
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.GreaterThanOrEqualTo(256));
#endif
            AssertZero(empty);
            GC.KeepAlive(retained);
        }
    }
}
