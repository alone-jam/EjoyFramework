using System;
using System.Numerics;
namespace EjoyFramework.GamePlay.Combat
{
    /// <summary>Bounded complete collider frames. No live physics mutation, implicit tick clamp, IO or per-query allocation.
    /// Owner thread only; each capture is a complete coherent world. Gameplay owns tick fallback, teams and damage.
    /// Generation prevents a historical hit on a retired lifetime from becoming damage to a respawned owner.</summary>
    public sealed class HistoricalHitWorld : IHistoricalSweepQuery
    {
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        private readonly HistoricalCollider[] shapes;
        private readonly uint[] ticks;
        private readonly int[] counts;
        private readonly int capacity;
        private ulong epoch;
        public uint LatestTick { get; private set; }
        public HistoricalHitWorld(int historyTicks = 16, int maxColliders = 128)
        {
            if (historyTicks < 1 || historyTicks > 4096 || maxColliders < 1 || maxColliders > 4096 || (long)historyTicks * maxColliders > 1048576) throw new ArgumentOutOfRangeException();
            ticks = new uint[historyTicks]; counts = new int[historyTicks]; capacity = maxColliders; shapes = new HistoricalCollider[historyTicks * maxColliders];
        }
        public void Reset(ulong worldEpoch)
        { Owner(); if (worldEpoch == 0) throw new ArgumentOutOfRangeException(nameof(worldEpoch)); epoch = worldEpoch; LatestTick = 0; Array.Clear(ticks, 0, ticks.Length); Array.Clear(counts, 0, counts.Length); }
        public void Capture(uint tick, ReadOnlySpan<HistoricalCollider> colliders)
        {
            Owner(); if (epoch == 0 || tick == 0 || tick <= LatestTick) throw new ArgumentOutOfRangeException(nameof(tick));
            if (colliders.Length > capacity) throw new ArgumentException("Collider frame exceeds capacity.");
            // Entire validation precedes publication, so malformed/duplicate frames do not destroy the previous slot.
            for (int i = 0; i < colliders.Length; i++)
            {
                if (!colliders[i].Valid) throw new ArgumentException("Invalid collider.");
                for (int j = 0; j < i; j++)
                    if (colliders[i].Owner == colliders[j].Owner && (colliders[i].Generation != colliders[j].Generation || colliders[i].ShapeId == colliders[j].ShapeId))
                        throw new ArgumentException("One owner requires one generation and unique subshape IDs per frame.");
            }
            int slot = (int)(tick % (uint)ticks.Length); colliders.CopyTo(shapes.AsSpan(slot * capacity, colliders.Length));
            counts[slot] = colliders.Length; ticks[slot] = tick; LatestTick = tick;
        }
        public HistoricalQueryStatus Sweep(ulong worldEpoch, uint tick, Vector3 origin, Vector3 direction, float distance, float radius,
            uint mask, ulong excludedOwner, out HistoricalHit hit)
        {
            Owner(); hit = default;
            HistoricalCollider.Vector(origin); HistoricalCollider.Vector(direction);
            float len = direction.Length();
            if (!HistoricalCollider.Finite(len) || len < .000001f || !HistoricalCollider.Finite(distance) || distance < 0 || distance > 1000000 ||
                !HistoricalCollider.Finite(radius) || radius < 0 || radius > 1000000) throw new ArgumentOutOfRangeException();
            if (epoch == 0 || worldEpoch != epoch) return HistoricalQueryStatus.WrongEpoch;
            int slot = (int)(tick % (uint)ticks.Length);
            if (tick == 0 || tick > LatestTick || LatestTick - tick >= ticks.Length || ticks[slot] != tick) return HistoricalQueryStatus.HistoryUnavailable;
            direction /= len; bool found = false; float best = distance;
            for (int i = 0; i < counts[slot]; i++)
            {
                var collider = shapes[slot * capacity + i];
                if (collider.Owner == excludedOwner || (collider.Mask & mask) == 0) continue;
                if (!HistoricalIntersection.Sweep(in collider, origin, direction, radius, out float t, out var normal) || t > best) continue;
                if (found && t == best && (collider.Owner > hit.Collider.Owner || (collider.Owner == hit.Collider.Owner && collider.ShapeId >= hit.Collider.ShapeId))) continue;
                found = true; best = t; hit = new HistoricalHit(in collider, t, origin + direction * t - normal * radius, normal);
            }
            return found ? HistoricalQueryStatus.Hit : HistoricalQueryStatus.Miss;
        }
        /// <summary>Complete unique-owner sphere overlap, sorted by owner. Force-inclusion bypasses only the mask.
        /// Output budget must cover the collider capacity; no partial result is returned.</summary>
        public HistoricalQueryStatus OverlapSphere(ulong worldEpoch,uint tick,Vector3 center,float radius,uint mask,
            ulong excludedOwner,ulong includedOwner,Span<HistoricalCollider> output,out int count)
        {
            Owner();count=0;HistoricalCollider.Vector(center);
            if(!HistoricalCollider.Finite(radius)||radius<0||radius>1000000||output.Length<capacity)throw new ArgumentException("Valid radius and complete output budget required.");
            if(epoch==0||worldEpoch!=epoch)return HistoricalQueryStatus.WrongEpoch;
            int slot=(int)(tick%(uint)ticks.Length);
            if(tick==0||tick>LatestTick||LatestTick-tick>=ticks.Length||ticks[slot]!=tick)return HistoricalQueryStatus.HistoryUnavailable;
            for(int i=0;i<counts[slot];i++)
            {
                var c=shapes[slot*capacity+i];
                if(c.Owner==excludedOwner||(c.Owner!=includedOwner&&(c.Mask&mask)==0)||!HistoricalIntersection.Overlap(in c,center,radius))continue;
                int at=0;while(at<count&&output[at].Owner<c.Owner)at++;
                if(at<count&&output[at].Owner==c.Owner){if(c.ShapeId<output[at].ShapeId)output[at]=c;continue;}
                for(int j=count;j>at;j--)output[j]=output[j-1];output[at]=c;count++;
            }
            return count==0?HistoricalQueryStatus.Miss:HistoricalQueryStatus.Hit;
        }
        private void Owner() { if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Use history on its constructing thread."); }
    }
}
