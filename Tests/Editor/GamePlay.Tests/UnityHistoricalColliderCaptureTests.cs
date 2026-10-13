using System;
using EjoyFramework.GamePlay.Combat;
using EjoyFramework.GamePlay.Unity.Combat;
using NUnit.Framework;
using UnityEngine;
using V=System.Numerics.Vector3;
namespace EjoyFramework.GamePlay.Tests
{
    public sealed class UnityHistoricalColliderCaptureTests
    {
        private GameObject root,bone;
        [SetUp] public void Setup(){root=new GameObject("rig");bone=new GameObject("bone");bone.transform.SetParent(root.transform,false);}
        [TearDown] public void Cleanup()=>UnityEngine.Object.DestroyImmediate(root);
        [Test] public void DisabledAnimatedSphereCapturesImmutablePoseAndLifetime()
        {
            var collider=bone.AddComponent<SphereCollider>();collider.enabled=false;collider.center=Vector3.up;collider.radius=.25f;
            root.transform.position=new Vector3(2,0,0);bone.transform.localScale=new Vector3(1,2,1);
            var first=UnityHistoricalColliderCapture.Capture(collider,8,3,7,4,12);
            Assert.That(first.A,Is.EqualTo(new V(2,2,0)));Assert.That(first.Radius,Is.EqualTo(.5f));Assert.That(first.Tag,Is.EqualTo(12));Assert.That(first.Generation,Is.EqualTo(3));
            var history=new HistoricalHitWorld(4,2);history.Reset(1);history.Capture(10,new[]{first});
            bone.transform.localPosition=new Vector3(0,10,0);history.Capture(11,new[]{UnityHistoricalColliderCapture.Capture(collider,8,4,7,4,12)});
            Assert.That(history.Sweep(1,10,new V(0,2,0),V.UnitX,5,0,4,0,out var hit),Is.EqualTo(HistoricalQueryStatus.Hit));Assert.That(hit.Collider.Generation,Is.EqualTo(3));
            Assert.That(history.Sweep(1,11,new V(0,2,0),V.UnitX,5,0,4,0,out _),Is.EqualTo(HistoricalQueryStatus.Miss));
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)] public void CapsuleUsesAuthoredAxisAndPerpendicularRadius(int axis)
        {
            var collider=bone.AddComponent<CapsuleCollider>();collider.direction=axis;collider.radius=.2f;collider.height=2;
            bone.transform.localRotation=Quaternion.Euler(20,35,50);bone.transform.localScale=new Vector3(2,3,4);
            var value=UnityHistoricalColliderCapture.Capture(collider,1,1,0,1);
            float radius=.2f*(axis==2?3:4);float height=2*(axis+2);
            Assert.That(value.Radius,Is.EqualTo(radius).Within(.00001));Assert.That((value.B-value.A).Length(),Is.EqualTo(height-2*radius).Within(.00001));
        }
        [Test] public void RotatedBoxKeepsCenterExtentsAndRejectsShear()
        {
            var collider=bone.AddComponent<BoxCollider>();collider.center=Vector3.right;collider.size=new Vector3(2,4,6);
            root.transform.position=new Vector3(5,0,0);bone.transform.localRotation=Quaternion.Euler(0,90,0);bone.transform.localScale=new Vector3(2,3,4);
            var shape=UnityHistoricalColliderCapture.Capture(collider,1,1,0,1);
            Assert.That(V.Distance(shape.A,new V(5,0,-2)),Is.LessThan(.00001));Assert.That(V.Distance(shape.B,new V(2,6,12)),Is.LessThan(.00001));
            root.transform.localScale=new Vector3(2,1,1);bone.transform.localRotation=Quaternion.Euler(0,45,0);
            Assert.Throws<ArgumentException>(()=>UnityHistoricalColliderCapture.Capture(collider,1,1,0,1));
        }
        [Test] public void UnsupportedShapeDoesNotSilentlySubstituteBounds()
        {var mesh=bone.AddComponent<MeshCollider>();Assert.Throws<NotSupportedException>(()=>UnityHistoricalColliderCapture.Capture(mesh,1,1,0,1));}
    }
}
