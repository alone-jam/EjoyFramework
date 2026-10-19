using System;
using System.Numerics;
using EjoyFramework.GamePlay.Combat;
using NUnit.Framework;
namespace EjoyFramework.GamePlay.Tests.Combat
{
    public sealed class BallisticProjectileTests
    {
        private static LinearProjectileState Grenade()=>new LinearProjectileState{Key=new ProjectileKey(1,1,1,2),Start=Vector3.UnitY,End=Vector3.UnitY+Vector3.UnitX*200,Position=Vector3.UnitY,Velocity=new Vector3(2,-10,0),Speed=10,Radius=.2f,ProximityRadius=.2f,Ballistic=true,Gravity=18,Restitution=.4f,BounceMask=8,Mask=~0u,MaxAgeSeconds=.1f,ImpactRetentionSeconds=1};
        [Test] public void EnvironmentBouncesThenFuseEmitsOneImpactAndRetainsIt()
        {
            var history=new HistoricalHitWorld(64,4);history.Reset(1);var world=new LinearProjectileWorld();world.Reset(1);var grenade=Grenade();world.Spawn(in grenade);var events=new ProjectileImpact[128];var states=new LinearProjectileState[128];int impacts=0;bool bounced=false;
            for(uint tick=1;tick<10;tick++)
            {
                history.Capture(tick,new[]{HistoricalCollider.Box(3,1,0,8,new Vector3(0,-.5f,0),new Vector3(100,.5f,100),Quaternion.Identity)});
                var r=world.Advance(tick,history,false,events);impacts+=r.Impacts;world.CopyTo(states);bounced|=states[0].BounceTick>0;
            }
            Assert.That(bounced,Is.True);Assert.That(impacts,Is.EqualTo(1));Assert.That(states[0].ImpactTick,Is.GreaterThan(0));Assert.That(world.Count,Is.EqualTo(1));
        }
        [Test] public void ActorProximityImpactsWithoutEnvironmentBounceAndRejectsInvalidConfig()
        {
            var history=new HistoricalHitWorld(4,4);history.Reset(1);history.Capture(1,new[]{HistoricalCollider.Sphere(2,1,0,2,new Vector3(0,.8f,0),.2f)});var world=new LinearProjectileWorld();world.Reset(1);var grenade=Grenade();grenade.ProximityRadius=1;world.Spawn(in grenade);var events=new ProjectileImpact[128];
            Assert.That(world.Advance(1,history,false,events).Impacts,Is.EqualTo(1));Assert.That(events[0].Hit.Collider.Owner,Is.EqualTo(2));Assert.That(events[0].Projectile.BounceTick,Is.Zero);
            grenade.Gravity=1000001;Assert.That(grenade.Valid,Is.False);grenade.Gravity=18;grenade.ProximityRadius=1000001;Assert.That(grenade.Valid,Is.False);grenade.ProximityRadius=1;grenade.Restitution=float.NaN;Assert.That(grenade.Valid,Is.False);
        }

        private static HistoricalHitWorld History(uint tick, params HistoricalCollider[] colliders)
        {
            var history=new HistoricalHitWorld(8,8);history.Reset(1);history.Capture(tick,colliders);return history;
        }
        private static LinearProjectileState Flight(Vector3 velocity)
        {
            var p=Grenade();p.Start=p.Position=Vector3.Zero;p.End=Vector3.UnitX*200;
            p.Velocity=velocity;p.Gravity=0;p.MaxAgeSeconds=10;p.Radius=p.ProximityRadius=.1f;return p;
        }
        [TestCase(0f)] [TestCase(.25f)]
        public void SlowFlightStillIntegratesGravity(float verticalVelocity)
        {
            var history=History(1);var world=new LinearProjectileWorld(1,10);world.Reset(1);
            var p=Flight(Vector3.UnitY*verticalVelocity);p.Gravity=10;world.Spawn(in p);
            var events=new ProjectileImpact[1];world.Advance(1,history,false,events);var states=new LinearProjectileState[1];world.CopyTo(states);
            Assert.That(states[0].Velocity.Y,Is.EqualTo(verticalVelocity-1).Within(.00001));
            Assert.That(states[0].Position.Y,Is.EqualTo((verticalVelocity-1)*.1f).Within(.00001));
        }
        [Test] public void StationaryProximityStillDetectsAnEnteringActor()
        {
            var history=History(1,HistoricalCollider.Sphere(2,1,0,2,new Vector3(.8f,0,0),.1f));
            var world=new LinearProjectileWorld(1,10);world.Reset(1);var p=Flight(Vector3.Zero);p.ProximityRadius=1;world.Spawn(in p);
            var events=new ProjectileImpact[1];Assert.That(world.Advance(1,history,false,events).Impacts,Is.EqualTo(1));
            Assert.That(events[0].Hit.Collider.Owner,Is.EqualTo(2));Assert.That(events[0].Hit.Distance,Is.Zero);
        }
        [TestCase(false)] [TestCase(true)]
        public void FuseStillEmitsOneRetainedImpactWithoutHistory(bool fallback)
        {
            var history=History(1);var world=new LinearProjectileWorld(1,10);world.Reset(1);
            var p=Flight(Vector3.UnitX);p.MaxAgeSeconds=.1f;world.Spawn(in p);var events=new ProjectileImpact[1];
            var result=world.Advance(3,history,fallback,events);
            // fallback=true has an available latest frame; false exercises the missing-history branch.
            Assert.That(result.Impacts,Is.EqualTo(1));Assert.That(result.Expired,Is.Zero);
            Assert.That(result.MissingHistory,Is.EqualTo(fallback?0:1));Assert.That(world.Count,Is.EqualTo(1));
            Assert.That(events[0].Projectile.ImpactTick,Is.EqualTo(3));Assert.That(events[0].Hit.Collider.Owner,Is.Zero);
            Assert.That(world.Advance(4,history,fallback,events).Impacts,Is.Zero);
            Assert.That(world.Advance(14,history,fallback,events).Expired,Is.EqualTo(1));
        }
        [Test] public void FuseStillEmitsWhenLatestFallbackAlsoHasNoHistory()
        {
            var history=new HistoricalHitWorld(8,8);history.Reset(1);var world=new LinearProjectileWorld(1,10);world.Reset(1);
            var p=Flight(Vector3.UnitX);p.MaxAgeSeconds=.1f;world.Spawn(in p);var events=new ProjectileImpact[1];
            var result=world.Advance(3,history,true,events);
            Assert.That(result.Impacts,Is.EqualTo(1));Assert.That(result.MissingHistory,Is.EqualTo(1));Assert.That(result.Fallbacks,Is.EqualTo(1));Assert.That(result.Expired,Is.Zero);
            Assert.That(events[0].RequestedQueryTick,Is.EqualTo(3));Assert.That(events[0].QueryTick,Is.Zero);
        }
        [Test] public void SweepDoesNotHitBeyondThisTicksCenterEndpoint()
        {
            var actor=HistoricalCollider.Sphere(2,1,0,2,new Vector3(1.45f,0,0),.1f);var history=History(1,actor);
            var world=new LinearProjectileWorld(1,10);world.Reset(1);var p=Flight(Vector3.UnitX*10);p.Radius=p.ProximityRadius=.2f;world.Spawn(in p);var events=new ProjectileImpact[1];
            Assert.That(world.Advance(1,history,false,events).Impacts,Is.Zero);
            history.Capture(2,new[]{actor});Assert.That(world.Advance(2,history,false,events).Impacts,Is.EqualTo(1));
        }
        [Test] public void WideActorProximityDoesNotCauseEarlyEnvironmentBounce()
        {
            var history=History(1,HistoricalCollider.Box(3,1,0,8,new Vector3(3,0,0),new Vector3(.1f,1,1),Quaternion.Identity));
            var world=new LinearProjectileWorld(1,10);world.Reset(1);var p=Flight(Vector3.UnitX*10);p.ProximityRadius=2;world.Spawn(in p);var events=new ProjectileImpact[1];
            world.Advance(1,history,false,events);var states=new LinearProjectileState[1];world.CopyTo(states);
            Assert.That(states[0].BounceTick,Is.Zero);Assert.That(states[0].Position.X,Is.EqualTo(1).Within(.00001));Assert.That(states[0].Velocity.X,Is.EqualTo(10));
        }
        [TestCase(3f,false)] [TestCase(2.3f,true)]
        public void PhysicalBounceAndWideActorQueriesChooseTheEarliestContact(float actorX,bool actorFirst)
        {
            // Wall surface at x=.7, expanded physical surface at x=.6.
            // Actor proximity begins at actorX-2.1; choose either side of that wall contact.
            var history=History(1,HistoricalCollider.Box(3,1,0,8,new Vector3(.8f,0,0),new Vector3(.1f,1,1),Quaternion.Identity),HistoricalCollider.Sphere(2,1,0,2,new Vector3(actorX,0,0),.1f));
            var world=new LinearProjectileWorld(1,10);world.Reset(1);var p=Flight(Vector3.UnitX*10);p.ProximityRadius=2;world.Spawn(in p);var events=new ProjectileImpact[1];var result=world.Advance(1,history,false,events);
            var states=new LinearProjectileState[1];world.CopyTo(states);
            Assert.That(result.Impacts,Is.EqualTo(actorFirst?1:0));Assert.That(states[0].BounceTick,Is.EqualTo(actorFirst?0:1));
            if(actorFirst)Assert.That(events[0].Hit.Collider.Owner,Is.EqualTo(2));
            else Assert.That(states[0].Velocity.X,Is.LessThan(0));
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void ChangedBallisticTrajectoryIsCorrectedAndAuthoritativeStateStillApplied(int change)
        {
            var replicas=new ProjectileReplicaSet(1);var transitions=new ProjectileReplicaTransition[3];replicas.Reset(1,transitions);
            var predicted=Flight(Vector3.UnitX*10);if(change==3)predicted.Ballistic=false;
            replicas.Predict(1,in predicted,out _);var authority=predicted;authority.LastTick=2;
            if(change==0||change==3)authority.Ballistic=!predicted.Ballistic;
            if(change==1)authority.Velocity+=Vector3.UnitY;
            if(change==2)authority.Gravity+=1;
            Assert.That(replicas.Reconcile(1,2,new[]{authority},transitions),Is.EqualTo(1));
            Assert.That(transitions[0].Change,Is.EqualTo(ProjectileReplicaChange.Corrected));
            var states=new LinearProjectileState[1];replicas.CopyVisibleTo(states);Assert.That(states[0].Ballistic,Is.EqualTo(authority.Ballistic));Assert.That(states[0].Velocity,Is.EqualTo(authority.Velocity));Assert.That(states[0].Gravity,Is.EqualTo(authority.Gravity));
        }
        [Test] public void MatchingBallisticParametersStillConfirmAndApplyCurrentAuthorityPosition()
        {
            var replicas=new ProjectileReplicaSet(1);var transitions=new ProjectileReplicaTransition[3];replicas.Reset(1,transitions);var p=Flight(Vector3.UnitX*10);replicas.Predict(1,in p,out _);
            p.LastTick=2;p.Position=Vector3.UnitX;replicas.Reconcile(1,2,new[]{p},transitions);
            Assert.That(transitions[0].Change,Is.EqualTo(ProjectileReplicaChange.Confirmed));var states=new LinearProjectileState[1];replicas.CopyVisibleTo(states);Assert.That(states[0].Position,Is.EqualTo(p.Position));
        }

        [Test] public void ColliderWithEnvironmentAndActorBitsNeverBouncesAtProximityRadius()
        {
            var history=History(1,HistoricalCollider.Box(3,1,0,8|2,new Vector3(3,0,0),new Vector3(.1f,1,1),Quaternion.Identity));
            var world=new LinearProjectileWorld(1,10);world.Reset(1);var p=Flight(Vector3.UnitX*10);p.ProximityRadius=2;world.Spawn(in p);var events=new ProjectileImpact[1];
            world.Advance(1,history,false,events);var states=new LinearProjectileState[1];world.CopyTo(states);Assert.That(states[0].BounceTick,Is.Zero);Assert.That(states[0].Position.X,Is.EqualTo(1).Within(.00001));
        }
        [Test] public void ZeroProximityRadiusStillUsesPhysicalBodyForEnvironmentContact()
        {
            var history=History(1,HistoricalCollider.Sphere(3,1,0,8,new Vector3(1.25f,0,0),.1f));
            var world=new LinearProjectileWorld(1,10);world.Reset(1);var p=Flight(Vector3.UnitX*10);p.Radius=.2f;p.ProximityRadius=0;world.Spawn(in p);var events=new ProjectileImpact[1];
            world.Advance(1,history,false,events);var states=new LinearProjectileState[1];world.CopyTo(states);Assert.That(states[0].BounceTick,Is.EqualTo(1));Assert.That(states[0].Position.X,Is.EqualTo(.95f).Within(.00001));
        }
        [Test] public void LatestFallbackUsesBothCollisionRadiiAndCountsOneFallback()
        {
            var history=History(1,HistoricalCollider.Sphere(2,1,0,2,new Vector3(.8f,0,0),.1f));
            var world=new LinearProjectileWorld(1,10);world.Reset(1);var p=Flight(Vector3.Zero);p.ProximityRadius=1;world.Spawn(in p);var events=new ProjectileImpact[1];
            var result=world.Advance(3,history,true,events);Assert.That(result.Impacts,Is.EqualTo(1));Assert.That(result.Fallbacks,Is.EqualTo(1));Assert.That(result.FutureFallbacks,Is.EqualTo(1));Assert.That(result.MissingHistory,Is.Zero);
            Assert.That(events[0].QueryTick,Is.EqualTo(1));Assert.That(events[0].RequestedQueryTick,Is.EqualTo(3));
        }
        [Test] public void IrrelevantBallisticParametersDoNotCorrectLinearPrediction()
        {
            var replicas=new ProjectileReplicaSet(1);var changes=new ProjectileReplicaTransition[3];replicas.Reset(1,changes);var p=Flight(Vector3.Zero);p.Ballistic=false;replicas.Predict(1,in p,out _);
            p.LastTick=2;p.Gravity=1;p.Velocity=Vector3.UnitY;replicas.Reconcile(1,2,new[]{p},changes);Assert.That(changes[0].Change,Is.EqualTo(ProjectileReplicaChange.Confirmed));
            var states=new LinearProjectileState[1];replicas.CopyVisibleTo(states);Assert.That(states[0].Gravity,Is.EqualTo(1));Assert.That(states[0].Velocity,Is.EqualTo(Vector3.UnitY));
        }
        [Test] public void WarmBallisticPhysicalAndProximityQueriesAllocateNoManagedMemory()
        {
            var world=new LinearProjectileWorld(1,10);var history=History(1);var p=Flight(Vector3.UnitX);p.ProximityRadius=1;var events=new ProjectileImpact[1];var empty=Array.Empty<HistoricalCollider>();
            for(int i=0;i<10;i++){world.Reset(1);history.Reset(1);history.Capture(1,empty);world.Spawn(in p);world.Advance(1,history,false,events);}
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<100;i++){world.Reset(1);history.Reset(1);history.Capture(1,empty);world.Spawn(in p);world.Advance(1,history,false,events);}
            Assert.That(GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero);
        }
    }
}
