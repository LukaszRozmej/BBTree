// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace BBTree;

/// <summary>Asks the CPU to load the first lines of a leaf that an enumeration reaches next.</summary>
/// <remarks>
/// Leaves are allocated in the order they split, so the next leaf lies anywhere in memory and the hardware prefetcher
/// cannot guess it. Leaves link to the next one, so an enumeration that enters a leaf knows the one after it, and the
/// leaf it reads meanwhile hides most of the latency. The CPU streams the rest of the leaf once its first lines arrive.
/// </remarks>
internal static class LeafPrefetch
{
    private const int Lines = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Start(object? leaf)
    {
        if (!Sse.IsSupported || leaf is null) return;
        // The reference is the address of the object; a GC that moves it in between only makes the hint stale.
        byte* address = (byte*)Unsafe.As<object, nint>(ref leaf);
        for (int line = 0; line < Lines; line++) Sse.Prefetch0(address + 64 * line);
    }

    /// <summary>Asks the CPU to load the live entries of a leaf that an insert or a removal shifts.</summary>
    /// <remarks>
    /// A search for the key reads only the inline keys, so the entries a shift moves are still in memory. Issued as
    /// the leaf is reached, these loads run beside those of the inline keys, and the shift finds the entries in cache.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Entries<TKey, TValue, TSlots>(Leaf<TKey, TValue, TSlots> leaf) where TSlots : struct, IInlineKey<TKey>
    {
        if (!Sse.IsSupported) return;
        byte* first = (byte*)Unsafe.AsPointer(ref leaf.SlotAt(0));
        byte* end = first + BBTreeLayout.NodeCapacity * Unsafe.SizeOf<Entry<TKey, TValue>>();
        for (byte* line = first; line < end; line += 64) Sse.Prefetch0(line);
    }
}
