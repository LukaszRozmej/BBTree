// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
            case BBTreeMapTests.KeyKind.UInt64: RunDifferential<ulong, IntegerKey<ulong>>(KeySources.UInt64, Comparer<ulong>.Default, seed); break;
            case BBTreeMapTests.KeyKind.Int64: RunDifferential<long, IntegerKey<long>>(KeySources.Int64, Comparer<long>.Default, seed); break;
            case BBTreeMapTests.KeyKind.UInt128: RunDifferential<UInt128, IntegerKey<UInt128>>(KeySources.UInt128, Comparer<UInt128>.Default, seed); break;
            case BBTreeMapTests.KeyKind.String: RunDifferential<string, StringKey>(KeySources.String, StringComparer.Ordinal, seed); break;
            case BBTreeMapTests.KeyKind.ByteArray: RunDifferential<byte[], ByteArrayKey>(KeySources.ByteArray, KeySources.ByteArrayComparer, seed); break;
            case BBTreeMapTests.KeyKind.Hash: RunDifferential<Bytes32, BytesKey<Bytes32>>(KeySources.Hash, KeySources.HashComparer, seed); break;
            case BBTreeMapTests.KeyKind.Int32: RunDifferential<int, IntegerKey<int>>(KeySources.Int32, Comparer<int>.Default, seed); break;
            case BBTreeMapTests.KeyKind.Int16: RunDifferential<short, IntegerKey<short>>(KeySources.Int16, Comparer<short>.Default, seed); break;
            case BBTreeMapTests.KeyKind.SByte: RunDifferential<sbyte, IntegerKey<sbyte>>(KeySources.SByte, Comparer<sbyte>.Default, seed); break;
            case BBTreeMapTests.KeyKind.Int128: RunDifferential<Int128, IntegerKey<Int128>>(KeySources.Int128, Comparer<Int128>.Default, seed); break;
            case BBTreeMapTests.KeyKind.Double: RunDifferential<double, FloatKey<double>>(KeySources.Double, KeySources.TotalOrder, seed); break;
            case BBTreeMapTests.KeyKind.Comparable: RunDifferential<Person, ComparableKey<Person>>(KeySources.Person, Comparer<Person>.Default, seed); break;
            case BBTreeMapTests.KeyKind.ComparablePrefixed: RunDifferential<Person, ComparableKey<Person, PersonPrefix>>(KeySources.Person, Comparer<Person>.Default, seed); break;
        }
    }

    [Test]
    public void Keeps_every_write_of_concurrent_writers_and_readers([Values(1, 2)] int seed, [Values] bool stringKeys)
    {
        if (stringKeys) RunConcurrent<string, StringKey>(static i => $"key/{i:x}", StringComparer.Ordinal, seed);
        else RunConcurrent<ulong, IntegerKey<ulong>>(static i => SplitMix(i), Comparer<ulong>.Default, seed);
    }

    [Test]
    public async Task Splits_keep_existing_keys_reachable_and_unique()
    {
        const int stableKeys = 2_048;
        const int readers = 4;
        IntegerKey<int>.ConcurrentMap<int> map = new();
        for (int i = 0; i < stableKeys; i++) map[i * 1_024] = i;

        using Barrier start = new(readers + 1);
        int finished = 0;
        Task[] tasks = new Task[readers + 1];
        tasks[0] = Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                for (int offset = 1; offset <= 128; offset++)
                {
                    for (int i = 0; i < stableKeys; i++) map.AddOrUpdate(i * 1_024 + offset, i);
                }
            }
            finally
            {
                Volatile.Write(ref finished, 1);
            }
        });

        for (int reader = 0; reader < readers; reader++)
        {
            int owner = reader;
            tasks[reader + 1] = Task.Run(() =>
            {
                start.SignalAndWait();
                for (int operation = 0; operation < 50_000 || Volatile.Read(ref finished) == 0; operation++)
                {
                    int key = (operation % (stableKeys / readers) * readers + owner) * 1_024;
                    if (map.AddOrUpdate(key, operation)) Assert.Fail($"Existing key {key} was inserted again.");
                    if (!map.TryGetValue(key, out int value) || value != operation) Assert.Fail($"Update of key {key} was lost.");
                    if (map.TryAdd(key, -1)) Assert.Fail($"Existing key {key} was duplicated.");
                    if (operation % 32 == 0)
                    {
                        using ConcurrentBBTreeMap<int, int, IntegerKey<int>>.Enumerator entries = map.EnumerateFrom(key).GetEnumerator();
                        if (!entries.MoveNext() || entries.Current.Key != key) Assert.Fail($"Range no longer starts at existing key {key}.");
                    }
                }
            });
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
        TreeInvariants<int, int, IntegerKey<int>>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength, concurrent: true);
        Assert.That(map.Count, Is.EqualTo(stableKeys * 129));
    }

    [Test]
    [NonParallelizable]
    public async Task Failed_split_callbacks_restore_nodes_and_release_locks([Values] SplitKind kind, [Values] bool transient)
    {
        ComparableKey<int, ThrowingPrefix>.ConcurrentMap<string> map = new();
        int count = kind == SplitKind.RootLeaf ? 48 : 84;
        for (int i = 0; i < count; i++) map[i] = i.ToString();

        int separator = kind == SplitKind.RootLeaf ? 36 : 72;
        if (kind == SplitKind.Internal)
        {
            while (map.Root.Count != BBTreeLayout.NodeCapacity - 1 ||
                ((InternalNode<int, ComparableKey<int, ThrowingPrefix>>)map.Root).ChildAt(map.Root.Count).Count != BBTreeLayout.NodeCapacity)
            {
                map[count] = count.ToString();
                count++;
            }
            separator = ((InternalNode<int, ComparableKey<int, ThrowingPrefix>>)map.Root).KeyAt(BBTreeLayout.NodeCapacity / 2);
        }

        int reordered = kind == SplitKind.Leaf ? 36 : 0;
        Assert.That(map.TryRemove(reordered, out string? removed), Is.True);
        Assert.That(map.TryAdd(reordered, removed!), Is.True);
        KeyValuePair<int, string>[] before = map.ToArray();
        ThrowingPrefix.Key = separator;
        ThrowingPrefix.Remaining = transient ? 1 : int.MaxValue;
        try
        {
            await Task.Run(() =>
            {
                if (transient) Assert.That(map.TryAdd(count, count.ToString()), Is.True);
                else Assert.That(() => map.TryAdd(count, count.ToString()), Throws.InvalidOperationException);

                ThrowingPrefix.Remaining = 0;
                if (!transient)
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(map.ToArray(), Is.EqualTo(before));
                        Assert.That(map.Count, Is.EqualTo(count));
                    }
                    Assert.That(map.TryAdd(count, count.ToString()), Is.True);
                }

                TreeInvariants<int, string, ComparableKey<int, ThrowingPrefix>>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength, concurrent: true);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(map.Count, Is.EqualTo(count + 1));
                    Assert.That(map[count], Is.EqualTo(count.ToString()));
                }
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            ThrowingPrefix.Remaining = 0;
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Failed_prefix_shrink_preserves_searches_and_releases_locks()
    {
        ConcurrentBBTreeMap<int, int, ThrowingInteger> map = new();
        for (int i = 0; i < 120; i++) map[i] = i;
        ThrowingInteger.Fail = true;
        try
        {
            await Task.Run(() =>
            {
                Assert.That(() => map.TryAdd(int.MaxValue, -1), Throws.InvalidOperationException);
                ThrowingInteger.Fail = false;
                for (int i = 0; i < 120; i++) Assert.That(map[i], Is.EqualTo(i));
                Assert.That(map.TryAdd(int.MaxValue, -1), Is.True);
                TreeInvariants<int, int, ThrowingInteger>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength, concurrent: true);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            ThrowingInteger.Fail = false;
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Failed_length_callback_does_not_change_or_lock_the_leaf()
    {
        ConcurrentBBTreeMap<int, int, ThrowingInteger> map = new();
        map[0] = 0;
        map[2] = 2;
        ThrowingInteger.FailLength = true;
        try
        {
            await Task.Run(() =>
            {
                Assert.That(() => map.TryAdd(1, 1), Throws.InvalidOperationException);
                ThrowingInteger.FailLength = false;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(map.Count, Is.EqualTo(2));
                    Assert.That(map.ContainsKey(1), Is.False);
                }
                Assert.That(map.TryAdd(1, 1), Is.True);
                TreeInvariants<int, int, ThrowingInteger>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength, concurrent: true);
                Assert.That(map.Select(static entry => entry.Key), Is.EqualTo(new[] { 0, 1, 2 }));
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            ThrowingInteger.FailLength = false;
        }
    }

    public enum SplitKind
    {
        RootLeaf,
        Leaf,
        Internal,
    }

    private struct ThrowingPrefix : ISortPrefix<int>
    {
        public static int Key;
        public static int Remaining;

        public static ulong Of(in int key)
        {
            if (key == Key && Remaining > 0)
            {
                Remaining--;
                throw new InvalidOperationException("Injected prefix failure.");
            }

            return (ulong)(uint)key << 32;
        }
    }

    [InlineArray(BBTreeLayout.NodeCapacity + BBTreeLayout.NodeCapacity / sizeof(uint))]
    private struct ThrowingInteger : IInlineKey<int>
    {
        private uint _slot;
        public static bool Fail;
        public static bool FailLength;

        public static int Compare(in int x, in int y) => x.CompareTo(y);
        public static int CommonPrefixLength(in int x, in int y) => IntegerKey<int>.CommonPrefixLength(x, y);
        public static int Length(in int key) => FailLength && key == 1 ? throw new InvalidOperationException("Injected length failure.") : sizeof(int);
        public static int FixedLength => sizeof(int);

        public static ulong WindowAt(in int key, int offset)
        {
            if (Fail && key == 72 && offset == 0) throw new InvalidOperationException("Injected window failure.");
            return IntegerKey<int>.WindowAt(key, offset);
        }
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
