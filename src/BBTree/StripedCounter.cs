// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace BBTree;

/// <summary>A count that threads change in cells of their own, so that writers on different cores share no cache line.</summary>
internal sealed class StripedCounter
{
    private readonly PaddedCount[] _cells = new PaddedCount[(int)BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount * 2)];

    public void Add(long delta) =>
        Interlocked.Add(ref _cells[Environment.CurrentManagedThreadId & (_cells.Length - 1)].Value, delta);

    public long Sum()
    {
        long sum = 0;
        for (int i = 0; i < _cells.Length; i++) sum += Volatile.Read(ref _cells[i].Value);
        return sum;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedCount
    {
        [FieldOffset(64)]
        public long Value;
    }
}
