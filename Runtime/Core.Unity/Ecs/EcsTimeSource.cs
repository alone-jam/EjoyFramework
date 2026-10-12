namespace EjoyFramework.Core.Unity
{
    /// <summary>自动推进时输入 TickClock 的帧时长来源；固定 tick 间隔不受来源影响。</summary>
    public enum EcsTimeSource
    {
        Scaled = 0,
        Unscaled = 1
    }
}
