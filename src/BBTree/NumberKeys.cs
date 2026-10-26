// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace BBTree;

/// <summary>
/// Inline keys of integer keys of any type, such as <see cref="int"/>, <see cref="ulong"/> or <see cref="Int128"/>:
/// their big-endian bytes, with the sign bit flipped for signed types, of which the nodes keep 2.
/// </summary>
/// <typeparam name="T">The integer type, of at most 16 bytes, as all built-in integer types are.</typeparam>
/// <remarks>
/// The JIT compiles the map for each <typeparamref name="T"/>, so the size and the signedness of the type are
/// constants there, and a window is a shift of the value.
/// </remarks>
[InlineArray(BBTreeLayout.NodeCapacity)]
public struct IntegerKey<T> : IInlineKey<T>, ITrustedInlineKey where T : unmanaged, IBinaryInteger<T>
{
    private ushort _slot;

    /// <inheritdoc/>
    public static int Compare(in T x, in T y) => x.CompareTo(y);

    /// <inheritdoc/>
    public static ulong WindowAt(in T key, int offset)
    {
        if (Size <= sizeof(ulong)) return OrderedBits.WindowAt(Bits(key), Size, offset);
        return offset < Size ? (ulong)((Wide(key) << (8 * offset)) >> 64) : 0;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CommonPrefixLength(in T x, in T y) =>
        Size <= sizeof(ulong) ? OrderedBits.CommonPrefixLength(Bits(x), Bits(y), Size) : WideCommonPrefixLength(x, y);

    /// <inheritdoc/>
    public static int Length(in T key) => Size;

    /// <inheritdoc/>
    public static int FixedLength => Size;

    private static int Size
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.SizeOf<T>();
    }

    /// <remarks>All bits set is -1 for a signed type and the maximum for an unsigned one.</remarks>
    private static bool IsSigned
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => T.IsNegative(T.AllBitsSet);
    }

    /// <summary>Gets the bits of a key of at most 8 bytes, in the low bytes and zero above them, ordered as unsigned.</summary>
    /// <remarks>
    /// Reads the bits rather than converting: the JIT judges a method for inlining by its IL, and a generic
    /// conversion is long IL even where it folds to a move.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Bits(in T key)
    {
        ulong bits = Size switch
        {
            sizeof(byte) => Unsafe.As<T, byte>(ref Unsafe.AsRef(in key)),
            sizeof(ushort) => Unsafe.As<T, ushort>(ref Unsafe.AsRef(in key)),
            sizeof(uint) => Unsafe.As<T, uint>(ref Unsafe.AsRef(in key)),
            _ => Unsafe.As<T, ulong>(ref Unsafe.AsRef(in key)),
        };

        return IsSigned ? bits ^ (1UL << (8 * Size - 1)) : bits;
    }

    private static int WideCommonPrefixLength(in T x, in T y) => (int)UInt128.LeadingZeroCount(Wide(x) ^ Wide(y)) / 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 Wide(in T key)
    {
        UInt128 bits = Unsafe.As<T, UInt128>(ref Unsafe.AsRef(in key));
        return IsSigned ? bits ^ (UInt128.One << 127) : bits;
    }

    /// <summary>A <see cref="BBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    public sealed class Map<TValue> : BBTreeMap<T, TValue, IntegerKey<T>>;

    /// <summary>A <see cref="ConcurrentBBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    public sealed class ConcurrentMap<TValue> : ConcurrentBBTreeMap<T, TValue, IntegerKey<T>>;
}

/// <summary>
/// Inline keys of floating-point keys, such as <see cref="double"/>, <see cref="float"/> or <see cref="Half"/>, in the
/// IEEE 754 total order: their bits, ordered as unsigned, of which the nodes keep 2.
/// </summary>
/// <typeparam name="T">The floating-point type, of at most 8 bytes.</typeparam>
/// <remarks>
/// The total order is <c>-NaN &lt; -∞ &lt; … &lt; -0 &lt; +0 &lt; … &lt; +∞ &lt; +NaN</c>, which differs from
/// <see cref="double.CompareTo(double)"/>: <c>-0</c> and <c>+0</c> are two keys, as are NaNs of different signs or
/// payloads, and negative NaNs order first. <see cref="double.NaN"/> and the NaN of <c>0.0 / 0.0</c> on x64 have
/// the sign bit set, so they order before negative infinity.
/// </remarks>
[InlineArray(BBTreeLayout.NodeCapacity)]
public struct FloatKey<T> : IInlineKey<T>, ITrustedInlineKey where T : unmanaged, IBinaryFloatingPointIeee754<T>
{
    private ushort _slot;

    /// <inheritdoc/>
    public static int Compare(in T x, in T y) => Bits(x).CompareTo(Bits(y));

    /// <inheritdoc/>
    public static ulong WindowAt(in T key, int offset) => OrderedBits.WindowAt(Bits(key), Size, offset);

    /// <inheritdoc/>
    public static int CommonPrefixLength(in T x, in T y) => OrderedBits.CommonPrefixLength(Bits(x), Bits(y), Size);

    /// <inheritdoc/>
    public static int Length(in T key) => Size;

    /// <inheritdoc/>
    public static int FixedLength => Size;

    private static int Size
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.SizeOf<T>();
    }

    /// <summary>Gets the bits of a key in the low bytes and zero above them, ordered as unsigned in the total order.</summary>
    /// <remarks>A negative value inverts all its bits, so that a larger magnitude orders first; a positive one sets its sign bit.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Bits(in T key)
    {
        ulong raw = Size switch
        {
            sizeof(ushort) => Unsafe.As<T, ushort>(ref Unsafe.AsRef(in key)),
            sizeof(uint) => Unsafe.As<T, uint>(ref Unsafe.AsRef(in key)),
            _ => Unsafe.As<T, ulong>(ref Unsafe.AsRef(in key)),
        };

        ulong sign = 1UL << (8 * Size - 1);
        ulong mask = ulong.MaxValue >> (64 - 8 * Size);
        return (raw & sign) != 0 ? ~raw & mask : raw | sign;
    }

    /// <summary>A <see cref="BBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    public sealed class Map<TValue> : BBTreeMap<T, TValue, FloatKey<T>>;

    /// <summary>A <see cref="ConcurrentBBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    public sealed class ConcurrentMap<TValue> : ConcurrentBBTreeMap<T, TValue, FloatKey<T>>;
}

/// <summary>Windows of fixed-size keys whose bits, in the low bytes of a word, order as unsigned integers.</summary>
internal static class OrderedBits
{
    /// <summary>Gets the bytes <c>offset..</c> of a key of <paramref name="size"/> bytes, big-endian, zero past the end.</summary>
    /// <remarks>The shift drops the bits above the key, so <paramref name="bits"/> may hold anything there.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong WindowAt(ulong bits, int size, int offset) => offset < size ? bits << (64 - 8 * size + 8 * offset) : 0;

    /// <summary>Gets the number of leading bytes that two keys of <paramref name="size"/> bytes share.</summary>
    /// <remarks>The bits above the keys must be zero: a bit set just past the key caps the count at its size.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CommonPrefixLength(ulong x, ulong y, int size) =>
        BitOperations.LeadingZeroCount(((x ^ y) << (64 - 8 * size)) | (size < sizeof(ulong) ? 1UL << (63 - 8 * size) : 0)) / 8;
}
