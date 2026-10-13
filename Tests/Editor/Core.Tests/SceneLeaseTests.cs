using System;
using NUnit.Framework;
using EjoyFramework.Core;
using EjoyFramework.Core.Scene;
namespace EjoyFramework.Tests
{
 public class SceneLeaseTests
 {
  private sealed class Helper:ISceneHelper
  {
   public Action<string> Complete,Unloaded;public Action<string,string> Fail,UnloadFail;public int Unloads;public bool ThrowLoad,ThrowUnload;
   public void LoadAsync(string n,int p,object d,Action<float> progress,Action<string> ok,Action<string,string> fail){if(ThrowLoad)throw new InvalidOperationException("load threw");Complete=ok;Fail=fail;}
   public void UnloadAsync(string n,object d,Action<string> ok,Action<string,string> fail){Unloads++;if(ThrowUnload)throw new InvalidOperationException("unload threw");Unloaded=ok;UnloadFail=fail;}
  }
  [Test]public void CancelPendingWaitsForLateSuccessThenUnloadsExactlyOnce()
  {
   Framework.MarkMainThread();var manager=new SceneManager();var helper=new Helper();manager.SetHelper(helper);
   var lease=new SceneLease(manager,"Battle");lease.Dispose();lease.Dispose();Assert.That(helper.Unloads,Is.Zero);
   helper.Complete("Battle");Assert.That(lease.State,Is.EqualTo(SceneLeaseState.Releasing));Assert.That(helper.Unloads,Is.EqualTo(1));
   helper.Unloaded("Battle");lease.Dispose();Assert.That(lease.State,Is.EqualTo(SceneLeaseState.Released));Assert.That(manager.SceneIsLoaded("Battle"),Is.False);
  }
  [Test]public void FailedLoadDoesNotUnloadAndReleaseFailureCanRetry()
  {
   Framework.MarkMainThread();var manager=new SceneManager();var helper=new Helper();manager.SetHelper(helper);
   var failed=new SceneLease(manager,"Missing");helper.Fail("Missing","not found");failed.Dispose();Assert.That(failed.State,Is.EqualTo(SceneLeaseState.Failed));Assert.That(helper.Unloads,Is.Zero);
   var lease=new SceneLease(manager,"Battle");helper.Complete("Battle");lease.Dispose();helper.UnloadFail("Battle","busy");
   Assert.That(lease.State,Is.EqualTo(SceneLeaseState.ReleaseFailed));lease.Dispose();Assert.That(helper.Unloads,Is.EqualTo(2));helper.Unloaded("Battle");Assert.That(lease.IsTerminal,Is.True);
  }
  [Test]public void ThrowingHelperRollsBackPendingStateAndAllowsRetry()
  {
   Framework.MarkMainThread();var manager=new SceneManager();var helper=new Helper {ThrowLoad=true};manager.SetHelper(helper);
   var failed=new SceneLease(manager,"Battle");Assert.That(failed.State,Is.EqualTo(SceneLeaseState.Failed));Assert.That(manager.SceneIsLoading("Battle"),Is.False);
   helper.ThrowLoad=false;var lease=new SceneLease(manager,"Battle");helper.Complete("Battle");helper.ThrowUnload=true;lease.Dispose();
   Assert.That(lease.State,Is.EqualTo(SceneLeaseState.ReleaseFailed));Assert.That(lease.Error,Is.EqualTo("unload threw"));Assert.That(manager.SceneIsUnloading("Battle"),Is.False);
   helper.ThrowUnload=false;lease.Dispose();helper.Unloaded("Battle");Assert.That(lease.IsTerminal,Is.True);
  }

 }
}
