using System;
using System.Numerics;
namespace EjoyFramework.GamePlay.Combat
{
    public readonly struct ProjectileKey : IEquatable<ProjectileKey>
    {
        public readonly ulong Owner;
        public readonly uint Generation, StartTick;
        public readonly ushort Kind;
        public ProjectileKey(ulong owner, uint generation, uint startTick, ushort kind)
        { if(owner==0||generation==0||startTick==0||kind==0)throw new ArgumentOutOfRangeException(); Owner=owner;Generation=generation;StartTick=startTick;Kind=kind; }
        public bool Equals(ProjectileKey other)=>Owner==other.Owner&&Generation==other.Generation&&StartTick==other.StartTick&&Kind==other.Kind;
        public override bool Equals(object value)=>value is ProjectileKey other&&Equals(other);
        public override int GetHashCode()=>unchecked(((Owner.GetHashCode()*397+(int)Generation)*397+(int)StartTick)*397+Kind);
        public bool IsValid=>Owner!=0&&Generation!=0&&StartTick!=0&&Kind!=0;
    }
    public struct LinearProjectileState
    {
        public ProjectileKey Key;
        public uint Id;
        public Vector3 Start, End, Position, ImpactNormal;
        public float Speed, Radius, MaxAgeSeconds, ImpactRetentionSeconds;
        public uint LastTick, ImpactTick, CollisionDelay, Mask;
        public bool Valid=>Key.IsValid&&LastTick>=Key.StartTick-1&&ImpactTick<=LastTick&&
            HistoricalCollider.Finite(Speed)&&Speed>0&&Speed<=1000000&&HistoricalCollider.Finite(Radius)&&Radius>=0&&Radius<=1000000&&
            HistoricalCollider.Finite(MaxAgeSeconds)&&MaxAgeSeconds>0&&MaxAgeSeconds<=3600&&HistoricalCollider.Finite(ImpactRetentionSeconds)&&ImpactRetentionSeconds>=0&&ImpactRetentionSeconds<=3600&&
            Finite(Start)&&Finite(End)&&Finite(Position)&&Finite(ImpactNormal)&&(End-Start).LengthSquared()>.000001f;
        private static bool Finite(Vector3 p)=>HistoricalCollider.Finite(p.X)&&HistoricalCollider.Finite(p.Y)&&HistoricalCollider.Finite(p.Z)&&Math.Abs(p.X)<=1000000&&Math.Abs(p.Y)<=1000000&&Math.Abs(p.Z)<=1000000;
        public Vector3 PositionAt(double tick, int rate)=>ImpactTick!=0?Position:Start+Vector3.Normalize(End-Start)*(float)(Math.Max(0,tick-Key.StartTick)/rate*Speed);
    }
    public readonly struct ProjectileImpact
    {
        public readonly LinearProjectileState Projectile;
        public readonly HistoricalHit Hit;
        public readonly uint QueryTick;
        public ProjectileImpact(in LinearProjectileState projectile,in HistoricalHit hit,uint queryTick){Projectile=projectile;Hit=hit;QueryTick=queryTick;}
    }
    public readonly struct ProjectileAdvanceResult
    {
        public readonly int Impacts, Expired, MissingHistory, Fallbacks;
        internal ProjectileAdvanceResult(int impacts,int expired,int missing,int fallbacks){Impacts=impacts;Expired=expired;MissingHistory=missing;Fallbacks=fallbacks;}
    }
    /// <summary>Bounded owner-thread straight-flight simulation. Damage, teams, history fallback policy and tuning belong to the caller.
    /// Sweeps the whole movement segment, emits an impact once, retains it, then expires. No live physics mutation or callbacks.</summary>
    public sealed class LinearProjectileWorld
    {
        private readonly int m_Owner=Environment.CurrentManagedThreadId, m_Rate;
        private readonly LinearProjectileState[] m_Items;
        private int m_Count;
        private uint m_Tick,m_NextId;
        private ulong m_Epoch;
        public int Count=>m_Count;
        public LinearProjectileWorld(int capacity=128,int tickRate=60)
        {if(capacity<1||capacity>4096||tickRate<1||tickRate>1000)throw new ArgumentOutOfRangeException();m_Items=new LinearProjectileState[capacity];m_Rate=tickRate;}
        private void Owner(){if(Environment.CurrentManagedThreadId!=m_Owner)throw new InvalidOperationException("Owner thread required.");}
        public void Reset(ulong epoch){Owner();if(epoch==0)throw new ArgumentOutOfRangeException();m_Epoch=epoch;m_Count=0;m_Tick=0;m_NextId=0;}
        public bool Spawn(in LinearProjectileState state)
        {
            Owner();if(m_Epoch==0||!state.Valid||state.ImpactTick!=0||state.LastTick!=state.Key.StartTick-1||state.Key.StartTick<=m_Tick)throw new ArgumentException("Fresh next-tick projectile required.");
            for(int i=0;i<m_Count;i++)if(m_Items[i].Key.Equals(state.Key))return false;
            if(m_Count==m_Items.Length)throw new InvalidOperationException("Projectile capacity exhausted.");var assigned=state;assigned.Id=checked(++m_NextId);m_Items[m_Count++]=assigned;return true;
        }
        public int CopyTo(Span<LinearProjectileState> destination){Owner();if(destination.Length<m_Count)throw new ArgumentException("Output too small.");m_Items.AsSpan(0,m_Count).CopyTo(destination);return m_Count;}
        public ProjectileAdvanceResult Advance(uint tick,HistoricalHitWorld history,bool fallbackToLatest,Span<ProjectileImpact> impacts)
        {
            Owner();if(m_Epoch==0||history==null||tick<=m_Tick||impacts.Length<m_Count)throw new ArgumentException("Monotonic tick, history and complete output budget required.");
            int hits=0,expired=0,missing=0,fallbacks=0;
            for(int i=0;i<m_Count;)
            {
                var p=m_Items[i];
                if(tick<p.Key.StartTick){i++;continue;}
                if((p.ImpactTick>0&&(tick-p.ImpactTick)/(double)m_Rate>p.ImpactRetentionSeconds)||
                    (p.ImpactTick==0&&(tick-p.Key.StartTick)/(double)m_Rate>p.MaxAgeSeconds))
                {m_Items[i]=m_Items[--m_Count];expired++;continue;}
                if(p.ImpactTick!=0){p.LastTick=tick;m_Items[i++]=p;continue;}
                uint queryTick=tick>p.CollisionDelay?tick-p.CollisionDelay:0;
                var next=p.PositionAt(tick,m_Rate);var delta=next-p.Position;float distance=delta.Length();
                var direction=Vector3.Normalize(p.End-p.Start);
                var result=history.Sweep(m_Epoch,queryTick,p.Position,direction,distance,p.Radius,p.Mask,p.Key.Owner,out var hit);
                if(result==HistoricalQueryStatus.WrongEpoch)throw new InvalidOperationException("Projectile/history epoch mismatch.");
                if(result==HistoricalQueryStatus.HistoryUnavailable&&fallbackToLatest)
                {queryTick=history.LatestTick;result=history.Sweep(m_Epoch,queryTick,p.Position,direction,distance,p.Radius,p.Mask,p.Key.Owner,out hit);fallbacks++;}
                if(result==HistoricalQueryStatus.HistoryUnavailable){missing++;i++;continue;}
                p.LastTick=tick;p.Position=next;
                if(result==HistoricalQueryStatus.Hit){p.Position=hit.Point;p.ImpactNormal=hit.Normal;p.ImpactTick=tick;impacts[hits++]=new ProjectileImpact(in p,in hit,queryTick);}
                m_Items[i++]=p;
            }
            m_Tick=tick;return new ProjectileAdvanceResult(hits,expired,missing,fallbacks);
        }
    }
}
