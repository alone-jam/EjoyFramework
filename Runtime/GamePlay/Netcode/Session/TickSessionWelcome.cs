using System;
using System.Buffers.Binary;

namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>Validated initialization for the opt-in tick-authoritative profile. This conveys
    /// timeline/schema identity, not account authentication. Transport must already be connected.</summary>
    public readonly struct TickSessionWelcome
    {
        public TickInputFrameContext Context { get; }
        public uint ServerTick { get; }
        public ushort TickRate { get; }
        public int ControlledEntityId { get; }
        public TickSessionWelcome(TickInputFrameContext context, uint serverTick, ushort tickRate, int controlledEntityId)
        { Context = context; ServerTick = serverTick; TickRate = tickRate; ControlledEntityId = controlledEntityId; }
    }
    public static class TickSessionWelcomeCodec
    {
        public const int Size = 40;
        private const uint Magic = 0x57544A45;
        private static bool Valid(in TickSessionWelcome value) => value.Context.SessionEpoch != 0 && value.Context.WorldEpoch != 0 &&
            value.Context.CommandSchemaId != 0 && value.Context.CodecRevision != 0 && value.TickRate > 0 && value.TickRate <= 1000 && value.ControlledEntityId > 0;
        public static bool TryEncode(in TickSessionWelcome value, Span<byte> destination)
        {
            if (!Valid(in value) || destination.Length < Size) return false;
            var target = destination.Slice(0, Size); target.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(target, Magic); target[4] = 1; target[6] = Size;
            BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(8), value.Context.SessionEpoch);
            BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(16), value.Context.WorldEpoch);
            BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(20), value.ServerTick);
            BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(24), value.TickRate);
            BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(26), value.Context.CommandSchemaId);
            BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(28), value.Context.CodecRevision);
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(32), value.ControlledEntityId); return true;
        }
        public static bool TryDecode(ReadOnlySpan<byte> source, ushort expectedSchema, ushort expectedRevision, out TickSessionWelcome value)
        {
            value = default;
            if (source.Length != Size || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic || source[4] != 1 || source[5] != 0 ||
                source[6] != Size || source[7] != 0 || source[30] != 0 || source[31] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(36)) != 0) return false;
            var context = new TickInputFrameContext(BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(8)), BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(16)),
                BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(26)), BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(28)));
            var candidate = new TickSessionWelcome(context, BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(20)),
                BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(24)), BinaryPrimitives.ReadInt32LittleEndian(source.Slice(32)));
            if (!Valid(in candidate) || context.CommandSchemaId != expectedSchema || context.CodecRevision != expectedRevision) return false;
            value = candidate; return true;
        }
    }
}
