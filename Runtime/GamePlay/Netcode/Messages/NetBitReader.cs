//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.GamePlay.Netcode.Messages
{
    /// <summary>
    /// Borrowed, bounded LSB-first bit reader. No staging over-read, allocation, alignment or
    /// implicit padding consumption. Copies share the source but have independent cursors.
    /// The source must remain valid and unchanged for the lifetime of this reader/view.
    /// </summary>
    public ref struct NetBitReader
    {
        private readonly ReadOnlySpan<byte> m_Buffer;
        private readonly long m_BitLimit;
        private long m_BitPosition;

        public long BitPosition => m_BitPosition;
        public long BitLimit => m_BitLimit;
        public long RemainingBits => m_BitLimit - m_BitPosition;

        public NetBitReader(ReadOnlySpan<byte> buffer) : this(buffer, (long)buffer.Length * 8) { }

        /// <summary>Starts at bit zero; bitLimit must be in [0, buffer.Length * 8]. Empty/default is valid.</summary>
        public NetBitReader(ReadOnlySpan<byte> buffer, long bitLimit)
        {
            if (bitLimit < 0 || bitLimit > (long)buffer.Length * 8)
                throw new ArgumentOutOfRangeException(nameof(bitLimit));
            m_Buffer = buffer;
            m_BitLimit = bitLimit;
            m_BitPosition = 0;
        }

        /// <summary>
        /// Reads 0..32 bits; the first bit becomes the result's least significant bit. Invalid
        /// width throws. Insufficient remaining bits returns false/zero without advancing.
        /// </summary>
        public bool TryReadBits(int bitCount, out uint value)
        {
            value = 0;
            if (bitCount < 0 || bitCount > 32) throw new ArgumentOutOfRangeException(nameof(bitCount));
            if (bitCount > RemainingBits) return false;
            long position = m_BitPosition;
            uint result = 0;
            int consumed = 0;
            while (consumed < bitCount)
            {
                int index = (int)(position >> 3);
                int offset = (int)(position & 7);
                int count = Math.Min(bitCount - consumed, 8 - offset);
                uint mask = (1u << count) - 1; // count <= 8, never a shift by 32.
                result |= (((uint)m_Buffer[index] >> offset) & mask) << consumed;
                position += count;
                consumed += count;
            }
            m_BitPosition = position;
            value = result;
            return true;
        }
    }
}
