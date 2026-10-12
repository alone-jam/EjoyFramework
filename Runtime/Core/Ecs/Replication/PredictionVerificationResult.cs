namespace EjoyFramework.Core.Ecs
{
    /// <summary>Exact authoritative-tick comparison; never requests or schedules replay.</summary>
    public enum PredictionVerificationResult : byte
    {
        Match,
        Mismatch,
        /// <summary>Both bindings are current, but the authority has no value yet.</summary>
        MissingAuthority,
        /// <summary>No retained prediction exists at the authoritative value's exact tick.</summary>
        MissingHistory,
        /// <summary>Either token is stale/unavailable or the full entity identities differ.</summary>
        InvalidBinding
    }
}
