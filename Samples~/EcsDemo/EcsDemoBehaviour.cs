using EjoyFramework.Core.Ecs;
using EjoyFramework.Core.Unity;
using UnityEngine;

namespace EjoyGame.Samples.EcsDemo
{
    [RequireComponent(typeof(EcsWorldComponent))]
    public sealed class EcsDemoBehaviour : MonoBehaviour
    {
        private EcsEntityView m_Target;
        private EcsEntityView m_Projectile;

        private void Start()
        {
            var driver = GetComponent<EcsWorldComponent>();
            var scenario = new ProjectileScenario(driver.World, driver.Loop);
            var target = scenario.SpawnTarget(5, 100);
            var projectile = scenario.Fire(0, target, 2, 100);
            m_Target = CreateView(driver.World, target, PrimitiveType.Cube, 1);
            m_Projectile = CreateView(driver.World, projectile, PrimitiveType.Sphere, 0.25f);
        }

        private EcsEntityView CreateView(World world, Entity entity, PrimitiveType shape, float scale)
        {
            var go = GameObject.CreatePrimitive(shape);
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * scale;
            Destroy(go.GetComponent<Collider>());
            var view = go.AddComponent<EcsEntityView>();
            view.Bind(world, entity);
            UpdateView(view);
            return view;
        }

        private void LateUpdate() { UpdateView(m_Target); UpdateView(m_Projectile); }

        private static void UpdateView(EcsEntityView view)
        {
            if (view == null) return;
            if (!view.IsBound) { view.gameObject.SetActive(false); return; }
            view.transform.localPosition = new Vector3(view.World.Get<Position>(view.Entity).X, 0, 0);
        }
    }
}
