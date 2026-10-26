// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Runtime.CompilerServices;

namespace BBTree;

/// <summary>Searches one node and keeps its inline keys in step with its prefix.</summary>
internal static class NodeSearch<TKey, TValue, TSlots> where TSlots : struct, IInlineKey<TKey>
{
    /// <summary>The most tied keys that a search compares one by one; past it, a binary search compares fewer.</summary>
    /// <remarks>Ties are rare where the inline keys summarize keys well, and all keys of a node tie where they do not.</remarks>
    private const int LinearTies = 8;

    /// <summary>Finds <paramref name="query"/> in <paramref name="leaf"/>.</summary>
    /// <returns>The index of the key, or the bitwise complement of the index where it goes.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Find<TQuery, TQueryKey>(Leaf<TKey, TValue, TSlots> leaf, in TQuery query) where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey> =>
        Find<TQuery, TQueryKey>(leaf, query, out _);

    /// <inheritdoc cref="Find{TQuery, TQueryKey}(Leaf{TKey, TValue, TSlots}, in TQuery)"/>
    /// <param name="leaf">The leaf to search.</param>
    /// <param name="query">The key to find.</param>
    /// <param name="inlineKey">The inline key of the query in the leaf, for an insert to reuse.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Find<TQuery, TQueryKey>(Leaf<TKey, TValue, TSlots> leaf, in TQuery query, out ulong inlineKey)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey>
    {
        int prefixLength = leaf.PrefixLength;
        ulong target = inlineKey = InlineSlots<TKey, TSlots>.Of<TQuery, TQueryKey>(true, query, prefixLength);
        int less = InlineSlots<TKey, TSlots>.Count(true, ref leaf.Slots, leaf.Count, target, out int equal);
        if (equal == 0) return ~less;
        if (InlineSlots<TKey, TSlots>.AreExact(true, prefixLength)) return less;

        int end = less + equal;
        if (equal > LinearTies) return FindInTies<TQuery, TQueryKey>(leaf, query, less, end);
        for (int i = less; i < end; i++)
        {
            int comparison = KeyDispatch<TQuery, TQueryKey, TKey>.Compare(query, leaf.EntryAt(i).Key);
            if (comparison == 0) return i;
            if (comparison < 0) return ~i;
        }

        return ~end;
    }

    /// <summary>Finds the key equal to <paramref name="query"/> in <paramref name="leaf"/>.</summary>
    /// <returns>The index of the key, or -1.</returns>
    /// <remarks>
    /// Unlike <see cref="Find{TQuery, TQueryKey}(Leaf{TKey, TValue, TSlots}, in TQuery)"/>, which places a missing key, it reads only the tied keys whose length tag matches,
    /// and none when the inline key holds the rest of the query.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FindExact<TQuery, TQueryKey>(Leaf<TKey, TValue, TSlots> leaf, in TQuery query)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey>
    {
        if (!InlineSlots<TKey, TSlots>.HasTags(true))
        {
            int index = Find<TQuery, TQueryKey>(leaf, query);
            return index >= 0 ? index : -1;
        }

        int prefixLength = leaf.PrefixLength;
        ulong target = InlineSlots<TKey, TSlots>.Of<TQuery, TQueryKey>(true, query, prefixLength);
        int less = InlineSlots<TKey, TSlots>.Count(true, ref leaf.Slots, leaf.Count, target, out int equal);
        if (equal == 0) return -1;

        int length = KeyDispatch<TQuery, TQueryKey, TKey>.Length(query);
        byte tag = (byte)Math.Min(length, byte.MaxValue);
        int width = InlineSlots<TKey, TSlots>.Width(true);
        bool heldInline = length < byte.MaxValue && length <= prefixLength + width &&
            KeyDispatch<TQuery, TQueryKey, TKey>.HoldsUnits(target, width);
        int end = less + equal;
        for (int i = less; i < end; i++)
        {
            if (InlineSlots<TKey, TSlots>.Tag(ref leaf.Slots, i) != tag) continue;
            if (heldInline || KeyDispatch<TQuery, TQueryKey, TKey>.AreEqual(query, leaf.EntryAt(i).Key)) return i;
        }

        return -1;
    }

    /// <summary>Gets the edge of <paramref name="node"/> that <paramref name="query"/> goes down: the number of separators at most the query.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChildIndex<TQuery, TQueryKey>(InternalNode<TKey, TSlots> node, in TQuery query)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey>
    {
        int prefixLength = node.PrefixLength;
        ulong target = InlineSlots<TKey, TSlots>.Of<TQuery, TQueryKey>(false, query, prefixLength);
        int less = InlineSlots<TKey, TSlots>.Count(false, ref node.Slots, node.Count, target, out int equal);
        if (equal == 0) return less;
        if (InlineSlots<TKey, TSlots>.AreExact(false, prefixLength)) return less + equal;

        int end = less + equal;
        if (equal > LinearTies) return ChildInTies<TQuery, TQueryKey>(node, query, less, end);
        int index = less;
        while (index < end && KeyDispatch<TQuery, TQueryKey, TKey>.Compare(query, node.KeyAt(index)) >= 0) index++;
        return index;
    }

    /// <summary>Finds <paramref name="query"/> among the keys <c>low..high</c> of a leaf, whose inline keys tie with its own.</summary>
    /// <returns>The index of the key, or the bitwise complement of the index where it goes.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int FindInTies<TQuery, TQueryKey>(Leaf<TKey, TValue, TSlots> leaf, in TQuery query, int low, int high)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey>
    {
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            int comparison = KeyDispatch<TQuery, TQueryKey, TKey>.Compare(query, leaf.EntryAt(middle).Key);
            if (comparison == 0) return middle;
            if (comparison < 0) high = middle;
            else low = middle + 1;
        }

        return ~low;
    }

    /// <summary>Gets the first of the separators <c>low..high</c>, whose inline keys tie with the query's, that is above <paramref name="query"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ChildInTies<TQuery, TQueryKey>(InternalNode<TKey, TSlots> node, in TQuery query, int low, int high)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey>
    {
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (KeyDispatch<TQuery, TQueryKey, TKey>.Compare(query, node.KeyAt(middle)) >= 0) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    /// <summary>Gets the number of leading units that a query and a key share, at most <paramref name="limit"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CommonPrefix<TQuery, TQueryKey>(in TQuery query, in TKey key, int limit)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey> =>
        Math.Min(KeyDispatch<TQuery, TQueryKey, TKey>.CommonPrefixLength(query, key), limit);

    public static int CommonPrefix(in TKey x, in TKey y, int limit) => CommonPrefix<TKey, TSlots>(x, y, limit);

    /// <summary>Gets key <paramref name="index"/> of a node of either kind.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref TKey KeyAt(Node<TKey, TSlots> node, int index) =>
        ref node.IsLeaf
            ? ref Unsafe.As<Leaf<TKey, TValue, TSlots>>(node).EntryAt(index).Key
            : ref Unsafe.As<InternalNode<TKey, TSlots>>(node).KeyAt(index);

    /// <summary>Sets the prefix length of <paramref name="node"/> and writes the window of each key there.</summary>
    public static void SetPrefix(Node<TKey, TSlots> node, int prefixLength)
    {
        if (node.PrefixLength == prefixLength) return;
        for (int i = 0; i < node.Count; i++)
        {
            InlineSlots<TKey, TSlots>.Set(node.IsLeaf, ref node.Slots, i, InlineSlots<TKey, TSlots>.Of(node.IsLeaf, KeyAt(node, i), prefixLength));
        }

        node.PrefixLength = (ushort)prefixLength;
    }

    /// <summary>Whether the first and the last key of <paramref name="node"/> differ right past its prefix.</summary>
    /// <remarks>
    /// Then the fences of the node, which lie outside its keys, share no more than its prefix,
    /// so a prefix cannot grow and a split skips reading the fences.
    /// </remarks>
    public static bool KeysDifferPastPrefix(Node<TKey, TSlots> node) =>
        node.Count > 1 && InlineSlots<TKey, TSlots>.DifferInFirstByte(node.IsLeaf,
            InlineSlots<TKey, TSlots>.Get(node.IsLeaf, ref node.Slots, 0),
            InlineSlots<TKey, TSlots>.Get(node.IsLeaf, ref node.Slots, node.Count - 1));
}
