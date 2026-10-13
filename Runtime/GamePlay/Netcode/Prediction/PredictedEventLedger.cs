using System;
namespace EjoyFramework.GamePlay.Netcode.Prediction
{
    public readonly struct PredictionEventScope : IEquatable<PredictionEventScope>
    {
        public readonly ulong Epoch, Owner;
        public readonly uint Generation;
        public PredictionEventScope(ulong epoch, ulong owner, uint generation)
        { if(epoch==0||owner==0||generation==0)throw new ArgumentOutOfRangeException();Epoch=epoch;Owner=owner;Generation=generation; }
        public bool Equals(PredictionEventScope other)=>Epoch==other.Epoch&&Owner==other.Owner&&Generation==other.Generation;
    }
    public readonly struct PredictionEventId : IEquatable<PredictionEventId>
    {
        public readonly uint Tick;
        public readonly ushort Channel, Ordinal;
        public PredictionEventId(uint tick, ushort channel=0, ushort ordinal=0)
        {if(tick==0)throw new ArgumentOutOfRangeException(nameof(tick));Tick=tick;Channel=channel;Ordinal=ordinal;}
        public bool Equals(PredictionEventId other)=>Tick==other.Tick&&Channel==other.Channel&&Ordinal==other.Ordinal;
    }
    public enum PredictedEventAdmission { Started, Duplicate, AlreadyResolved, WrongScope, CapacityExceeded }
    public enum PredictionEventResolution { Confirmed, AuthoritativeOnly, Rejected, HistoryExpired, ScopeReset }
    public readonly struct PredictionEventTransition
    {
        public readonly PredictionEventScope Scope;
        public readonly PredictionEventId Id;
        public readonly PredictionEventResolution Resolution;
        public PredictionEventTransition(PredictionEventScope scope, PredictionEventId id, PredictionEventResolution resolution)
        {Scope=scope;Id=id;Resolution=resolution;}
    }
    /// <summary>Bounded owner-thread presentation ledger. Predict starts a speculative effect once;
    /// complete authoritative event windows confirm or retract it. Reordered windows cannot replay effects.
    /// No gameplay mutation, callbacks or IO. Caller stops/reclaims effects for rejection, expiry and reset.
    /// Scope changes require Reset; delayed packets must retain their original scope. Not a durable event log.</summary>
    public sealed class PredictedEventLedger
    {
        private readonly int ownerThread=Environment.CurrentManagedThreadId;
        private readonly PredictionEventId[] pending;
        private int count;
        private PredictionEventScope scope;
        private bool open;
        public int PendingCount=>count;
        public uint ResolvedThrough { get; private set; }
        public PredictedEventLedger(int capacity=128)
        {if(capacity<1||capacity>4096)throw new ArgumentOutOfRangeException(nameof(capacity));pending=new PredictionEventId[capacity];}
        public int Reset(PredictionEventScope next,Span<PredictionEventTransition> output,uint resolvedThrough=0)
        {
            Owner();if(next.Epoch==0||next.Owner==0||next.Generation==0)throw new ArgumentException("Invalid scope.");
            if(scope.Epoch!=0&&scope.Equals(next))throw new ArgumentException("Reset requires a new session or lifetime scope.");
            if(output.Length<count)throw new ArgumentException("Output too small.");
            for(int i=0;i<count;i++)output[i]=new PredictionEventTransition(scope,pending[i],PredictionEventResolution.ScopeReset);
            int written=count;count=0;scope=next;open=true;ResolvedThrough=resolvedThrough;return written;
        }
        /// <summary>Retracts pending effects and closes the scope, for disconnect/disposal. Reset admits a new scope.</summary>
        public int Close(Span<PredictionEventTransition> output)
        {
            Owner();if(output.Length<count)throw new ArgumentException("Output too small.");
            for(int i=0;i<count;i++)output[i]=new PredictionEventTransition(scope,pending[i],PredictionEventResolution.ScopeReset);
            int written=count;count=0;open=false;return written;
        }
        public PredictedEventAdmission Predict(PredictionEventScope expected,PredictionEventId id)
        {
            Owner();if(id.Tick==0)throw new ArgumentException("Invalid event.");
            if(!open||!scope.Equals(expected))return PredictedEventAdmission.WrongScope;
            if(id.Tick<=ResolvedThrough)return PredictedEventAdmission.AlreadyResolved;
            if(Find(id)>=0)return PredictedEventAdmission.Duplicate;
            if(count==pending.Length)return PredictedEventAdmission.CapacityExceeded;
            pending[count++]=id;return PredictedEventAdmission.Started;
        }
        /// <summary>Events must be the COMPLETE set for the inclusive interval, not a delta or latest event.
        /// Missing earlier coverage expires speculative effects rather than claiming they were rejected.
        /// Returns -1 for a foreign scope and 0 for an older/duplicate window. Inputs validate before mutation.</summary>
        public int ResolveWindow(PredictionEventScope expected,uint first,uint last,ReadOnlySpan<PredictionEventId> events,Span<PredictionEventTransition> output)
        {
            Owner();if(!open||!scope.Equals(expected))return -1;
            if(first==0||last<first||events.Length>pending.Length)throw new ArgumentException("Invalid complete window.");
            if(output.Length<count+events.Length)throw new ArgumentException("Output too small.");
            for(int i=0;i<events.Length;i++)
            {
                if(events[i].Tick<first||events[i].Tick>last)throw new ArgumentException("Event outside window.");
                for(int j=0;j<i;j++)if(events[i].Equals(events[j]))throw new ArgumentException("Duplicate authoritative event.");
            }
            if(last<=ResolvedThrough)return 0;
            int written=0;
            for(int i=0;i<events.Length;i++)
            {
                var id=events[i];if(id.Tick<=ResolvedThrough)continue;
                int index=Find(id);var result=index<0?PredictionEventResolution.AuthoritativeOnly:PredictionEventResolution.Confirmed;
                if(index>=0)Remove(index);
                output[written++]=new PredictionEventTransition(scope,id,result);
            }
            for(int i=count-1;i>=0;i--)
            {
                var id=pending[i];if(id.Tick>last)continue;
                output[written++]=new PredictionEventTransition(scope,id,id.Tick<first?PredictionEventResolution.HistoryExpired:PredictionEventResolution.Rejected);Remove(i);
            }
            ResolvedThrough=last;return written;
        }
        private int Find(PredictionEventId id){for(int i=0;i<count;i++)if(pending[i].Equals(id))return i;return -1;}
        private void Remove(int index){pending[index]=pending[--count];pending[count]=default;}
        private void Owner(){if(Environment.CurrentManagedThreadId!=ownerThread)throw new InvalidOperationException("Use the constructing thread.");}
    }
}
