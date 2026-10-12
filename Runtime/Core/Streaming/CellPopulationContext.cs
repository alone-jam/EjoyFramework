using System;

namespace EjoyFramework.Core.Streaming
{
    /// <summary>Immutable metadata captured before IO starts; independent of later manager unregister/reuse.</summary>
    public readonly struct CellPopulationContext
    {
        public int CellId { get; }
        public int Layer { get; }
        public int Cx { get; }
        public int Cz { get; }
        public int ContentKey { get; }
        public int InitialLod { get; }
        public long Generation { get; }
        public int PartitionKey { get; }

        public CellPopulationContext(int cellId, int layer, int cx, int cz, int contentKey,
            int initialLod, long generation, int partitionKey)
        {
            if (cellId < 0) throw new ArgumentOutOfRangeException(nameof(cellId));
            if (layer < 0 || layer > 15) throw new ArgumentOutOfRangeException(nameof(layer));
            if (initialLod < 0) throw new ArgumentOutOfRangeException(nameof(initialLod));
            if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
            if (partitionKey < 0) throw new ArgumentOutOfRangeException(nameof(partitionKey));
            CellId = cellId; Layer = layer; Cx = cx; Cz = cz; ContentKey = contentKey;
            InitialLod = initialLod; Generation = generation; PartitionKey = partitionKey;
        }
    }
}
