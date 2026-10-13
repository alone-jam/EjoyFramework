using System;
using EjoyFramework.GamePlay.Unity.ThreeC;
using NUnit.Framework;
using UnityEngine;

namespace EjoyFramework.GamePlay.Tests
{
    public sealed class CharacterControllerStepperTests
    {
        private GameObject body, floor, wall;
        private CharacterController controller;
        private CharacterControllerStepper stepper;
        [SetUp] public void SetUp()
        {
            body = new GameObject("tick body"); controller = body.AddComponent<CharacterController>();
            controller.radius = 0.4f; controller.height = 1.8f; controller.center = new Vector3(0, 0.9f, 0);
            controller.minMoveDistance = 0; controller.skinWidth = 0.01f;
            stepper = new CharacterControllerStepper(controller);
            floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.transform.position = new Vector3(0, -0.5f, 0); floor.transform.localScale = new Vector3(20, 1, 20);
            wall = GameObject.CreatePrimitive(PrimitiveType.Cube); wall.transform.position = new Vector3(2, 1, 0); wall.transform.localScale = new Vector3(1, 2, 20);
            Physics.SyncTransforms();
        }
        [TearDown] public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(body); UnityEngine.Object.DestroyImmediate(floor); UnityEngine.Object.DestroyImmediate(wall);
        }
        [Test] public void ActualCollisionUsesExplicitDurationAndRestoredStart()
        {
            var start = new Vector3(0, 0.02f, 0); var delta = new Vector3(3, -0.1f, 0);
            var first = stepper.Move(start, delta, 0.1f);
            Assert.That(first.IsGrounded, Is.True);
            Assert.That(first.Position.x, Is.LessThan(1.2f)); Assert.That(first.Position.x, Is.GreaterThan(1));
            Assert.That(first.Velocity.x, Is.EqualTo((first.Position.x - start.x) / 0.1f).Within(0.0001));
            var replay = stepper.Move(start, delta, 0.2f);
            Assert.That(Vector3.Distance(first.Position, replay.Position), Is.LessThan(0.001f));
            Assert.That(replay.Velocity.x, Is.EqualTo(first.Velocity.x * 0.5f).Within(0.001));
        }
        [TestCase(0.25f, true)] [TestCase(1.2f, false)]
        public void AuthoredStepOffsetClimbsLowObstacleAndBlocksTallOne(float height, bool climbs)
        {
            controller.stepOffset = 0.5f;
            var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                obstacle.transform.position = new Vector3(0, height / 2, 2);
                obstacle.transform.localScale = new Vector3(4, height, 1);
                Physics.SyncTransforms();
                var position = new Vector3(0, 0.02f, 0); float peak = position.y;
                for (int i = 0; i < 60; i++)
                {
                    position = stepper.Move(position, new Vector3(0, -0.1f, 0.05f), 1f / 60).Position;
                    peak = Mathf.Max(peak, position.y);
                }
                if (climbs) { Assert.That(position.z, Is.GreaterThan(2.5f)); Assert.That(peak, Is.GreaterThan(0.2f)); }
                else Assert.That(position.z, Is.LessThan(1.2f));
            }
            finally { UnityEngine.Object.DestroyImmediate(obstacle); }
        }
        [TestCase(30f, true)] [TestCase(65f, false)]
        public void SlopeLimitIsRespectedByTickAdapter(float angle, bool climbs)
        {
            controller.slopeLimit = 50;
            var ramp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                float radians = angle * Mathf.Deg2Rad;
                ramp.transform.localScale = new Vector3(4, 0.2f, 6);
                ramp.transform.rotation = Quaternion.Euler(-angle, 0, 0);
                ramp.transform.position = new Vector3(0, 3 * Mathf.Sin(radians) - 0.1f * Mathf.Cos(radians), 3 * Mathf.Cos(radians));
                Physics.SyncTransforms();
                var position = new Vector3(0, 0.02f, -0.8f);
                for (int i = 0; i < 100; i++) position = stepper.Move(position, new Vector3(0, -0.1f, 0.05f), 1f / 60).Position;
                if (climbs) Assert.That(position.y, Is.GreaterThan(0.8f));
                else Assert.That(position.y, Is.LessThan(0.3f));
            }
            finally { UnityEngine.Object.DestroyImmediate(ramp); }
        }
        [Test] public void InvalidDeltaOrDisabledControllerCannotMutateTransform()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => stepper.Move(Vector3.one, Vector3.zero, 0));
            Assert.That(body.transform.position, Is.EqualTo(Vector3.zero));
            controller.enabled = false;
            Assert.Throws<InvalidOperationException>(() => stepper.RestorePosition(Vector3.one));
            Assert.That(controller.enabled, Is.False);
        }
    }
}
