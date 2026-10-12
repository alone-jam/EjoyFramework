namespace EjoyFramework.Core.Ecs
{
    public enum BaselineWriteResult : byte
    {
        Accepted,
        DuplicateTick,
        OlderTick,
        StaleBinding,
        TargetUnavailable
    }
}
