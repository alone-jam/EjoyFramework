using System;
using System.Collections.Generic;
using EjoyFramework.GamePlay.Combat;
using UnityEngine;
using UnityEngine.SceneManagement;
using V=System.Numerics.Vector3;
namespace EjoyFramework.GamePlay.Unity.Combat
{
    /// <summary>Composes immutable static Unity mesh geometry with historical dynamic shapes.
    /// Use a dedicated layer containing exactly the supplied static colliders; moving geometry belongs
    /// in history. Checks transforms/enabled state once per captured tick and fails on mutation.
    /// All buffers are allocated at construction; sweeps do not mutate live physics or allocate.</summary>
    public sealed class StaticPhysicsHistoricalQuery : IHistoricalSweepQuery
    {
        private readonly HistoricalHitWorld history;
        private readonly PhysicsScene scene;
        private readonly Collider[] geometry,overlaps;
        private readonly Matrix4x4[] poses;
        private readonly Dictionary<int,ushort> ids;
        private readonly ulong owner;
        private readonly uint mask;
        private readonly int layers;
        private uint validatedTick;
        public uint LatestTick=>history.LatestTick;
        public StaticPhysicsHistoricalQuery(HistoricalHitWorld history,PhysicsScene scene,Collider[] staticGeometry,int layerMask,ulong owner,uint categoryMask)
        {
            this.history=history??throw new ArgumentNullException(nameof(history));
            if(!scene.IsValid()||staticGeometry==null||staticGeometry.Length==0||staticGeometry.Length>4096||layerMask==0||owner==0||categoryMask==0)throw new ArgumentException("Bounded static geometry and explicit layer/identity required.");
            this.scene=scene;this.owner=owner;mask=categoryMask;layers=layerMask;
            geometry=(Collider[])staticGeometry.Clone();overlaps=new Collider[geometry.Length+1];poses=new Matrix4x4[geometry.Length];ids=new Dictionary<int,ushort>(geometry.Length);
            for(int i=0;i<geometry.Length;i++)
            {
                var c=geometry[i];
                if(!c||!c.enabled||!c.gameObject.activeInHierarchy||c.isTrigger||!c.gameObject.isStatic||(layers&(1<<c.gameObject.layer))==0||c.gameObject.scene.GetPhysicsScene()!=scene)throw new ArgumentException("Geometry must be enabled, non-trigger, static and in the specified scene/layer.");
                ids.Add(c.GetInstanceID(),(ushort)i);poses[i]=c.transform.localToWorldMatrix;
            }
        }
        public HistoricalQueryStatus Sweep(ulong epoch,uint tick,V origin,V direction,float distance,float radius,uint queryMask,ulong excludedOwner,out HistoricalHit hit)
        {
            var result=history.Sweep(epoch,tick,origin,direction,distance,radius,queryMask,excludedOwner,out hit);
            if(result==HistoricalQueryStatus.WrongEpoch||result==HistoricalQueryStatus.HistoryUnavailable)return result;
            if(validatedTick!=history.LatestTick)
            {
                for(int i=0;i<geometry.Length;i++)if(!geometry[i]||!geometry[i].enabled||!geometry[i].gameObject.activeInHierarchy||geometry[i].isTrigger||(layers&(1<<geometry[i].gameObject.layer))==0||geometry[i].transform.localToWorldMatrix!=poses[i])throw new InvalidOperationException("Static collision geometry changed; rebuild the scene query explicitly.");
                validatedTick=history.LatestTick;
            }
            if((queryMask&mask)==0||excludedOwner==owner)return result;
            var start=U(origin);var dir=U(V.Normalize(direction));
            // Unity casts omit initial overlaps. Resolve these explicitly and reject unknown layer occupants.
            if(radius>0)
            {
                int count=scene.OverlapSphere(start,radius,overlaps,layers,QueryTriggerInteraction.Ignore);
                if(count==overlaps.Length)throw new InvalidOperationException("Static overlap budget exhausted.");
                if(count>0)
                {
                    ushort best=ushort.MaxValue;Collider selected=null;
                    for(int i=0;i<count;i++){ushort id=Identity(overlaps[i]);if(id<best){best=id;selected=overlaps[i];}}
                    var point=selected.ClosestPoint(start);var normal=start-point;if(normal.sqrMagnitude<.000001f)normal=-dir;
                    hit=HistoricalHit.StaticGeometry(owner,1,best,mask,0,0,N(point),N(normal));return HistoricalQueryStatus.Hit;
                }
            }
            RaycastHit staticHit;
            bool found=radius>0?scene.SphereCast(start,radius,dir,out staticHit,distance,layers,QueryTriggerInteraction.Ignore):scene.Raycast(start,dir,out staticHit,distance,layers,QueryTriggerInteraction.Ignore);
            if(found)
            {
                ushort id=Identity(staticHit.collider);
                if(result!=HistoricalQueryStatus.Hit||staticHit.distance<hit.Distance)
                {hit=HistoricalHit.StaticGeometry(owner,1,id,mask,0,staticHit.distance,N(staticHit.point),N(staticHit.normal));return HistoricalQueryStatus.Hit;}
            }
            return result;
        }
        private ushort Identity(Collider c)=>ids.TryGetValue(c.GetInstanceID(),out var id)?id:throw new InvalidOperationException("Unexpected collider in the static query layer.");
        private static Vector3 U(V value)=>new Vector3(value.X,value.Y,value.Z);
        private static V N(Vector3 value)=>new V(value.x,value.y,value.z);
    }
}
