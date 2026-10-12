using System;
using NUnit.Framework;
using Is = NUnit.Framework.Is;
#if UNITY_5_3_OR_NEWER
using UnityEngine.TestTools.Constraints;
#endif

namespace EjoyFramework.Tests.Ecs
{
    public sealed class EcsAllocationTests
    {
        /// <summary>
        /// Measures one invocation. The caller must warm this exact delegate/path first;
        /// warmup is deliberately not hidden here because it may consume state under test.
        /// </summary>
        internal static void AssertZero(TestDelegate body)
        {
#if UNITY_5_3_OR_NEWER
            Assert.That(body, Is.Not.AllocatingGCMemory());
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
#endif
        }

        [Test]
        public void Detector_ObservesKnownAllocation_AndAcceptsWarmedEmptyBody()
        {
            byte[] retained = null;
            TestDelegate allocating = () => retained = new byte[1024];
            TestDelegate empty = () => { };
            for (int i = 0; i < 32; i++) { allocating(); empty(); }
#if UNITY_5_3_OR_NEWER
            Assert.That(allocating, new AllocatingGCMemoryConstraint());
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
            allocating();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.GreaterThanOrEqualTo(1024));
#endif
            AssertZero(empty);
            GC.KeepAlive(retained);
        }
    }
}
