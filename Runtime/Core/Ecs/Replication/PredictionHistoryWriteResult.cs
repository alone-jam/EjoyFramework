namespace EjoyFramework.Core.Ecs
{
    /// <summary>Prediction-history mutation outcome. Rejections preserve all retained samples and ordering.</summary>
    public enum PredictionHistoryWriteResult : byte
    {
        /// <summary>Recorded/replaced a sample, or completed an allowed truncation (including a no-op).</summary>
        Accepted,
        /// <summary>Recording precedes the current head. Explicitly truncate before rebuilding a branch.</summary>
        OlderTick,
        /// <summary>The truncation anchor precedes the monotonically advanced retention floor.</summary>
        OutsideWindow,
        StaleBinding,
        TargetUnavailable
    }
}
