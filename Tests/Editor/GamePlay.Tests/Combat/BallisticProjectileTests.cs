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
            grenade.Restitution=float.NaN;Assert.That(grenade.Valid,Is.False);
        }
    }
}
