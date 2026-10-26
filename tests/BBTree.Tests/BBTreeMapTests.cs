// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace BBTree.Tests;

[Parallelizable(ParallelScope.All)]
public class BBTreeMapTests
{
    [Test]
    public void Matches_sorted_dictionary_on_random_operations([Values(1, 2, 3)] int seed, [Values] KeyKind keys)
    {
        switch (keys)
        {
            case KeyKind.UInt64: RunDifferential<ulong, UInt64InlineKey>(KeySources.UInt64, Comparer<ulong>.Default, seed); break;
            case KeyKind.Int64: RunDifferential<long, Int64InlineKey>(KeySources.Int64, Comparer<long>.Default, seed); break;
            case KeyKind.UInt128: RunDifferential<UInt128, UInt128InlineKey>(KeySources.UInt128, Comparer<UInt128>.Default, seed); break;
            case KeyKind.String: RunDifferential<string, StringInlineKey>(KeySources.String, StringComparer.Ordinal, seed); break;
            case KeyKind.ByteArray: RunDifferential<byte[], ByteArrayInlineKey>(KeySources.ByteArray, KeySources.ByteArrayComparer, seed); break;
            case KeyKind.Hash: RunDifferential<Bytes32, BytesInlineKey<Bytes32>>(KeySources.Hash, KeySources.HashComparer, seed); break;
        }
    }

    [Test]
    public void Keeps_order_through_monotonic_inserts_and_removals([Values] bool ascending, [Values] bool removeFromFront)
    {
        const int count = 50_000;
        BBTreeMap<ulong, ulong, UInt64InlineKey> map = new();
        for (int i = 0; i < count; i++)
        {
            ulong key = ascending ? (ulong)i : (ulong)(count - i);
            map[key] = key * 3;
        }

        Check(map);
        Assert.That(map.Select(static entry => entry.Key), Is.Ordered.And.Unique);
        Assert.That(map.Count, Is.EqualTo(count));

        for (int i = 0; i < count; i++)
        {
            ulong key = removeFromFront ? (ulong)(i + (ascending ? 0 : 1)) : (ulong)(count - i - (ascending ? 1 : 0));
            Assert.That(map.Remove(key, out ulong value), Is.True);
            Assert.That(value, Is.EqualTo(key * 3));
            if (i % 997 == 0) Check(map);
        }

        Assert.That(map.Count, Is.Zero);
        Check(map);
    }

    [Test]
    public void Shrinks_the_shared_prefix_for_a_key_that_shares_less()
    {
        BBTreeMap<ulong, int, UInt64InlineKey> map = new();
        for (int i = 0; i < 10_000; i++) map[0xFEDC_BA98_7600_0000UL + (ulong)i * 7] = i;
        Assert.That(map.SharedLength, Is.EqualTo(5));
        Check(map);

        map[1] = -1;
        Assert.That(map.SharedLength, Is.Zero);
        Check(map);
        Assert.That(map[1], Is.EqualTo(-1));
        Assert.That(map[0xFEDC_BA98_7600_0000UL + 7 * 5_000], Is.EqualTo(5_000));
    }

    [Test]
    public void Misses_keys_outside_the_shared_prefix()
    {
        BBTreeMap<string, int, StringInlineKey> map = new();
        for (int i = 0; i < 1_000; i++) map[$"prefix/{i:D5}"] = i;

        Assert.That(map.ContainsKey("prefiy"), Is.False);
        Assert.That(map.ContainsKey("a"), Is.False);
        Assert.That(map.Remove("zzz"), Is.False);
        Assert.That(map.EnumerateFrom("a").GetEnumerator().MoveNext(), Is.True);
        Assert.That(map.EnumerateFrom("z").GetEnumerator().MoveNext(), Is.False);
        Assert.That(map.EnumerateFrom("prefix/00500").Select(static entry => entry.Value), Is.EqualTo(Enumerable.Range(500, 500)));
    }

    [Test]
    public void Alternate_lookup_matches_lookups_by_key([Values(1, 2)] int seed)
    {
        Random random = new(seed);
        BBTreeMap<string, int, StringInlineKey> map = new();
        BBTreeMap<string, int, StringInlineKey>.AlternateLookup<ReadOnlySpan<char>, StringInlineKey> spans =
            map.GetAlternateLookup<ReadOnlySpan<char>, StringInlineKey>();
        SortedDictionary<string, int> expected = new(StringComparer.Ordinal);
        for (int op = 0; op < 30_000; op++)
        {
            string key = KeySources.String(random);
            ReadOnlySpan<char> span = $"#{key}#".AsSpan(1, key.Length);
            switch (random.Next(4))
            {
                case 0 or 1:
                    Assert.That(spans.TryAdd(span, op), Is.EqualTo(expected.TryAdd(key, op)));
                    break;
                case 2:
                    Assert.That(spans.Remove(span), Is.EqualTo(expected.Remove(key)));
                    break;
                default:
                    Assert.That(spans.TryGetValue(span, out int value), Is.EqualTo(expected.TryGetValue(key, out int expectedValue)));
                    Assert.That(value, Is.EqualTo(expectedValue));
                    break;
            }
        }

        Check(map, expected);
        BBTreeMap<byte[], int, ByteArrayInlineKey> bytes = new();
        bytes.GetAlternateLookup<ReadOnlySpan<byte>, ByteArrayInlineKey>()[[1, 2, 3]] = 7;
        Assert.That(bytes[[1, 2, 3]], Is.EqualTo(7));
    }

    [Test]
    public void Removes_first_entries_in_order()
    {
        BBTreeMap<long, long, Int64InlineKey> map = new();
        Random random = new(7);
        SortedSet<long> expected = [];
        for (int i = 0; i < 20_000; i++)
        {
            long key = KeySources.Int64(random);
            map[key] = key;
            expected.Add(key);
        }

        foreach (long key in expected)
        {
            Assert.That(map.TryGetFirst(out long first, out _), Is.True);
            Assert.That(first, Is.EqualTo(key));
            Assert.That(map.TryRemoveFirst(out long removed, out long value), Is.True);
            Assert.That((removed, value), Is.EqualTo((key, key)));
        }

        Assert.That(map.TryRemoveFirst(out _, out _), Is.False);
        Check(map);
    }

    [Test]
    public void Throws_when_changed_during_enumeration()
    {
        BBTreeMap<ulong, ulong, UInt64InlineKey> map = new() { };
        map[1] = 1;
        map[2] = 2;
        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (KeyValuePair<ulong, ulong> entry in map) map[entry.Key + 10] = 0;
        });
    }

    public enum KeyKind
    {
        UInt64,
        Int64,
        UInt128,
        String,
        ByteArray,
        Hash,
    }

    private static void RunDifferential<TKey, TInlineKey>(Func<Random, TKey> nextKey, IComparer<TKey> comparer, int seed)
        where TKey : notnull
        where TInlineKey : struct, IInlineKey<TKey>
    {
        const int operations = 60_000;
        Random random = new(seed);
        BBTreeMap<TKey, int, TInlineKey> map = new();
        SortedDictionary<TKey, int> expected = new(comparer);
        for (int op = 0; op < operations; op++)
        {
            TKey key = nextKey(random);
            switch (random.Next(12))
            {
                case < 5:
                    map[key] = op;
                    expected[key] = op;
                    break;
                case 5:
                    Assert.That(map.TryAdd(key, op), Is.EqualTo(expected.TryAdd(key, op)));
                    break;
                case 6 or 7:
                    bool removed = map.Remove(key, out int removedValue);
                    Assert.That(removed, Is.EqualTo(expected.Remove(key, out int expectedValue)));
                    if (removed) Assert.That(removedValue, Is.EqualTo(expectedValue));
                    break;
                case 8:
                    Assert.That(map.TryGetValue(key, out int value), Is.EqualTo(expected.TryGetValue(key, out int expectedFound)));
                    Assert.That(value, Is.EqualTo(expectedFound));
                    break;
                case 9:
                    Assert.That(map.EnumerateFrom(key).Take(20).Select(static e => e.Key),
                        Is.EqualTo(expected.Keys.Where(k => comparer.Compare(k, key) >= 0).Take(20)));
                    break;
                case 10:
                    ref int slot = ref map.GetValueRefOrAddDefault(key, out bool exists);
                    Assert.That(exists, Is.EqualTo(expected.ContainsKey(key)));
                    slot++;
                    expected[key] = expected.GetValueOrDefault(key) + 1;
                    break;
                default:
                    if (random.Next(20) == 0 && map.TryRemoveFirst(out TKey? firstKey, out int firstValue))
                    {
                        KeyValuePair<TKey, int> expectedFirst = expected.First();
                        Assert.That(firstKey, Is.EqualTo(expectedFirst.Key));
                        Assert.That(firstValue, Is.EqualTo(expectedFirst.Value));
                        expected.Remove(expectedFirst.Key);
                    }

                    break;
            }

            if (op % 5_000 == 0) Check(map, expected);
        }

        Check(map, expected);
        foreach (TKey key in expected.Keys.OrderBy(_ => random.Next()).ToArray())
        {
            Assert.That(map.Remove(key), Is.True);
            expected.Remove(key);
            if (expected.Count % 3_000 == 0) Check(map, expected);
        }

        Check(map, expected);
    }

    private static void Check<TKey, TValue, TInlineKey>(BBTreeMap<TKey, TValue, TInlineKey> map, SortedDictionary<TKey, TValue>? expected = null)
        where TKey : notnull
        where TInlineKey : struct, IInlineKey<TKey>
    {
        TreeInvariants<TKey, TValue, TInlineKey>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength);
        if (expected is null) return;
        Assert.That(map.Count, Is.EqualTo(expected.Count));
        Assert.That(map.ToArray(), Is.EqualTo(expected.ToArray()));
    }
}
