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
        // Optional semi-implicit ballistic flight. BounceMask is caller-defined environment category.
        public bool Ballistic;
        public Vector3 Velocity;
        public float Gravity,Restitution,ProximityRadius,OwnerIgnoreSeconds;
        public uint BounceMask,BounceTick;
        public bool Valid=>Finite(Velocity)&&HistoricalCollider.Finite(Gravity)&&Gravity>=0&&Gravity<=1000000&&HistoricalCollider.Finite(Restitution)&&Restitution>=0&&Restitution<=1&&HistoricalCollider.Finite(ProximityRadius)&&ProximityRadius>=0&&ProximityRadius<=1000000&&HistoricalCollider.Finite(OwnerIgnoreSeconds)&&OwnerIgnoreSeconds>=0&&OwnerIgnoreSeconds<=3600&&BounceTick<=LastTick&&Key.IsValid&&LastTick>=Key.StartTick-1&&ImpactTick<=LastTick&&
            HistoricalCollider.Finite(Speed)&&Speed>0&&Speed<=1000000&&HistoricalCollider.Finite(Radius)&&Radius>=0&&Radius<=1000000&&
            HistoricalCollider.Finite(MaxAgeSeconds)&&MaxAgeSeconds>0&&MaxAgeSeconds<=3600&&HistoricalCollider.Finite(ImpactRetentionSeconds)&&ImpactRetentionSeconds>=0&&ImpactRetentionSeconds<=3600&&
            Finite(Start)&&Finite(End)&&Finite(Position)&&Finite(ImpactNormal)&&(End-Start).LengthSquared()>.000001f;
        private static bool Finite(Vector3 p)=>HistoricalCollider.Finite(p.X)&&HistoricalCollider.Finite(p.Y)&&HistoricalCollider.Finite(p.Z)&&Math.Abs(p.X)<=1000000&&Math.Abs(p.Y)<=1000000&&Math.Abs(p.Z)<=1000000;
        public Vector3 PositionAt(double tick, int rate)=>ImpactTick!=0?Position:Ballistic?Position+Velocity*(float)(Math.Max(0,tick-LastTick)/rate)-Vector3.UnitY*(float)(.5*Gravity*Math.Pow(Math.Max(0,tick-LastTick)/rate,2)):Start+Vector3.Normalize(End-Start)*(float)(Math.Max(0,tick-Key.StartTick)/rate*Speed);
    }
    public readonly struct ProjectileImpact
    {
        public readonly LinearProjectileState Projectile;
        public readonly HistoricalHit Hit;
        public readonly uint QueryTick, RequestedQueryTick;
        public bool UsedLatestHistory => QueryTick != RequestedQueryTick;
        public ProjectileImpact(in LinearProjectileState projectile,in HistoricalHit hit,uint queryTick,uint? requestedQueryTick=null){Projectile=projectile;Hit=hit;QueryTick=queryTick;RequestedQueryTick=requestedQueryTick??queryTick;}
    }
    public readonly struct ProjectileAdvanceResult
    {
        public readonly int Impacts, Expired, MissingHistory, Fallbacks, FutureFallbacks, PastFallbacks;
        internal ProjectileAdvanceResult(int impacts,int expired,int missing,int fallbacks,int futureFallbacks,int pastFallbacks){Impacts=impacts;Expired=expired;MissingHistory=missing;Fallbacks=fallbacks;FutureFallbacks=futureFallbacks;PastFallbacks=pastFallbacks;}
    }
    /// <summary>Bounded owner-thread linear or optional ballistic simulation (legacy class name retained). Damage, teams, history fallback policy and tuning belong to the caller.
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
        public ProjectileAdvanceResult Advance(uint tick,IHistoricalSweepQuery history,bool fallbackToLatest,Span<ProjectileImpact> impacts)
        {
            Owner();if(m_Epoch==0||history==null||tick<=m_Tick||impacts.Length<m_Count)throw new ArgumentException("Monotonic tick, history and complete output budget required.");
            int hits=0,expired=0,missing=0,fallbacks=0,futureFallbacks=0,pastFallbacks=0;
            for(int i=0;i<m_Count;)
            {
                var p=m_Items[i];
                if(tick<p.Key.StartTick){i++;continue;}
                bool overAge=(tick-p.Key.StartTick)/(float)m_Rate>p.MaxAgeSeconds;
                if(p.ImpactTick>0&&(tick-p.ImpactTick)/(float)m_Rate>p.ImpactRetentionSeconds)
                {m_Items[i]=m_Items[--m_Count];expired++;continue;}
                if(p.ImpactTick!=0){p.LastTick=tick;m_Items[i++]=p;continue;}
                uint queryTick=p.Ballistic?tick:tick>p.CollisionDelay?tick-p.CollisionDelay:0;
                uint requestedQueryTick=queryTick;
                var velocity=p.Velocity;var next=p.PositionAt(tick,m_Rate);
                if(p.Ballistic)
                {
                    float elapsed=(tick-p.LastTick)/(float)m_Rate;
                    velocity-=Vector3.UnitY*p.Gravity*elapsed;
                    next=p.Position+velocity*elapsed;
                }
                var delta=next-p.Position;float travel=delta.Length();
                var direction=travel>.000001f?delta/travel:Vector3.Normalize(p.End-p.Start);
                HistoricalHit hit;var result=Sweep(history,in p,tick,queryTick,direction,travel,out hit);
                if(result==HistoricalQueryStatus.WrongEpoch)throw new InvalidOperationException("Projectile/history epoch mismatch.");
                if(result==HistoricalQueryStatus.HistoryUnavailable&&fallbackToLatest)
                {if(queryTick>history.LatestTick)futureFallbacks++;else pastFallbacks++;queryTick=history.LatestTick;result=Sweep(history,in p,tick,queryTick,direction,travel,out hit);fallbacks++;}
                if(result==HistoricalQueryStatus.HistoryUnavailable)
                {
                    missing++;
                    // A ballistic fuse is independent of collision history. Let the normal
                    // movement/fuse path retain its default-hit impact even when no frame exists.
                    if(!p.Ballistic||!overAge)
                    {if(overAge){m_Items[i]=m_Items[--m_Count];expired++;}else i++;continue;}
                }
                p.LastTick=tick;p.Position=next;if(p.Ballistic)p.Velocity=velocity;
                if(result==HistoricalQueryStatus.Hit)
                {
                    p.Position=hit.Point;p.ImpactNormal=hit.Normal;
                    if(p.Ballistic&&(hit.Collider.Mask&p.BounceMask)!=0)
                    {p.Position+=hit.Normal*p.Radius;p.Velocity=Vector3.Reflect(velocity,hit.Normal)*p.Restitution;p.BounceTick=tick;}
                    else {p.ImpactTick=tick;impacts[hits++]=new ProjectileImpact(in p,in hit,queryTick,requestedQueryTick);}
                }
                if(p.Ballistic&&p.ImpactTick==0&&overAge){p.ImpactTick=tick;impacts[hits++]=new ProjectileImpact(in p,default,queryTick,requestedQueryTick);}
                // Resolve the final movement query before age-based despawn. An impact on
                // that last update enters retained impact state instead of vanishing.
                if(p.ImpactTick==0&&overAge){m_Items[i]=m_Items[--m_Count];expired++;continue;}
                m_Items[i++]=p;
            }
            m_Tick=tick;return new ProjectileAdvanceResult(hits,expired,missing,fallbacks,futureFallbacks,pastFallbacks);
        }

        private HistoricalQueryStatus Sweep(IHistoricalSweepQuery history,in LinearProjectileState p,uint tick,uint queryTick,Vector3 direction,float travel,out HistoricalHit hit)
        {
            ulong excludedOwner=p.Ballistic&&(tick-p.Key.StartTick)/(float)m_Rate>=p.OwnerIgnoreSeconds?0:p.Key.Owner;
            // Sweep distance is center travel; the query already expands shapes by Radius.
            var result=history.Sweep(m_Epoch,queryTick,p.Position,direction,travel,p.Radius,p.Mask,excludedOwner,out hit);
            uint proximityMask=p.Mask&~p.BounceMask;
            if(!p.Ballistic||p.ProximityRadius<=p.Radius||proximityMask==0||result==HistoricalQueryStatus.WrongEpoch||result==HistoricalQueryStatus.HistoryUnavailable)return result;
            var proximity=history.Sweep(m_Epoch,queryTick,p.Position,direction,travel,p.ProximityRadius,proximityMask,excludedOwner,out var actor);
            if(proximity==HistoricalQueryStatus.WrongEpoch||proximity==HistoricalQueryStatus.HistoryUnavailable)return proximity;
            // Environment categories always use physical body contact, even if a collider
            // carries additional category bits. Keep physical contact on an exact tie.
            if(proximity==HistoricalQueryStatus.Hit&&(actor.Collider.Mask&p.BounceMask)==0&&(result!=HistoricalQueryStatus.Hit||actor.Distance<hit.Distance))
            {hit=actor;return HistoricalQueryStatus.Hit;}
            return result;
        }
    }
}
