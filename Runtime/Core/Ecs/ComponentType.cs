using System;
using System.Threading;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Runtime-local type IDs, never reset or recycled. Allocation order is nondeterministic;
    /// these IDs are not stable serialization, save-game or network identifiers.
    /// </summary>
    internal static class ComponentType
    {
        internal const int MaxTypeCount = 0x7FEFFFFF;
        private static int s_LastId = -1;

        internal static int Allocate()
        {
            while (true)
            {
                int current = Volatile.Read(ref s_LastId);
                if (current >= MaxTypeCount - 1)
                    throw new InvalidOperationException("Component type IDs exhausted for this runtime lifetime.");
                int next = current + 1;
                if (Interlocked.CompareExchange(ref s_LastId, next, current) == current) return next;
            }
        }
    }

    internal static class ComponentType<T> where T : struct
    {
        internal static readonly int Id;

        // CLR generic static initialization serializes first use of each T; the allocator
        // additionally coordinates simultaneous first use of different component types.
        static ComponentType() => Id = ComponentType.Allocate();
    }
}
