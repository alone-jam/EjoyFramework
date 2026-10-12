using System;

namespace EjoyFramework.Core.Unity
{
    public enum EcsUpdateMode
    {
        Manual = 0,
        Update = 1,
        [Obsolete("FixedUpdate is mapped to Update-driven fixed ticks. Use Update or Manual.")]
        FixedUpdate = 2
    }
}
