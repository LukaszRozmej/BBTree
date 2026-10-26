// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace BBTree;

/// <summary>Reads, writes, moves and searches the inline keys of one node.</summary>
/// <remarks>
/// <para>
/// Leaves keep the element size of <typeparamref name="TSlots"/> of each window; internal nodes keep at most 4 bytes,
/// as their keys are separators that searches compare only to choose a child. Callers pass the kind of the node as a
/// constant where they can, so the JIT keeps only one branch of each width check.
/// </para>
/// <para>
/// A <typeparamref name="TSlots"/> one byte per slot longer than its slots also keeps a length tag of each leaf key:
/// its length, saturated at 255. A lookup whose inline key ties then rules out keys of other lengths, and proves a
/// key that ends within its inline key equal, without reading it.
/// </para>
/// </remarks>
internal static class InlineSlots<TKey, TSlots> where TSlots : struct, IInlineKey<TKey>
{
    private const int Capacity = BBTreeLayout.NodeCapacity;

    private static int LeafWidth
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.SizeOf<TSlots>() / Capacity >= 8 ? 8 : Unsafe.SizeOf<TSlots>() / Capacity >= 4 ? 4 : 2;
    }

    /// <summary>Whether a node of the kind keeps the length tags of its keys: only leaves do.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasTags(bool leaf) => leaf && Unsafe.SizeOf<TSlots>() > Capacity * LeafWidth;

    /// <summary>The bytes of each window that a node of the kind keeps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Width(bool leaf) => leaf ? LeafWidth : Math.Min(LeafWidth, sizeof(uint));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Shift(bool leaf) => 64 - 8 * Width(leaf);

    /// <summary>The slots of a node with no keys: all hold the greatest inline key.</summary>
    public static readonly TSlots Empty = CreateEmpty();

    /// <summary>The inline key of <paramref name="key"/> in a node of prefix length <paramref name="prefixLength"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Of(bool leaf, in TKey key, int prefixLength) => KeyDispatch<TKey, TSlots, TKey>.WindowAt(key, prefixLength) >> Shift(leaf);

    /// <summary>The inline key of <paramref name="query"/> in a node of prefix length <paramref name="prefixLength"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Of<TQuery, TQueryKey>(bool leaf, in TQuery query, int prefixLength)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey> =>
        KeyDispatch<TQuery, TQueryKey, TKey>.WindowAt(query, prefixLength) >> Shift(leaf);

    /// <remarks>The keys behind references have no fixed length; checking them first spares shared generic code a runtime lookup.</remarks>
    private static int FixedLength
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => typeof(TSlots) == typeof(StringKey) || typeof(TSlots) == typeof(ByteArrayKey) ? 0 : TSlots.FixedLength;
    }

    /// <summary>Whether equal inline keys at <paramref name="prefixLength"/> prove equal keys.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreExact(bool leaf, int prefixLength) =>
        FixedLength > 0 && prefixLength + Width(leaf) >= FixedLength;

    /// <summary>Whether inline keys <paramref name="x"/> and <paramref name="y"/> differ in their first byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool DifferInFirstByte(bool leaf, ulong x, ulong y) => ((x ^ y) >> (8 * Width(leaf) - 8)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Get(bool leaf, ref TSlots slots, int index)
    {
        ref byte first = ref Unsafe.As<TSlots, byte>(ref slots);
        if (Width(leaf) == 2) return Unsafe.Add(ref Unsafe.As<byte, ushort>(ref first), index);
        if (Width(leaf) == 4) return Unsafe.Add(ref Unsafe.As<byte, uint>(ref first), index);
        return Unsafe.Add(ref Unsafe.As<byte, ulong>(ref first), index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(bool leaf, ref TSlots slots, int index, ulong inlineKey)
    {
        ref byte first = ref Unsafe.As<TSlots, byte>(ref slots);
        if (Width(leaf) == 2) Unsafe.Add(ref Unsafe.As<byte, ushort>(ref first), index) = (ushort)inlineKey;
        else if (Width(leaf) == 4) Unsafe.Add(ref Unsafe.As<byte, uint>(ref first), index) = (uint)inlineKey;
        else Unsafe.Add(ref Unsafe.As<byte, ulong>(ref first), index) = inlineKey;
    }

    /// <summary>Gets the length tag of key <paramref name="index"/> of a leaf.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Tag(ref TSlots slots, int index) =>
        Unsafe.Add(ref Unsafe.As<TSlots, byte>(ref slots), Capacity * LeafWidth + index);

    /// <summary>Moves slots <c>index..count</c> one up and writes <paramref name="inlineKey"/> and <paramref name="tag"/> at <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Insert(bool leaf, ref TSlots slots, int index, int count, ulong inlineKey, byte tag)
    {
        ref byte first = ref Unsafe.As<TSlots, byte>(ref slots);
        ElementShift.Up(ref Unsafe.Add(ref first, index * Width(leaf)), (count - index) * Width(leaf), Width(leaf));
        Set(leaf, ref slots, index, inlineKey);
        if (HasTags(leaf))
        {
            ref byte tags = ref Unsafe.Add(ref first, Capacity * LeafWidth);
            ElementShift.Up(ref Unsafe.Add(ref tags, index), count - index, 1);
            Unsafe.Add(ref tags, index) = tag;
        }
    }

    /// <summary>Moves slots <c>index + 1..count</c> one down and fills slot <c>count - 1</c> with the greatest inline key.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RemoveAt(bool leaf, ref TSlots slots, int index, int count)
    {
        ref byte first = ref Unsafe.As<TSlots, byte>(ref slots);
        ElementShift.Down(ref Unsafe.Add(ref first, (index + 1) * Width(leaf)), (count - index - 1) * Width(leaf), Width(leaf));
        Set(leaf, ref slots, count - 1, ulong.MaxValue >> Shift(leaf));
        if (HasTags(leaf))
        {
            ref byte tags = ref Unsafe.Add(ref first, Capacity * LeafWidth);
            ElementShift.Down(ref Unsafe.Add(ref tags, index + 1), count - index - 1, 1);
        }
    }

    /// <summary>Copies <paramref name="length"/> slots, and their tags, from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    public static void Copy(bool leaf, ref TSlots source, int sourceIndex, ref TSlots destination, int destinationIndex, int length)
    {
        Bytes(ref source).Slice(sourceIndex * Width(leaf), length * Width(leaf)).CopyTo(Bytes(ref destination)[(destinationIndex * Width(leaf))..]);
        CopyTags(leaf, ref source, sourceIndex, ref destination, destinationIndex, length);
    }

    /// <summary>Copies <paramref name="length"/> tags from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    public static void CopyTags(bool leaf, ref TSlots source, int sourceIndex, ref TSlots destination, int destinationIndex, int length)
    {
        if (HasTags(leaf)) Tags(ref source).Slice(sourceIndex, length).CopyTo(Tags(ref destination)[destinationIndex..]);
    }

    /// <summary>Fills slots <c>from..</c> with the greatest inline key.</summary>
    public static void Clear(bool leaf, ref TSlots slots, int from) =>
        Bytes(ref slots)[(from * Width(leaf))..(Capacity * Width(leaf))].Fill(byte.MaxValue);

    /// <summary>
    /// Counts the inline keys below <paramref name="target"/> and those equal to it among the first <paramref name="count"/> slots.
    /// </summary>
    /// <remarks>The count is clipped to the slots, which keeps torn reads of a concurrent map in bounds.</remarks>
    /// <returns>The number of inline keys below the target. The equal ones follow them, as the slots are sorted.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count(bool leaf, ref TSlots slots, int count, ulong target, out int equal)
    {
        count = Math.Clamp(count, 0, Capacity);
        int less;
        if (Width(leaf) == 2 && Vector256.IsHardwareAccelerated) less = CountBelowUInt16(ref slots, (ushort)target);
        else if (Width(leaf) == 4 && Vector256.IsHardwareAccelerated) less = CountBelowUInt32(ref slots, (uint)target);
        else if (Width(leaf) == 8 && Vector256.IsHardwareAccelerated) less = CountBelowUInt64(ref slots, count, target);
        else less = CountBelowScalar(leaf, ref slots, count, target);

        // The slots are sorted, so the equal ones follow those below; they are rare, and a scalar compare finds them.
        less = Math.Min(less, count);
        int end = less;
        while (end < count && Get(leaf, ref slots, end) == target) end++;
        equal = end - less;
        return less;
    }

    /// <remarks>
    /// Counts every slot: the greatest inline key past the count is never below the target.
    /// A slot <c>s</c> is below <c>t</c> when <c>min(s, t - 1) == s</c>, an unsigned compare in two instructions.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountBelowUInt16(ref TSlots slots, ushort target)
    {
        if (target == 0) return 0;
        ref ushort first = ref Unsafe.As<TSlots, ushort>(ref slots);
        Vector256<ushort> below = Vector256.Create((ushort)(target - 1));
        int less = 0;
        for (int i = 0; i < Capacity; i += Vector256<ushort>.Count)
        {
            Vector256<ushort> v = Vector256.LoadUnsafe(ref first, (nuint)i);
            less += BitOperations.PopCount(Vector256.Equals(Vector256.Min(v, below), v).AsByte().ExtractMostSignificantBits());
        }

        return less >> 1;
    }

    /// <remarks>Sums the compare masks, each lane -1 or 0, in vectors: one horizontal sum, where a mask per vector would take a move and a count.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountBelowUInt32(ref TSlots slots, uint target)
    {
        if (target == 0) return 0;
        ref uint first = ref Unsafe.As<TSlots, uint>(ref slots);
        Vector256<uint> below = Vector256.Create(target - 1);
        Vector256<uint> less = Vector256<uint>.Zero;
        for (int i = 0; i < Capacity; i += Vector256<uint>.Count)
        {
            Vector256<uint> v = Vector256.LoadUnsafe(ref first, (nuint)i);
            less -= Vector256.Equals(Vector256.Min(v, below), v);
        }

        return (int)Vector256.Sum(less);
    }

    /// <remarks>Counts only the vectors that hold keys: eight-byte slots fill 16 vectors when the node is full.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountBelowUInt64(ref TSlots slots, int count, ulong target)
    {
        ref ulong first = ref Unsafe.As<TSlots, ulong>(ref slots);
        Vector256<ulong> targets = Vector256.Create(target);
        int less = 0;
        for (int i = 0; i < count; i += Vector256<ulong>.Count)
        {
            Vector256<ulong> v = Vector256.LoadUnsafe(ref first, (nuint)i);
            less += BitOperations.PopCount(Vector256.LessThan(v, targets).ExtractMostSignificantBits());
        }

        return less;
    }

    private static int CountBelowScalar(bool leaf, ref TSlots slots, int count, ulong target)
    {
        int less = 0;
        for (int i = 0; i < count; i++) less += Get(leaf, ref slots, i) < target ? 1 : 0;
        return less;
    }

    private static Span<byte> Tags(ref TSlots slots) =>
        MemoryMarshal.CreateSpan(ref Unsafe.Add(ref Unsafe.As<TSlots, byte>(ref slots), Capacity * LeafWidth), Capacity);

    private static Span<byte> Bytes(ref TSlots slots) =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<TSlots, byte>(ref slots), Unsafe.SizeOf<TSlots>());

    private static TSlots CreateEmpty()
    {
        int size = Unsafe.SizeOf<TSlots>();
        if (size != Capacity * LeafWidth && size != Capacity * (LeafWidth + 1))
        {
            throw new InvalidOperationException(
                $"{typeof(TSlots).Name} must be an inline array of {Capacity} elements of 2, 4 or 8 bytes, and may add a byte per element for length tags.");
        }

        TSlots slots = default;
        Bytes(ref slots).Fill(byte.MaxValue);
        return slots;
    }
}
