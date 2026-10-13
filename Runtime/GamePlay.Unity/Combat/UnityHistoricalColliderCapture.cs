using System;
using EjoyFramework.GamePlay.Combat;
using UnityEngine;
using V = System.Numerics.Vector3;
using Q = System.Numerics.Quaternion;
namespace EjoyFramework.GamePlay.Unity.Combat
{
    /// <summary>Captures authored Unity primitive shapes as immutable world-space history values.
    /// Disabled colliders are intentional: rigs can supply history without participating in live physics.
    /// Call after evaluating the authoritative animation pose, on the Unity main thread.
    /// Mesh/terrain colliders and sheared boxes/capsules are rejected, never approximated silently.</summary>
    public static class UnityHistoricalColliderCapture
    {
        public static HistoricalCollider Capture(Collider source,ulong owner,uint generation,ushort shapeId,uint mask,int tag=0)
        {
            if(!source)throw new ArgumentNullException(nameof(source));
            var t=source.transform;
            var x=t.TransformVector(Vector3.right);var y=t.TransformVector(Vector3.up);var z=t.TransformVector(Vector3.forward);
            var scale=new Vector3(x.magnitude,y.magnitude,z.magnitude);
            if(scale.x<.000001f||scale.y<.000001f||scale.z<.000001f)throw new ArgumentException("Degenerate collider transform.");
            if(source is SphereCollider sphere)
                return HistoricalCollider.Sphere(owner,generation,shapeId,mask,Vector(t.TransformPoint(sphere.center)),sphere.radius*Mathf.Max(scale.x,Mathf.Max(scale.y,scale.z)),tag);
            if(Mathf.Abs(Vector3.Dot(x.normalized,y.normalized))>.0001f||Mathf.Abs(Vector3.Dot(x.normalized,z.normalized))>.0001f||Mathf.Abs(Vector3.Dot(y.normalized,z.normalized))>.0001f)
                throw new ArgumentException("Sheared collider transform cannot be represented by a primitive history shape.");
            if(source is BoxCollider box)
            {
                var r=t.rotation;
                return HistoricalCollider.Box(owner,generation,shapeId,mask,Vector(t.TransformPoint(box.center)),Vector(Vector3.Scale(box.size,scale)*.5f),new Q(r.x,r.y,r.z,r.w),tag);
            }
            if(source is CapsuleCollider capsule)
            {
                int a=capsule.direction;var axis=a==0?x:a==1?y:z;
                float radial=a==0?Mathf.Max(scale.y,scale.z):a==1?Mathf.Max(scale.x,scale.z):Mathf.Max(scale.x,scale.y);
                float radius=capsule.radius*radial;
                var center=t.TransformPoint(capsule.center);var half=axis.normalized*Mathf.Max(0,capsule.height*scale[a]*.5f-radius);
                return HistoricalCollider.Capsule(owner,generation,shapeId,mask,Vector(center-half),Vector(center+half),radius,tag);
            }
            throw new NotSupportedException("Historical capture requires a sphere, capsule or box collider.");
        }
        private static V Vector(Vector3 value)=>new V(value.x,value.y,value.z);
    }
}
