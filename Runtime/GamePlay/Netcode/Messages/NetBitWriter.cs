//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.GamePlay.Netcode.Messages
{
    /// <summary>
    /// Caller-owned fixed-capacity LSB-first bit writer. No allocation or implicit growth.
    /// Each successful write zeroes high unused bits in its last written byte, even over a
    /// prefilled buffer; bytes beyond WrittenSpan stay untouched. Construction does not clear.
    /// Copies alias storage with independent cursors: do not use them as concurrent writers.
    /// </summary>
    public ref struct NetBitWriter
    {
        private Span<byte> m_Buffer;
        private readonly long m_BitLimit;
        private long m_BitPosition;

        public long BitPosition => m_BitPosition;
        public long BitLimit => m_BitLimit;
        public long RemainingBits => m_BitLimit - m_BitPosition;
        public int WrittenByteCount => (int)((m_BitPosition + 7) / 8);
        /// <summary>Borrowed canonical bytes; later writes or buffer reuse may change this view.</summary>
        public ReadOnlySpan<byte> WrittenSpan => m_Buffer.Slice(0, WrittenByteCount);

        public NetBitWriter(Span<byte> buffer) : this(buffer, (long)buffer.Length * 8) { }

        public NetBitWriter(Span<byte> buffer, long bitLimit)
        {
            if (bitLimit < 0 || bitLimit > (long)buffer.Length * 8)
                throw new ArgumentOutOfRangeException(nameof(bitLimit));
            m_Buffer = buffer;
            m_BitLimit = bitLimit;
            m_BitPosition = 0;
        }

        /// <summary>
        /// Writes 0..32 bits without truncating value. Width zero accepts only value zero.
        /// Invalid arguments throw before mutation. Insufficient capacity returns false with
        /// the entire destination and cursor unchanged. Argument validation precedes capacity.
        /// </summary>
        public bool TryWriteBits(uint value, int bitCount)
        {
            if (bitCount < 0 || bitCount > 32) throw new ArgumentOutOfRangeException(nameof(bitCount));
            if (bitCount < 32 && (value >> bitCount) != 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Value does not fit the requested bit width.");
            if (bitCount > RemainingBits) return false;
            long position = m_BitPosition;
            int consumed = 0;
            while (consumed < bitCount)
            {
                int index = (int)(position >> 3);
                int offset = (int)(position & 7);
                int count = Math.Min(bitCount - consumed, 8 - offset);
                uint mask = (1u << count) - 1;
                uint earlierBits = (uint)m_Buffer[index] & ((1u << offset) - 1);
                uint chunk = ((value >> consumed) & mask) << offset;
                // Preserve only already-written lower bits; clear canonical tail padding.
                m_Buffer[index] = (byte)(earlierBits | chunk);
                position += count;
                consumed += count;
            }
            m_BitPosition = position;
            return true;
        }
    }
}
