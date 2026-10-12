using System;
using EjoyFramework.Core.Ecs;

namespace EjoyGame.Samples.EcsDemo
{
    /// <summary>One fixed-unit position tolerance is sample policy, not a framework default.</summary>
    public readonly struct CorrectionPosition : IPredicted<CorrectionPosition>
    {
        public readonly int Units;
        public CorrectionPosition(int units) => Units = units;
        public bool VerifyPrediction(in CorrectionPosition predicted)
            => Math.Abs((long)Units - predicted.Units) <= 1;
    }

    /// <summary>Velocity uses exact integer units per tick.</summary>
    public readonly struct CorrectionVelocity : IPredicted<CorrectionVelocity>
    {
        public readonly int Units;
        public CorrectionVelocity(int units) => Units = units;
        public bool VerifyPrediction(in CorrectionVelocity predicted) => Units == predicted.Units;
    }

    public readonly struct CorrectionSnapshot
    {
        public uint Tick { get; }
        public CorrectionPosition Position { get; }
        public CorrectionVelocity Velocity { get; }
        internal CorrectionSnapshot(uint tick, CorrectionPosition position, CorrectionVelocity velocity)
        { Tick = tick; Position = position; Velocity = velocity; }
    }

    public enum CorrectionOutcome : byte { ResyncRequiredNoChange, Matched, Corrected }

    public enum CorrectionReason : byte
    {
        None,
        ScopeChanged,
        AuthorityUnavailable,
        AuthorityEpochMismatch,
        AuthorityTickMismatch,
        PredictionUnavailable,
        InputUnavailable,
        ReplayRejected,
        PrerequisiteUnavailable
    }

    /// <summary>No-change results apply only to preflight. Execution failures throw and require owner recovery.</summary>
    public readonly struct CorrectionResult
    {
        public CorrectionOutcome Outcome { get; }
        public CorrectionReason Reason { get; }
        internal CorrectionResult(CorrectionOutcome outcome, CorrectionReason reason = CorrectionReason.None)
        { Outcome = outcome; Reason = reason; }
    }
}
