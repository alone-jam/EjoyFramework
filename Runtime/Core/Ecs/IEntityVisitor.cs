namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Value-type query visitor. Pass the concrete struct by ref to Query.ForEach; do not store
    /// it as an interface (that boxes it). The working copy is written back even when Visit throws.
    /// Visit may change existing component values or request despawn, but not change structure.
    /// </summary>
    public interface IEntityVisitor
    {
        void Visit(Entity entity);
    }

    /// <summary>Visitor requiring one component; references are borrowed only for the Visit call.</summary>
    public interface IEntityVisitor<T1> where T1 : struct
    {
        void Visit(Entity entity, ref T1 component1);
    }

    /// <summary>Visitor requiring two components; references are borrowed only for the Visit call.</summary>
    public interface IEntityVisitor<T1, T2> where T1 : struct where T2 : struct
    {
        void Visit(Entity entity, ref T1 component1, ref T2 component2);
    }

    /// <summary>Visitor requiring three components; references are borrowed only for the Visit call.</summary>
    public interface IEntityVisitor<T1, T2, T3> where T1 : struct where T2 : struct where T3 : struct
    {
        void Visit(Entity entity, ref T1 component1, ref T2 component2, ref T3 component3);
    }

    /// <summary>Visitor requiring four components; references are borrowed only for the Visit call.</summary>
    public interface IEntityVisitor<T1, T2, T3, T4>
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct
    {
        void Visit(Entity entity, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4);
    }
}
