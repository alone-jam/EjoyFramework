namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// The receiver is the authoritative value. Compare with the supplied prediction using
    /// component-specific tolerance; implementations must be deterministic and side-effect-free.
    /// Invoke on a local copy, never a writable reference to retained baseline storage.
    /// Unmanaged does not make pointers, native handles or local Entity identities wire-safe.
    /// </summary>
    public interface IPredicted<T> where T : unmanaged, IPredicted<T>
    {
        bool VerifyPrediction(in T predicted);
    }
}
