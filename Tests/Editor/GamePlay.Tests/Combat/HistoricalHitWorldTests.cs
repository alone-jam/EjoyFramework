using System;
using System.Numerics;
using EjoyFramework.GamePlay.Combat;
using NUnit.Framework;
namespace EjoyFramework.GamePlay.Tests
{
    public sealed class HistoricalHitWorldTests
    {
        private HistoricalHitWorld history;
        [SetUp] public void Setup() { history = new HistoricalHitWorld(4, 8); history.Reset(1); }
        private HistoricalQueryStatus Shoot(uint tick, out HistoricalHit hit, uint mask = uint.MaxValue, ulong exclude = 0, float radius = 0)
            => history.Sweep(1, tick, Vector3.Zero, Vector3.UnitZ, 100, radius, mask, exclude, out hit);
        [Test] public void HeterogeneousNearestShapeWinsRegardlessOfCaptureOrder()
        {
            history.Capture(1, new[] { HistoricalCollider.Sphere(1,1,0,1,new Vector3(0,0,8),1), HistoricalCollider.Box(2,1,0,1,new Vector3(0,0,4),Vector3.One,Quaternion.Identity), HistoricalCollider.Capsule(3,1,0,1,new Vector3(0,-1,6),new Vector3(0,1,6),.5f) });
            Assert.That(Shoot(1,out var hit),Is.EqualTo(HistoricalQueryStatus.Hit)); Assert.That(hit.Collider.Owner,Is.EqualTo(2)); Assert.That(hit.Distance,Is.EqualTo(3).Within(.0001));
        }
        [Test] public void MovingCoverAndTargetAreQueriedAtTheSameHistoricalTick()
        {
            var target = HistoricalCollider.Sphere(2,1,0,2,new Vector3(0,0,10),1);
            history.Capture(1,new[] {target,HistoricalCollider.Box(3,1,0,4,new Vector3(0,0,5),Vector3.One,Quaternion.Identity)});
            history.Capture(2,new[] {target,HistoricalCollider.Box(3,1,0,4,new Vector3(5,0,5),Vector3.One,Quaternion.Identity)});
            Shoot(1,out var old); Shoot(2,out var current); Assert.That(old.Collider.Owner,Is.EqualTo(3)); Assert.That(current.Collider.Owner,Is.EqualTo(2));
        }
        [Test] public void MasksExcludeFriendlyOwnersBeforeNearestSelection()
        {
            history.Capture(1,new[] { HistoricalCollider.Sphere(1,1,0,1,new Vector3(0,0,2),.5f), HistoricalCollider.Sphere(2,1,0,2,new Vector3(0,0,4),.5f) });
            Shoot(1,out var enemy,~1u); Assert.That(enemy.Collider.Owner,Is.EqualTo(2));
            Assert.That(Shoot(1,out _,2,2),Is.EqualTo(HistoricalQueryStatus.Miss));
        }
        [Test] public void HitRetainsRetiredLifetimeInsteadOfRelabellingItAsRespawn()
        {
            history.Capture(1,new[] {HistoricalCollider.Sphere(5,1,0,1,new Vector3(0,0,4),1)});
            history.Capture(2,new[] {HistoricalCollider.Sphere(5,2,0,1,new Vector3(0,0,4),1)});
            Shoot(1,out var old); Shoot(2,out var next); Assert.That(old.Collider.Generation,Is.EqualTo(1)); Assert.That(next.Collider.Generation,Is.EqualTo(2));
        }
        [Test] public void MissingFutureAndEvictedTicksNeverSilentlyClamp()
        {
            history.Capture(1,Array.Empty<HistoricalCollider>()); history.Capture(5,Array.Empty<HistoricalCollider>());
            Assert.That(Shoot(1,out _),Is.EqualTo(HistoricalQueryStatus.HistoryUnavailable)); Assert.That(Shoot(3,out _),Is.EqualTo(HistoricalQueryStatus.HistoryUnavailable)); Assert.That(Shoot(6,out _),Is.EqualTo(HistoricalQueryStatus.HistoryUnavailable));
            Assert.That(Shoot(5,out _),Is.EqualTo(HistoricalQueryStatus.Miss));
        }
        [Test] public void EpochResetMakesOldFramesUnavailable()
        {
            history.Capture(1,new[] {HistoricalCollider.Sphere(1,1,0,1,new Vector3(0,0,5),1)}); history.Reset(2);
            Assert.That(Shoot(1,out _),Is.EqualTo(HistoricalQueryStatus.WrongEpoch));
            Assert.That(history.Sweep(2,1,Vector3.Zero,Vector3.UnitZ,10,0,1,0,out _),Is.EqualTo(HistoricalQueryStatus.HistoryUnavailable));
        }
        [Test] public void InvalidCompleteFrameCannotOverwritePriorSlot()
        {
            var shape=HistoricalCollider.Sphere(1,1,0,1,new Vector3(0,0,5),1); history.Capture(1,new[]{shape});
            Assert.Throws<ArgumentException>(()=>history.Capture(5,new[]{shape,shape})); Assert.That(history.LatestTick,Is.EqualTo(1)); Assert.That(Shoot(1,out _),Is.EqualTo(HistoricalQueryStatus.Hit));
            Assert.Throws<ArgumentException>(()=>history.Capture(2,new[]{default(HistoricalCollider)}));
        }
        [Test] public void TwoGenerationsOfOneOwnerCannotBePublishedInOneFrame()
        { Assert.Throws<ArgumentException>(()=>history.Capture(1,new[]{HistoricalCollider.Sphere(1,1,0,1,Vector3.One,1),HistoricalCollider.Sphere(1,2,1,1,Vector3.One,1)})); }
        [Test] public void SphereSweepUsesRadiusAndCapsuleEndCaps()
        {
            history.Capture(1,new[]{HistoricalCollider.Capsule(1,1,0,1,new Vector3(0,1,5),new Vector3(0,2,5),.5f)});
            Assert.That(Shoot(1,out _),Is.EqualTo(HistoricalQueryStatus.Miss)); Assert.That(Shoot(1,out var hit,radius:.75f),Is.EqualTo(HistoricalQueryStatus.Hit)); Assert.That(hit.Distance,Is.EqualTo(4.25).Within(.001));
        }
        [Test] public void CapsuleParallelAxisAndStartingOverlapAreDefined()
        {
            history.Capture(1,new[]{HistoricalCollider.Capsule(1,1,0,1,new Vector3(0,0,3),new Vector3(0,0,6),1)}); Shoot(1,out var hit); Assert.That(hit.Distance,Is.EqualTo(2).Within(.001));
            Assert.That(history.Sweep(1,1,new Vector3(0,0,4),Vector3.UnitZ,10,0,1,0,out hit),Is.EqualTo(HistoricalQueryStatus.Hit)); Assert.That(hit.Distance,Is.Zero);
        }
        [Test] public void RotatedBoxAndRoundedCornerAreExactRatherThanExpandedAabb()
        {
            history.Capture(1,new[]{HistoricalCollider.Box(1,1,0,1,new Vector3(0,0,5),Vector3.One,Quaternion.CreateFromAxisAngle(Vector3.UnitY,(float)Math.PI/4))});
            Shoot(1,out var hit); Assert.That(hit.Distance,Is.EqualTo(5-Math.Sqrt(2)).Within(.001));
            history.Capture(2,new[]{HistoricalCollider.Box(1,1,0,1,new Vector3(0,0,5),Vector3.One,Quaternion.Identity)});
            Assert.That(history.Sweep(1,2,new Vector3(1.9f,1.9f,0),Vector3.UnitZ,20,1,1,0,out _),Is.EqualTo(HistoricalQueryStatus.Miss));
            Assert.That(history.Sweep(1,2,new Vector3(1.5f,1.5f,0),Vector3.UnitZ,20,1,1,0,out hit),Is.EqualTo(HistoricalQueryStatus.Hit));
            Assert.That(hit.Distance,Is.EqualTo(4-Math.Sqrt(.5)).Within(.001));
        }
        [Test] public void ExactDistanceLimitAndStableIdentityTieBreak()
        {
            history.Capture(1,new[]{HistoricalCollider.Sphere(2,1,0,1,new Vector3(0,0,5),1),HistoricalCollider.Sphere(1,1,0,1,new Vector3(0,0,5),1)});
            Assert.That(history.Sweep(1,1,Vector3.Zero,Vector3.UnitZ,4,0,1,0,out var hit),Is.EqualTo(HistoricalQueryStatus.Hit)); Assert.That(hit.Collider.Owner,Is.EqualTo(1));
            Assert.That(history.Sweep(1,1,Vector3.Zero,Vector3.UnitZ,3.9f,0,1,0,out _),Is.EqualTo(HistoricalQueryStatus.Miss));
        }
        [Test] public void LargeWorldSphereDoesNotLoseRadiusToFloatSquaredDistance()
        { history.Capture(1,new[]{HistoricalCollider.Sphere(1,1,0,1,new Vector3(0,0,100000),1)}); history.Sweep(1,1,Vector3.Zero,Vector3.UnitZ,100001,0,1,0,out var hit); Assert.That(hit.Distance,Is.EqualTo(99999).Within(.01)); }
        [Test] public void NonfiniteOrZeroRayIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(()=>history.Sweep(1,1,Vector3.Zero,Vector3.Zero,1,0,1,0,out _));
            Assert.Throws<ArgumentOutOfRangeException>(()=>HistoricalCollider.Sphere(1,1,0,1,Vector3.Zero,float.NaN));
        }
        [Test] public void WarmCaptureAndHeterogeneousQueryAllocateNoManagedBytes()
        {
            var shapes=new[]{HistoricalCollider.Box(1,1,0,1,new Vector3(0,0,5),Vector3.One,Quaternion.Identity),HistoricalCollider.Capsule(2,1,0,1,new Vector3(0,-1,10),new Vector3(0,1,10),1)};
            for(uint tick=1;tick<20;tick++){history.Capture(tick,shapes);Shoot(tick,out _,radius:.2f);}
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(uint tick=20;tick<120;tick++){history.Capture(tick,shapes);Shoot(tick,out _,radius:.2f);}
            Assert.That(GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero);
        }
    }
}
