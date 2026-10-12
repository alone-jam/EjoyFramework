using System;
using EjoyFramework.GamePlay.Netcode.Messages;
using EjoyFramework.GamePlay.Netcode.Sync;
using NUnit.Framework;

namespace EjoyFramework.GamePlay.Tests.Netcode.Sync
{
    // Explicit field codec, not a replacement for any missing production dependency.
    internal readonly struct SnapshotTestState
    {
        public float Position { get; }
        public byte Flags { get; }
        public SnapshotTestState(float position, byte flags) { Position = position; Flags = flags; }
    }

    internal readonly struct SnapshotTestCodec : IComponentStateCodec<SnapshotTestState>
    {
        public ushort ComponentSchemaId => 0x1234;
        public ushort CodecRevision => 0x5678;
        public uint StateBitCount => 13;

        public bool TryWrite(ref NetBitWriter writer, in SnapshotTestState value)
        {
            if (float.IsNaN(value.Position) || float.IsInfinity(value.Position)
                || value.Position < -512 || value.Position > 511
                || value.Position != Math.Truncate(value.Position) || (value.Flags & ~5) != 0) return false;
            return writer.TryWriteBits(value.Flags, 3) && writer.TryWriteBits((uint)(value.Position + 512), 10);
        }

        public bool TryRead(ref NetBitReader reader, out SnapshotTestState value)
        {
            value = default;
            if (!reader.TryReadBits(3, out uint flags) || (flags & ~5u) != 0
                || !reader.TryReadBits(10, out uint position)) return false;
            value = new SnapshotTestState((int)position - 512, (byte)flags);
            return true;
        }
    }

    // Each instance has an immutable definition; every bit in this test-only layout must be zero.
    internal readonly struct SnapshotZeroCodec : IComponentStateCodec<SnapshotTestState>
    {
        public ushort ComponentSchemaId { get; }
        public ushort CodecRevision { get; }
        public uint StateBitCount { get; }
        public SnapshotZeroCodec(uint bits, ushort schema = 0x1234, ushort revision = 0x5678)
        { StateBitCount = bits; ComponentSchemaId = schema; CodecRevision = revision; }

        public bool TryWrite(ref NetBitWriter writer, in SnapshotTestState value)
        {
            for (uint remaining = StateBitCount; remaining != 0;)
            {
                int width = (int)Math.Min(remaining, 32u);
                if (!writer.TryWriteBits(0, width)) return false;
                remaining -= (uint)width;
            }
            return true;
        }

        public bool TryRead(ref NetBitReader reader, out SnapshotTestState value)
        {
            value = default;
            for (uint remaining = StateBitCount; remaining != 0;)
            {
                int width = (int)Math.Min(remaining, 32u);
                if (!reader.TryReadBits(width, out uint bits) || bits != 0) return false;
                remaining -= (uint)width;
            }
            return true;
        }
    }

    public enum SnapshotCodecFault { ReturnFalse, Short, Throw, WrongRemaining, OverConsume }

    // Fault injection on the final zero-flags value, after the first record was processed.
    internal readonly struct SnapshotFaultCodec : IComponentStateCodec<SnapshotTestState>
    {
        private readonly SnapshotCodecFault m_Fault;
        public SnapshotFaultCodec(SnapshotCodecFault fault) { m_Fault = fault; }
        public ushort ComponentSchemaId => 0x1234;
        public ushort CodecRevision => 0x5678;
        public uint StateBitCount => 13;

        public bool TryWrite(ref NetBitWriter writer, in SnapshotTestState value)
        {
            if (value.Flags != 0) return new SnapshotTestCodec().TryWrite(ref writer, in value);
            switch (m_Fault)
            {
                case SnapshotCodecFault.ReturnFalse: return false;
                case SnapshotCodecFault.Short: return writer.TryWriteBits(0, 12);
                case SnapshotCodecFault.OverConsume: return writer.TryWriteBits(0, 14);
                case SnapshotCodecFault.Throw: throw new InvalidOperationException("test codec write fault");
                default:
                    writer = new NetBitWriter(new byte[2], 14);
                    return writer.TryWriteBits(0, 13); // Correct position, incorrect remaining budget.
            }
        }

        public bool TryRead(ref NetBitReader reader, out SnapshotTestState value)
        {
            var probe = reader;
            probe.TryReadBits(3, out uint flags);
            if (flags != 0) return new SnapshotTestCodec().TryRead(ref reader, out value);
            value = default;
            switch (m_Fault)
            {
                case SnapshotCodecFault.ReturnFalse: return false;
                case SnapshotCodecFault.Short: return reader.TryReadBits(12, out _);
                case SnapshotCodecFault.OverConsume: return reader.TryReadBits(14, out _);
                case SnapshotCodecFault.Throw: throw new InvalidOperationException("test codec read fault");
                default:
                    reader = new NetBitReader(new byte[2], 14);
                    return reader.TryReadBits(13, out _);
            }
        }
    }

    internal readonly struct SnapshotThrowingDefinitionCodec : IComponentStateCodec<SnapshotTestState>
    {
        public ushort ComponentSchemaId => throw new InvalidOperationException("test codec definition fault");
        public ushort CodecRevision => 1;
        public uint StateBitCount => 13;
        public bool TryWrite(ref NetBitWriter writer, in SnapshotTestState value) => false;
        public bool TryRead(ref NetBitReader reader, out SnapshotTestState value) { value = default; return false; }
    }

    internal static class ComponentSnapshotTestData
    {
        internal static ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotTestCodec> Codec()
            => new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotTestCodec>(default);

        internal static ComponentSnapshotHeader Header(uint count = 2, uint bits = 13,
            ulong snapshotId = 0x1112131415161718UL, uint tick = 0x55667788, uint ack = 0x01020304)
            => new ComponentSnapshotHeader(0x0102030405060708UL, 0x11223344, snapshotId,
                tick, ack, 0x1234, 0x5678, bits, count);

        internal static ComponentSnapshotContext Context(uint bits = 13)
            => new ComponentSnapshotContext(0x0102030405060708UL, 0x11223344, 0x1234, 0x5678, bits);

        internal static ComponentStateRecord<SnapshotTestState>[] Records() => new[]
        {
            Record(0x01020304, 0x05060708, 211, 5), Record(0x11223344, 0x99AABBCC, -512, 0)
        };

        internal static ComponentStateRecord<SnapshotTestState> Record(uint id, uint generation,
            float position = 0, byte flags = 0)
            => new ComponentStateRecord<SnapshotTestState>(new NetworkEntityKey(id, generation),
                new SnapshotTestState(position, flags));

        // Independently authored wire oracle: no production writer/serializer constructs these bytes.
        internal static byte[] Golden() => new byte[]
        {
            0x45, 0x4A, 0x43, 0x53, 0x01, 0x00, 0x30, 0x00,
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x44, 0x33, 0x22, 0x11, 0x18, 0x17, 0x16, 0x15,
            0x14, 0x13, 0x12, 0x11, 0x88, 0x77, 0x66, 0x55,
            0x04, 0x03, 0x02, 0x01, 0x34, 0x12, 0x78, 0x56,
            0x0D, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
            0x04, 0x03, 0x02, 0x01, 0x08, 0x07, 0x06, 0x05, 0x9D, 0x16,
            0x44, 0x33, 0x22, 0x11, 0xCC, 0xBB, 0xAA, 0x99, 0x00, 0x00
        };

        internal static void RejectDecode(byte[] frame, ComponentSnapshotError expectedError,
            ComponentSnapshotContext? expectedContext = null, ComponentSnapshotLimits? limits = null,
            int capacity = 2)
        {
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotTestCodec>(
                default, limits ?? ComponentSnapshotLimits.Default);
            var context = expectedContext ?? Context();
            var scratch = new ComponentStateRecord<SnapshotTestState>[capacity];
            var before = frame == null ? null : (byte[])frame.Clone();
            var header = Header();
            int count = 99;
            Assert.That(codec.TryDecode(frame, in context, scratch, out header, out count, out var error), Is.False);
            Assert.That(error, Is.EqualTo(expectedError));
            Assert.That(header, Is.EqualTo(default(ComponentSnapshotHeader)));
            Assert.That(count, Is.Zero);
            Assert.That(frame, Is.EqualTo(before));
        }

        internal static void RejectEncode(ComponentSnapshotHeader header,
            ComponentStateRecord<SnapshotTestState>[] records, ComponentSnapshotError expectedError,
            ComponentSnapshotLimits? limits = null, int capacity = 68, bool preflight = true)
        {
            var codec = new ComponentSnapshotFrameCodec<SnapshotTestState, SnapshotTestCodec>(
                default, limits ?? ComponentSnapshotLimits.Default);
            var destination = new byte[capacity];
            Array.Fill(destination, (byte)0xCC);
            var before = (byte[])destination.Clone();
            int written = 99;
            Assert.That(codec.TryEncode(in header, records, destination, out written, out var error), Is.False);
            Assert.That(error, Is.EqualTo(expectedError));
            Assert.That(written, Is.Zero);
            if (preflight) Assert.That(destination, Is.EqualTo(before));
        }
    }
}
