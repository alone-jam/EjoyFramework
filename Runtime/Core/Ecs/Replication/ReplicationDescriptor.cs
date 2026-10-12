namespace EjoyFramework.Core.Ecs
{
    /// <summary>
    /// Immutable external schema identity and mode. SchemaId zero denotes a missing descriptor.
    /// Stable IDs do not validate field layout, codec version or compatibility; M4 must do that.
    /// </summary>
    public readonly struct ReplicationDescriptor
    {
        public ushort SchemaId { get; }
        public ReplicationMode Mode { get; }

        internal ReplicationDescriptor(ushort schemaId, ReplicationMode mode)
        {
            SchemaId = schemaId;
            Mode = mode;
        }
    }
}
