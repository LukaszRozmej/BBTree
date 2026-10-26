// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using BBTree;

internal static class Churn
{
    public static void Run(int repetitions)
    {
        for (int warmup = 0; warmup < 3; warmup++)
        {
            Measure(rounds: 100);
            Thread.Sleep(200);
        }
        for (int repetition = 0; repetition < repetitions; repetition++)
        {
            var result = Measure(rounds: 100);
            Console.WriteLine($"churn int32, live window 10000, 100 rounds: {result.Nanoseconds:F1} ns/op; " +
                $"allocated {result.Allocated} B; retained with 10000 keys {result.LiveBytes} B; retained empty {result.EmptyBytes} B");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (double Nanoseconds, long Allocated, long LiveBytes, long EmptyBytes) Measure(int rounds)
    {
        const int window = 10_000;
        long before = GC.GetTotalMemory(forceFullCollection: true);
        IntegerKey<int>.ConcurrentMap<int> map = new();
        for (int i = 0; i < window; i++) map[i] = i;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int round = 1; round <= rounds; round++)
        {
            for (int i = 0; i < window; i++) map[round * window + i] = i;
            for (int i = 0; i < window; i++)
            {
                if (!map.TryRemove((round - 1) * window + i, out int value) || value != i) throw new InvalidOperationException();
            }
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (map.Count != window) throw new InvalidOperationException();
        for (int i = 0; i < window; i++)
        {
            if (map[rounds * window + i] != i) throw new InvalidOperationException();
        }

        long liveBytes = GC.GetTotalMemory(forceFullCollection: true) - before;
        for (int i = 0; i < window; i++) map.TryRemove(rounds * window + i, out _);
        if (map.Count != 0) throw new InvalidOperationException();
        long emptyBytes = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(map);
        return (elapsed * (1e9 / Stopwatch.Frequency) / (rounds * 2L * window), allocated, liveBytes, emptyBytes);
    }
}
