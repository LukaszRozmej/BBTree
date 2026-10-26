// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace BBTree;

/// <summary>The optimistic lock of a node: <see cref="Node{TKey, TSlots}.Version"/>, bit 0 set while a writer holds it.</summary>
/// <remarks>
/// <para>
/// A reader takes the version, reads the node, and validates that the version did not change; writes lock the node,
/// change it, and unlock it with the next version, so a read that overlapped a write fails validation and restarts.
/// Reads cause no writes to shared cache lines (optimistic lock coupling, Leis et al., DaMoN 2016).
/// </para>
/// <para>
/// A node that a reader reaches after a writer unlinked it stays valid memory, as the GC keeps it while the reader
/// holds it; the reader validates against a version that the writer changed and restarts.
/// </para>
/// </remarks>
internal static class NodeLatch
{
    private const long Locked = 1;

    /// <summary>Takes the version of <paramref name="version"/> to read under, or fails while a writer holds it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryRead(ref long version, out long observed)
    {
        observed = Volatile.Read(ref version);
        return (observed & Locked) == 0;
    }

    /// <summary>Whether no writer changed the node since <paramref name="observed"/>: the reads in between were consistent.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Validate(ref long version, long observed)
    {
        // x86 keeps loads in order; elsewhere, the reads of the node must complete before the version is read again.
        if (!X86Base.IsSupported) Interlocked.MemoryBarrier();
        return Volatile.Read(ref version) == observed;
    }

    /// <summary>Locks a node read under <paramref name="observed"/>, failing if a writer changed it since.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryUpgrade(ref long version, long observed) =>
        Interlocked.CompareExchange(ref version, observed | Locked, observed) == observed;

    /// <summary>Locks a node, waiting while another writer holds it.</summary>
    public static void Lock(ref long version)
    {
        SpinWait spin = default;
        while (true)
        {
            long observed = Volatile.Read(ref version);
            if ((observed & Locked) == 0 && TryUpgrade(ref version, observed)) return;
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>Unlocks a node with the next version.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unlock(ref long version) => Volatile.Write(ref version, version + 1);
}
