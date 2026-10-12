namespace EjoyFramework.Core.Ecs
{
    /// <summary>State ownership category, independent of transport and wire encoding.</summary>
    public enum ReplicationMode : byte
    {
        Replicated = 0,
        Predicted = 1,
        Interpolated = 2
    }
}
