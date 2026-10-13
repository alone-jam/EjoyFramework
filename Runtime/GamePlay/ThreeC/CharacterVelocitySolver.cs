using System;

namespace EjoyFramework.GamePlay.ThreeC
{
    /// <summary>Engine-independent horizontal friction and projected acceleration.
    /// Callers own locomotion states, jump rules and the simulation clock.</summary>
    public static class CharacterVelocitySolver
    {
        public static ThreeCVector3 StepHorizontal(in ThreeCVector3 velocity, float inputX, float inputZ,
            float maxSpeed, float acceleration, float friction, float deltaSeconds, bool limitSpeed = false)
        {
            CheckFinite(velocity.X, nameof(velocity)); CheckFinite(velocity.Y, nameof(velocity)); CheckFinite(velocity.Z, nameof(velocity));
            CheckFinite(inputX, nameof(inputX)); CheckFinite(inputZ, nameof(inputZ));
            CheckNonnegative(maxSpeed, nameof(maxSpeed)); CheckNonnegative(acceleration, nameof(acceleration));
            CheckNonnegative(friction, nameof(friction)); CheckNonnegative(deltaSeconds, nameof(deltaSeconds));
            if (deltaSeconds == 0) return velocity;
            double x = velocity.X, z = velocity.Z;
            double speed = Math.Sqrt(x * x + z * z);
            if (speed > 0)
            {
                double remaining = Math.Max(0, speed - Math.Max(1, speed) * friction * deltaSeconds);
                x *= remaining / speed; z *= remaining / speed;
            }
            double magnitude = Math.Sqrt((double)inputX * inputX + (double)inputZ * inputZ);
            if (magnitude > 0)
            {
                double dx = inputX / magnitude, dz = inputZ / magnitude;
                double target = maxSpeed * Math.Min(1, magnitude);
                double deficit = target - (x * dx + z * dz);
                double gain = Math.Max(0, Math.Min(deficit, (double)acceleration * maxSpeed * deltaSeconds));
                x += dx * gain; z += dz * gain;
            }
            double resultSpeed = Math.Sqrt(x * x + z * z);
            if (limitSpeed && resultSpeed > maxSpeed)
            {
                x *= maxSpeed / resultSpeed; z *= maxSpeed / resultSpeed;
            }
            if (Math.Abs(x) > float.MaxValue || Math.Abs(z) > float.MaxValue)
                throw new ArithmeticException("Horizontal velocity exceeds the supported float range.");
            return new ThreeCVector3((float)x, velocity.Y, (float)z);
        }

        public static float ApplyGravity(float verticalVelocity, float gravity, float terminalSpeed, float deltaSeconds)
        {
            CheckFinite(verticalVelocity, nameof(verticalVelocity)); CheckNonnegative(gravity, nameof(gravity));
            CheckNonnegative(terminalSpeed, nameof(terminalSpeed)); CheckNonnegative(deltaSeconds, nameof(deltaSeconds));
            if (deltaSeconds == 0) return verticalVelocity;
            return (float)Math.Max(-terminalSpeed, verticalVelocity - (double)gravity * deltaSeconds);
        }

        private static void CheckFinite(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(name);
        }
        private static void CheckNonnegative(float value, string name)
        {
            CheckFinite(value, name);
            if (value < 0) throw new ArgumentOutOfRangeException(name);
        }
    }
}
