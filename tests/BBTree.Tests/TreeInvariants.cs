// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System.Collections.Generic;
using NUnit.Framework;

namespace BBTree.Tests;

/// <summary>Checks the structure of a tree: order, fences, prefixes, inline keys, depth, the leaf chain and the count.</summary>
internal static class TreeInvariants<TKey, TValue, TInlineKey> where TInlineKey : struct, IInlineKey<TKey>
{
    /// <param name="concurrent">
    /// Checks a concurrent map, whose removals leave nodes below their minimum, and whose borders may keep a prefix
    /// shorter than the shared prefix.
    /// </param>
    public static void Check(Node<TKey, TInlineKey>? root, int count, TKey sharedKey, int sharedLength, bool concurrent = false)
    {
        if (root is null)
        {
            Assert.That(count, Is.Zero, "an empty tree has no entries");
            return;
        }

        List<Leaf<TKey, TValue, TInlineKey>> leaves = [];
        int leafDepth = -1;
        CheckNode(root, isRoot: true, depth: 0, ref leafDepth, false, default!, false, default!, sharedKey, sharedLength, concurrent, leaves);

        int total = 0;
        for (int i = 0; i < leaves.Count; i++)
        {
            total += leaves[i].Count;
            Leaf<TKey, TValue, TInlineKey>? expectedNext = i + 1 < leaves.Count ? leaves[i + 1] : null;
            Assert.That(leaves[i].Next, Is.SameAs(expectedNext), "leaves link in key order");
        }

        Assert.That(total, Is.EqualTo(count), "the leaves hold every entry");
    }

    private static void CheckNode(
        Node<TKey, TInlineKey> node, bool isRoot, int depth, ref int leafDepth,
        bool hasLow, TKey low, bool hasHigh, TKey high, TKey sharedKey, int sharedLength, bool concurrent,
        List<Leaf<TKey, TValue, TInlineKey>> leaves)
    {
        int minCount = concurrent ? (node.IsLeaf ? 0 : 1) : isRoot ? 1 : BBTreeLayout.MinNodeLength;
        Assert.That(node.Count, Is.InRange(minCount, BBTreeLayout.NodeCapacity));

        int fencePrefix = hasLow && hasHigh ? NodeSearch<TKey, TValue, TInlineKey>.CommonPrefix(low, high, int.MaxValue) : sharedLength;
        Assert.That(node.PrefixLength, Is.LessThanOrEqualTo(fencePrefix), "a prefix fits the fences");
        if (!concurrent && (!hasLow || !hasHigh)) Assert.That(node.PrefixLength, Is.EqualTo(sharedLength), "a border takes the shared prefix");

        for (int i = 0; i < BBTreeLayout.NodeCapacity; i++)
        {
            ulong inlineKey = InlineSlots<TKey, TInlineKey>.Get(node.IsLeaf, ref node.Slots, i);
            if (i < node.Count)
            {
                TKey key = NodeSearch<TKey, TValue, TInlineKey>.KeyAt(node, i);
                Assert.That(inlineKey, Is.EqualTo(InlineSlots<TKey, TInlineKey>.Of(node.IsLeaf, key, node.PrefixLength)), "inline keys follow keys");
                Assert.That(NodeSearch<TKey, TValue, TInlineKey>.CommonPrefix(key, sharedKey, sharedLength), Is.EqualTo(sharedLength), "keys share the shared prefix");
                if (hasLow) Assert.That(TInlineKey.Compare(key, low), Is.GreaterThanOrEqualTo(0), "keys are at least the low fence");
                if (hasHigh) Assert.That(TInlineKey.Compare(key, high), Is.LessThan(0), "keys are below the high fence");
                if (i > 0) Assert.That(TInlineKey.Compare(NodeSearch<TKey, TValue, TInlineKey>.KeyAt(node, i - 1), key), Is.LessThan(0), "keys are sorted");
            }
            else
            {
                Assert.That(inlineKey, Is.EqualTo(ulong.MaxValue >> (64 - 8 * InlineSlots<TKey, TInlineKey>.Width(node.IsLeaf))), "free slots hold the greatest inline key");
            }
        }

        if (node.IsLeaf)
        {
            if (leafDepth < 0) leafDepth = depth;
            Assert.That(depth, Is.EqualTo(leafDepth), "all leaves are at one depth");
            Leaf<TKey, TValue, TInlineKey> leaf = (Leaf<TKey, TValue, TInlineKey>)node;
            if (Leaf<TKey, TValue, TInlineKey>.IsOrdered) CheckOrder(leaf);
            leaves.Add(leaf);
            return;
        }

        InternalNode<TKey, TInlineKey> internalNode = (InternalNode<TKey, TInlineKey>)node;
        for (int i = 0; i <= node.Count; i++)
        {
            Node<TKey, TInlineKey> child = internalNode.ChildAt(i);
            Assert.That(child, Is.Not.Null, "edges up to the count hold children");
            bool childHasLow = i > 0 || hasLow;
            TKey childLow = i > 0 ? internalNode.KeyAt(i - 1) : low;
            bool childHasHigh = i < node.Count || hasHigh;
            TKey childHigh = i < node.Count ? internalNode.KeyAt(i) : high;
            CheckNode(child, false, depth + 1, ref leafDepth, childHasLow, childLow, childHasHigh, childHigh, sharedKey, sharedLength, concurrent, leaves);
        }

        for (int i = node.Count + 1; i <= BBTreeLayout.NodeCapacity; i++)
        {
            Assert.That(internalNode.ChildSpan[i], Is.Null, "edges past the count are empty");
        }
    }

    private static void CheckOrder(Leaf<TKey, TValue, TInlineKey> leaf)
    {
        bool[] named = new bool[BBTreeLayout.NodeCapacity];
        for (int i = 0; i < leaf.Count; i++)
        {
            int slot = leaf.OrderAt(i);
            Assert.That(slot, Is.LessThan(leaf.Count), "the order names slots below the count");
            Assert.That(named[slot], Is.False, "the order names each slot once");
            named[slot] = true;
        }

        for (int slot = leaf.Count; slot < BBTreeLayout.NodeCapacity; slot++)
        {
            Assert.That(leaf.SlotAt(slot).Key, Is.EqualTo(default(TKey)), "free slots hold no keys");
        }
    }
}
