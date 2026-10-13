using System;
using System.Numerics;
namespace EjoyFramework.GamePlay.Combat
{
    public enum HistoricalShape : byte { Sphere = 1, Capsule = 2, Box = 3, ExternalStatic = 4 }
    /// <summary>World-space value collider with owner/lifetime/subshape identity and caller-defined category mask/tag.</summary>
    public readonly struct HistoricalCollider
    {
        public ulong Owner { get; }
        public uint Generation { get; }
        public ushort ShapeId { get; }
        public uint Mask { get; }
        public int Tag { get; }
        public HistoricalShape Shape { get; }
        public Vector3 A { get; }
        public Vector3 B { get; }
        public Quaternion Rotation { get; }
        public float Radius { get; }
        private HistoricalCollider(ulong owner, uint generation, ushort shapeId, uint mask, int tag, HistoricalShape shape, Vector3 a, Vector3 b, Quaternion rotation, float radius)
        { Owner = owner; Generation = generation; ShapeId = shapeId; Mask = mask; Tag = tag; Shape = shape; A = a; B = b; Rotation = rotation; Radius = radius; }
        public static HistoricalCollider Sphere(ulong owner, uint generation, ushort shapeId, uint mask, Vector3 center, float radius, int tag = 0)
        { Identity(owner, generation); Vector(center); Positive(radius); return new HistoricalCollider(owner, generation, shapeId, mask, tag, HistoricalShape.Sphere, center, default, Quaternion.Identity, radius); }
        public static HistoricalCollider Capsule(ulong owner, uint generation, ushort shapeId, uint mask, Vector3 start, Vector3 end, float radius, int tag = 0)
        { Identity(owner, generation); Vector(start); Vector(end); Positive(radius); return new HistoricalCollider(owner, generation, shapeId, mask, tag, HistoricalShape.Capsule, start, end, Quaternion.Identity, radius); }
        public static HistoricalCollider Box(ulong owner, uint generation, ushort shapeId, uint mask, Vector3 center, Vector3 halfExtents, Quaternion rotation, int tag = 0)
        {
            Identity(owner, generation); Vector(center); Vector(halfExtents); Positive(halfExtents.X); Positive(halfExtents.Y); Positive(halfExtents.Z);
            float length = rotation.LengthSquared(); if (!Finite(length) || length < .000001f) throw new ArgumentOutOfRangeException(nameof(rotation));
            return new HistoricalCollider(owner, generation, shapeId, mask, tag, HistoricalShape.Box, center, halfExtents, Quaternion.Normalize(rotation), 0);
        }
        internal static HistoricalCollider StaticIdentity(ulong owner,uint generation,ushort shapeId,uint mask,int tag)
        { Identity(owner,generation); return new HistoricalCollider(owner,generation,shapeId,mask,tag,HistoricalShape.ExternalStatic,default,default,Quaternion.Identity,0); }
        internal bool Valid => Owner != 0 && Generation != 0 && Shape >= HistoricalShape.Sphere && Shape <= HistoricalShape.Box;
        internal static void Vector(Vector3 v) { if (!Finite(v.X) || !Finite(v.Y) || !Finite(v.Z) || Math.Abs(v.X) > 1000000 || Math.Abs(v.Y) > 1000000 || Math.Abs(v.Z) > 1000000) throw new ArgumentOutOfRangeException(nameof(v)); }
        internal static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static void Positive(float v) { if (!Finite(v) || v <= 0 || v > 1000000) throw new ArgumentOutOfRangeException(nameof(v)); }
        private static void Identity(ulong owner, uint generation) { if (owner == 0 || generation == 0) throw new ArgumentOutOfRangeException(); }
    }
    public enum HistoricalQueryStatus { Hit, Miss, HistoryUnavailable, WrongEpoch }
    public readonly struct HistoricalHit
    {
        public HistoricalCollider Collider { get; }
        public float Distance { get; }
        public Vector3 Point { get; }
        public Vector3 Normal { get; }
        /// <summary>External immutable static geometry hit; this identity is not a capturable primitive frame.</summary>
        public static HistoricalHit StaticGeometry(ulong owner,uint generation,ushort shapeId,uint mask,int tag,float distance,Vector3 point,Vector3 normal)
        {
            if(!HistoricalCollider.Finite(distance)||distance<0||distance>1000000)throw new ArgumentOutOfRangeException(nameof(distance));
            HistoricalCollider.Vector(point);HistoricalCollider.Vector(normal);
            if(normal.LengthSquared()<.000001f)throw new ArgumentOutOfRangeException(nameof(normal));
            var identity=HistoricalCollider.StaticIdentity(owner,generation,shapeId,mask,tag);
            return new HistoricalHit(in identity,distance,point,Vector3.Normalize(normal));
        }
        internal HistoricalHit(in HistoricalCollider collider, float distance, Vector3 point, Vector3 normal)
        { Collider = collider; Distance = distance; Point = point; Normal = normal; }
    }
}
