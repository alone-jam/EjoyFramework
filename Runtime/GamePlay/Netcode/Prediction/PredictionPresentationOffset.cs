using System;
namespace EjoyFramework.GamePlay.Netcode.Prediction
{
    /// <summary>Presentation-only correction offset. Simulation is never blended. One allocation at construction.</summary>
    public sealed class PredictionPresentationOffset
    {
        private readonly float[] offset;
        public PredictionPresentationOffset(int dimensions)
        { if (dimensions < 1 || dimensions > 64) throw new ArgumentOutOfRangeException(nameof(dimensions)); offset = new float[dimensions]; }
        public void Clear() => Array.Clear(offset, 0, offset.Length);
        public void Correct(ReadOnlySpan<float> before, ReadOnlySpan<float> after)
        {
            Validate(before); Validate(after);
            for (int i = 0; i < offset.Length; i++)
                if (!Finite(offset[i] + before[i] - after[i])) throw new ArgumentOutOfRangeException(nameof(after));
            for (int i = 0; i < offset.Length; i++) offset[i] += before[i] - after[i];
        }
        public void Advance(double deltaSeconds, double halfLifeSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds < 0 ||
                double.IsNaN(halfLifeSeconds) || double.IsInfinity(halfLifeSeconds) || halfLifeSeconds <= 0) throw new ArgumentOutOfRangeException();
            float decay = (float)Math.Exp(-Math.Log(2) * deltaSeconds / halfLifeSeconds);
            for (int i = 0; i < offset.Length; i++) offset[i] *= decay;
        }
        public void Apply(ReadOnlySpan<float> simulated, Span<float> rendered)
        {
            Validate(simulated); if (rendered.Length != offset.Length) throw new ArgumentException("Dimension mismatch.");
            for (int i = 0; i < offset.Length; i++) rendered[i] = simulated[i] + offset[i];
        }
        private void Validate(ReadOnlySpan<float> values)
        { if (values.Length != offset.Length) throw new ArgumentException("Dimension mismatch."); foreach (float v in values) if (!Finite(v)) throw new ArgumentOutOfRangeException(nameof(values)); }
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
