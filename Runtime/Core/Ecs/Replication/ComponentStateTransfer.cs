using System;

namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Explicit typed copies between live components and caller-owned state. Call on the World's
    /// owning thread under its single-thread contract. These helpers neither schedule simulation
    /// nor provide a coherent multi-component transaction; stores themselves remain World-read-only.
    /// </summary>
    public static class ComponentStateTransfer
    {
        /// <summary>
        /// Copies a live, non-despawning component with its membership identity. Allowed during
        /// traversal; the caller establishes the capture time. False leaves outputs at default.
        /// </summary>
        public static bool TryCapture<T>(World world, Entity entity, out ulong componentLifetime, out T value)
            where T : unmanaged
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            world.RequireNotDisposed();
            componentLifetime = 0;
            value = default;
            if (world.IsDespawning(entity)
                || !world.TryGetComponentLifetime<T>(entity, out ulong lifetime)
                || !world.TryGet(entity, out T captured)) return false;
            componentLifetime = lifetime;
            value = captured;
            return true;
        }

        /// <summary>
        /// Restores one existing component only when the validated stored tick exactly matches.
        /// Never creates/adds components, falls back to another tick, changes the source baseline,
        /// or resets time. False performs no World write. Mutations require a structural-safe point.
        /// Sequential calls are NOT an atomic multi-component restore: prevalidate the entire
        /// scope into locals/scratch first if a caller needs that stronger guarantee.
        /// </summary>
        public static bool TryRestoreExact<T>(World world, AuthoritativeBaselineStore<T> source,
            in AuthoritativeBaselineStore<T>.Binding binding, uint baselineTick) where T : unmanaged
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (source == null) throw new ArgumentNullException(nameof(source));
            world.RequireStructuralChange();
            if (baselineTick == 0) throw new ArgumentOutOfRangeException(nameof(baselineTick));
            if (!source.TryGet(in binding, out uint storedTick, out T value)
                || binding.Entity.WorldId != world.Id || storedTick != baselineTick) return false;
            // Validated same World, generation, component membership and exact tick. Get cannot
            // silently add a component; no callbacks/allocations occur between validation and write.
            world.Get<T>(binding.Entity) = value;
            return true;
        }

        /// <summary>
        /// Restores two distinct existing memberships from the same exact baseline tick. All
        /// validation finishes before either write; no user callbacks or allocations intervene.
        /// This immediate typed operation is not a delayed transaction or a World-wide rollback.
        /// The caller must assemble both baselines from one authority timeline; matching numeric
        /// ticks cannot establish a shared remote origin across independent stores.
        /// </summary>
        public static bool TryRestoreExact<T1, T2>(World world,
            AuthoritativeBaselineStore<T1> source1, in AuthoritativeBaselineStore<T1>.Binding binding1,
            AuthoritativeBaselineStore<T2> source2, in AuthoritativeBaselineStore<T2>.Binding binding2,
            uint baselineTick) where T1 : unmanaged where T2 : unmanaged
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (source1 == null) throw new ArgumentNullException(nameof(source1));
            if (source2 == null) throw new ArgumentNullException(nameof(source2));
            world.RequireStructuralChange();
            if (baselineTick == 0) throw new ArgumentOutOfRangeException(nameof(baselineTick));
            if (!source1.TryGet(in binding1, out uint tick1, out T1 value1)
                || !source2.TryGet(in binding2, out uint tick2, out T2 value2)) return false;
            if (binding1.Entity.WorldId != world.Id || binding2.Entity.WorldId != world.Id
                || tick1 != baselineTick || tick2 != baselineTick) return false;
            if (binding1.Entity == binding2.Entity && ComponentType<T1>.Id == ComponentType<T2>.Id)
                throw new ArgumentException("Restore targets must be distinct component memberships.");
            world.Get<T1>(binding1.Entity) = value1;
            world.Get<T2>(binding2.Entity) = value2;
            return true;
        }
    }
}
