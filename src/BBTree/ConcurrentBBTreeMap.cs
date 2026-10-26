// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace BBTree;

/// <summary>An ordered map that threads share: a B+-tree whose nodes search inline keys, read without locks.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <typeparam name="TInlineKey">Orders the keys and stores their inline keys; see <see cref="IInlineKey{TKey}"/>.</typeparam>
/// <remarks>
/// <para>
/// A port of the latched tree of bbtree (https://github.com/XiangpengHao/bbtree), with optimistic lock coupling
/// (Leis et al., DaMoN 2016): every node has a version, a read validates the versions of the nodes it read and
/// restarts if a writer changed one, and a write locks only the nodes it changes. Reads write no shared memory, so
/// they scale with cores. Values are copied out under validation, never handed out by reference.
/// </para>
/// <para>
/// The GC reclaims nodes: a reader that reaches a node a writer just replaced still reads valid memory, and its
/// validation fails. A full internal node splits on the way down, so a leaf split locks only the leaf and its parent.
/// Removals leave nodes below their minimum, as merges would lock siblings; an empty leaf still routes searches.
/// </para>
/// <para>
/// Enumeration copies one entry at a time under validation of its leaf: it sees each key at most once, in order, and is weakly
/// consistent with concurrent writes. <see cref="IKeyQuery{TQuery, TKey}.Compare"/> may see keys that a writer is moving;
/// its result is discarded then, but it must not throw.
/// </para>
/// <para>
/// Through <see cref="IReadOnlyDictionary{TKey, TValue}"/>, <see cref="IReadOnlyCollection{T}.Count"/> is exact only
/// when no write runs concurrently, and the keys and the values are live, weakly consistent enumerations of the map,
/// not snapshots.
/// </para>
/// </remarks>
[CollectionBuilder(typeof(BBTreeMapBuilder), nameof(BBTreeMapBuilder.CreateConcurrent))]
public partial class ConcurrentBBTreeMap<TKey, TValue, TInlineKey> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TInlineKey : struct, IInlineKey<TKey>
{
    private const int Capacity = BBTreeLayout.NodeCapacity;
    private const int MaxSharedLength = 64;

    private volatile Node<TKey, TInlineKey> _root = new Leaf<TKey, TValue, TInlineKey>(0);
    private volatile SharedPrefix? _shared;

    /// <summary>The length a running shrink of the shared prefix fits the borders to, before it publishes it.</summary>
    private volatile int _shrinkingTo = int.MaxValue;
    private readonly Lock _sharedLock = new();
    private readonly StripedCounter _count = new();

    /// <summary>The number of entries; exact when no write runs concurrently.</summary>
    public int Count => (int)_count.Sum();

    internal Node<TKey, TInlineKey> Root => _root;

    internal int SharedLength => _shared?.Length ?? 0;

    internal TKey SharedKey => _shared is { } shared ? shared.Key : default!;

    /// <summary>Gets the value of <paramref name="key"/>, or sets it, adding the key if needed.</summary>
    public TValue this[in TKey key]
    {
        get => TryGetValue(key, out TValue? value) ? value : throw new KeyNotFoundException($"The key '{key}' is not in the map.");
        set => Upsert(key, value, overwrite: true);
    }

    /// <summary>Whether the map has <paramref name="key"/>.</summary>
    public bool ContainsKey(in TKey key) => TryGetValue(key, out _);

    /// <summary>Gets the value of <paramref name="key"/>.</summary>
    /// <returns>Whether the map has the key.</returns>
    public bool TryGetValue(in TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        SharedPrefix? shared = _shared;
        if (shared is { Length: > 0 } && NodeSearch<TKey, TValue, TInlineKey>.CommonPrefix(key, shared.Key, shared.Length) < shared.Length)
        {
            value = default;
            return false;
        }

        SpinWait spin = default;
        int faults = 0;
        while (true)
        {
            if (ComparesNeverThrow)
            {
                if (TryRead(key, out bool found, out value)) return found;
            }
            else
            {
                try
                {
                    if (TryRead(key, out bool found, out value)) return found;
                }
                catch when (RetryAfter(ref faults))
                {
                }
            }

            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>Adds <paramref name="key"/> and <paramref name="value"/> if the map has no such key.</summary>
    /// <returns>Whether the map added the entry.</returns>
    public bool TryAdd(in TKey key, TValue value) => Upsert(key, value, overwrite: false);

    /// <summary>Sets the value of <paramref name="key"/>, adding the key if needed.</summary>
    /// <returns>Whether the map added the key.</returns>
    public bool AddOrUpdate(in TKey key, TValue value) => Upsert(key, value, overwrite: true);

    /// <summary>Removes <paramref name="key"/> and gives its value.</summary>
    /// <returns>Whether the map had the key.</returns>
    public bool TryRemove(in TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        SharedPrefix? shared = _shared;
        if (shared is { Length: > 0 } && NodeSearch<TKey, TValue, TInlineKey>.CommonPrefix(key, shared.Key, shared.Length) < shared.Length)
        {
            value = default;
            return false;
        }

        SpinWait spin = default;
        int faults = 0;
        while (true)
        {
            bool done;
            bool removed = false;
            value = default!;
            if (ComparesNeverThrow)
            {
                done = TryRemoveOnce(key, out removed, out value);
            }
            else
            {
                try
                {
                    done = TryRemoveOnce(key, out removed, out value);
                }
                catch when (RetryAfter(ref faults))
                {
                    done = false;
                }
            }

            if (done)
            {
                if (removed) _count.Add(-1);
                return removed;
            }

            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>Gets an enumerator of the entries in key order.</summary>
    public Enumerator GetEnumerator() => new(this, hasStart: false, default!, empty: false);

    /// <summary>Enumerates the entries whose keys are at least <paramref name="start"/>, in key order.</summary>
    public Range EnumerateFrom(in TKey start) => new(this, start);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>One optimistic search for <paramref name="key"/>.</summary>
    /// <returns>Whether the search validated; if not, the caller retries.</returns>
    /// <remarks>
    /// Reference keys run shared generic code, which ran slower inlined into a caller than in a method of its own;
    /// value keys run faster inlined.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryRead(in TKey key, out bool found, out TValue value) =>
        RuntimeHelpers.IsReferenceOrContainsReferences<TKey>() ? TryReadOutOfLine(key, out found, out value) : TryReadCore(key, out found, out value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryReadOutOfLine(in TKey key, out bool found, out TValue value) => TryReadCore(key, out found, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadCore(in TKey key, out bool found, out TValue value)
    {
        found = false;
        value = default!;
        Node<TKey, TInlineKey> node = _root;
        if (!NodeLatch.TryRead(ref node.Version, out long version) || node != _root) return false;

        while (!node.IsLeaf)
        {
            InternalNode<TKey, TInlineKey> internalNode = AsInternal(node);
            Node<TKey, TInlineKey>? child = internalNode.ChildAt(NodeSearch<TKey, TValue, TInlineKey>.ChildIndex<TKey, TInlineKey>(internalNode, key));
            if (child is null || !NodeLatch.Validate(ref node.Version, version)) return false;
            node = child;
            if (!NodeLatch.TryRead(ref node.Version, out version)) return false;
        }

        Leaf<TKey, TValue, TInlineKey> leaf = AsLeaf(node);
        int index = NodeSearch<TKey, TValue, TInlineKey>.FindExact<TKey, TInlineKey>(leaf, key);
        if (index >= 0) value = leaf.EntryAt(index).Value;
        if (!NodeLatch.Validate(ref node.Version, version)) return false;
        found = index >= 0;
        return true;
    }

    /// <summary>Whether comparisons of keys never throw, even on keys that a writer is moving, which built-in keys guarantee.</summary>
    private static bool ComparesNeverThrow
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => default(TInlineKey) is ITrustedInlineKey;
    }

    /// <summary>
    /// Whether an attempt that threw is retried: a comparison of keys that a writer was moving may throw, and the next
    /// attempt reads them whole. A comparison that throws on every attempt throws to the caller.
    /// </summary>
    private static bool RetryAfter(ref int faults) => ++faults < 8;

    private bool Upsert(in TKey key, TValue value, bool overwrite)
    {
        SharedPrefix? shared = _shared;
        if (shared is null || (shared.Length > 0 && NodeSearch<TKey, TValue, TInlineKey>.CommonPrefix(key, shared.Key, shared.Length) < shared.Length))
        {
            ShrinkSharedPrefix(key);
        }

        SpinWait spin = default;
        int faults = 0;
        while (true)
        {
            bool done;
            bool added = false;
            if (ComparesNeverThrow)
            {
                done = TryUpsertOnce(key, value, overwrite, out added);
            }
            else
            {
                try
                {
                    done = TryUpsertOnce(key, value, overwrite, out added);
                }
                catch when (RetryAfter(ref faults))
                {
                    done = false;
                }
            }

            if (done)
            {
                if (added) _count.Add(1);
                return added;
            }

            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>One attempt to insert or update <paramref name="key"/>; fails if a concurrent write got in the way, or after a split.</summary>
    private bool TryUpsertOnce(in TKey key, TValue value, bool overwrite, out bool added)
    {
        added = false;
        Node<TKey, TInlineKey> node = _root;
        if (!NodeLatch.TryRead(ref node.Version, out long version) || node != _root) return false;

        InternalNode<TKey, TInlineKey>? parent = null;
        long parentVersion = 0;
        int parentEdge = 0;
        while (!node.IsLeaf)
        {
            InternalNode<TKey, TInlineKey> internalNode = AsInternal(node);
            if (internalNode.Count == Capacity)
            {
                // Split a full node on the way down, so that a split below always finds room in its parent.
                SplitInternal(parent, parentVersion, parentEdge, internalNode, version);
                return false;
            }

            int edge = NodeSearch<TKey, TValue, TInlineKey>.ChildIndex<TKey, TInlineKey>(internalNode, key);
            Node<TKey, TInlineKey>? child = internalNode.ChildAt(edge);
            if (child is null || !NodeLatch.Validate(ref node.Version, version)) return false;
            parent = internalNode;
            parentVersion = version;
            parentEdge = edge;
            node = child;
            if (!NodeLatch.TryRead(ref node.Version, out version)) return false;
        }

        Leaf<TKey, TValue, TInlineKey> leaf = AsLeaf(node);
        LeafPrefetch.Entries(leaf);
        int index = NodeSearch<TKey, TValue, TInlineKey>.Find<TKey, TInlineKey>(leaf, key, out ulong inlineKey);
        if (index >= 0 && !overwrite) return NodeLatch.Validate(ref leaf.Version, version);
        if (index < 0 && leaf.Count == Capacity)
        {
            SplitLeaf(parent, parentVersion, parentEdge, leaf, version, ~index);
            return false;
        }

        if (!NodeLatch.TryUpgrade(ref leaf.Version, version)) return false;
        if (index >= 0)
        {
            leaf.EntryAt(index).Value = value;
        }
        else
        {
            NodeOps<TKey, TValue, TInlineKey>.InsertEntry(leaf, ~index, key, value, inlineKey);
            added = true;
        }

        NodeLatch.Unlock(ref leaf.Version);
        return true;
    }

    private bool TryRemoveOnce(in TKey key, out bool removed, out TValue value)
    {
        removed = false;
        value = default!;
        Node<TKey, TInlineKey> node = _root;
        if (!NodeLatch.TryRead(ref node.Version, out long version) || node != _root) return false;

        while (!node.IsLeaf)
        {
            InternalNode<TKey, TInlineKey> internalNode = AsInternal(node);
            Node<TKey, TInlineKey>? child = internalNode.ChildAt(NodeSearch<TKey, TValue, TInlineKey>.ChildIndex<TKey, TInlineKey>(internalNode, key));
            if (child is null || !NodeLatch.Validate(ref node.Version, version)) return false;
            node = child;
            if (!NodeLatch.TryRead(ref node.Version, out version)) return false;
        }

        Leaf<TKey, TValue, TInlineKey> leaf = AsLeaf(node);
        LeafPrefetch.Entries(leaf);
        int index = NodeSearch<TKey, TValue, TInlineKey>.FindExact<TKey, TInlineKey>(leaf, key);
        if (index < 0) return NodeLatch.Validate(ref leaf.Version, version);
        if (!NodeLatch.TryUpgrade(ref leaf.Version, version)) return false;
        value = leaf.EntryAt(index).Value;
        NodeOps<TKey, TValue, TInlineKey>.RemoveEntry(leaf, index);
        NodeLatch.Unlock(ref leaf.Version);
        removed = true;
        return true;
    }

    /// <summary>Splits a full internal node read under <paramref name="version"/>, locking it and its parent; does nothing if either changed.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SplitInternal(InternalNode<TKey, TInlineKey>? parent, long parentVersion, int parentEdge, InternalNode<TKey, TInlineKey> node, long version)
    {
        if (!TryLockPair(parent, parentVersion, node, version)) return;

        InternalNode<TKey, TInlineKey> right = NodeOps<TKey, TValue, TInlineKey>.SplitInternal(node, Capacity / 2, out TKey separator);
        Publish(parent, parentEdge, node, separator, right);
        NodeLatch.Unlock(ref node.Version);
        if (parent is not null) NodeLatch.Unlock(ref parent.Version);
    }

    /// <summary>Splits a full leaf read under <paramref name="version"/> for an insertion at <paramref name="index"/>, locking it and its parent.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SplitLeaf(InternalNode<TKey, TInlineKey>? parent, long parentVersion, int parentEdge, Leaf<TKey, TValue, TInlineKey> leaf, long version, int index)
    {
        if (!TryLockPair(parent, parentVersion, leaf, version)) return;

        Leaf<TKey, TValue, TInlineKey> right = NodeOps<TKey, TValue, TInlineKey>.SplitLeaf(leaf, NodeOps<TKey, TValue, TInlineKey>.SplitPoint(index));
        Publish(parent, parentEdge, leaf, right.EntryAt(0).Key, right);
        NodeLatch.Unlock(ref leaf.Version);
        if (parent is not null) NodeLatch.Unlock(ref parent.Version);
    }

    /// <summary>Locks a node and its parent, both read under the given versions; the root has no parent and must still be the root.</summary>
    private bool TryLockPair(InternalNode<TKey, TInlineKey>? parent, long parentVersion, Node<TKey, TInlineKey> node, long version)
    {
        if (parent is not null && !NodeLatch.TryUpgrade(ref parent.Version, parentVersion)) return false;
        if (!NodeLatch.TryUpgrade(ref node.Version, version))
        {
            if (parent is not null) NodeLatch.Unlock(ref parent.Version);
            return false;
        }

        if (parent is null && node != _root)
        {
            NodeLatch.Unlock(ref node.Version);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Fits the prefixes of both halves of a split to their fences, then links the right half into the parent,
    /// or under a new root. The caller holds the locks of the node and its parent.
    /// </summary>
    /// <remarks>
    /// Fences come from the locked parent only. A half on a border of its parent takes the prefix of the parent,
    /// which every key below the parent shares; a half of the root takes the shared prefix.
    /// </remarks>
    private void Publish(InternalNode<TKey, TInlineKey>? parent, int edge, Node<TKey, TInlineKey> left, TKey separator, Node<TKey, TInlineKey> right)
    {
        // A split under a running shrink takes its target, so that no border node it makes outlasts the shrink longer.
        int sharedLength = Math.Min(_shared?.Length ?? 0, _shrinkingTo);
        if (parent is null)
        {
            NodeOps<TKey, TValue, TInlineKey>.GrowPrefix(left, false, default!, true, separator, sharedLength);
            NodeOps<TKey, TValue, TInlineKey>.GrowPrefix(right, true, separator, false, default!, sharedLength);
            InternalNode<TKey, TInlineKey> root = new(Math.Min(sharedLength, left.PrefixLength));
            root.ChildAt(0) = left;
            NodeOps<TKey, TValue, TInlineKey>.InsertSeparator(root, 0, separator, right);
            _root = root;
            return;
        }

        int parentPrefix = parent.PrefixLength;
        bool hasLow = edge > 0;
        bool hasHigh = edge < parent.Count;
        TKey low = hasLow ? parent.KeyAt(edge - 1) : default!;
        TKey high = hasHigh ? parent.KeyAt(edge) : default!;
        NodeOps<TKey, TValue, TInlineKey>.GrowPrefix(left, hasLow, low, true, separator, parentPrefix);
        NodeOps<TKey, TValue, TInlineKey>.GrowPrefix(right, true, separator, hasHigh, high, parentPrefix);
        NodeOps<TKey, TValue, TInlineKey>.InsertSeparator(parent, edge, separator, right);
    }

    /// <summary>Makes the shared prefix what <paramref name="key"/> shares with every key, and fits the borders of the tree to it first.</summary>
    /// <remarks>
    /// The borders shrink before the shorter prefix is published, so a search that sees it finds the borders fitted.
    /// Writers lock nodes without waiting, and restart instead, so this walk is the only one that waits for locks.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ShrinkSharedPrefix(in TKey key)
    {
        lock (_sharedLock)
        {
            SharedPrefix? shared = _shared;
            if (shared is null)
            {
                // The root leaf has prefix 0, at most any shared prefix.
                _shared = new SharedPrefix(key, Math.Min(TInlineKey.Length(key), MaxSharedLength));
                return;
            }

            int length = NodeSearch<TKey, TValue, TInlineKey>.CommonPrefix(key, shared.Key, shared.Length);
            if (length >= shared.Length) return;

            _shrinkingTo = length;
            FitBorder(length, left: true);
            FitBorder(length, left: false);
            _shared = new SharedPrefix(shared.Key, length);
            _shrinkingTo = int.MaxValue;
        }
    }

    private void FitBorder(int length, bool left)
    {
        while (true)
        {
            Node<TKey, TInlineKey> node = _root;
            NodeLatch.Lock(ref node.Version);
            if (node != _root)
            {
                NodeLatch.Unlock(ref node.Version);
                continue;
            }

            while (true)
            {
                if (node.PrefixLength > length) NodeSearch<TKey, TValue, TInlineKey>.SetPrefix(node, length);
                if (node.IsLeaf)
                {
                    NodeLatch.Unlock(ref node.Version);
                    return;
                }

                Node<TKey, TInlineKey> child = AsInternal(node).ChildAt(left ? 0 : node.Count);
                NodeLatch.Lock(ref child.Version);
                NodeLatch.Unlock(ref node.Version);
                node = child;
            }
        }
    }

    private Leaf<TKey, TValue, TInlineKey>? FirstLeaf()
    {
        SpinWait spin = default;
        while (true)
        {
            Node<TKey, TInlineKey> node = _root;
            if (!NodeLatch.TryRead(ref node.Version, out long version) || node != _root)
            {
                spin.SpinOnce(sleep1Threshold: -1);
                continue;
            }

            bool valid = true;
            while (!node.IsLeaf)
            {
                Node<TKey, TInlineKey>? child = AsInternal(node).ChildAt(0);
                if (child is null || !NodeLatch.Validate(ref node.Version, version) || !NodeLatch.TryRead(ref child.Version, out version))
                {
                    valid = false;
                    break;
                }

                node = child;
            }

            if (valid) return AsLeaf(node);
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>Starts an enumeration at the first key at least <paramref name="start"/>.</summary>
    private Enumerator EnumeratorFrom(in TKey start)
    {
        SharedPrefix? shared = _shared;
        if (shared is { Length: > 0 } && NodeSearch<TKey, TValue, TInlineKey>.CommonPrefix(start, shared.Key, shared.Length) < shared.Length)
        {
            // The start differs from every key within the shared prefix, so it is below or above all of them.
            return new Enumerator(this, hasStart: false, default!, empty: TInlineKey.Compare(start, shared.Key) > 0);
        }

        return new Enumerator(this, hasStart: true, start, empty: false);
    }

    /// <summary>Finds the leaf that holds <paramref name="start"/> or would; the start shares the shared prefix.</summary>
    private Leaf<TKey, TValue, TInlineKey> LeafOf(in TKey start)
    {

        SpinWait spin = default;
        while (true)
        {
            Node<TKey, TInlineKey> node = _root;
            if (NodeLatch.TryRead(ref node.Version, out long version) && node == _root)
            {
                bool valid = true;
                while (!node.IsLeaf)
                {
                    InternalNode<TKey, TInlineKey> internalNode = AsInternal(node);
                    Node<TKey, TInlineKey>? child = internalNode.ChildAt(NodeSearch<TKey, TValue, TInlineKey>.ChildIndex<TKey, TInlineKey>(internalNode, start));
                    if (child is null || !NodeLatch.Validate(ref node.Version, version) || !NodeLatch.TryRead(ref child.Version, out version))
                    {
                        valid = false;
                        break;
                    }

                    node = child;
                }

                if (valid) return AsLeaf(node);
            }

            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Leaf<TKey, TValue, TInlineKey> AsLeaf(Node<TKey, TInlineKey> node) => Unsafe.As<Leaf<TKey, TValue, TInlineKey>>(node);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static InternalNode<TKey, TInlineKey> AsInternal(Node<TKey, TInlineKey> node) => Unsafe.As<InternalNode<TKey, TInlineKey>>(node);

    /// <summary>What every key shares: the first <see cref="Length"/> units of <see cref="Key"/>. Replaced, never changed.</summary>
    private sealed class SharedPrefix(TKey key, int length)
    {
        public readonly TKey Key = key;
        public readonly int Length = length;
    }

    /// <summary>Enumerates entries in key order, copying each entry under validation of its leaf.</summary>
    /// <remarks>
    /// <para>
    /// A validation is one load of the version, which costs less than to copy entries in chunks: a chunk makes the
    /// enumerator large, and each enumeration then clears and copies it. When a writer changes the leaf, the enumeration
    /// finds the first key after the last one it returned again.
    /// </para>
    /// <para>
    /// No method takes the address of the enumerator, so the JIT keeps its fields in registers: the searches that
    /// find a position again are static and return the position.
    /// </para>
    /// </remarks>
    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly ConcurrentBBTreeMap<TKey, TValue, TInlineKey> _map;
        private readonly bool _hasStart;
        private readonly TKey _start;
        private Leaf<TKey, TValue, TInlineKey>? _leaf;
        private long _version;
        private int _index;
        private int _count;
        private int _entered;
        private bool _returned;
        private Entry<TKey, TValue> _current;

        internal Enumerator(ConcurrentBBTreeMap<TKey, TValue, TInlineKey> map, bool hasStart, TKey start, bool empty)
        {
            _map = map;
            _hasStart = hasStart;
            _start = start;
            if (empty) return;
            MoveTo(hasStart ? Find(map, start, after: false) : First(map));
        }

        /// <inheritdoc/>
        public readonly KeyValuePair<TKey, TValue> Current
        {
            // An entry is laid out as the pair is: its key, then its value.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.As<Entry<TKey, TValue>, KeyValuePair<TKey, TValue>>(ref Unsafe.AsRef(in _current));
        }

        readonly object IEnumerator.Current => Current;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (_index < _count)
            {
                Leaf<TKey, TValue, TInlineKey> leaf = _leaf!;
                // A torn read of an order names a slot of the leaf all the same, and fails validation.
                Entry<TKey, TValue> entry = leaf.EntryAt(_index);
                if (NodeLatch.Validate(ref leaf.Version, _version))
                {
                    _current = entry;
                    _index++;
                    return true;
                }
            }

            return MoveNextSlow();
        }

        /// <summary>Returns this enumerator, so that <c>foreach</c> can run over it.</summary>
        public readonly Enumerator GetEnumerator() => this;

        /// <inheritdoc/>
        public void Reset() => throw new NotSupportedException();

        /// <inheritdoc/>
        public readonly void Dispose() { }

        /// <summary>Moves to the leaves after the current one, or finds the position again after a writer changed the leaf.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MoveNextSlow()
        {
            // The fast path returns entries without noting it: an index past the entry index of the leaf shows it.
            if (_index > _entered) _returned = true;
            while (_leaf is not null)
            {
                Leaf<TKey, TValue, TInlineKey> leaf = _leaf;
                if (_index < _count)
                {
                    Entry<TKey, TValue> entry = leaf.EntryAt(_index);
                    if (NodeLatch.Validate(ref leaf.Version, _version))
                    {
                        _current = entry;
                        _index++;
                        _returned = true;
                        return true;
                    }
                }
                else
                {
                    Leaf<TKey, TValue, TInlineKey>? next = leaf.Next;
                    if (NodeLatch.Validate(ref leaf.Version, _version))
                    {
                        MoveTo(Enter(next, 0));
                        continue;
                    }
                }

                MoveTo(_returned ? Find(_map, _current.Key, after: true) : _hasStart ? Find(_map, _start, after: false) : First(_map));
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MoveTo(in Position position)
        {
            _leaf = position.Leaf;
            _version = position.Version;
            _index = _entered = position.Index;
            _count = position.Count;
        }

        /// <summary>Starts reading <paramref name="leaf"/> at <paramref name="index"/>, under the version it has now.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Position Enter(Leaf<TKey, TValue, TInlineKey>? leaf, int index)
        {
            // A missing leaf has no entries, so the fast path of MoveNext reads no leaf.
            if (leaf is null) return default;
            LeafPrefetch.Start(leaf.Next);
            long version;
            SpinWait spin = default;
            while (!NodeLatch.TryRead(ref leaf.Version, out version)) spin.SpinOnce(sleep1Threshold: -1);
            return new Position(leaf, version, index, Math.Clamp(leaf.Count, 0, Capacity));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Position First(ConcurrentBBTreeMap<TKey, TValue, TInlineKey> map) => Enter(map.FirstLeaf(), 0);

        /// <summary>Finds the first key at least <paramref name="key"/>, or after it; the key shares the shared prefix.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Position Find(ConcurrentBBTreeMap<TKey, TValue, TInlineKey> map, TKey key, bool after)
        {
            while (true)
            {
                Position position = Enter(map.LeafOf(key), 0);
                Leaf<TKey, TValue, TInlineKey> leaf = position.Leaf!;
                int index = NodeSearch<TKey, TValue, TInlineKey>.Find<TKey, TInlineKey>(leaf, key);
                int start = Math.Min(index >= 0 ? (after ? index + 1 : index) : ~index, position.Count);
                if (NodeLatch.Validate(ref leaf.Version, position.Version)) return position with { Index = start };
            }
        }

        private readonly record struct Position(Leaf<TKey, TValue, TInlineKey>? Leaf, long Version, int Index, int Count);
    }

    /// <summary>The entries from a start key on, in key order.</summary>
    public readonly struct Range : IEnumerable<KeyValuePair<TKey, TValue>>
    {
        private readonly ConcurrentBBTreeMap<TKey, TValue, TInlineKey> _map;
        private readonly TKey _start;

        internal Range(ConcurrentBBTreeMap<TKey, TValue, TInlineKey> map, TKey start)
        {
            _map = map;
            _start = start;
        }

        /// <summary>Gets an enumerator of the entries of the range in key order.</summary>
        public Enumerator GetEnumerator() => _map.EnumeratorFrom(_start);

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
