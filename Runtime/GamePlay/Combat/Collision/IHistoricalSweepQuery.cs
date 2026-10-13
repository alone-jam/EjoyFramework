using System.Numerics;
namespace EjoyFramework.GamePlay.Combat
{
    /// <summary>A history-aware sweep source. Static geometry adapters must preserve epoch/missing-history
    /// outcomes; the simulation caller alone chooses whether to retry unavailable history at LatestTick.</summary>
    public interface IHistoricalSweepQuery
    {
        uint LatestTick { get; }
        HistoricalQueryStatus Sweep(ulong worldEpoch,uint tick,Vector3 origin,Vector3 direction,float distance,float radius,uint mask,ulong excludedOwner,out HistoricalHit hit);
    }
}
