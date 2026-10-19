using System;
using EjoyFramework.GamePlay.Combat;
using EjoyFramework.GamePlay.Unity.Combat;
using NUnit.Framework;
using UnityEngine;
using V=System.Numerics.Vector3;
namespace EjoyFramework.GamePlay.Tests
{
    public sealed class StaticPhysicsHistoricalQueryTests
    {
        private GameObject wall;private MeshCollider collider;private HistoricalHitWorld history;private StaticPhysicsHistoricalQuery query;
        [SetUp] public void Setup()
        {
            wall=GameObject.CreatePrimitive(PrimitiveType.Cube);var mesh=wall.GetComponent<MeshFilter>().sharedMesh;UnityEngine.Object.DestroyImmediate(wall.GetComponent<BoxCollider>());
            collider=wall.AddComponent<MeshCollider>();collider.sharedMesh=mesh;wall.layer=29;wall.isStatic=false;wall.transform.position=new Vector3(0,0,5);Physics.SyncTransforms();
            history=new HistoricalHitWorld(4,4);history.Reset(1);history.Capture(1,new[]{HistoricalCollider.Sphere(8,2,0,2,new V(0,0,10),.5f)});
            query=new StaticPhysicsHistoricalQuery(history,Physics.defaultPhysicsScene,new Collider[]{collider},1<<29,7,0x80000000);
        }
        [TearDown] public void Cleanup()=>UnityEngine.Object.DestroyImmediate(wall);
        [Test] public void ExplicitUnbakedStaticMeshOccludesHistoricalActorAndPreservesHistoryAdmission()
        {
            Assert.That(query.Sweep(1,1,V.Zero,V.UnitZ,20,.1f,uint.MaxValue,0,out var hit),Is.EqualTo(HistoricalQueryStatus.Hit));
            Assert.That(hit.Collider.Owner,Is.EqualTo(7));Assert.That(hit.Collider.Shape,Is.EqualTo(HistoricalShape.ExternalStatic));Assert.That(hit.Distance,Is.EqualTo(4.4f).Within(.001f));
            Assert.Throws<ArgumentException>(()=>history.Capture(2,new[]{hit.Collider}));
            Assert.That(query.Sweep(1,1,V.Zero,V.UnitZ,20,.1f,uint.MaxValue,7,out hit),Is.EqualTo(HistoricalQueryStatus.Hit));Assert.That(hit.Collider.Owner,Is.EqualTo(8));
            Assert.That(query.Sweep(1,2,V.Zero,V.UnitZ,20,.1f,uint.MaxValue,0,out _),Is.EqualTo(HistoricalQueryStatus.HistoryUnavailable));
            Assert.That(query.Sweep(2,1,V.Zero,V.UnitZ,20,.1f,uint.MaxValue,0,out _),Is.EqualTo(HistoricalQueryStatus.WrongEpoch));
        }
        [Test] public void SweptProjectileHitsStaticMeshWithoutTunnelingAndReportsFallback()
        {
            var world=new LinearProjectileWorld(2);world.Reset(1);
            var shot=new LinearProjectileState{Key=new ProjectileKey(1,1,1,1),Start=V.Zero,End=V.UnitZ*200,Position=V.Zero,Speed=600,Radius=.1f,MaxAgeSeconds=1,ImpactRetentionSeconds=1,LastTick=0,Mask=uint.MaxValue};
            world.Spawn(in shot);var hits=new ProjectileImpact[2];var result=world.Advance(2,query,true,hits);
            Assert.That(result.Impacts,Is.EqualTo(1));Assert.That(result.FutureFallbacks,Is.EqualTo(1));Assert.That(hits[0].UsedLatestHistory,Is.True);Assert.That(hits[0].Hit.Collider.Owner,Is.EqualTo(7));
        }
        [Test] public void InitialOverlapIsVisibleAndMovedStaticGeometryIsRejected()
        {
            Assert.That(query.Sweep(1,1,new V(0,0,4.4f),V.UnitZ,10,.2f,uint.MaxValue,0,out var hit),Is.EqualTo(HistoricalQueryStatus.Hit));Assert.That(hit.Distance,Is.Zero);
            wall.transform.position=Vector3.right;Physics.SyncTransforms();history.Capture(2,Array.Empty<HistoricalCollider>());
            Assert.Throws<InvalidOperationException>(()=>query.Sweep(1,2,V.Zero,V.UnitZ,20,.1f,uint.MaxValue,0,out _));
        }

        [Test] public void ColliderKeysPreserveDistinctRegistrationIdsForSameNamedObjects()
        {
            var other=UnityEngine.Object.Instantiate(wall);other.name=wall.name;
            try
            {
                wall.transform.position=new Vector3(-2,0,5);other.transform.position=new Vector3(2,0,5);Physics.SyncTransforms();
                var second=other.GetComponent<MeshCollider>();
                query=new StaticPhysicsHistoricalQuery(history,Physics.defaultPhysicsScene,new Collider[]{collider,second},1<<29,7,0x80000000);
                Assert.That(query.Sweep(1,1,new V(-2,0,0),V.UnitZ,20,0,uint.MaxValue,0,out var firstHit),Is.EqualTo(HistoricalQueryStatus.Hit));
                Assert.That(firstHit.Collider.ShapeId,Is.EqualTo(0));
                Assert.That(query.Sweep(1,1,new V(2,0,0),V.UnitZ,20,0,uint.MaxValue,0,out var secondHit),Is.EqualTo(HistoricalQueryStatus.Hit));
                Assert.That(secondHit.Collider.ShapeId,Is.EqualTo(1));
                Assert.Throws<ArgumentException>(()=>new StaticPhysicsHistoricalQuery(history,Physics.defaultPhysicsScene,new Collider[]{collider,collider},1<<29,7,0x80000000));
            }
            finally { UnityEngine.Object.DestroyImmediate(other); }
        }

        [Test] public void UnregisteredColliderInStaticLayerIsRejected()
        {
            var other=UnityEngine.Object.Instantiate(wall);
            try
            {
                other.transform.position=new Vector3(3,0,5);Physics.SyncTransforms();
                Assert.Throws<InvalidOperationException>(()=>query.Sweep(1,1,new V(3,0,0),V.UnitZ,20,0,uint.MaxValue,0,out _));
                // A non-convex mesh is a surface: intersect its front face, rather than
                // expecting a small sphere fully inside the closed mesh to overlap it.
                var overlaps=new Collider[2];
                int count=Physics.defaultPhysicsScene.OverlapSphere(new Vector3(3,0,4.45f),.1f,overlaps,1<<29,QueryTriggerInteraction.Ignore);
                Assert.That(count,Is.EqualTo(1));Assert.That(overlaps[0],Is.EqualTo(other.GetComponent<MeshCollider>()));
                Assert.Throws<InvalidOperationException>(()=>query.Sweep(1,1,new V(3,0,4.45f),V.UnitZ,20,.1f,uint.MaxValue,0,out _));
            }
            finally { UnityEngine.Object.DestroyImmediate(other); }
        }

        [Test] public void ConvexUnregisteredColliderContainingOverlapOriginIsRejected()
        {
            var other=UnityEngine.Object.Instantiate(wall);
            try
            {
                other.transform.position=new Vector3(3,0,5);var solid=other.GetComponent<MeshCollider>();solid.convex=true;Physics.SyncTransforms();
                var overlaps=new Collider[2];
                int count=Physics.defaultPhysicsScene.OverlapSphere(new Vector3(3,0,5),.1f,overlaps,1<<29,QueryTriggerInteraction.Ignore);
                Assert.That(count,Is.EqualTo(1));Assert.That(overlaps[0],Is.EqualTo(solid));
                Assert.Throws<InvalidOperationException>(()=>query.Sweep(1,1,new V(3,0,5),V.UnitZ,0,.1f,uint.MaxValue,0,out _));
            }
            finally { UnityEngine.Object.DestroyImmediate(other); }
        }
        [Test] public void NonConvexInteriorWithoutSurfaceOverlapDoesNotInventAStaticHit()
        {
            var other=UnityEngine.Object.Instantiate(wall);
            try
            {
                other.transform.position=new Vector3(3,0,5);Physics.SyncTransforms();
                var overlaps=new Collider[2];
                Assert.That(Physics.defaultPhysicsScene.OverlapSphere(new Vector3(3,0,5),.1f,overlaps,1<<29,QueryTriggerInteraction.Ignore),Is.Zero);
                // Zero travel isolates initial-overlap admission from ray/backface settings.
                Assert.That(query.Sweep(1,1,new V(3,0,5),V.UnitZ,0,.1f,uint.MaxValue,0,out _),Is.EqualTo(HistoricalQueryStatus.Miss));
            }
            finally { UnityEngine.Object.DestroyImmediate(other); }
        }
    }
}
