namespace EjoyFramework.Core.Ecs
{
    /// <summary>Outcome of adding a sample; rejected writes leave retained history unchanged.</summary>
    public enum HistoryWriteResult : byte
    {
        Accepted,
        DuplicateTick,
        OutsideWindow,
        StaleBinding,
        TargetUnavailable
    }
}
