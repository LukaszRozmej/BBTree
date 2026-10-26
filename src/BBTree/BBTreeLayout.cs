// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

namespace BBTree;

/// <summary>Constants shared by all maps.</summary>
public static class BBTreeLayout
{
    /// <summary>The number of keys that one node holds.</summary>
    public const int NodeCapacity = 48;

    /// <summary>The fewest keys that a node below the root holds after a removal.</summary>
    internal const int MinNodeLength = NodeCapacity / 8;

    /// <summary>The maximum height of a tree. A tree of minimal nodes reaches it only past 2^48 keys.</summary>
    internal const int MaxHeight = 24;
}
