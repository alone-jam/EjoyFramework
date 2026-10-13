using System;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using EjoyFramework.GamePlay.Units;

namespace EjoyFramework.Tests.GamePlay.Units
{
    public class UnitRaycastQueryTests
    {
        private UnitWorld world;
        private UnitModel source;
        private UnitRaycastQuery query;
        [SetUp] public void SetUp()
        {
            world = new UnitWorld(); source = Add(1, 1, 0, 0); query = new UnitRaycastQuery(world);
        }
        private UnitModel Add(int id, int faction, float x, float y)
        {
            var unit = new UnitModel(id, faction); unit.ConfigureHealth(100, 100);
            unit.SetPosition(x, y); world.Add(unit); return unit;
        }
        private UnitModel Cast(float dx = 1, float dy = 0, float length = 10)
        {
            world.SyncSpatial(); query.Cast(source, dx, dy, length, 1, TargetFilter.Default, out var hit, out _);return hit;
        }
        [Test] public void ChoosesFirstSurfaceNotNearestCenter()
        {
            var edge = Add(2, 2, 3, .99f); var center = Add(3, 2, 3.2f, 0);
            Assert.That(Cast(), Is.SameAs(center));
        }
        [Test] public void FiltersAlliesDeadAndUntargetable()
        {
            Add(2, 1, 2, 0); Add(3, 2, 3, 0).Kill(1);Add(4, 2, 4, 0).IsTargetable=false;
            var enemy=Add(5, 2, 5, 0);Assert.That(Cast(),Is.SameAs(enemy));
        }
        [Test] public void IncludesCircleIntersectingEndpoint()
        { var enemy=Add(2,2,10.5f,0);Assert.That(Cast(),Is.SameAs(enemy)); }
        [Test] public void RejectsBehindOriginAndZeroDirection()
        { Add(2,2,-2,0);Assert.That(Cast(),Is.Null);Add(3,2,2,0);Assert.That(Cast(0,0),Is.Null); }
        [Test] public void InsideHitAndTieUseStableId()
        {
            Add(9,2,.2f,0);var low=Add(2,2,.2f,0);world.SyncSpatial();
            Assert.That(query.Cast(source,4,0,10,1,TargetFilter.Default,out var hit,out var distance),Is.True);
            Assert.That(hit,Is.SameAs(low));Assert.That(distance,Is.Zero);
        }
        [Test] public void MovementAndRemovalUpdateBroadPhase()
        {
            var unit=Add(2,2,100,100);Assert.That(Cast(),Is.Null);unit.SetPosition(3,0);
            Assert.That(Cast(),Is.SameAs(unit));world.Remove(2);Assert.That(Cast(),Is.Null);
        }
        [Test] public void MaxRangeLimitsHitAndFarCrowdIsNotScanned()
        {
            for(int i=0;i<1000;i++)Add(100+i,2,100+i,100);
            Add(2,2,8,0);world.SyncSpatial();
            Assert.That(query.Cast(source,1,0,10,1,TargetFilter.EnemiesInRange(4),out _,out _),Is.False);
            Assert.That(query.LastCandidateCount,Is.LessThan(5));
        }
        [Test] public void WarmedQueriesDoNotAllocate()
        {
            for(int i=0;i<128;i++)Add(10+i,2,i%16,i/16);
            world.SyncSpatial();
            NUnit.Framework.TestDelegate run=()=>{for(int i=0;i<64;i++)query.Cast(source,1,0,10,1,TargetFilter.Default,out _,out _);};
            run();run();Assert.That(run,Is.Not.AllocatingGCMemory());
        }
        [Test] public void InvalidLengthsAreRejected()
        { Assert.Throws<ArgumentOutOfRangeException>(()=>query.Cast(source,1,0,float.NaN,1,TargetFilter.Default,out _,out _)); }
    }
}
