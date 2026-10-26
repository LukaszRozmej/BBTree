// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace BBTree;

/// <summary>An ordered map for one thread: a B+-tree whose nodes search inline keys.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <typeparam name="TInlineKey">Orders the keys and stores their inline keys; see <see cref="IInlineKey{TKey}"/>.</typeparam>
/// <remarks>
/// <para>
/// A port of bbtree (https://github.com/XiangpengHao/bbtree). Each node keeps, beside its keys, a dense array of
/// 2, 4 or 8 bytes of each key, taken past the units that all keys of the node share. A search counts the inline
/// keys below its own with vector compares, without branches, and reads full keys only where inline keys tie.
/// </para>
/// <para>
/// Leaves are linked in key order, so iteration and range scans read each leaf once. The map is not thread safe;
/// use <see cref="ConcurrentBBTreeMap{TKey, TValue, TInlineKey}"/> to share it.
/// </para>
/// </remarks>
public sealed class BBTreeMap<TKey, TValue, TInlineKey> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TInlineKey : struct, IInlineKey<TKey>
{
    private const int Capacity = BBTreeLayout.NodeCapacity;

    /// <summary>The most units of the shared prefix that the map keeps, so that every search checks it in a few loads.</summary>
    private const int MaxSharedLength = 64;

    private Node<TKey, TInlineKey>? _root;

    /// <summary>A key of the map, or one that it held: every key shares its first <see cref="_sharedLength"/> units.</summary>
    private TKey _sharedKey = default!;
    private int _sharedLength;
    private int _count;
    private int _version;

    /// <summary>Gets the number of entries.</summary>
    public int Count => _count;

    internal Node<TKey, TInlineKey>? Root => _root;

    internal int SharedLength => _sharedLength;

    internal TKey SharedKey => _sharedKey;

    /// <summary>Gets or sets the value of <paramref name="key"/>.</summary>
    /// <exception cref="KeyNotFoundException">On get, the map has no such key.</exception>
    public TValue this[in TKey key]
    {
        get
        {
            ref TValue value = ref GetValueRefOrNullRef(key);
            if (Unsafe.IsNullRef(ref value)) ThrowKeyNotFound(key);
            return value;
        }
        set => GetValueRefOrAddDefault(key, out _) = value;
    }

    /// <summary>Whether the map has <paramref name="key"/>.</summary>
    public bool ContainsKey(in TKey key) => !Unsafe.IsNullRef(ref GetValueRefOrNullRef(key));

    /// <summary>Gets the value of <paramref name="key"/>.</summary>
    /// <returns>Whether the map has the key.</returns>
    public bool TryGetValue(in TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ref TValue found = ref GetValueRefOrNullRef(key);
        if (Unsafe.IsNullRef(ref found))
        {
            value = default;
            return false;
        }

        value = found;
        return true;
    }

    /// <summary>Gets a reference to the value of <paramref name="key"/>, or a null reference when the map has no such key.</summary>
    /// <remarks>The reference is valid until the next change of the map.</remarks>
    public ref TValue GetValueRefOrNullRef(in TKey key) => ref FindValue<TKey, TInlineKey>(key);

    /// <summary>Gets a view of the map that searches it by <typeparamref name="TAlternate"/>, such as a span of chars for string keys.</summary>
    /// <typeparam name="TAlternate">The type that the view searches with.</typeparam>
    /// <typeparam name="TAlternateKey">Compares and windows <typeparamref name="TAlternate"/> as the keys of the map; usually <typeparamref name="TInlineKey"/>.</typeparam>
    public AlternateLookup<TAlternate, TAlternateKey> GetAlternateLookup<TAlternate, TAlternateKey>()
        where TAlternate : allows ref struct
        where TAlternateKey : IKeyQuery<TAlternate, TKey> => new(this);

    /// <summary>Adds <paramref name="key"/> and <paramref name="value"/> if the map has no such key.</summary>
    /// <returns>Whether the map added the entry.</returns>
    public bool TryAdd(in TKey key, TValue value)
    {
        ref TValue slot = ref GetValueRefOrAddDefault(key, out bool exists);
        if (exists) return false;
        slot = value;
        return true;
    }

    /// <summary>Gets a reference to the value of <paramref name="key"/>, after adding the key with a default value if the map has no such key.</summary>
    /// <remarks>The reference is valid until the next change of the map.</remarks>
    public ref TValue GetValueRefOrAddDefault(in TKey key, out bool exists) => ref GetOrAdd<TKey, TInlineKey>(key, out exists);

    /// <summary>Removes <paramref name="key"/>.</summary>
    /// <returns>Whether the map had the key.</returns>
    public bool Remove(in TKey key) => Remove(key, out _);

    /// <summary>Removes <paramref name="key"/> and gives its value.</summary>
    /// <returns>Whether the map had the key.</returns>
    public bool Remove(in TKey key, [MaybeNullWhen(false)] out TValue value) => Remove<TKey, TInlineKey>(key, out value);

    /// <summary>Removes the entry with the least key.</summary>
    public bool TryRemoveFirst([MaybeNullWhen(false)] out TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        Node<TKey, TInlineKey>? node = _root;
        if (node is null)
        {
            key = default;
            value = default;
            return false;
        }

        while (!node.IsLeaf) node = AsInternal(node).ChildAt(0);

        Leaf<TKey, TValue, TInlineKey> leaf = AsLeaf(node);
        ref Entry<TKey, TValue> entry = ref leaf.EntryAt(0);
        key = entry.Key;
        value = entry.Value;
        RemoveAt(leaf, 0);
        return true;
    }

    /// <summary>Gets the entry with the smallest key.</summary>
    /// <returns>Whether the map has entries.</returns>
    public bool TryGetFirst([MaybeNullWhen(false)] out TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        Leaf<TKey, TValue, TInlineKey>? leaf = FirstLeaf();
        if (leaf is null)
        {
            key = default;
            value = default;
            return false;
        }

        ref Entry<TKey, TValue> entry = ref leaf.EntryAt(0);
        key = entry.Key;
        value = entry.Value;
        return true;
    }

    /// <summary>Gets the entry with the largest key.</summary>
    /// <returns>Whether the map has entries.</returns>
    public bool TryGetLast([MaybeNullWhen(false)] out TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        Node<TKey, TInlineKey>? node = _root;
        if (node is null)
        {
            key = default;
            value = default;
            return false;
        }

        while (!node.IsLeaf) node = AsInternal(node).ChildAt(node.Count);
        ref Entry<TKey, TValue> entry = ref AsLeaf(node).EntryAt(node.Count - 1);
        key = entry.Key;
        value = entry.Value;
        return true;
    }

    /// <summary>Removes all entries.</summary>
    public void Clear()
    {
        _root = null;
        _sharedKey = default!;
        _sharedLength = 0;
        _count = 0;
        _version++;
    }

    /// <summary>Gets an enumerator of the entries in key order.</summary>
    public Enumerator GetEnumerator() => new(this, FirstLeaf(), 0);

    /// <summary>Enumerates the entries whose keys are at least <paramref name="start"/>, in key order.</summary>
    public Range EnumerateFrom(in TKey start) => EnumerateFrom<TKey, TInlineKey>(start);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => new BoxedEnumerator(GetEnumerator());

    IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator(GetEnumerator());

    /// <remarks>
    /// Reference keys run shared generic code, which ran slower inlined into a caller than in a method of its own;
    /// value keys run faster inlined.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref TValue FindValue<TQuery, TQueryKey>(scoped in TQuery query)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey> =>
        ref RuntimeHelpers.IsReferenceOrContainsReferences<TKey>() ? ref FindValueOutOfLine<TQuery, TQueryKey>(query) : ref FindValueCore<TQuery, TQueryKey>(query);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref TValue FindValueOutOfLine<TQuery, TQueryKey>(scoped in TQuery query)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey> =>
        ref FindValueCore<TQuery, TQueryKey>(query);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref TValue FindValueCore<TQuery, TQueryKey>(scoped in TQuery query)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey>
    {
        Node<TKey, TInlineKey>? node = _root;
        if (node is null || !SharesPrefix<TQuery, TQueryKey>(query)) return ref Unsafe.NullRef<TValue>();

        while (!node.IsLeaf)
        {
            InternalNode<TKey, TInlineKey> internalNode = AsInternal(node);
            node = internalNode.ChildAt(NodeSearch<TKey, TValue, TInlineKey>.ChildIndex<TQuery, TQueryKey>(internalNode, query));
        }

        Leaf<TKey, TValue, TInlineKey> leaf = AsLeaf(node);
        int index = NodeSearch<TKey, TValue, TInlineKey>.FindExact<TQuery, TQueryKey>(leaf, query);
        return ref index >= 0 ? ref leaf.EntryAt(index).Value : ref Unsafe.NullRef<TValue>();
    }

    private ref TValue GetOrAdd<TQuery, TQueryKey>(scoped in TQuery query, out bool exists)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey>
    {
        Node<TKey, TInlineKey>? node = _root;
        if (node is null) return ref AddFirst(KeyDispatch<TQuery, TQueryKey, TKey>.ToKey(query), out exists);

        if (_sharedLength > 0)
        {
            int common = NodeSearch<TKey, TValue, TInlineKey>.CommonPrefix<TQuery, TQueryKey>(query, _sharedKey, _sharedLength);
            if (common < _sharedLength) ShrinkSharedPrefix(common);
        }

        while (!node.IsLeaf)
        {
            InternalNode<TKey, TInlineKey> internalNode = AsInternal(node);
            node = internalNode.ChildAt(NodeSearch<TKey, TValue, TInlineKey>.ChildIndex<TQuery, TQueryKey>(internalNode, query));
        }

        Leaf<TKey, TValue, TInlineKey> leaf = AsLeaf(node);
        LeafPrefetch.Entries(leaf);
        int index = NodeSearch<TKey, TValue, TInlineKey>.Find<TQuery, TQueryKey>(leaf, query, out ulong inlineKey);
        if (index >= 0)
        {
            exists = true;
            return ref leaf.EntryAt(index).Value;
        }

        exists = false;
        _count++;
        _version++;
        index = ~index;
        TKey key = KeyDispatch<TQuery, TQueryKey, TKey>.ToKey(query);
        if (leaf.Count < Capacity)
        {
            NodeOps<TKey, TValue, TInlineKey>.InsertEntry(leaf, index, key, default!, inlineKey);
            return ref leaf.EntryAt(index).Value;
        }

        return ref SplitAndInsert(leaf, index, key);
    }

    private bool Remove<TQuery, TQueryKey>(scoped in TQuery query, [MaybeNullWhen(false)] out TValue value)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey>
    {
        Node<TKey, TInlineKey>? node = _root;
        if (node is null || !SharesPrefix<TQuery, TQueryKey>(query))
        {
            value = default;
            return false;
        }

        while (!node.IsLeaf)
        {
            InternalNode<TKey, TInlineKey> internalNode = AsInternal(node);
            node = internalNode.ChildAt(NodeSearch<TKey, TValue, TInlineKey>.ChildIndex<TQuery, TQueryKey>(internalNode, query));
        }

        Leaf<TKey, TValue, TInlineKey> leaf = AsLeaf(node);
        LeafPrefetch.Entries(leaf);
        int index = NodeSearch<TKey, TValue, TInlineKey>.FindExact<TQuery, TQueryKey>(leaf, query);
        if (index < 0)
        {
            value = default;
            return false;
        }

        value = leaf.EntryAt(index).Value;
        RemoveAt(leaf, index);
        return true;
    }

    /// <remarks>Kept out of line: inlined into an enumeration loop, it would spend the JIT's inlining budget that the enumerator needs.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Range EnumerateFrom<TQuery, TQueryKey>(scoped in TQuery start)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey>
    {
        Node<TKey, TInlineKey>? node = _root;
        if (node is null) return new Range(this, null, 0);

        if (!SharesPrefix<TQuery, TQueryKey>(start))
        {
            // The start differs from every key within the shared prefix, so it is below or above all of them.
            return KeyDispatch<TQuery, TQueryKey, TKey>.Compare(start, _sharedKey) < 0 ? new Range(this, FirstLeaf(), 0) : new Range(this, null, 0);
        }

        while (!node.IsLeaf)
        {
            InternalNode<TKey, TInlineKey> internalNode = AsInternal(node);
            node = internalNode.ChildAt(NodeSearch<TKey, TValue, TInlineKey>.ChildIndex<TQuery, TQueryKey>(internalNode, start));
        }

        Leaf<TKey, TValue, TInlineKey> leaf = AsLeaf(node);
        int index = NodeSearch<TKey, TValue, TInlineKey>.Find<TQuery, TQueryKey>(leaf, start);
        return new Range(this, leaf, index >= 0 ? index : ~index);
    }

    private ref TValue AddFirst(TKey key, out bool exists)
    {
        _sharedKey = key;
        _sharedLength = Math.Min(TInlineKey.Length(key), MaxSharedLength);
        Leaf<TKey, TValue, TInlineKey> leaf = new(_sharedLength);
        NodeOps<TKey, TValue, TInlineKey>.InsertEntry(leaf, 0, key, default!);
        _root = leaf;
        _count = 1;
        _version++;
        exists = false;
        return ref leaf.EntryAt(0).Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SharesPrefix<TQuery, TQueryKey>(scoped in TQuery query)
        where TQuery : allows ref struct
        where TQueryKey : IKeyQuery<TQuery, TKey> =>
        _sharedLength == 0 || NodeSearch<TKey, TValue, TInlineKey>.CommonPrefix<TQuery, TQueryKey>(query, _sharedKey, _sharedLength) == _sharedLength;

    /// <summary>Shrinks the shared prefix for a key that shares less, and with it the prefix of every node on the borders of the tree.</summary>
    private void ShrinkSharedPrefix(int length)
    {
        _sharedLength = length;
        for (Node<TKey, TInlineKey>? node = _root; node is not null; node = node.IsLeaf ? null : AsInternal(node).ChildAt(0))
        {
            if (node.PrefixLength > length) NodeSearch<TKey, TValue, TInlineKey>.SetPrefix(node, length);
        }

        for (Node<TKey, TInlineKey>? node = _root; node is not null; node = node.IsLeaf ? null : AsInternal(node).ChildAt(node.Count))
        {
            if (node.PrefixLength > length) NodeSearch<TKey, TValue, TInlineKey>.SetPrefix(node, length);
        }
    }

    /// <summary>Records the way down to the leaf of <paramref name="key"/>.</summary>
    /// <returns>The depth of the leaf.</returns>
    /// <remarks>
    /// Searches keep no path: most inserts and removals change one leaf. The few that split or merge nodes go down
    /// again, through nodes they have just read, rather than every operation zeroing and filling a path.
    /// </remarks>
    private int Descend(in TKey key, ref NodePath<TKey, TInlineKey> path)
    {
        Node<TKey, TInlineKey> node = _root!;
        int depth = 0;
        while (!node.IsLeaf)
        {
            InternalNode<TKey, TInlineKey> internalNode = AsInternal(node);
            int edge = NodeSearch<TKey, TValue, TInlineKey>.ChildIndex<TKey, TInlineKey>(internalNode, key);
            path.Push(depth++, internalNode, edge);
            node = internalNode.ChildAt(edge);
        }

        return depth;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref TValue SplitAndInsert(Leaf<TKey, TValue, TInlineKey> leaf, int index, TKey key)
    {
        NodePath<TKey, TInlineKey> path = default;
        int depth = Descend(key, ref path);
        int split = NodeOps<TKey, TValue, TInlineKey>.SplitPoint(index);
        Leaf<TKey, TValue, TInlineKey> right = NodeOps<TKey, TValue, TInlineKey>.SplitLeaf(leaf, split);
        Leaf<TKey, TValue, TInlineKey> target = index <= split ? leaf : right;
        int targetIndex = index <= split ? index : index - split;
        NodeOps<TKey, TValue, TInlineKey>.InsertEntry(target, targetIndex, key, default!);

        TKey separator = right.EntryAt(0).Key;
        path.Fences(depth, out bool hasLow, out TKey low, out bool hasHigh, out TKey high);
        NodeOps<TKey, TValue, TInlineKey>.GrowPrefix(leaf, hasLow, low, true, separator, _sharedLength);
        NodeOps<TKey, TValue, TInlineKey>.GrowPrefix(right, true, separator, hasHigh, high, _sharedLength);
        InsertSeparator(ref path, depth, separator, right);
        return ref target.EntryAt(targetIndex).Value;
    }

    /// <summary>Inserts the separator and the right half of a split of the node at <paramref name="depth"/> into its parent, splitting up the tree as needed.</summary>
    private void InsertSeparator(ref NodePath<TKey, TInlineKey> path, int depth, TKey separator, Node<TKey, TInlineKey> right)
    {
        while (depth > 0)
        {
            depth--;
            InternalNode<TKey, TInlineKey> parent = path.Nodes[depth]!;
            int edge = path.Edges[depth];
            if (parent.Count < Capacity)
            {
                NodeOps<TKey, TValue, TInlineKey>.InsertSeparator(parent, edge, separator, right);
                return;
            }

            int split = NodeOps<TKey, TValue, TInlineKey>.SplitPoint(edge);
            InternalNode<TKey, TInlineKey> sibling = NodeOps<TKey, TValue, TInlineKey>.SplitInternal(parent, split, out TKey up);
            if (edge <= split) NodeOps<TKey, TValue, TInlineKey>.InsertSeparator(parent, edge, separator, right);
            else NodeOps<TKey, TValue, TInlineKey>.InsertSeparator(sibling, edge - split - 1, separator, right);

            path.Fences(depth, out bool hasLow, out TKey low, out bool hasHigh, out TKey high);
            NodeOps<TKey, TValue, TInlineKey>.GrowPrefix(parent, hasLow, low, true, up, _sharedLength);
            NodeOps<TKey, TValue, TInlineKey>.GrowPrefix(sibling, true, up, hasHigh, high, _sharedLength);
            separator = up;
            right = sibling;
        }

        InternalNode<TKey, TInlineKey> root = new(_sharedLength);
        root.ChildAt(0) = _root!;
        root.Count = 0;
        NodeOps<TKey, TValue, TInlineKey>.InsertSeparator(root, 0, separator, right);
        _root = root;
    }

    private void RemoveAt(Leaf<TKey, TValue, TInlineKey> leaf, int index)
    {
        TKey key = leaf.EntryAt(index).Key;
        NodeOps<TKey, TValue, TInlineKey>.RemoveEntry(leaf, index);
        _version++;
        if (--_count == 0)
        {
            Clear();
            return;
        }

        if (leaf != _root && leaf.Count < BBTreeLayout.MinNodeLength) Rebalance(key, leaf);
    }

    /// <summary>Fixes a node below its minimum: merges it with a sibling if both fit in one node, else moves one entry over. Goes up while parents fall below theirs.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Rebalance(in TKey key, Node<TKey, TInlineKey> node)
    {
        NodePath<TKey, TInlineKey> path = default;
        int depth = Descend(key, ref path);
        while (depth > 0 && node.Count < BBTreeLayout.MinNodeLength)
        {
            int parentDepth = depth - 1;
            InternalNode<TKey, TInlineKey> parent = path.Nodes[parentDepth]!;
            int edge = path.Edges[parentDepth];
            int separatorIndex = edge > 0 ? edge - 1 : 0;
            Node<TKey, TInlineKey> left = parent.ChildAt(separatorIndex);
            Node<TKey, TInlineKey> right = parent.ChildAt(separatorIndex + 1);

            path.Fences(parentDepth, out bool parentHasLow, out TKey parentLow, out bool parentHasHigh, out TKey parentHigh);
            bool hasLow = separatorIndex > 0 || parentHasLow;
            TKey low = separatorIndex > 0 ? parent.KeyAt(separatorIndex - 1) : parentLow;
            bool hasHigh = separatorIndex + 1 < parent.Count || parentHasHigh;
            TKey high = separatorIndex + 1 < parent.Count ? parent.KeyAt(separatorIndex + 1) : parentHigh;

            bool isLeaf = node.IsLeaf;
            int mergedCount = left.Count + right.Count + (isLeaf ? 0 : 1);
            if (mergedCount > Capacity)
            {
                TKey newSeparator = isLeaf
                    ? edge > 0
                        ? NodeOps<TKey, TValue, TInlineKey>.MoveLastEntry(AsLeaf(left), AsLeaf(right), hasHigh, high, _sharedLength)
                        : NodeOps<TKey, TValue, TInlineKey>.MoveFirstEntry(AsLeaf(left), AsLeaf(right), hasLow, low, _sharedLength)
                    : edge > 0
                        ? NodeOps<TKey, TValue, TInlineKey>.MoveLastChild(AsInternal(left), parent.KeyAt(separatorIndex), AsInternal(right), hasHigh, high, _sharedLength)
                        : NodeOps<TKey, TValue, TInlineKey>.MoveFirstChild(AsInternal(left), parent.KeyAt(separatorIndex), AsInternal(right), hasLow, low, _sharedLength);
                NodeOps<TKey, TValue, TInlineKey>.ReplaceSeparator(parent, separatorIndex, newSeparator);
                return;
            }

            int prefixLength = NodeOps<TKey, TValue, TInlineKey>.FencePrefix(hasLow, low, hasHigh, high, _sharedLength);
            if (isLeaf) NodeOps<TKey, TValue, TInlineKey>.MergeLeaves(AsLeaf(left), AsLeaf(right), prefixLength);
            else NodeOps<TKey, TValue, TInlineKey>.MergeInternal(AsInternal(left), parent.KeyAt(separatorIndex), AsInternal(right), prefixLength);
            NodeOps<TKey, TValue, TInlineKey>.RemoveSeparator(parent, separatorIndex);

            if (parentDepth == 0 && parent.Count == 0)
            {
                _root = left;
                return;
            }

            node = parent;
            depth = parentDepth;
        }
    }

    private Leaf<TKey, TValue, TInlineKey>? FirstLeaf()
    {
        Node<TKey, TInlineKey>? node = _root;
        if (node is null) return null;
        while (!node.IsLeaf) node = AsInternal(node).ChildAt(0);
        return AsLeaf(node);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Leaf<TKey, TValue, TInlineKey> AsLeaf(Node<TKey, TInlineKey> node) => Unsafe.As<Leaf<TKey, TValue, TInlineKey>>(node);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static InternalNode<TKey, TInlineKey> AsInternal(Node<TKey, TInlineKey> node) => Unsafe.As<InternalNode<TKey, TInlineKey>>(node);

    [DoesNotReturn]
    private static void ThrowKeyNotFound(in TKey key) => throw new KeyNotFoundException($"The key '{key}' is not in the map.");

    /// <summary>Enumerates entries in key order, leaf by leaf.</summary>
    /// <remarks>
    /// <para>Checks for changes of the map as it moves to the next leaf.</para>
    /// <para>
    /// It is not <see cref="IDisposable"/>, so <c>foreach</c> calls no <c>Dispose</c> in a <c>finally</c>: in shared
    /// generic code, as for keys of reference types, that call takes the address of the enumerator, which then stays
    /// in memory rather than in registers. The interfaces of the map enumerate through a <see cref="IEnumerator{T}"/>
    /// that wraps it.
    /// </para>
    /// </remarks>
    public struct Enumerator
    {
        private readonly BBTreeMap<TKey, TValue, TInlineKey> _map;
        private readonly int _version;
        private Leaf<TKey, TValue, TInlineKey>? _leaf;

        // Byte offsets of the current entry and of the end of the entries: a step adds the size of an entry, and the
        // entry needs no index scaling.
        private nint _offset;
        private nint _end;

        internal Enumerator(BBTreeMap<TKey, TValue, TInlineKey> map, Leaf<TKey, TValue, TInlineKey>? leaf, int index)
        {
            _map = map;
            _version = map._version;
            _leaf = leaf;
            _offset = (nint)(index - 1) * Unsafe.SizeOf<Entry<TKey, TValue>>();
            _end = (nint)(leaf?.Count ?? 0) * Unsafe.SizeOf<Entry<TKey, TValue>>();
        }

        /// <summary>Gets the entry at the current position.</summary>
        public readonly KeyValuePair<TKey, TValue> Current
        {
            // An entry is laid out as the pair is: its key, then its value.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.As<Entry<TKey, TValue>, KeyValuePair<TKey, TValue>>(ref Leaf<TKey, TValue, TInlineKey>.IsOrdered
                ? ref _leaf!.EntryAt((int)((nuint)_offset / (nuint)Unsafe.SizeOf<Entry<TKey, TValue>>()))
                : ref Unsafe.AddByteOffset(ref _leaf!.SlotAt(0), _offset));
        }

        /// <summary>Moves to the next entry.</summary>
        /// <returns>Whether there is a next entry.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => (_offset += Unsafe.SizeOf<Entry<TKey, TValue>>()) < _end || MoveToNextLeaf();

        private bool MoveToNextLeaf()
        {
            if (_version != _map._version) ThrowModified();
            while (_leaf is not null)
            {
                if (_offset < _end)
                {
                    LeafPrefetch.Start(_leaf.Next);
                    return true;
                }

                _leaf = _leaf.Next;
                _end = (nint)(_leaf?.Count ?? 0) * Unsafe.SizeOf<Entry<TKey, TValue>>();
                _offset = 0;
            }

            return false;
        }

        [DoesNotReturn]
        private static void ThrowModified() => throw new InvalidOperationException("The map changed during enumeration.");
    }

    private sealed class BoxedEnumerator(Enumerator enumerator) : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private Enumerator _enumerator = enumerator;

        public KeyValuePair<TKey, TValue> Current => _enumerator.Current;

        object IEnumerator.Current => Current;

        public bool MoveNext() => _enumerator.MoveNext();

        public void Reset() => throw new NotSupportedException();

        public void Dispose() { }
    }

    /// <summary>The entries from a start key on, in key order.</summary>
    public readonly struct Range : IEnumerable<KeyValuePair<TKey, TValue>>
    {
        private readonly BBTreeMap<TKey, TValue, TInlineKey> _map;
        private readonly Leaf<TKey, TValue, TInlineKey>? _leaf;
        private readonly int _index;

        internal Range(BBTreeMap<TKey, TValue, TInlineKey> map, Leaf<TKey, TValue, TInlineKey>? leaf, int index)
        {
            _map = map;
            _leaf = leaf;
            _index = index;
        }

        /// <summary>Gets an enumerator of the entries of the range in key order.</summary>
        public Enumerator GetEnumerator() => new(_map, _leaf, _index);

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => new BoxedEnumerator(GetEnumerator());

        IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator(GetEnumerator());
    }

    /// <summary>Searches and changes the map by <typeparamref name="TAlternate"/> keys; makes a <typeparamref name="TKey"/> only when an insert adds one.</summary>
    public readonly struct AlternateLookup<TAlternate, TAlternateKey>
        where TAlternate : allows ref struct
        where TAlternateKey : IKeyQuery<TAlternate, TKey>
    {
        internal AlternateLookup(BBTreeMap<TKey, TValue, TInlineKey> map) => Map = map;

        /// <summary>Gets the map that this lookup searches.</summary>
        public BBTreeMap<TKey, TValue, TInlineKey> Map { get; }

        /// <summary>Gets or sets the value of <paramref name="key"/>.</summary>
        /// <exception cref="KeyNotFoundException">On get, the map has no such key.</exception>
        public TValue this[TAlternate key]
        {
            get
            {
                ref TValue value = ref Map.FindValue<TAlternate, TAlternateKey>(key);
                if (Unsafe.IsNullRef(ref value)) throw new KeyNotFoundException("The key is not in the map.");
                return value;
            }
            set => Map.GetOrAdd<TAlternate, TAlternateKey>(key, out _) = value;
        }

        /// <summary>Whether the map has <paramref name="key"/>.</summary>
        public bool ContainsKey(TAlternate key) => !Unsafe.IsNullRef(ref Map.FindValue<TAlternate, TAlternateKey>(key));

        /// <summary>Gets the value of <paramref name="key"/>.</summary>
        /// <returns>Whether the map has the key.</returns>
        public bool TryGetValue(TAlternate key, [MaybeNullWhen(false)] out TValue value)
        {
            ref TValue found = ref Map.FindValue<TAlternate, TAlternateKey>(key);
            if (Unsafe.IsNullRef(ref found))
            {
                value = default;
                return false;
            }

            value = found;
            return true;
        }

        /// <summary>Adds <paramref name="key"/> and <paramref name="value"/> if the map has no such key.</summary>
        /// <returns>Whether the entry was added.</returns>
        public bool TryAdd(TAlternate key, TValue value)
        {
            ref TValue slot = ref Map.GetOrAdd<TAlternate, TAlternateKey>(key, out bool exists);
            if (exists) return false;
            slot = value;
            return true;
        }

        /// <inheritdoc cref="BBTreeMap{TKey, TValue, TInlineKey}.GetValueRefOrAddDefault"/>
        public ref TValue GetValueRefOrAddDefault(TAlternate key, out bool exists) => ref Map.GetOrAdd<TAlternate, TAlternateKey>(key, out exists);

        /// <inheritdoc cref="BBTreeMap{TKey, TValue, TInlineKey}.GetValueRefOrNullRef"/>
        public ref TValue GetValueRefOrNullRef(TAlternate key) => ref Map.FindValue<TAlternate, TAlternateKey>(key);

        /// <summary>Removes <paramref name="key"/>.</summary>
        /// <returns>Whether the map had the key.</returns>
        public bool Remove(TAlternate key) => Map.Remove<TAlternate, TAlternateKey>(key, out _);

        /// <summary>Removes <paramref name="key"/> and gives its value.</summary>
        /// <returns>Whether the map had the key.</returns>
        public bool Remove(TAlternate key, [MaybeNullWhen(false)] out TValue value) => Map.Remove<TAlternate, TAlternateKey>(key, out value);

        /// <inheritdoc cref="BBTreeMap{TKey, TValue, TInlineKey}.EnumerateFrom(in TKey)"/>
        public Range EnumerateFrom(TAlternate start) => Map.EnumerateFrom<TAlternate, TAlternateKey>(start);
    }
}
