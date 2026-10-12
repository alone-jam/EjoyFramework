namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// The receiver is the earlier sample; returns a new interpolated value towards to.
    /// Callers supply a finite weight in [0,1] and invoke on a local copy of retained state.
    /// Implementations must be deterministic and side-effect-free. This is not a serializer.
    /// </summary>
    public interface IInterpolated<T> where T : unmanaged, IInterpolated<T>
    {
        T Interpolate(in T to, float alpha);
    }
}
