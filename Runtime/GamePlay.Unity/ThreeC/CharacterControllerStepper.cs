using System;
using UnityEngine;

namespace EjoyFramework.GamePlay.Unity.ThreeC
{
    /// <summary>Explicit-clock CharacterController adapter for tick simulation and correction replay.
    /// Does not own the controller or rewind other physics objects. Replay must restore the collision
    /// world separately; Unity physics is not claimed to be bitwise deterministic across machines.</summary>
    public sealed class CharacterControllerStepper
    {
        private readonly CharacterController m_Controller;
        public CharacterControllerStepper(CharacterController controller)
        {
            m_Controller = controller != null ? controller : throw new ArgumentNullException(nameof(controller));
        }

        public CharacterControllerStep Move(Vector3 start, Vector3 displacement, float deltaSeconds)
        {
            Validate(start, nameof(start)); Validate(displacement, nameof(displacement));
            if (float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
            RequireActive();
            var current = m_Controller.transform.position;
            if (current.x != start.x || current.y != start.y || current.z != start.z) RestorePosition(start);
            var flags = m_Controller.Move(displacement);
            var position = m_Controller.transform.position;
            return new CharacterControllerStep(position, (position - start) / deltaSeconds, flags);
        }

        public void RestorePosition(Vector3 position)
        {
            Validate(position, nameof(position)); RequireActive();
            m_Controller.enabled = false;
            try { m_Controller.transform.position = position; }
            finally { m_Controller.enabled = true; }
            Physics.SyncTransforms();
        }

        private void RequireActive()
        {
            if (m_Controller == null || !m_Controller.enabled || !m_Controller.gameObject.activeInHierarchy)
                throw new InvalidOperationException("An active enabled CharacterController is required.");
        }
        private static void Validate(Vector3 value, string name)
        {
            if (float.IsNaN(value.x) || float.IsInfinity(value.x) || float.IsNaN(value.y) ||
                float.IsInfinity(value.y) || float.IsNaN(value.z) || float.IsInfinity(value.z))
                throw new ArgumentOutOfRangeException(name);
        }
    }

    public readonly struct CharacterControllerStep
    {
        public CharacterControllerStep(Vector3 position, Vector3 velocity, CollisionFlags collisions)
        {
            Position = position; Velocity = velocity; Collisions = collisions;
        }
        public Vector3 Position { get; }
        public Vector3 Velocity { get; }
        public CollisionFlags Collisions { get; }
        public bool IsGrounded => (Collisions & CollisionFlags.Below) != 0;
        public bool HitCeiling => (Collisions & CollisionFlags.Above) != 0;
    }
}
