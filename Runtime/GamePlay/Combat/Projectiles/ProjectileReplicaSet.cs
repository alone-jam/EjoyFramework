using System;
using System.Numerics;
namespace EjoyFramework.GamePlay.Combat
{
    public enum ProjectileReplicaChange { Predicted, Confirmed, Corrected, Appeared, Impacted, Rejected, Removed, TimedOut, Reset }
    public readonly struct ProjectileReplicaTransition
    {
        public readonly ProjectileReplicaChange Change;
        public readonly LinearProjectileState State;
        public ProjectileReplicaTransition(ProjectileReplicaChange change,in LinearProjectileState state){Change=change;State=state;}
    }
    /// <summary>Bounded prediction-to-authority view lifecycle for complete monotonic projectile frames.
    /// Caller validates network session and owns visual objects. Tick/owner/generation/type identify a projectile;
    /// matching geometry confirms its existing view, changed geometry corrects it. Timeout hides views but retains
    /// identity until the next complete frame/reset, so a late packet cannot repeat a prior impact.</summary>
    public sealed class ProjectileReplicaSet
    {
        private struct Entry { public LinearProjectileState State; public bool Predicted, Visible, ImpactShown, Seen; }
        private readonly Entry[] m_Items;
        private readonly int m_Owner=Environment.CurrentManagedThreadId;
        private int m_Count;
        private ulong m_Session;
        public uint AppliedTick { get; private set; }
        public int Count=>m_Count;
        public int VisibleCount { get {int count=0;for(int i=0;i<m_Count;i++)if(m_Items[i].Visible)count++;return count;} }
        public int TransitionCapacity=>m_Items.Length*3;
        public ProjectileReplicaSet(int capacity=128){if(capacity<1||capacity>4096)throw new ArgumentOutOfRangeException();m_Items=new Entry[capacity];}
        private void Owner(){if(Environment.CurrentManagedThreadId!=m_Owner)throw new InvalidOperationException("Owner thread required.");}
        private void Output(Span<ProjectileReplicaTransition> output){if(output.Length<TransitionCapacity)throw new ArgumentException("Complete transition budget required.");}
        public int Reset(ulong session,Span<ProjectileReplicaTransition> output)
        {Owner();Output(output);if(session==0)throw new ArgumentOutOfRangeException();int count=0;for(int i=0;i<m_Count;i++)if(m_Items[i].Visible)output[count++]=new ProjectileReplicaTransition(ProjectileReplicaChange.Reset,in m_Items[i].State);m_Count=0;m_Session=session;AppliedTick=0;return count;}
        public bool Predict(ulong session,in LinearProjectileState state,out ProjectileReplicaTransition transition)
        {
            Owner();transition=default;if(session!=m_Session||session==0||state.Key.StartTick<=AppliedTick)return false;
            if(!state.Valid||state.ImpactTick!=0)throw new ArgumentException("Valid speculative flight required.");
            for(int i=0;i<m_Count;i++)if(m_Items[i].State.Key.Equals(state.Key))return false;
            if(m_Count==m_Items.Length)throw new InvalidOperationException("Replica capacity exhausted.");
            m_Items[m_Count++]=new Entry{State=state,Predicted=true,Visible=true};transition=new ProjectileReplicaTransition(ProjectileReplicaChange.Predicted,in state);return true;
        }
        public int Reconcile(ulong session,uint tick,ReadOnlySpan<LinearProjectileState> states,Span<ProjectileReplicaTransition> output,float positionTolerance=.1f)
        {
            Owner();Output(output);if(session!=m_Session||session==0)return -1;if(tick<=AppliedTick)return 0;
            if(states.Length>m_Items.Length||!HistoricalCollider.Finite(positionTolerance)||positionTolerance<0)throw new ArgumentException();
            // Validate and budget before changing any state.
            int needed=states.Length;
            for(int i=0;i<states.Length;i++)
            {
                if(!states[i].Valid||states[i].Key.StartTick>tick||states[i].LastTick>tick)throw new ArgumentException("Invalid authority frame.");
                for(int j=0;j<i;j++)if(states[i].Key.Equals(states[j].Key))throw new ArgumentException("Duplicate projectile key.");
            }
            for(int i=0;i<m_Count;i++)if(m_Items[i].Predicted&&m_Items[i].State.Key.StartTick>=tick)
            {bool present=false;for(int j=0;j<states.Length;j++)if(states[j].Key.Equals(m_Items[i].State.Key)){present=true;break;}if(!present)needed++;}
            if(needed>m_Items.Length)throw new InvalidOperationException("Complete frame and pending predictions exceed capacity.");
            int count=0;
            for(int i=0;i<m_Count;i++)m_Items[i].Seen=false;
            // Remove absent authoritative objects and rejected predictions before allocating new entries.
            for(int i=0;i<m_Count;)
            {
                bool present=false;for(int j=0;j<states.Length;j++)if(states[j].Key.Equals(m_Items[i].State.Key)){present=true;break;}
                if(!present&&(!m_Items[i].Predicted||m_Items[i].State.Key.StartTick<tick))
                {var old=m_Items[i];if(old.Visible)output[count++]=new ProjectileReplicaTransition(old.Predicted?ProjectileReplicaChange.Rejected:ProjectileReplicaChange.Removed,in old.State);m_Items[i]=m_Items[--m_Count];continue;}i++;
            }
            for(int j=0;j<states.Length;j++)
            {
                var state=states[j];int index=-1;for(int i=0;i<m_Count;i++)if(m_Items[i].State.Key.Equals(state.Key)){index=i;break;}
                if(index<0)
                {
                    // Complete frames cannot legitimately introduce an already-omitted old launch.
                    if(state.Key.StartTick<=AppliedTick)continue;
                    index=m_Count++;m_Items[index]=new Entry{State=state,Visible=true};output[count++]=new ProjectileReplicaTransition(ProjectileReplicaChange.Appeared,in state);
                }
                ref var entry=ref m_Items[index];
                if(entry.Predicted)
                {bool matches=Vector3.Distance(entry.State.Start,state.Start)<=positionTolerance&&Vector3.Distance(entry.State.End,state.End)<=positionTolerance&&
                        entry.State.Ballistic==state.Ballistic&&(!state.Ballistic||(entry.State.Velocity==state.Velocity&&entry.State.Gravity==state.Gravity));
                    output[count++]=new ProjectileReplicaTransition(matches?ProjectileReplicaChange.Confirmed:ProjectileReplicaChange.Corrected,in state);}
                else if(!entry.Visible)output[count++]=new ProjectileReplicaTransition(ProjectileReplicaChange.Appeared,in state);
                entry.State=state;entry.Predicted=false;entry.Visible=true;entry.Seen=true;
                if(state.ImpactTick!=0&&!entry.ImpactShown){entry.ImpactShown=true;output[count++]=new ProjectileReplicaTransition(ProjectileReplicaChange.Impacted,in state);}
            }
            AppliedTick=tick;return count;
        }
        public int Expire(double estimatedTick,int timeoutTicks,Span<ProjectileReplicaTransition> output)
        {
            Owner();Output(output);if(double.IsNaN(estimatedTick)||double.IsInfinity(estimatedTick)||estimatedTick<0||timeoutTicks<1)throw new ArgumentOutOfRangeException();
            int count=0;for(int i=0;i<m_Count;i++)if(m_Items[i].Visible&&estimatedTick-Math.Max(AppliedTick,m_Items[i].State.Key.StartTick)>timeoutTicks)
            {m_Items[i].Visible=false;output[count++]=new ProjectileReplicaTransition(ProjectileReplicaChange.TimedOut,in m_Items[i].State);}return count;
        }
        public int RetireOwner(ulong owner,uint liveGeneration,Span<ProjectileReplicaTransition> output)
        {
            Owner();Output(output);int count=0;
            for(int i=0;i<m_Count;)
            {var old=m_Items[i];if(old.Predicted&&old.State.Key.Owner==owner&&old.State.Key.Generation!=liveGeneration)
                {if(old.Visible)output[count++]=new ProjectileReplicaTransition(ProjectileReplicaChange.Reset,in old.State);m_Items[i]=m_Items[--m_Count];continue;}i++;}return count;
        }
        public int CopyVisibleTo(Span<LinearProjectileState> destination)
        {Owner();if(destination.Length<m_Count)throw new ArgumentException();int count=0;for(int i=0;i<m_Count;i++)if(m_Items[i].Visible)destination[count++]=m_Items[i].State;return count;}
    }
}
