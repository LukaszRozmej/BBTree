// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BBTree;

/// <summary>The header and the inline keys that leaves and internal nodes share.</summary>
/// <remarks>
/// <para>
/// Inline key <c>i</c> is the window of key <c>i</c> at <see cref="PrefixLength"/>. The slots at and past
/// <see cref="Count"/> hold the greatest inline key, so that a search counts every slot without masking.
/// </para>
/// <para>
/// <see cref="PrefixLength"/> is at most the common prefix of the fences of the node: the separators beside it
/// in its ancestors. Every key that a search routes to the node shares these units, so the inline keys skip them.
/// A node with no separator on one side takes the prefix that all keys of the tree share.
/// </para>
/// <para>
/// The layout is sequential: the header fills 16 bytes, so the inline keys start 8-aligned on the line that holds it.
/// An unaligned 32-byte vector load spans two cache lines more often.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal abstract class Node<TKey, TSlots>(bool isLeaf, int prefixLength)
    where TSlots : struct, IInlineKey<TKey>
{
    /// <summary>The optimistic lock of the concurrent map: bit 0 is the write lock, a write adds 2.</summary>
    public long Version;
    public int Count;
    public ushort PrefixLength = (ushort)prefixLength;
    public readonly bool IsLeaf = isLeaf;
    public TSlots Slots = InlineSlots<TKey, TSlots>.Empty;
}

internal sealed class Leaf<TKey, TValue, TSlots>(int prefixLength) : Node<TKey, TSlots>(isLeaf: true, prefixLength)
    where TSlots : struct, IInlineKey<TKey>
{
    /// <summary>The next leaf in key order.</summary>
    public Leaf<TKey, TValue, TSlots>? Next;

    /// <summary>The slot in <see cref="Entries"/> of each entry, in key order, while <see cref="IsOrdered"/>.</summary>
    /// <remarks>Next to the length tags, which a search reads just before it.</remarks>
    public EntryOrder Order;

    /// <summary>The entries: in key order, or, while <see cref="IsOrdered"/>, in the slots that <see cref="Order"/> names.</summary>
    public EntryBuffer<TKey, TValue> Entries;

    /// <summary>Whether <see cref="Order"/> keeps the order of the entries, which then do not move in their slots.</summary>
    /// <remarks>
    /// <para>
    /// The GC runs a write barrier for each reference that a move writes, so an insert or a removal that shifts the
    /// entries costs more for entries that hold references. Such entries stay in their slots: an insert writes its entry
    /// to slot <see cref="Node{TKey, TSlots}.Count"/> and inserts that slot into <see cref="Order"/>, which shifts as the
    /// inline keys do, and a removal moves the last entry into the slot it frees.
    /// </para>
    /// <para>
    /// The first <see cref="Node{TKey, TSlots}.Count"/> bytes of <see cref="Order"/> are a permutation of the slots
    /// below the count; the slots from the count on hold no references.
    /// </para>
    /// </remarks>
    public static bool IsOrdered
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => RuntimeHelpers.IsReferenceOrContainsReferences<Entry<TKey, TValue>>();
    }

    /// <summary>Gets entry <paramref name="index"/> in key order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Entry<TKey, TValue> EntryAt(int index) => ref SlotAt(IsOrdered ? OrderAt(index) : index);

    /// <summary>Gets the entry in slot <paramref name="slot"/> of <see cref="Entries"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Entry<TKey, TValue> SlotAt(int slot) =>
        ref Unsafe.Add(ref Unsafe.As<EntryBuffer<TKey, TValue>, Entry<TKey, TValue>>(ref Entries), slot);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte OrderAt(int index) => ref Unsafe.Add(ref Unsafe.As<EntryOrder, byte>(ref Order), index);

    /// <summary>The slots of <see cref="Entries"/>.</summary>
    public Span<Entry<TKey, TValue>> EntrySpan =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<EntryBuffer<TKey, TValue>, Entry<TKey, TValue>>(ref Entries), BBTreeLayout.NodeCapacity);
}

internal sealed class InternalNode<TKey, TSlots>(int prefixLength) : Node<TKey, TSlots>(isLeaf: false, prefixLength)
    where TSlots : struct, IInlineKey<TKey>
{
    /// <remarks>Right after the inline keys, so that the edge a search takes is near the lines it read.</remarks>
    public ChildBuffer<TKey, TSlots> Children;

    /// <summary>The separators: separator <c>i</c> is the least key that child <c>i + 1</c> held when it was made.</summary>
    /// <remarks>Read only where inline keys tie.</remarks>
    public KeyBuffer<TKey> Keys;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TKey KeyAt(int index) => ref Unsafe.Add(ref Unsafe.As<KeyBuffer<TKey>, TKey>(ref Keys), index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Node<TKey, TSlots> ChildAt(int index) =>
        ref Unsafe.Add(ref Unsafe.As<ChildBuffer<TKey, TSlots>, Node<TKey, TSlots>>(ref Children), index)!;

    public Span<TKey> KeySpan => MemoryMarshal.CreateSpan(ref Unsafe.As<KeyBuffer<TKey>, TKey>(ref Keys), BBTreeLayout.NodeCapacity);

    public Span<Node<TKey, TSlots>?> ChildSpan =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<ChildBuffer<TKey, TSlots>, Node<TKey, TSlots>?>(ref Children), BBTreeLayout.NodeCapacity + 1);
}

/// <summary>A key and its value, side by side, so that the line a search reads for the key brings the value.</summary>
internal struct Entry<TKey, TValue>
{
    public TKey Key;
    public TValue Value;
}

[InlineArray(BBTreeLayout.NodeCapacity)]
internal struct EntryBuffer<TKey, TValue>
{
    private Entry<TKey, TValue> _entry;
}

[InlineArray(BBTreeLayout.NodeCapacity)]
internal struct EntryOrder
{
    private byte _slot;
}

[InlineArray(BBTreeLayout.NodeCapacity)]
internal struct KeyBuffer<TKey>
{
    private TKey _key;
}

[InlineArray(BBTreeLayout.NodeCapacity + 1)]
internal struct ChildBuffer<TKey, TSlots> where TSlots : struct, IInlineKey<TKey>
{
    private Node<TKey, TSlots>? _child;
}
