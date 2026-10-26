// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System.Runtime.CompilerServices;

namespace BBTree;

/// <summary>The way down from the root: the internal node at each depth and the edge taken from it.</summary>
/// <remarks>Nodes keep no parent pointers, so splits, merges and fences read their ancestors from here.</remarks>
internal struct NodePath<TKey, TSlots> where TSlots : struct, IInlineKey<TKey>
{
    public PathNodes<TKey, TSlots> Nodes;
    public PathEdges Edges;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int depth, InternalNode<TKey, TSlots> node, int edge)
    {
        Nodes[depth] = node;
        Edges[depth] = edge;
    }

    /// <summary>Gets the fences of the node at <paramref name="depth"/>: the nearest separators beside the path on each side.</summary>
    /// <remarks>A missing fence is a border of the tree.</remarks>
    public readonly void Fences(int depth, out bool hasLow, out TKey low, out bool hasHigh, out TKey high)
    {
        hasLow = false;
        hasHigh = false;
        low = default!;
        high = default!;
        for (int i = depth - 1; i >= 0; i--)
        {
            InternalNode<TKey, TSlots> node = Nodes[i]!;
            int edge = Edges[i];
            if (!hasLow && edge > 0)
            {
                low = node.KeyAt(edge - 1);
                hasLow = true;
            }

            if (!hasHigh && edge < node.Count)
            {
                high = node.KeyAt(edge);
                hasHigh = true;
            }

            if (hasLow && hasHigh) return;
        }
    }
}

[InlineArray(BBTreeLayout.MaxHeight)]
internal struct PathNodes<TKey, TSlots> where TSlots : struct, IInlineKey<TKey>
{
    private InternalNode<TKey, TSlots>? _node;
}

[InlineArray(BBTreeLayout.MaxHeight)]
internal struct PathEdges
{
    private int _edge;
}
