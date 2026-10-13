using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using EjoyFramework.Core;
using EjoyFramework.Core.ObjectPool;
namespace EjoyFramework.Tests
{
 public class PooledObjectSetTests
 {
  private ObjectPoolManager manager;
  private PooledObjectSet<int, Item> set;
  private int created;
  private sealed class Item:ObjectBase
  {
   public bool Ready;public int RetireCount,Spawns;
   public Item(){Initialize("enemy",new object());}
   protected internal override void OnSpawn(){Ready=false;Spawns++;}
   protected internal override void Release(bool shutdown){}
  }
  [SetUp]public void Setup(){Framework.MarkMainThread();manager=new ObjectPoolManager();var pool=manager.CreateSingleSpawnObjectPool<Item>("test",60,64,float.MaxValue,0);created=0;set=new PooledObjectSet<int,Item>(pool,key=>{created++;return new Item();},v=>v.RetireCount++,v=>v.Ready,64,"enemy");}
  [TearDown]public void Teardown(){set.Clear();manager.Shutdown();}
  [Test]public void RetiresOnceAndWaitsBeforeReturning()
  {
   var view=set.Touch(1);set.Sweep();set.Sweep();set.Sweep();
   Assert.That(view.RetireCount,Is.EqualTo(1));Assert.That(set.Count,Is.EqualTo(1));
   view.Ready=true;set.Sweep();Assert.That(set.Count,Is.Zero);
  }
  [Test]public void ReusesNamedBucketAndInvokesSpawnOnEveryCheckout()
  {
   var first=set.Touch(1);set.Sweep();first.Ready=true;set.Sweep();
   var second=set.Touch(2);Assert.That(second,Is.SameAs(first));Assert.That(created,Is.EqualTo(1));Assert.That(second.Spawns,Is.EqualTo(2));
  }
  [Test]public void ClearReturnsActiveAndRetiringWithoutLeaking()
  {
   var a=set.Touch(1);set.Touch(2);set.Sweep();set.Touch(2);set.Sweep();set.Clear();
   Assert.That(set.Count,Is.Zero);set.Touch(3);set.Touch(4);Assert.That(created,Is.EqualTo(2));
  }
  [Test]public void WarmedTouchSweepDoesNotAllocate()
  {
   NUnit.Framework.TestDelegate run=()=>{for(int n=0;n<32;n++){for(int i=0;i<32;i++)set.Touch(i);set.Sweep();}};
   run();run();Assert.That(run,Is.Not.AllocatingGCMemory());
  }
  [Test]public void RejectsReusingRetiringKeyUntilCleared()
  {set.Touch(1);set.Sweep();set.Sweep();Assert.Throws<System.InvalidOperationException>(()=>set.Touch(1));set.Clear();Assert.DoesNotThrow(()=>set.Touch(1));}
 }
}
