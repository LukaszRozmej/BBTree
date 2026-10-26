// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace BBTree;

/// <summary>
/// Inline keys of unmanaged keys ordered by their bytes in memory, such as hashes:
/// their bytes, of which the nodes keep 2.
/// </summary>
/// <remarks>The map orders the keys by their bytes, not by their <see cref="IComparable{T}"/> implementation.</remarks>
[InlineArray(BBTreeLayout.NodeCapacity)]
public struct BytesKey<T> : IInlineKey<T>, ITrustedInlineKey where T : unmanaged
{
    private ushort _slot;

    /// <inheritdoc/>
    public static int Compare(in T x, in T y) => Bytes(x).SequenceCompareTo(Bytes(y));

    /// <inheritdoc/>
    public static ulong WindowAt(in T key, int offset) => ByteWindows.At(Bytes(key), offset);

    /// <inheritdoc/>
    public static int CommonPrefixLength(in T x, in T y) => Bytes(x).CommonPrefixLength(Bytes(y));

    /// <inheritdoc/>
    public static int Length(in T key) => Unsafe.SizeOf<T>();

    /// <inheritdoc/>
    public static int FixedLength => Unsafe.SizeOf<T>();

    private static ReadOnlySpan<byte> Bytes(in T key) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in key));

    /// <summary>A <see cref="BBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    [CollectionBuilder(typeof(BBTreeMapBuilder), nameof(BBTreeMapBuilder.CreateBytesMap))]
    public sealed class Map<TValue> : BBTreeMap<T, TValue, BytesKey<T>>;

    /// <summary>A <see cref="ConcurrentBBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    [CollectionBuilder(typeof(BBTreeMapBuilder), nameof(BBTreeMapBuilder.CreateBytesConcurrentMap))]
    public sealed class ConcurrentMap<TValue> : ConcurrentBBTreeMap<T, TValue, BytesKey<T>>;
}

/// <summary>Inline keys of <see cref="byte"/> array keys in lexicographic order: their bytes, of which the nodes keep 4.</summary>
/// <remarks>A map of these keys also searches by spans of bytes; see <see cref="BBTreeMap{TKey, TValue, TInlineKey}.GetAlternateLookup{TAlternate, TAlternateKey}"/>.</remarks>
[InlineArray(BBTreeLayout.NodeCapacity + BBTreeLayout.NodeCapacity / sizeof(uint))]
public struct ByteArrayKey : IInlineKey<byte[]>, IKeyQuery<ReadOnlySpan<byte>, byte[]>, ITrustedInlineKey
{
    private uint _slot;

    /// <inheritdoc/>
    public static int Compare(in byte[] x, in byte[] y) => x.AsSpan().SequenceCompareTo(y);

    /// <inheritdoc/>
    public static ulong WindowAt(in byte[] key, int offset) => ByteWindows.At(key, offset);

    /// <inheritdoc/>
    public static int CommonPrefixLength(in byte[] x, in byte[] y) => x.AsSpan().CommonPrefixLength(y);

    /// <inheritdoc/>
    public static int Length(in byte[] key) => key.Length;

    /// <inheritdoc/>
    public static int Length(in ReadOnlySpan<byte> query) => query.Length;

    /// <inheritdoc/>
    public static int FixedLength => 0;

    /// <inheritdoc/>
    public static int Compare(in ReadOnlySpan<byte> query, in byte[] key) => query.SequenceCompareTo(key);

    /// <inheritdoc/>
    public static ulong WindowAt(in ReadOnlySpan<byte> query, int offset) => ByteWindows.At(query, offset);

    /// <inheritdoc/>
    public static int CommonPrefixLength(in ReadOnlySpan<byte> query, in byte[] key) => query.CommonPrefixLength(key);

    /// <inheritdoc/>
    public static byte[] ToKey(in ReadOnlySpan<byte> query) => query.ToArray();

    /// <summary>A <see cref="BBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    [CollectionBuilder(typeof(BBTreeMapBuilder), nameof(BBTreeMapBuilder.CreateByteArrayMap))]
    public sealed class Map<TValue> : BBTreeMap<byte[], TValue, ByteArrayKey>;

    /// <summary>A <see cref="ConcurrentBBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    [CollectionBuilder(typeof(BBTreeMapBuilder), nameof(BBTreeMapBuilder.CreateByteArrayConcurrentMap))]
    public sealed class ConcurrentMap<TValue> : ConcurrentBBTreeMap<byte[], TValue, ByteArrayKey>;
}

/// <summary>Inline keys of <see cref="string"/> keys in ordinal order, of which the nodes keep 4 bytes: one byte for each of 4 chars.</summary>
/// <remarks>
/// <para>
/// The units are chars. A window holds each char as a byte, <c>0xFF</c> for every char from <c>U+00FF</c> on, and ends
/// at the first such char, so that it orders like the chars it summarizes.
/// </para>
/// <para>A map of these keys also searches by spans of chars; see <see cref="BBTreeMap{TKey, TValue, TInlineKey}.GetAlternateLookup{TAlternate, TAlternateKey}"/>.</para>
/// </remarks>
[InlineArray(BBTreeLayout.NodeCapacity + BBTreeLayout.NodeCapacity / sizeof(uint))]
public struct StringKey : IInlineKey<string>, IKeyQuery<ReadOnlySpan<char>, string>, ITrustedInlineKey
{
    private uint _slot;

    /// <inheritdoc/>
    public static int Compare(in string x, in string y) => string.CompareOrdinal(x, y);

    /// <inheritdoc/>
    public static ulong WindowAt(in string key, int offset) => CharWindows.At(key, offset);

    /// <inheritdoc/>
    public static int CommonPrefixLength(in string x, in string y) => x.AsSpan().CommonPrefixLength(y);

    /// <inheritdoc/>
    public static int Length(in string key) => key.Length;

    /// <inheritdoc/>
    public static int Length(in ReadOnlySpan<char> query) => query.Length;

    /// <inheritdoc/>
    public static int FixedLength => 0;

    /// <inheritdoc/>
    public static int Compare(in ReadOnlySpan<char> query, in string key) => query.SequenceCompareTo(key);

    /// <inheritdoc/>
    public static ulong WindowAt(in ReadOnlySpan<char> query, int offset) => CharWindows.At(query, offset);

    /// <inheritdoc/>
    public static int CommonPrefixLength(in ReadOnlySpan<char> query, in string key) => query.CommonPrefixLength(key);

    /// <inheritdoc/>
    public static string ToKey(in ReadOnlySpan<char> query) => new(query);

    /// <summary>A <see cref="BBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    [CollectionBuilder(typeof(BBTreeMapBuilder), nameof(BBTreeMapBuilder.CreateStringMap))]
    public sealed class Map<TValue> : BBTreeMap<string, TValue, StringKey>;

    /// <summary>A <see cref="ConcurrentBBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    [CollectionBuilder(typeof(BBTreeMapBuilder), nameof(BBTreeMapBuilder.CreateStringConcurrentMap))]
    public sealed class ConcurrentMap<TValue> : ConcurrentBBTreeMap<string, TValue, StringKey>;
}

internal static class CharWindows
{
    /// <summary>
    /// Gets chars <c>offset..offset + 4</c> as bytes in the high half of a word, big-endian, saturated at <c>0xFF</c>
    /// and zero past the first saturated char.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two strings that share their first <c>offset</c> chars order like their windows where the windows differ: the
    /// first char where they differ decides both, unless both saturate there, and then both windows end there alike.
    /// </para>
    /// <para>
    /// The slots of <see cref="StringKey"/> keep 4 bytes, so 4 chars fill them: one word holds the chars, and
    /// chars below <c>U+00FF</c> narrow without vectors.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong At(ReadOnlySpan<char> chars, int offset)
    {
        int length = chars.Length;
        if (offset >= length) return 0;

        ref byte first = ref Unsafe.As<char, byte>(ref MemoryMarshal.GetReference(chars));
        ulong wide;
        if (length >= 4)
        {
            // The 4 chars that hold the window, which end at the last char when fewer remain.
            int start = Math.Min(offset, length - 4);
            wide = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, 2 * start)) >> (16 * (offset - start));
        }
        else
        {
            wide = Short(ref first, length) >> (16 * offset);
        }

        // No char is U+00FF or more: adding one carries such a char, and only such a char, into its high byte.
        if (((wide | (wide + 0x0001_0001_0001_0001)) & 0xFF00_FF00_FF00_FF00) == 0)
        {
            ulong pairs = (wide | (wide >> 8)) & 0x0000_FFFF_0000_FFFF;
            return (ulong)BinaryPrimitives.ReverseEndianness((uint)(pairs | (pairs >> 16))) << 32;
        }

        return Saturated(wide);
    }

    /// <summary>Gets the chars of a string shorter than 4 chars, zero past its end; reads nothing past the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Short(ref byte first, int length) => length switch
    {
        1 => Unsafe.ReadUnaligned<ushort>(ref first),
        2 => Unsafe.ReadUnaligned<uint>(ref first),
        _ => Unsafe.ReadUnaligned<uint>(ref first) | ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref first, 4)) << 32),
    };

    /// <summary>Narrows 4 chars of which one is at least <c>U+00FF</c>.</summary>
    private static ulong Saturated(ulong wide)
    {
        Vector128<ushort> chars = Vector128.CreateScalar(wide).AsUInt16();
        Vector128<byte> bytes = Vector128.Narrow(Vector128.Min(chars, Vector128.Create((ushort)byte.MaxValue)), chars);
        ulong window = (ulong)BinaryPrimitives.ReverseEndianness(bytes.AsUInt32().ToScalar()) << 32;
        uint saturated = Vector128.Equals(bytes, Vector128.Create(byte.MaxValue)).ExtractMostSignificantBits() & 0xF;
        return saturated == 0 ? window : window & (ulong.MaxValue << (56 - 8 * BitOperations.TrailingZeroCount(saturated)));
    }
}

internal static class ByteWindows
{
    /// <summary>Gets the bytes <c>offset..offset + 8</c> of <paramref name="bytes"/>, big-endian, zero past the end.</summary>
    /// <remarks>Loads at most two overlapping words, ending at the last byte, rather than a byte at a time.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong At(ReadOnlySpan<byte> bytes, int offset)
    {
        int length = bytes.Length;
        if (offset >= length) return 0;

        ref byte first = ref MemoryMarshal.GetReference(bytes);
        if (length >= sizeof(ulong))
        {
            // The 8 bytes that hold the window, which end at the last byte when fewer remain.
            int start = Math.Min(offset, length - sizeof(ulong));
            ulong word = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, start)));
            return word << (8 * (offset - start));
        }

        ulong all;
        if (length >= sizeof(uint))
        {
            ulong head = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<uint>(ref first));
            ulong tail = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref first, length - sizeof(uint))));
            all = (head << 32) | (tail << (8 * (sizeof(ulong) - length)));
        }
        else if (length >= sizeof(ushort))
        {
            ulong head = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref first));
            ulong tail = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref first, length - sizeof(ushort))));
            all = (head << 48) | (tail << (8 * (sizeof(ulong) - length)));
        }
        else
        {
            all = (ulong)first << 56;
        }

        return all << (8 * offset);
    }
}
