using System;
using NUnit.Framework;
using EjoyFramework.Core.Fsm;
namespace EjoyFramework.Tests
{
 public class FsmRunnerTests
 {
  private sealed class Owner{public int Updates,Leaves,Destroyed;}
  private sealed class Running:FsmState<Owner>
  {
   protected internal override void OnUpdate(IFsm<Owner> f,float dt,float real){f.Owner.Updates++;if(f.CurrentStateTime>=.5f)ChangeState<Done>(f);}
   protected internal override void OnLeave(IFsm<Owner> f,bool shutdown){f.Owner.Leaves++;}
   protected internal override void OnDestroy(IFsm<Owner> f){f.Owner.Destroyed++;}
  }
  private sealed class Done:FsmState<Owner>{}
  [Test]public void ExplicitClockTransitionsAndDisposalAreScoped()
  {
   var owner=new Owner();var other=new Owner();using(var b=new FsmRunner<Owner>(other,new Running(),new Done()))
   {
    b.Start<Running>();var a=new FsmRunner<Owner>(owner,new Running(),new Done());a.Start<Running>();
    a.Tick(.25f,.25f);Assert.That(a.StateMachine.CurrentState,Is.TypeOf<Running>());
    a.Tick(.25f,.25f);Assert.That(a.StateMachine.CurrentState,Is.TypeOf<Done>());Assert.That(other.Updates,Is.Zero);
    a.Dispose();a.Dispose();Assert.That(owner.Leaves,Is.EqualTo(1));Assert.That(owner.Destroyed,Is.EqualTo(1));
    Assert.Throws<ObjectDisposedException>(()=>a.Tick(0,0));
   }
  }
 }
}
