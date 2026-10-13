using System;
using EjoyFramework.GamePlay.Netcode.Prediction;
using NUnit.Framework;
namespace EjoyFramework.GamePlay.Tests
{
    public sealed class PredictedEventLedgerTests
    {
        private readonly PredictionEventScope scope=new PredictionEventScope(7,42,3);
        private PredictedEventLedger ledger;
        private PredictionEventTransition[] output;
        [SetUp] public void Setup(){ledger=new PredictedEventLedger(8);output=new PredictionEventTransition[16];ledger.Reset(scope,output);}
        [Test] public void PredictionAndAuthoritativeConfirmationNeverStartTwice()
        {
            var id=new PredictionEventId(10);Assert.That(ledger.Predict(scope,id),Is.EqualTo(PredictedEventAdmission.Started));
            Assert.That(ledger.Predict(scope,id),Is.EqualTo(PredictedEventAdmission.Duplicate));
            Assert.That(ledger.ResolveWindow(scope,1,10,new[]{id},output),Is.EqualTo(1));Assert.That(output[0].Resolution,Is.EqualTo(PredictionEventResolution.Confirmed));
            Assert.That(ledger.ResolveWindow(scope,1,10,new[]{id},output),Is.Zero);Assert.That(ledger.Predict(scope,id),Is.EqualTo(PredictedEventAdmission.AlreadyResolved));
        }
        [Test] public void CompleteWindowDistinguishesRejectedExpiredAndFuturePending()
        {
            ledger.Predict(scope,new PredictionEventId(2));ledger.Predict(scope,new PredictionEventId(8));ledger.Predict(scope,new PredictionEventId(20));
            int n=ledger.ResolveWindow(scope,5,10,Array.Empty<PredictionEventId>(),output);Assert.That(n,Is.EqualTo(2));
            Assert.That(output[0].Resolution,Is.EqualTo(PredictionEventResolution.Rejected));Assert.That(output[1].Resolution,Is.EqualTo(PredictionEventResolution.HistoryExpired));Assert.That(ledger.PendingCount,Is.EqualTo(1));
        }
        [Test] public void ReorderedOverlappingWindowsOnlyPresentNewAuthoritativeEvents()
        {
            var a=new PredictionEventId(9);var b=new PredictionEventId(12);
            ledger.ResolveWindow(scope,1,10,new[]{a},output);Assert.That(output[0].Resolution,Is.EqualTo(PredictionEventResolution.AuthoritativeOnly));
            Assert.That(ledger.ResolveWindow(scope,1,8,Array.Empty<PredictionEventId>(),output),Is.Zero);
            Assert.That(ledger.ResolveWindow(scope,4,12,new[]{a,b},output),Is.EqualTo(1));Assert.That(output[0].Id.Tick,Is.EqualTo(12));
        }
        [Test] public void LifetimeResetRetractsPendingAndRejectsOldPackets()
        {
            ledger.Predict(scope,new PredictionEventId(5));var next=new PredictionEventScope(8,42,4);
            Assert.That(ledger.Reset(next,output,20),Is.EqualTo(1));Assert.That(output[0].Scope.Equals(scope),Is.True);Assert.That(output[0].Resolution,Is.EqualTo(PredictionEventResolution.ScopeReset));
            Assert.That(ledger.ResolveWindow(scope,1,100,Array.Empty<PredictionEventId>(),output),Is.EqualTo(-1));Assert.That(ledger.Predict(scope,new PredictionEventId(30)),Is.EqualTo(PredictedEventAdmission.WrongScope));
            Assert.That(ledger.Predict(next,new PredictionEventId(19)),Is.EqualTo(PredictedEventAdmission.AlreadyResolved));Assert.That(ledger.Predict(next,new PredictionEventId(21)),Is.EqualTo(PredictedEventAdmission.Started));
        }
        [Test] public void ChannelsAndOrdinalsHaveIndependentIdentity()
        {
            var events=new[]{new PredictionEventId(4,0,0),new PredictionEventId(4,0,1),new PredictionEventId(4,1,0)};
            foreach(var id in events)Assert.That(ledger.Predict(scope,id),Is.EqualTo(PredictedEventAdmission.Started));
            Assert.That(ledger.ResolveWindow(scope,1,4,events,output),Is.EqualTo(3));Assert.That(ledger.PendingCount,Is.Zero);
        }
        [Test] public void CapacityAndOutputErrorsDoNotLosePredictedEffects()
        {
            for(uint i=1;i<=8;i++)ledger.Predict(scope,new PredictionEventId(i));
            Assert.That(ledger.Predict(scope,new PredictionEventId(9)),Is.EqualTo(PredictedEventAdmission.CapacityExceeded));
            Assert.Throws<ArgumentException>(()=>ledger.ResolveWindow(scope,1,8,Array.Empty<PredictionEventId>(),new PredictionEventTransition[7]));
            Assert.That(ledger.PendingCount,Is.EqualTo(8));Assert.That(ledger.ResolvedThrough,Is.Zero);
        }
        [Test] public void MalformedCompleteWindowsAreAtomic()
        {
            var id=new PredictionEventId(4);ledger.Predict(scope,id);
            Assert.Throws<ArgumentException>(()=>ledger.ResolveWindow(scope,1,5,new[]{id,id},output));
            Assert.Throws<ArgumentException>(()=>ledger.ResolveWindow(scope,5,8,new[]{id},output));
            Assert.That(ledger.PendingCount,Is.EqualTo(1));Assert.That(ledger.ResolvedThrough,Is.Zero);
        }
        [Test] public void DisconnectClosesScopeAndCancelsPendingBeforeReconnect()
        {
            ledger.Predict(scope,new PredictionEventId(8));Assert.That(ledger.Close(output),Is.EqualTo(1));
            Assert.That(output[0].Resolution,Is.EqualTo(PredictionEventResolution.ScopeReset));
            Assert.That(ledger.Predict(scope,new PredictionEventId(9)),Is.EqualTo(PredictedEventAdmission.WrongScope));
            Assert.Throws<ArgumentException>(()=>ledger.Reset(scope,output));
            var next=new PredictionEventScope(9,42,3);ledger.Reset(next,output,8);Assert.That(ledger.Predict(next,new PredictionEventId(9)),Is.EqualTo(PredictedEventAdmission.Started));
        }
        [Test] public void SameScopeResetCannotEraseDeduplicationHistory()
        { Assert.Throws<ArgumentException>(()=>ledger.Reset(scope,output)); }
        [Test] public void WarmPredictionAndResolutionAllocateNoManagedBytes()
        {
            var events=new PredictionEventId[1];events[0]=new PredictionEventId(1);ledger.Predict(scope,events[0]);ledger.ResolveWindow(scope,1,1,events,output);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(uint tick=2;tick<2000;tick++){events[0]=new PredictionEventId(tick);ledger.Predict(scope,events[0]);ledger.ResolveWindow(scope,tick,tick,events,output);}
            Assert.That(GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero);
        }
    }
}
