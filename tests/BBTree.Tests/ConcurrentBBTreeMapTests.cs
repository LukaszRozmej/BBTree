// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace BBTree.Tests;

[Parallelizable(ParallelScope.All)]
public class ConcurrentBBTreeMapTests
{
    [Test]
    public void Matches_sorted_dictionary_on_one_thread([Values(1, 2)] int seed, [Values] BBTreeMapTests.KeyKind keys)
    {
        switch (keys)
        {
            case BBTreeMapTests.KeyKind.UInt64: RunDifferential<ulong, UInt64InlineKey>(KeySources.UInt64, Comparer<ulong>.Default, seed); break;
            case BBTreeMapTests.KeyKind.Int64: RunDifferential<long, Int64InlineKey>(KeySources.Int64, Comparer<long>.Default, seed); break;
            case BBTreeMapTests.KeyKind.UInt128: RunDifferential<UInt128, UInt128InlineKey>(KeySources.UInt128, Comparer<UInt128>.Default, seed); break;
            case BBTreeMapTests.KeyKind.String: RunDifferential<string, StringInlineKey>(KeySources.String, StringComparer.Ordinal, seed); break;
            case BBTreeMapTests.KeyKind.ByteArray: RunDifferential<byte[], ByteArrayInlineKey>(KeySources.ByteArray, KeySources.ByteArrayComparer, seed); break;
            case BBTreeMapTests.KeyKind.Hash: RunDifferential<Bytes32, BytesInlineKey<Bytes32>>(KeySources.Hash, KeySources.HashComparer, seed); break;
        }
    }

    [Test]
    public void Keeps_every_write_of_concurrent_writers_and_readers([Values(1, 2)] int seed, [Values] bool stringKeys)
    {
        if (stringKeys) RunConcurrent<string, StringInlineKey>(static i => $"key/{i:x}", StringComparer.Ordinal, seed);
        else RunConcurrent<ulong, UInt64InlineKey>(static i => SplitMix(i), Comparer<ulong>.Default, seed);
    }

    private static ulong SplitMix(ulong x)
    {
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EB;
        return x ^ (x >> 31);
    }

    /// <summary>
    /// Writers own disjoint keys and insert, update and remove them; readers check that keys present from the start,
    /// which no writer removes, are always found with their value, and that enumeration stays ordered.
    /// </summary>
    private static void RunConcurrent<TKey, TInlineKey>(Func<ulong, TKey> makeKey, IComparer<TKey> comparer, int seed)
        where TKey : notnull
        where TInlineKey : struct, IInlineKey<TKey>
    {
        const int writers = 4;
        const int readers = 3;
        const int stableKeys = 20_000;
        const int operationsPerWriter = 150_000;
        ConcurrentBBTreeMap<TKey, ulong, TInlineKey> map = new();
        for (ulong i = 0; i < stableKeys; i++) map[makeKey(i * (writers + 1))] = i;

        using CancellationTokenSource writing = new();
        Dictionary<TKey, ulong>[] owned = new Dictionary<TKey, ulong>[writers];
        Task[] writerTasks = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            Random random = new(seed * 31 + writer);
            Dictionary<TKey, ulong> expected = owned[writer] = [];
            for (int op = 0; op < operationsPerWriter; op++)
            {
                TKey key = makeKey((ulong)random.Next(30_000) * (writers + 1) + (ulong)writer + 1);
                if (random.Next(3) == 0)
                {
                    Assert.That(map.TryRemove(key, out _), Is.EqualTo(expected.Remove(key)));
                }
                else
                {
                    ulong value = (ulong)op;
                    Assert.That(map.AddOrUpdate(key, value), Is.EqualTo(expected.TryAdd(key, value)));
                    expected[key] = value;
                }
            }
        })).ToArray();

        Task[] readerTasks = Enumerable.Range(0, readers).Select(reader => Task.Run(() =>
        {
            Random random = new(seed * 17 + reader);
            while (!writing.IsCancellationRequested)
            {
                ulong i = (ulong)random.Next(stableKeys);
                Assert.That(map.TryGetValue(makeKey(i * (writers + 1)), out ulong value), Is.True, "a key that no writer removes stays");
                Assert.That(value, Is.EqualTo(i));
                if (random.Next(500) == 0) AssertOrdered(map.EnumerateFrom(makeKey(i * (writers + 1))).Take(300).Select(static e => e.Key), comparer);
            }
        })).ToArray();

        Task.WaitAll(writerTasks);
        writing.Cancel();
        Task.WaitAll(readerTasks);

        SortedDictionary<TKey, ulong> all = new(comparer);
        for (ulong i = 0; i < stableKeys; i++) all[makeKey(i * (writers + 1))] = i;
        foreach (Dictionary<TKey, ulong> expected in owned)
        {
            foreach ((TKey key, ulong value) in expected) all[key] = value;
        }

        Check(map, all);
    }

    private static void AssertOrdered<TKey>(IEnumerable<TKey> keys, IComparer<TKey> comparer)
    {
        TKey? previous = default;
        bool first = true;
        foreach (TKey key in keys)
        {
            if (!first) Assert.That(comparer.Compare(previous!, key), Is.LessThan(0), "enumeration is ordered");
            previous = key;
            first = false;
        }
    }

    private static void RunDifferential<TKey, TInlineKey>(Func<Random, TKey> nextKey, IComparer<TKey> comparer, int seed)
        where TKey : notnull
        where TInlineKey : struct, IInlineKey<TKey>
    {
        Random random = new(seed);
        ConcurrentBBTreeMap<TKey, int, TInlineKey> map = new();
        SortedDictionary<TKey, int> expected = new(comparer);
        for (int op = 0; op < 60_000; op++)
        {
            TKey key = nextKey(random);
            switch (random.Next(10))
            {
                case < 4:
                    Assert.That(map.AddOrUpdate(key, op), Is.EqualTo(!expected.ContainsKey(key)));
                    expected[key] = op;
                    break;
                case 4:
                    Assert.That(map.TryAdd(key, op), Is.EqualTo(expected.TryAdd(key, op)));
                    break;
                case 5 or 6:
                    bool removed = map.TryRemove(key, out int removedValue);
                    Assert.That(removed, Is.EqualTo(expected.Remove(key, out int expectedValue)));
                    if (removed) Assert.That(removedValue, Is.EqualTo(expectedValue));
                    break;
                case 7:
                    Assert.That(map.EnumerateFrom(key).Take(20).Select(static e => e.Key),
                        Is.EqualTo(expected.Keys.Where(k => comparer.Compare(k, key) >= 0).Take(20)));
                    break;
                default:
                    Assert.That(map.TryGetValue(key, out int value), Is.EqualTo(expected.TryGetValue(key, out int expectedFound)));
                    Assert.That(value, Is.EqualTo(expectedFound));
                    break;
            }

            if (op % 10_000 == 0) Check(map, expected);
        }

        Check(map, expected);
    }

    private static void Check<TKey, TValue, TInlineKey>(ConcurrentBBTreeMap<TKey, TValue, TInlineKey> map, SortedDictionary<TKey, TValue> expected)
        where TKey : notnull
        where TInlineKey : struct, IInlineKey<TKey>
    {
        TreeInvariants<TKey, TValue, TInlineKey>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength, concurrent: true);
        Assert.That(map.Count, Is.EqualTo(expected.Count));
        Assert.That(map.ToArray(), Is.EqualTo(expected.ToArray()));
    }
}
