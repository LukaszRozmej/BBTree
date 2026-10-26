// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BBTree;

/// <summary>Changes to one node or to two siblings: inserts, removals, splits, merges and moves between siblings.</summary>
/// <remarks>
/// Every change moves the inline keys with their keys. A change that widens the fences of a node shrinks
/// its prefix to what the new fences share; a change that narrows them may grow it.
/// </remarks>
internal static class NodeOps<TKey, TValue, TSlots> where TSlots : struct, IInlineKey<TKey>
{
    private const int Capacity = BBTreeLayout.NodeCapacity;

    /// <summary>How many entries a node that receives the entries of a split keeps: two minimums, so that one removal does not merge it again.</summary>
    private const int ArrivingLength = 2 * BBTreeLayout.MinNodeLength < Capacity / 2 ? 2 * BBTreeLayout.MinNodeLength : Capacity / 2;

    private static bool EntriesHoldReferences => RuntimeHelpers.IsReferenceOrContainsReferences<Entry<TKey, TValue>>();

    private static bool KeysHoldReferences => RuntimeHelpers.IsReferenceOrContainsReferences<TKey>();

    /// <summary>Gets where a full node splits for an insertion at <paramref name="index"/>.</summary>
    /// <remarks>
    /// An insertion at either end leaves the side that takes it about one quarter full, so that ascending or
    /// descending inserts fill nodes densely. An interior insertion splits the node in half.
    /// </remarks>
    public static int SplitPoint(int index) =>
        index == 0 ? ArrivingLength - 1
        : index == Capacity ? Capacity - ArrivingLength
        : Capacity / 2;

    /// <summary>Gets the prefix that a node with these fences may take.</summary>
    public static int FencePrefix(bool hasLow, in TKey low, bool hasHigh, in TKey high, int sharedLength) =>
        hasLow && hasHigh ? NodeSearch<TKey, TValue, TSlots>.CommonPrefix(low, high, ushort.MaxValue) : sharedLength;

    /// <summary>Grows the prefix of a node whose fences narrowed.</summary>
    public static void GrowPrefix(Node<TKey, TSlots> node, bool hasLow, in TKey low, bool hasHigh, in TKey high, int sharedLength)
    {
        if (NodeSearch<TKey, TValue, TSlots>.KeysDifferPastPrefix(node)) return;
        int prefixLength = FencePrefix(hasLow, low, hasHigh, high, sharedLength);
        if (prefixLength > node.PrefixLength) NodeSearch<TKey, TValue, TSlots>.SetPrefix(node, prefixLength);
    }

    public static void InsertEntry(Leaf<TKey, TValue, TSlots> leaf, int index, in TKey key, in TValue value) =>
        InsertEntry(leaf, index, key, value, InlineSlots<TKey, TSlots>.Of(true, key, leaf.PrefixLength));

    /// <summary>Inserts an entry whose inline key in the leaf, <paramref name="inlineKey"/>, a search already took.</summary>
    public static void InsertEntry(Leaf<TKey, TValue, TSlots> leaf, int index, in TKey key, in TValue value, ulong inlineKey) =>
        InsertEntry(leaf, index, key, value, inlineKey, InlineSlots<TKey, TSlots>.HasTags(true) ? KeyDispatch<TKey, TSlots, TKey>.Tag(key) : (byte)0);

    /// <summary>Inserts an entry with a precomputed inline key and length tag, without invoking key callbacks.</summary>
    public static void InsertEntry(Leaf<TKey, TValue, TSlots> leaf, int index, in TKey key, in TValue value, ulong inlineKey, byte tag)
    {
        int count = leaf.Count;
        ref Entry<TKey, TValue> entry = ref Unsafe.NullRef<Entry<TKey, TValue>>();
        if (Leaf<TKey, TValue, TSlots>.IsOrdered)
        {
            entry = ref leaf.SlotAt(count);
            ElementShift.Up(ref leaf.OrderAt(index), count - index, 1);
            leaf.OrderAt(index) = (byte)count;
        }
        else
        {
            ElementShift.Up(ref Unsafe.As<Entry<TKey, TValue>, byte>(ref leaf.EntryAt(index)), (count - index) * Unsafe.SizeOf<Entry<TKey, TValue>>(), Unsafe.SizeOf<Entry<TKey, TValue>>());
            entry = ref leaf.EntryAt(index);
        }

        entry.Key = key;
        entry.Value = value;
        InlineSlots<TKey, TSlots>.Insert(true, ref leaf.Slots, index, count, inlineKey, tag);
        leaf.Count = count + 1;
    }

    public static void RemoveEntry(Leaf<TKey, TValue, TSlots> leaf, int index)
    {
        int count = leaf.Count;
        if (Leaf<TKey, TValue, TSlots>.IsOrdered)
        {
            int slot = leaf.OrderAt(index);
            int last = count - 1;
            if (slot != last)
            {
                leaf.SlotAt(slot) = leaf.SlotAt(last);
                int moved = MemoryMarshal.CreateReadOnlySpan(ref leaf.OrderAt(0), count).IndexOf((byte)last);
                leaf.OrderAt(moved) = (byte)slot;
            }

            leaf.SlotAt(last) = default;
            ElementShift.Down(ref leaf.OrderAt(index + 1), count - index - 1, 1);
        }
        else
        {
            ElementShift.Down(ref Unsafe.As<Entry<TKey, TValue>, byte>(ref leaf.EntryAt(index + 1)), (count - index - 1) * Unsafe.SizeOf<Entry<TKey, TValue>>(), Unsafe.SizeOf<Entry<TKey, TValue>>());
        }
        InlineSlots<TKey, TSlots>.RemoveAt(true, ref leaf.Slots, index, count);
        leaf.Count = count - 1;
    }

    /// <summary>Moves the entries from <paramref name="split"/> on to a new leaf after <paramref name="leaf"/>.</summary>
    public static Leaf<TKey, TValue, TSlots> SplitLeaf(Leaf<TKey, TValue, TSlots> leaf, int split)
    {
        SortEntries(leaf);
        Leaf<TKey, TValue, TSlots> right = new(leaf.PrefixLength);
        int moved = leaf.Count - split;
        Span<Entry<TKey, TValue>> moving = leaf.EntrySpan.Slice(split, moved);
        moving.CopyTo(right.EntrySpan);
        if (EntriesHoldReferences) moving.Clear();
        InlineSlots<TKey, TSlots>.Copy(true, ref leaf.Slots, split, ref right.Slots, 0, moved);
        InlineSlots<TKey, TSlots>.Clear(true, ref leaf.Slots, split);
        SetOrder(right, 0, moved);
        right.Count = moved;
        leaf.Count = split;
        right.Next = leaf.Next;
        leaf.Next = right;
        return right;
    }

    /// <summary>Moves the separators past <paramref name="split"/> and their children to a new node; separator <paramref name="split"/> goes up.</summary>
    public static InternalNode<TKey, TSlots> SplitInternal(InternalNode<TKey, TSlots> node, int split, out TKey up)
    {
        InternalNode<TKey, TSlots> right = new(node.PrefixLength);
        int count = node.Count;
        int moved = count - split - 1;
        up = node.KeyAt(split);
        node.KeySpan.Slice(split + 1, moved).CopyTo(right.KeySpan);
        InlineSlots<TKey, TSlots>.Copy(false, ref node.Slots, split + 1, ref right.Slots, 0, moved);
        node.ChildSpan.Slice(split + 1, moved + 1).CopyTo(right.ChildSpan);
        if (KeysHoldReferences) node.KeySpan[split..count].Clear();
        node.ChildSpan[(split + 1)..(count + 1)].Clear();
        InlineSlots<TKey, TSlots>.Clear(false, ref node.Slots, split);
        right.Count = moved;
        node.Count = split;
        return right;
    }

    /// <summary>Inserts separator <paramref name="key"/> at <paramref name="index"/> and <paramref name="child"/> right of it.</summary>
    /// <remarks>Computes the inline key before mutation, so a key callback failure leaves the parent unchanged.</remarks>
    public static void InsertSeparator(InternalNode<TKey, TSlots> node, int index, in TKey key, Node<TKey, TSlots> child)
    {
        ulong inlineKey = InlineSlots<TKey, TSlots>.Of(false, key, node.PrefixLength);
        int count = node.Count;
        Span<TKey> keys = node.KeySpan;
        keys.Slice(index, count - index).CopyTo(keys[(index + 1)..]);
        keys[index] = key;
        InlineSlots<TKey, TSlots>.Insert(false, ref node.Slots, index, count, inlineKey, 0);
        Span<Node<TKey, TSlots>?> children = node.ChildSpan;
        children.Slice(index + 1, count - index).CopyTo(children[(index + 2)..]);
        children[index + 1] = child;
        node.Count = count + 1;
    }

    /// <summary>Removes separator <paramref name="index"/> and the child right of it.</summary>
    public static void RemoveSeparator(InternalNode<TKey, TSlots> node, int index)
    {
        int count = node.Count;
        Span<TKey> keys = node.KeySpan;
        keys.Slice(index + 1, count - index - 1).CopyTo(keys[index..]);
        if (KeysHoldReferences) keys[count - 1] = default!;
        InlineSlots<TKey, TSlots>.RemoveAt(false, ref node.Slots, index, count);
        Span<Node<TKey, TSlots>?> children = node.ChildSpan;
        children.Slice(index + 2, count - index - 1).CopyTo(children[(index + 1)..]);
        children[count] = null;
        node.Count = count - 1;
    }

    /// <summary>Replaces separator <paramref name="index"/> of <paramref name="node"/> with <paramref name="key"/>.</summary>
    public static void ReplaceSeparator(InternalNode<TKey, TSlots> node, int index, in TKey key)
    {
        node.KeyAt(index) = key;
        InlineSlots<TKey, TSlots>.Set(false, ref node.Slots, index, InlineSlots<TKey, TSlots>.Of(false, key, node.PrefixLength));
    }

    /// <summary>Appends the entries of <paramref name="right"/> to <paramref name="left"/>, both at <paramref name="prefixLength"/>.</summary>
    public static void MergeLeaves(Leaf<TKey, TValue, TSlots> left, Leaf<TKey, TValue, TSlots> right, int prefixLength)
    {
        NodeSearch<TKey, TValue, TSlots>.SetPrefix(left, prefixLength);
        SortEntries(left);
        SortEntries(right);
        int leftCount = left.Count;
        int rightCount = right.Count;
        right.EntrySpan[..rightCount].CopyTo(left.EntrySpan[leftCount..]);
        SetOrder(left, leftCount, leftCount + rightCount);
        AppendSlots(left, leftCount, right, rightCount, prefixLength);
        left.Count = leftCount + rightCount;
        left.Next = right.Next;
    }

    /// <summary>Appends <paramref name="separator"/> and the separators and children of <paramref name="right"/> to <paramref name="left"/>.</summary>
    public static void MergeInternal(InternalNode<TKey, TSlots> left, in TKey separator, InternalNode<TKey, TSlots> right, int prefixLength)
    {
        NodeSearch<TKey, TValue, TSlots>.SetPrefix(left, prefixLength);
        int leftCount = left.Count;
        int rightCount = right.Count;
        left.KeyAt(leftCount) = separator;
        InlineSlots<TKey, TSlots>.Set(false, ref left.Slots, leftCount, InlineSlots<TKey, TSlots>.Of(false, separator, prefixLength));
        right.KeySpan[..rightCount].CopyTo(left.KeySpan[(leftCount + 1)..]);
        AppendSlots(left, leftCount + 1, right, rightCount, prefixLength);
        right.ChildSpan[..(rightCount + 1)].CopyTo(left.ChildSpan[(leftCount + 1)..]);
        left.Count = leftCount + 1 + rightCount;
    }

    /// <summary>Moves the last entry of <paramref name="left"/> to the front of <paramref name="right"/>; it becomes their separator.</summary>
    /// <returns>The new separator.</returns>
    public static TKey MoveLastEntry(Leaf<TKey, TValue, TSlots> left, Leaf<TKey, TValue, TSlots> right, bool rightHasHigh, in TKey rightHigh, int sharedLength)
    {
        Entry<TKey, TValue> moved = left.EntryAt(left.Count - 1);
        RemoveEntry(left, left.Count - 1);
        InsertEntry(right, 0, moved.Key, moved.Value);
        NodeSearch<TKey, TValue, TSlots>.SetPrefix(right, FencePrefix(true, moved.Key, rightHasHigh, rightHigh, sharedLength));
        return moved.Key;
    }

    /// <summary>Moves the first entry of <paramref name="right"/> to the end of <paramref name="left"/>.</summary>
    /// <returns>The new separator: the new first key of <paramref name="right"/>.</returns>
    public static TKey MoveFirstEntry(Leaf<TKey, TValue, TSlots> left, Leaf<TKey, TValue, TSlots> right, bool leftHasLow, in TKey leftLow, int sharedLength)
    {
        Entry<TKey, TValue> moved = right.EntryAt(0);
        RemoveEntry(right, 0);
        InsertEntry(left, left.Count, moved.Key, moved.Value);
        TKey separator = right.EntryAt(0).Key;
        NodeSearch<TKey, TValue, TSlots>.SetPrefix(left, FencePrefix(leftHasLow, leftLow, true, separator, sharedLength));
        return separator;
    }

    /// <summary>Rotates the last child of <paramref name="left"/> through <paramref name="separator"/> to the front of <paramref name="right"/>.</summary>
    /// <returns>The new separator: the last separator of <paramref name="left"/>.</returns>
    public static TKey MoveLastChild(InternalNode<TKey, TSlots> left, in TKey separator, InternalNode<TKey, TSlots> right, bool rightHasHigh, in TKey rightHigh, int sharedLength)
    {
        int leftCount = left.Count;
        TKey up = left.KeyAt(leftCount - 1);
        Node<TKey, TSlots> child = left.ChildAt(leftCount);
        if (KeysHoldReferences) left.KeyAt(leftCount - 1) = default!;
        left.ChildAt(leftCount) = null!;
        InlineSlots<TKey, TSlots>.Clear(false, ref left.Slots, leftCount - 1);
        left.Count = leftCount - 1;

        int rightCount = right.Count;
        Span<TKey> keys = right.KeySpan;
        keys[..rightCount].CopyTo(keys[1..]);
        keys[0] = separator;
        InlineSlots<TKey, TSlots>.Insert(false, ref right.Slots, 0, rightCount, InlineSlots<TKey, TSlots>.Of(false, separator, right.PrefixLength), 0);
        Span<Node<TKey, TSlots>?> children = right.ChildSpan;
        children[..(rightCount + 1)].CopyTo(children[1..]);
        children[0] = child;
        right.Count = rightCount + 1;
        NodeSearch<TKey, TValue, TSlots>.SetPrefix(right, FencePrefix(true, up, rightHasHigh, rightHigh, sharedLength));
        return up;
    }

    /// <summary>Rotates the first child of <paramref name="right"/> through <paramref name="separator"/> to the end of <paramref name="left"/>.</summary>
    /// <returns>The new separator: the first separator of <paramref name="right"/>.</returns>
    public static TKey MoveFirstChild(InternalNode<TKey, TSlots> left, in TKey separator, InternalNode<TKey, TSlots> right, bool leftHasLow, in TKey leftLow, int sharedLength)
    {
        int rightCount = right.Count;
        TKey up = right.KeyAt(0);
        Node<TKey, TSlots> child = right.ChildAt(0);
        Span<TKey> keys = right.KeySpan;
        keys[1..rightCount].CopyTo(keys);
        if (KeysHoldReferences) keys[rightCount - 1] = default!;
        InlineSlots<TKey, TSlots>.RemoveAt(false, ref right.Slots, 0, rightCount);
        Span<Node<TKey, TSlots>?> children = right.ChildSpan;
        children[1..(rightCount + 1)].CopyTo(children);
        children[rightCount] = null;
        right.Count = rightCount - 1;

        int leftCount = left.Count;
        left.KeyAt(leftCount) = separator;
        InlineSlots<TKey, TSlots>.Set(false, ref left.Slots, leftCount, InlineSlots<TKey, TSlots>.Of(false, separator, left.PrefixLength));
        left.ChildAt(leftCount + 1) = child;
        left.Count = leftCount + 1;
        NodeSearch<TKey, TValue, TSlots>.SetPrefix(left, FencePrefix(leftHasLow, leftLow, true, up, sharedLength));
        return up;
    }

    /// <summary>Moves the entries of <paramref name="leaf"/> to the slots of their order, for changes that move entries in runs.</summary>
    private static void SortEntries(Leaf<TKey, TValue, TSlots> leaf)
    {
        if (!Leaf<TKey, TValue, TSlots>.IsOrdered) return;
        int count = leaf.Count;
        EntryBuffer<TKey, TValue> sorted = default;
        Span<Entry<TKey, TValue>> entries = MemoryMarshal.CreateSpan(ref Unsafe.As<EntryBuffer<TKey, TValue>, Entry<TKey, TValue>>(ref sorted), Capacity);
        for (int i = 0; i < count; i++) entries[i] = leaf.EntryAt(i);
        entries[..count].CopyTo(leaf.EntrySpan);
        SetOrder(leaf, 0, count);
    }

    /// <summary>Names slot <c>i</c> as entry <c>i</c> for <c>i</c> in <c>from..to</c>.</summary>
    private static void SetOrder(Leaf<TKey, TValue, TSlots> leaf, int from, int to)
    {
        if (!Leaf<TKey, TValue, TSlots>.IsOrdered) return;
        for (int i = from; i < to; i++) leaf.OrderAt(i) = (byte)i;
    }

    /// <summary>Restores a node's contents from a copy while retaining its current lock version.</summary>
    public static void Restore(Node<TKey, TSlots> node, Node<TKey, TSlots> saved)
    {
        node.Count = saved.Count;
        node.PrefixLength = saved.PrefixLength;
        node.Slots = saved.Slots;
        if (node.IsLeaf)
        {
            Leaf<TKey, TValue, TSlots> leaf = (Leaf<TKey, TValue, TSlots>)node;
            Leaf<TKey, TValue, TSlots> source = (Leaf<TKey, TValue, TSlots>)saved;
            leaf.Entries = source.Entries;
            leaf.Order = source.Order;
            leaf.Next = source.Next;
        }
        else
        {
            InternalNode<TKey, TSlots> internalNode = (InternalNode<TKey, TSlots>)node;
            InternalNode<TKey, TSlots> source = (InternalNode<TKey, TSlots>)saved;
            internalNode.Keys = source.Keys;
            internalNode.Children = source.Children;
        }
    }

    private static void AppendSlots(Node<TKey, TSlots> left, int leftIndex, Node<TKey, TSlots> right, int rightCount, int prefixLength)
    {
        if (right.PrefixLength == prefixLength)
        {
            InlineSlots<TKey, TSlots>.Copy(left.IsLeaf, ref right.Slots, 0, ref left.Slots, leftIndex, rightCount);
            return;
        }

        InlineSlots<TKey, TSlots>.CopyTags(left.IsLeaf, ref right.Slots, 0, ref left.Slots, leftIndex, rightCount);
        for (int i = 0; i < rightCount; i++)
        {
            ulong inlineKey = InlineSlots<TKey, TSlots>.Of(left.IsLeaf, NodeSearch<TKey, TValue, TSlots>.KeyAt(right, i), prefixLength);
            InlineSlots<TKey, TSlots>.Set(left.IsLeaf, ref left.Slots, leftIndex + i, inlineKey);
        }
    }
}
