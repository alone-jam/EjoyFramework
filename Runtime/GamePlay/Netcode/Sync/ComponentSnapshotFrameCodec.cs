//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

#nullable enable
using System;
using System.Buffers.Binary;
using EjoyFramework.GamePlay.Netcode.Messages;

namespace EjoyFramework.GamePlay.Netcode.Sync
{
    /// <summary>
    /// Offline homogeneous full-component batch codec. Uses constrained struct calls and
    /// caller-owned storage: no boxing, reflection, raw-state copying or per-record allocation.
    /// The codec definition is copied per operation and must satisfy IComponentStateCodec's
    /// deterministic/stateless contract. default(FrameCodec) fails closed with InvalidLimits.
    /// </summary>
    /// <remarks>
    /// STRICT NONALIASING PRECONDITION (not dynamically checked): wire storage, typed input,
    /// decode scratch and out-parameter storage must not overlap. Inputs must remain unchanged
    /// for the synchronous call; each writable buffer requires exclusive ownership. Do not use
    /// reinterpretation to make typed records/out-parameters alias bytes. Invalid aliasing is
    /// caller misuse with no output guarantees. Disjoint calls with a stateless codec may run
    /// concurrently. Unexpected codec exceptions propagate, leaving success outputs reset.
    /// This does not authenticate, apply to a World, or establish a commit/ack transaction.
    /// </remarks>
    public readonly struct ComponentSnapshotFrameCodec<TState, TCodec>
        where TState : unmanaged
        where TCodec : struct, IComponentStateCodec<TState>
    {
        public const uint Magic = 0x53434A45; // ASCII EJCS in little-endian wire order.
        public const ushort CurrentVersion = 1;
        public const int HeaderSize = ComponentSnapshotLimits.MinimumFrameBytes;
        private const int EntityKeySize = 8;
        private const int VersionOffset = 4;
        private const int HeaderSizeOffset = 6;
        private const int SessionEpochOffset = 8;
        private const int WorldEpochOffset = 16;
        private const int SnapshotIdOffset = 20;
        private const int ServerTickOffset = 28;
        private const int ProcessedCommandTickOffset = 32;
        private const int ComponentSchemaOffset = 36;
        private const int CodecRevisionOffset = 38;
        private const int StateBitsOffset = 40;
        private const int EntityCountOffset = 44;

        private readonly TCodec m_Codec;
        private readonly ComponentSnapshotLimits m_Limits;

        public ComponentSnapshotFrameCodec(TCodec codec) : this(codec, ComponentSnapshotLimits.Default) { }

        /// <summary>Copies configuration. A default limits value is rejected by both Try methods.</summary>
        public ComponentSnapshotFrameCodec(TCodec codec, ComponentSnapshotLimits limits)
        {
            m_Codec = codec;
            m_Limits = limits;
        }

        /// <summary>
        /// On false or a codec exception bytesWritten remains zero. Structural preflight failures
        /// do not write destination; typed failures MAY leave a partially written workspace.
        /// Only true makes destination[0..bytesWritten] sendable. Extra capacity is untouched.
        /// The header's EntityCount must equal records.Length; no implicit sorting or filtering.
        /// </summary>
        public bool TryEncode(in ComponentSnapshotHeader header, ReadOnlySpan<ComponentStateRecord<TState>> records,
            Span<byte> destination, out int bytesWritten, out ComponentSnapshotError error)
        {
            var value = header;
            bytesWritten = 0;
            error = ComponentSnapshotError.None;
            if (!m_Limits.IsValid) return Reject(ComponentSnapshotError.InvalidLimits, out error);
            if (!ValidHeader(in value)) return Reject(ComponentSnapshotError.InvalidHeader, out error);
            if (value.EntityCount != (uint)records.Length) return Reject(ComponentSnapshotError.EntityCountMismatch, out error);
            if (!TryGetLayout(in value, out int recordBytes, out int frameBytes, out error)) return false;
            var codec = m_Codec;
            if (!MatchesCodec(ref codec, in value, out error)) return false;
            if (destination.Length < frameBytes) return Reject(ComponentSnapshotError.DestinationTooSmall, out error);
            if (!ValidateKeys(records, out error)) return false;
            WriteHeader(in value, destination);
            int offset = HeaderSize;
            for (int i = 0; i < records.Length; i++, offset += recordBytes)
            {
                var record = records[i];
                WriteKey(record.Entity, destination.Slice(offset, EntityKeySize));
                var payload = destination.Slice(offset + EntityKeySize, recordBytes - EntityKeySize);
                var state = record.State;
                if (!WriteState(ref codec, in state, payload, value.StateBitCount, out error)) return false;
            }
            bytesWritten = frameBytes;
            error = ComponentSnapshotError.None;
            return true;
        }

        /// <summary>
        /// Validates the entire frame before publishing header/count. On false or a codec
        /// exception header is default and recordCount is zero; scratch MAY contain partial
        /// work and MUST NOT be authoritative storage. On true only scratch[0..recordCount]
        /// is output. Values own their state: copy them before reusing scratch; source bytes
        /// may be reused immediately after return. No trailing bytes or wildcard context.
        /// </summary>
        public bool TryDecode(ReadOnlySpan<byte> frame, in ComponentSnapshotContext expected,
            Span<ComponentStateRecord<TState>> scratch, out ComponentSnapshotHeader header,
            out int recordCount, out ComponentSnapshotError error)
        {
            var context = expected;
            header = default;
            recordCount = 0;
            error = ComponentSnapshotError.None;
            if (!m_Limits.IsValid) return Reject(ComponentSnapshotError.InvalidLimits, out error);
            if (!ValidContext(in context)) return Reject(ComponentSnapshotError.InvalidContext, out error);
            if (!TryReadHeader(frame, out var value, out error)) return false;
            if (!MatchesContext(in value, in context, out error)) return false;
            if (!TryGetLayout(in value, out int recordBytes, out int frameBytes, out error)) return false;
            var codec = m_Codec;
            if (!MatchesCodec(ref codec, in value, out error)) return false;
            if (frame.Length != frameBytes) return Reject(ComponentSnapshotError.FrameLengthMismatch, out error);
            int count = (int)value.EntityCount; // Bounded by TryGetLayout before narrowing or loops.
            if (scratch.Length < count) return Reject(ComponentSnapshotError.ScratchTooSmall, out error);
            if (!ValidateWireRecords(frame, count, recordBytes, value.StateBitCount, out error)) return false;
            int offset = HeaderSize;
            for (int i = 0; i < count; i++, offset += recordBytes)
            {
                var payload = frame.Slice(offset + EntityKeySize, recordBytes - EntityKeySize);
                var reader = new NetBitReader(payload, value.StateBitCount);
                if (!codec.TryRead(ref reader, out var state)) return Reject(ComponentSnapshotError.StateRejected, out error);
                if (reader.BitPosition != value.StateBitCount || reader.RemainingBits != 0)
                    return Reject(ComponentSnapshotError.CodecBitCountMismatch, out error);
                scratch[i] = new ComponentStateRecord<TState>(ReadKey(frame.Slice(offset, EntityKeySize)), state);
            }
            header = value;
            recordCount = count;
            error = ComponentSnapshotError.None;
            return true;
        }

        private bool TryGetLayout(in ComponentSnapshotHeader value, out int recordBytes,
            out int frameBytes, out ComponentSnapshotError error)
        {
            recordBytes = 0;
            frameBytes = 0;
            if (value.EntityCount > (uint)m_Limits.MaxRecords) return Reject(ComponentSnapshotError.TooManyRecords, out error);
            if (value.StateBitCount > (uint)m_Limits.MaxStateBits) return Reject(ComponentSnapshotError.StateTooLarge, out error);
            ulong stride = EntityKeySize + ((ulong)value.StateBitCount + 7) / 8;
            ulong total = HeaderSize + (ulong)value.EntityCount * stride;
            if (total > (ulong)m_Limits.MaxFrameBytes) return Reject(ComponentSnapshotError.FrameTooLarge, out error);
            recordBytes = (int)stride;
            frameBytes = (int)total;
            error = ComponentSnapshotError.None;
            return true;
        }

        private static bool WriteState(ref TCodec codec, in TState state, Span<byte> payload,
            uint bits, out ComponentSnapshotError error)
        {
            var writer = new NetBitWriter(payload, bits);
            if (!codec.TryWrite(ref writer, in state)) return Reject(ComponentSnapshotError.StateRejected, out error);
            if (writer.BitPosition != bits || writer.RemainingBits != 0)
                return Reject(ComponentSnapshotError.CodecBitCountMismatch, out error);
            if (!CanonicalPadding(payload, bits)) return Reject(ComponentSnapshotError.NonCanonicalPadding, out error);
            error = ComponentSnapshotError.None;
            return true;
        }

        private static bool ValidateKeys(ReadOnlySpan<ComponentStateRecord<TState>> records, out ComponentSnapshotError error)
        {
            uint previous = 0;
            for (int i = 0; i < records.Length; i++)
            {
                var key = records[i].Entity;
                if (!ValidateKey(key, previous, out error)) return false;
                previous = key.NetworkId;
            }
            error = ComponentSnapshotError.None;
            return true;
        }

        private static bool ValidateWireRecords(ReadOnlySpan<byte> frame, int count, int recordBytes,
            uint bits, out ComponentSnapshotError error)
        {
            uint previous = 0;
            int offset = HeaderSize;
            for (int i = 0; i < count; i++, offset += recordBytes)
            {
                var key = ReadKey(frame.Slice(offset, EntityKeySize));
                if (!ValidateKey(key, previous, out error)) return false;
                var payload = frame.Slice(offset + EntityKeySize, recordBytes - EntityKeySize);
                if (!CanonicalPadding(payload, bits)) return Reject(ComponentSnapshotError.NonCanonicalPadding, out error);
                previous = key.NetworkId;
            }
            error = ComponentSnapshotError.None;
            return true;
        }

        private static bool ValidateKey(NetworkEntityKey key, uint previous, out ComponentSnapshotError error)
        {
            if (key.NetworkId == 0 || key.SpawnGeneration == 0) return Reject(ComponentSnapshotError.InvalidEntityKey, out error);
            if (key.NetworkId <= previous) return Reject(ComponentSnapshotError.EntityOrderViolation, out error);
            error = ComponentSnapshotError.None;
            return true;
        }

        private static bool MatchesCodec(ref TCodec codec, in ComponentSnapshotHeader value, out ComponentSnapshotError error)
        {
            ushort schema = codec.ComponentSchemaId;
            ushort revision = codec.CodecRevision;
            uint bits = codec.StateBitCount;
            if (schema == 0 || revision == 0) return Reject(ComponentSnapshotError.InvalidCodec, out error);
            if (schema != value.ComponentSchemaId || revision != value.CodecRevision || bits != value.StateBitCount)
                return Reject(ComponentSnapshotError.CodecIdentityMismatch, out error);
            error = ComponentSnapshotError.None;
            return true;
        }

        private static bool MatchesContext(in ComponentSnapshotHeader value, in ComponentSnapshotContext context,
            out ComponentSnapshotError error)
        {
            if (value.SessionEpoch != context.SessionEpoch) return Reject(ComponentSnapshotError.SessionEpochMismatch, out error);
            if (value.WorldEpoch != context.WorldEpoch) return Reject(ComponentSnapshotError.WorldEpochMismatch, out error);
            if (value.ComponentSchemaId != context.ComponentSchemaId) return Reject(ComponentSnapshotError.ComponentSchemaMismatch, out error);
            if (value.CodecRevision != context.CodecRevision) return Reject(ComponentSnapshotError.CodecRevisionMismatch, out error);
            if (value.StateBitCount != context.StateBitCount) return Reject(ComponentSnapshotError.StateBitCountMismatch, out error);
            error = ComponentSnapshotError.None;
            return true;
        }

        private static bool ValidHeader(in ComponentSnapshotHeader value)
            => value.SessionEpoch != 0 && value.WorldEpoch != 0 && value.SnapshotId != 0 && value.ServerTick != 0
                && value.ComponentSchemaId != 0 && value.CodecRevision != 0 && value.LastProcessedCommandTick <= value.ServerTick;

        private static bool ValidContext(in ComponentSnapshotContext value)
            => value.SessionEpoch != 0 && value.WorldEpoch != 0 && value.ComponentSchemaId != 0 && value.CodecRevision != 0;

        private static bool CanonicalPadding(ReadOnlySpan<byte> payload, uint bits)
        {
            int used = (int)(bits & 7);
            return used == 0 || (payload[payload.Length - 1] >> used) == 0;
        }

        private static bool Reject(ComponentSnapshotError reason, out ComponentSnapshotError error)
        {
            error = reason;
            return false;
        }

        private static NetworkEntityKey ReadKey(ReadOnlySpan<byte> bytes)
            => new NetworkEntityKey(BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4)));

        private static void WriteKey(NetworkEntityKey key, Span<byte> bytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, key.NetworkId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(4), key.SpawnGeneration);
        }

        private static bool TryReadHeader(ReadOnlySpan<byte> frame, out ComponentSnapshotHeader value,
            out ComponentSnapshotError error)
        {
            value = default;
            if (frame.Length < HeaderSize) return Reject(ComponentSnapshotError.HeaderTruncated, out error);
            if (BinaryPrimitives.ReadUInt32LittleEndian(frame) != Magic) return Reject(ComponentSnapshotError.InvalidMagic, out error);
            if (BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(VersionOffset)) != CurrentVersion)
                return Reject(ComponentSnapshotError.UnsupportedVersion, out error);
            if (BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(HeaderSizeOffset)) != HeaderSize)
                return Reject(ComponentSnapshotError.InvalidHeaderSize, out error);
            value = new ComponentSnapshotHeader(
                BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(SessionEpochOffset)),
                BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(WorldEpochOffset)),
                BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(SnapshotIdOffset)),
                BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(ServerTickOffset)),
                BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(ProcessedCommandTickOffset)),
                BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(ComponentSchemaOffset)),
                BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(CodecRevisionOffset)),
                BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(StateBitsOffset)),
                BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(EntityCountOffset)));
            if (!ValidHeader(in value)) return Reject(ComponentSnapshotError.InvalidHeader, out error);
            error = ComponentSnapshotError.None;
            return true;
        }

        private static void WriteHeader(in ComponentSnapshotHeader value, Span<byte> bytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(VersionOffset), CurrentVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(HeaderSizeOffset), HeaderSize);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(SessionEpochOffset), value.SessionEpoch);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(WorldEpochOffset), value.WorldEpoch);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(SnapshotIdOffset), value.SnapshotId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(ServerTickOffset), value.ServerTick);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(ProcessedCommandTickOffset), value.LastProcessedCommandTick);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(ComponentSchemaOffset), value.ComponentSchemaId);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(CodecRevisionOffset), value.CodecRevision);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(StateBitsOffset), value.StateBitCount);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(EntityCountOffset), value.EntityCount);
        }
    }
}
