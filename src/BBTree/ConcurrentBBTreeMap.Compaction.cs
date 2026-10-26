// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System.Collections.Generic;
using System.Threading;

namespace BBTree;

public partial class ConcurrentBBTreeMap<TKey, TValue, TInlineKey>
{
    private int _leaves = 1;
    private int _emptyLeaves = 1;

    private bool NeedsCompaction
    {
        get
        {
            int empty = Volatile.Read(ref _emptyLeaves);
            int leaves = Volatile.Read(ref _leaves);
            return leaves > 1 && (empty == leaves || (empty >= 32 && empty >= leaves / 2));
        }
    }

    /// <summary>Reclaims empty leaves when enough have accumulated to amortize rebuilding the tree.</summary>
    /// <remarks>Retired nodes stay locked; surviving leaves unlock with new versions after the replacement root is published.</remarks>
    private void TryCompact()
    {
        List<Node<TKey, TInlineKey>> locked = new();
        if (!_sharedLock.TryEnter()) return;
        bool published = false;
        try
        {
            if (!NeedsCompaction) return;
            // Holding the root prevents splits above it; descendants are locked in the same order as prefix fitting.
            while (true)
            {
                Node<TKey, TInlineKey> root = _root;
                locked.Add(root);
                NodeLatch.Lock(ref root.Version);
                if (root == _root)
                {
                    LockChildren(root, locked);
                    break;
                }

                NodeLatch.Unlock(ref root.Version);
                locked.Clear();
            }

            int sharedLength = _shared?.Length ?? 0;
            List<(Node<TKey, TInlineKey> Node, TKey First)> leaves = [];
            foreach (Node<TKey, TInlineKey> node in locked)
            {
                if (!node.IsLeaf || node.Count == 0) continue;
                Leaf<TKey, TValue, TInlineKey> leaf = AsLeaf(node);
                leaves.Add((leaf, leaf.EntryAt(0).Key));
            }

            // Removing empty neighbours can widen a leaf's fences. Shorter prefixes also remain valid if a callback fails.
            for (int i = 0; i < leaves.Count; i++)
            {
                int prefix = NodeOps<TKey, TValue, TInlineKey>.FencePrefix(i > 0, leaves[i].First,
                    i + 1 < leaves.Count, i + 1 < leaves.Count ? leaves[i + 1].First : default!, sharedLength);
                if (leaves[i].Node.PrefixLength > prefix) NodeSearch<TKey, TValue, TInlineKey>.SetPrefix(leaves[i].Node, prefix);
            }

            Node<TKey, TInlineKey> replacement = BuildRouting(leaves, sharedLength);
            for (int i = 0; i < leaves.Count; i++) AsLeaf(leaves[i].Node).Next = i + 1 < leaves.Count ? AsLeaf(leaves[i + 1].Node) : null;
            _leaves = leaves.Count == 0 ? 1 : leaves.Count;
            _emptyLeaves = leaves.Count == 0 ? 1 : 0;
            _root = replacement;
            published = true;
        }
        catch when (!ComparesNeverThrow)
        {
            // Maintenance must not turn a committed removal into a failure when a custom key callback throws.
        }
        finally
        {
            foreach (Node<TKey, TInlineKey> node in locked)
            {
                if (!published || (node.IsLeaf && node.Count > 0)) NodeLatch.Unlock(ref node.Version);
            }

            _sharedLock.Exit();
        }
    }

    private static Node<TKey, TInlineKey> BuildRouting(List<(Node<TKey, TInlineKey> Node, TKey First)> level, int sharedLength)
    {
        if (level.Count == 0) return new Leaf<TKey, TValue, TInlineKey>(sharedLength);
        while (level.Count > 1)
        {
            int groups = (level.Count + Capacity) / (Capacity + 1);
            List<(Node<TKey, TInlineKey> Node, TKey First)> parents = new(groups);
            int offset = 0;
            for (int group = 0; group < groups; group++)
            {
                int children = (level.Count - offset + groups - group - 1) / (groups - group);
                InternalNode<TKey, TInlineKey> parent = new(sharedLength);
                parent.ChildAt(0) = level[offset].Node;
                for (int i = 1; i < children; i++) NodeOps<TKey, TValue, TInlineKey>.InsertSeparator(parent, i - 1, level[offset + i].First, level[offset + i].Node);
                parents.Add((parent, level[offset].First));
                offset += children;
            }

            level = parents;
        }

        return level[0].Node;
    }

    private static void LockChildren(Node<TKey, TInlineKey> node, List<Node<TKey, TInlineKey>> locked)
    {
        if (node.IsLeaf) return;
        InternalNode<TKey, TInlineKey> parent = AsInternal(node);
        for (int i = 0; i <= parent.Count; i++)
        {
            Node<TKey, TInlineKey> child = parent.ChildAt(i);
            locked.Add(child);
            NodeLatch.Lock(ref child.Version);
            LockChildren(child, locked);
        }
    }
}
