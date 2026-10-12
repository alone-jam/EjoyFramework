using System;
using EjoyFramework.Core.Ecs;

namespace EjoyGame.Samples.EcsDemo
{
    public struct Position { public float X; }
    public struct Velocity { public float X; }
    public struct Health { public int Value; }
    public struct Projectile { public Entity Target; public int Damage; }
    public struct Hit { public Entity Target; public int Damage; }

    /// <summary>一维投射物示例：移动 → 越过目标位置即命中 → 扣血 → 死亡。纯 C#，无需 Unity。</summary>
    public sealed class ProjectileScenario
    {
        private readonly World m_World;

        public ProjectileScenario(World world, SystemGroup systems)
        {
            m_World = world;
            systems.Add(new MovementSystem(world), 0);
            systems.Add(new HitSystem(world), 10);
            systems.Add(new DamageSystem(world), 20);
            systems.Add(new DeathSystem(world), 30);
        }

        public ProjectileScenario(World world, EcsLoop loop)
        {
            m_World = world;
            loop.Add(EcsPhase.TickSimulation, new MovementSystem(world), 0);
            loop.Add(EcsPhase.TickResolve, new HitSystem(world), 10);
            loop.Add(EcsPhase.TickResolve, new DamageSystem(world), 20);
            loop.Add(EcsPhase.TickResolve, new DeathSystem(world), 30);
        }

        public Entity SpawnTarget(float x, int health)
        {
            var entity = m_World.CreateEntity();
            m_World.Set(entity, new Position { X = x });
            m_World.Set(entity, new Health { Value = health });
            return entity;
        }

        public Entity Fire(float x, Entity target, float speed, int damage)
        {
            var entity = m_World.CreateEntity();
            m_World.Set(entity, new Position { X = x });
            m_World.Set(entity, new Velocity { X = speed });
            m_World.Set(entity, new Projectile { Target = target, Damage = damage });
            return entity;
        }
    }

    /// <summary>
    /// Float-based sample movement. Rejects deltas above float.MaxValue before iteration;
    /// callers must still keep positions, velocities and movement results within float range.
    /// </summary>
    public sealed class MovementSystem : ISystem
    {
        private readonly Query m_Query;
        private MovementVisitor m_Visitor;
        public MovementSystem(World world) => m_Query = world.Query();
        public void Update(World world, CommandBuffer commands, in EcsTime time)
        {
            if (time.DeltaTime > float.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(time),
                    "This float-based sample requires DeltaTime at most float.MaxValue.");
            m_Visitor.DeltaTime = (float)time.DeltaTime;
            m_Query.ForEach<MovementVisitor, Position, Velocity>(ref m_Visitor);
        }

        private struct MovementVisitor : IEntityVisitor<Position, Velocity>
        {
            public float DeltaTime;
            public void Visit(Entity entity, ref Position position, ref Velocity velocity)
                => position.X += velocity.X * DeltaTime;
        }
    }

    internal sealed class HitSystem : ISystem
    {
        private readonly World m_World;
        private readonly Query m_Query;
        private readonly ComponentAction<Position, Velocity, Projectile> m_Check;
        private CommandBuffer m_Commands;
        public HitSystem(World world) { m_World = world; m_Query = world.Query().WithNone<Hit>(); m_Check = Check; }
        public void Update(World world, CommandBuffer commands, in EcsTime time)
        {
            m_Commands = commands;
            m_Query.ForEach(m_Check);
        }
        private void Check(Entity entity, ref Position position, ref Velocity velocity, ref Projectile projectile)
        {
            if (!m_World.TryGet(projectile.Target, out Position target)) { m_Commands.DestroyEntity(entity); return; }
            bool reached = velocity.X >= 0 ? position.X >= target.X : position.X <= target.X;
            if (reached) m_Commands.Set(entity, new Hit { Target = projectile.Target, Damage = projectile.Damage });
        }
    }

    internal sealed class DamageSystem : ISystem
    {
        private readonly World m_World;
        private readonly Query m_Query;
        private readonly ComponentAction<Hit> m_Apply;
        private CommandBuffer m_Commands;
        public DamageSystem(World world) { m_World = world; m_Query = world.Query(); m_Apply = Apply; }
        public void Update(World world, CommandBuffer commands, in EcsTime time)
        {
            m_Commands = commands;
            m_Query.ForEach(m_Apply);
        }
        private void Apply(Entity entity, ref Hit hit)
        {
            if (m_World.Has<Health>(hit.Target)) m_World.Get<Health>(hit.Target).Value -= hit.Damage;
            m_Commands.DestroyEntity(entity);
        }
    }

    internal sealed class DeathSystem : ISystem
    {
        private readonly Query m_Query;
        private readonly ComponentAction<Health> m_Remove;
        private CommandBuffer m_Commands;
        public DeathSystem(World world) { m_Query = world.Query(); m_Remove = Remove; }
        public void Update(World world, CommandBuffer commands, in EcsTime time)
        {
            m_Commands = commands;
            m_Query.ForEach(m_Remove);
        }
        private void Remove(Entity entity, ref Health health)
        {
            if (health.Value <= 0) m_Commands.DestroyEntity(entity);
        }
    }
}
