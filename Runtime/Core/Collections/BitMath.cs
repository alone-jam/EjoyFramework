//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

namespace EjoyFramework.Core
{
    /// <summary>
    /// 64 位字的位运算。.NET Standard 2.1 没有 <c>System.Numerics.BitOperations</c>，这里用 de Bruijn 乘法
    /// 与 SWAR 实现：纯整数运算，只查一张 64 字节的表。
    /// </summary>
    internal static class BitMath
    {
        private const ulong DeBruijn64 = 0x03F79D71B4CB0A89UL;

        private static readonly byte[] s_DeBruijnIndex =
        {
            0, 1, 48, 2, 57, 49, 28, 3, 61, 58, 50, 42, 38, 29, 17, 4,
            62, 55, 59, 36, 53, 51, 43, 22, 45, 39, 33, 30, 24, 18, 12, 5,
            63, 47, 56, 27, 60, 41, 37, 16, 54, 35, 52, 21, 44, 32, 23, 11,
            46, 26, 40, 15, 34, 20, 31, 10, 25, 14, 19, 9, 13, 8, 7, 6,
        };

        /// <summary>最低置位的位号；<paramref name="value"/> 为 0 时返回 64。</summary>
        public static int TrailingZeroCount(ulong value)
        {
            if (value == 0UL)
            {
                return 64;
            }

            // value & -value 只保留最低置位；乘 de Bruijn 常数后高 6 位唯一对应位号。
            return s_DeBruijnIndex[unchecked((value & (0UL - value)) * DeBruijn64) >> 58];
        }

        /// <summary>置位数。</summary>
        public static int PopCount(ulong value)
        {
            value -= (value >> 1) & 0x5555555555555555UL;
            value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
            value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)(unchecked(value * 0x0101010101010101UL) >> 56);
        }
    }
}
