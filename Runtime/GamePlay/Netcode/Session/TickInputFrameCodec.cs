//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Buffers.Binary;

namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>
    /// Offline, allocation-free, whole-frame v1 codec. Not a transport, authentication layer,
    /// tick-window policy or command-schema decoder. No client DeltaTime is present. Outputs
    /// must use separate storage from input/destination; spans require exclusive synchronous use.
    /// </summary>
    public static class TickInputFrameCodec
    {
        public const uint Magic = 0x49544A45; // ASCII EJTI in little-endian wire order.
        public const ushort CurrentVersion = 1;
        public const int HeaderSize = 32;
        public const int DefaultMaxPayloadBits = 8192;
        public const int MaximumPayloadBits = 1024 * 1024;

        private const int VersionOffset = 4;
        private const int HeaderSizeOffset = 6;
        private const int SessionEpochOffset = 8;
        private const int WorldEpochOffset = 16;
        private const int CommandTickOffset = 20;
        private const int CommandSchemaOffset = 24;
        private const int CodecRevisionOffset = 26;
        private const int PayloadBitsOffset = 28;

        /// <summary>
        /// Validates header, exact payload length, zero high padding and destination capacity
        /// BEFORE any write. Failure returns false/error and bytesWritten=0, preserving destination.
        /// Payload may overlap destination: memmove precedes header writes. Only the written prefix
        /// changes. Header values are copied before writes; no automatic version downgrade occurs.
        /// </summary>
        public static bool TryEncode(in TickInputFrameHeader header, ReadOnlySpan<byte> payload,
            Span<byte> destination, out int bytesWritten, out TickInputFrameError error,
            int maxPayloadBits = DefaultMaxPayloadBits)
        {
            var value = header;
            bytesWritten = 0;
            if (!ValidLimit(maxPayloadBits)) return Reject(TickInputFrameError.InvalidPayloadLimit, out error);
            if (!ValidHeader(in value)) return Reject(TickInputFrameError.InvalidHeader, out error);
            if (value.PayloadBitLength > (uint)maxPayloadBits) return Reject(TickInputFrameError.PayloadTooLarge, out error);
            int payloadBytes = PayloadBytes(value.PayloadBitLength);
            if (payload.Length != payloadBytes) return Reject(TickInputFrameError.PayloadLengthMismatch, out error);
            if (!CanonicalPadding(payload, value.PayloadBitLength)) return Reject(TickInputFrameError.NonCanonicalPadding, out error);
            long required = (long)HeaderSize + payloadBytes;
            if (destination.Length < required) return Reject(TickInputFrameError.DestinationTooSmall, out error);
            // Span.CopyTo has memmove semantics, including partially overlapping spans.
            // Do not write the header first: it may overlap the original payload source.
            payload.CopyTo(destination.Slice(HeaderSize, payloadBytes));
            WriteHeader(in value, destination.Slice(0, HeaderSize));
            bytesWritten = (int)required;
            error = TickInputFrameError.None;
            return true;
        }

        /// <summary>
        /// Requires exactly one complete frame: validates all fixed header fields, exact nonzero
        /// expected context, size/limit, padding and absence of trailing bytes before publication.
        /// Failure returns default header/empty payload. Success BORROWS source storage: consume
        /// within the owning receive callback/buffer lifetime, before reuse; never retain across
        /// an await or pool return. Epoch matches are NOT authentication or authorization.
        /// </summary>
        public static bool TryDecode(ReadOnlySpan<byte> source, in TickInputFrameContext expected,
            out TickInputFrameHeader header, out ReadOnlySpan<byte> payload, out TickInputFrameError error,
            int maxPayloadBits = DefaultMaxPayloadBits)
        {
            var context = expected;
            header = default;
            payload = default;
            if (!ValidLimit(maxPayloadBits)) return Reject(TickInputFrameError.InvalidPayloadLimit, out error);
            if (!ValidContext(in context)) return Reject(TickInputFrameError.InvalidContext, out error);
            if (source.Length < HeaderSize) return Reject(TickInputFrameError.HeaderTruncated, out error);
            if (BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic) return Reject(TickInputFrameError.InvalidMagic, out error);
            if (BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(VersionOffset)) != CurrentVersion)
                return Reject(TickInputFrameError.UnsupportedVersion, out error);
            if (BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(HeaderSizeOffset)) != HeaderSize)
                return Reject(TickInputFrameError.InvalidHeaderSize, out error);
            var value = ReadHeader(source);
            if (!ValidHeader(in value)) return Reject(TickInputFrameError.InvalidHeader, out error);
            if (value.SessionEpoch != context.SessionEpoch) return Reject(TickInputFrameError.SessionEpochMismatch, out error);
            if (value.WorldEpoch != context.WorldEpoch) return Reject(TickInputFrameError.WorldEpochMismatch, out error);
            if (value.CommandSchemaId != context.CommandSchemaId) return Reject(TickInputFrameError.CommandSchemaMismatch, out error);
            if (value.CodecRevision != context.CodecRevision) return Reject(TickInputFrameError.CodecRevisionMismatch, out error);
            if (value.PayloadBitLength > (uint)maxPayloadBits) return Reject(TickInputFrameError.PayloadTooLarge, out error);
            int payloadBytes = PayloadBytes(value.PayloadBitLength);
            long required = (long)HeaderSize + payloadBytes;
            if (source.Length != required) return Reject(TickInputFrameError.PayloadLengthMismatch, out error);
            var view = source.Slice(HeaderSize, payloadBytes);
            if (!CanonicalPadding(view, value.PayloadBitLength)) return Reject(TickInputFrameError.NonCanonicalPadding, out error);
            header = value;
            payload = view;
            error = TickInputFrameError.None;
            return true;
        }

        private static bool ValidLimit(int maximum) => maximum >= 0 && maximum <= MaximumPayloadBits;
        private static bool ValidHeader(in TickInputFrameHeader value)
            => value.SessionEpoch != 0 && value.WorldEpoch != 0 && value.CommandTick != 0
                && value.CommandSchemaId != 0 && value.CodecRevision != 0;
        private static bool ValidContext(in TickInputFrameContext value)
            => value.SessionEpoch != 0 && value.WorldEpoch != 0 && value.CommandSchemaId != 0 && value.CodecRevision != 0;
        private static int PayloadBytes(uint bits) => (int)(((ulong)bits + 7) / 8);
        private static bool CanonicalPadding(ReadOnlySpan<byte> bytes, uint bits)
        {
            int used = (int)(bits & 7);
            return used == 0 || (bytes[bytes.Length - 1] >> used) == 0;
        }
        private static bool Reject(TickInputFrameError reason, out TickInputFrameError error)
        {
            error = reason;
            return false;
        }

        private static void WriteHeader(in TickInputFrameHeader value, Span<byte> bytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(VersionOffset), CurrentVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(HeaderSizeOffset), HeaderSize);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(SessionEpochOffset), value.SessionEpoch);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(WorldEpochOffset), value.WorldEpoch);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(CommandTickOffset), value.CommandTick);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(CommandSchemaOffset), value.CommandSchemaId);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(CodecRevisionOffset), value.CodecRevision);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(PayloadBitsOffset), value.PayloadBitLength);
        }

        private static TickInputFrameHeader ReadHeader(ReadOnlySpan<byte> bytes)
            => new TickInputFrameHeader(
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(SessionEpochOffset)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(WorldEpochOffset)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(CommandTickOffset)),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(CommandSchemaOffset)),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(CodecRevisionOffset)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(PayloadBitsOffset)));
    }
}
