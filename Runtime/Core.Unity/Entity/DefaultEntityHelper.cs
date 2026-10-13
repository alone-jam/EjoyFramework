//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using EjoyFramework.Core.Entity;
using UnityEngine;

namespace EjoyFramework.Core.Unity
{
    /// <summary>
    /// 默认实体辅助器。基于 Unity Instantiate + EntityLogic 挂载。
    /// 业务可以注入自定义 helper 覆盖 Spawn 流程（例如池化 prefab、AB 加载后特殊处理）。
    /// </summary>
    public sealed class DefaultEntityHelper : IEntityHelper
    {
        private readonly Transform m_InstanceRoot;

        public DefaultEntityHelper(Transform instanceRoot)
        {
            m_InstanceRoot = instanceRoot;
        }

        public object InstantiateEntity(object entityAsset)
        {
            var prefab = entityAsset as GameObject;
            if (prefab == null) throw new FrameworkException("Entity asset is not a GameObject prefab.");
            GameObject go = Object.Instantiate(prefab);
            if (m_InstanceRoot != null) go.transform.SetParent(m_InstanceRoot, false);
            return go;
        }

        public IEntity CreateEntity(object entityInstance, IEntityGroup entityGroup, object userData)
        {
            var go = entityInstance as GameObject;
            if (go == null) throw new FrameworkException("Entity instance is not a GameObject.");

            // Keep framework IDs independent of Unity's opaque EntityId in 6000.6+.
            // Validate before adding logic or invoking any lifecycle callback.
            EntityData data = userData as EntityData;
#if UNITY_6000_6_OR_NEWER
            if (data == null)
                throw new FrameworkException("Unity 6000.6+ requires explicit EntityData.EntityId. Pass the same integer ID used by ShowEntity; Unity EntityId cannot be converted to a framework ID.");
            int entityId = data.EntityId;
#else
            int entityId = data != null ? data.EntityId : go.GetInstanceID();
#endif

            EntityLogic logic = go.GetComponent<EntityLogic>();
            if (logic == null)
            {
                // 默认挂一个 EmptyEntityLogic 让生命周期回调能跑
                logic = go.AddComponent<EmptyEntityLogic>();
            }

            // 解析 EntityData
            string assetName = data != null ? data.AssetName : go.name;

            var entity = new DefaultEntity(entityId, assetName, entityGroup, go, logic);
            logic.Entity = entity;

            // OnInit 仅首次实例化触发（池复用时被 m_Inited 守卫跳过）；OnShow 每次显示都触发。
            try { logic.InternalOnInit(userData); } catch (System.Exception ex) { FrameworkLog.Error("EntityLogic.OnInit threw: {0}", ex); }
            try { logic.InternalOnShow(userData); } catch (System.Exception ex) { FrameworkLog.Error("EntityLogic.OnShow threw: {0}", ex); }

            return entity;
        }

        public void HideEntity(IEntity entity, bool isShutdown, object userData)
        {
            EntityLogic logic = LogicOf(entity);
            if (logic == null) return;
            try { logic.InternalOnHide(isShutdown, userData); }
            catch (System.Exception ex) { FrameworkLog.Error("EntityLogic.OnHide threw: {0}", ex); }
        }

        public void UpdateEntity(IEntity entity, float elapseSeconds, float realElapseSeconds)
        {
            EntityLogic logic = LogicOf(entity);
            if (logic == null) return;
            logic.InternalOnUpdate(elapseSeconds, realElapseSeconds);
        }

        public void AttachEntity(IEntity child, IEntity parent, object userData)
        {
            EntityLogic childLogic = LogicOf(child);
            EntityLogic parentLogic = LogicOf(parent);
            if (childLogic == null || parentLogic == null) return;
            childLogic.transform.SetParent(parentLogic.transform, false);
            try { parentLogic.InternalOnAttached(childLogic, parentLogic.transform, userData); }
            catch (System.Exception ex) { FrameworkLog.Error("EntityLogic.OnAttached threw: {0}", ex); }
        }

        public void DetachEntity(IEntity child, IEntity parent, object userData)
        {
            EntityLogic childLogic = LogicOf(child);
            EntityLogic parentLogic = LogicOf(parent);
            if (childLogic == null) return;
            if (m_InstanceRoot != null) childLogic.transform.SetParent(m_InstanceRoot, false);
            if (parentLogic != null)
            {
                try { parentLogic.InternalOnDetached(childLogic, userData); }
                catch (System.Exception ex) { FrameworkLog.Error("EntityLogic.OnDetached threw: {0}", ex); }
            }
        }

        public void ReleaseEntity(object entityAsset, object entityInstance)
        {
            var go = entityInstance as GameObject;
            if (go != null)
            {
                EntityLogic logic = go.GetComponent<EntityLogic>();
                if (logic != null)
                {
                    try { logic.InternalOnRecycle(); }
                    catch (System.Exception ex) { FrameworkLog.Error("EntityLogic.OnRecycle threw: {0}", ex); }
                }
                Object.Destroy(go);
            }
            // entityAsset (prefab) 不在这里释放；ResourceManager.UnloadAsset 由 EntityInstanceObject 负责
        }

        private static EntityLogic LogicOf(IEntity entity)
        {
            return (entity as DefaultEntity)?.Logic;
        }
    }

    /// <summary>
    /// 默认实体包装。
    /// </summary>
    internal sealed class DefaultEntity : IEntity
    {
        public int Id { get; private set; }
        public string EntityAssetName { get; private set; }
        public object Handle { get; private set; }
        public IEntityGroup EntityGroup { get; private set; }
        public EntityLogic Logic { get; private set; }

        public DefaultEntity(int id, string assetName, IEntityGroup group, GameObject go, EntityLogic logic)
        {
            Id = id;
            EntityAssetName = assetName;
            EntityGroup = group;
            Handle = go;
            Logic = logic;
        }
    }

    /// <summary>
    /// 业务侧传入 ShowEntity 的 userData，用于设置 EntityId / AssetName。
    /// Unity 6000.6+ 必须显式提供，EntityId 应与 ShowEntity 的整数 ID 一致。
    /// </summary>
    public sealed class EntityData
    {
        public int EntityId;
        public string AssetName;
        public object UserData;
    }

    /// <summary>
    /// 占位 EntityLogic，业务可继承自定义。
    /// </summary>
    internal sealed class EmptyEntityLogic : EntityLogic { }
}
