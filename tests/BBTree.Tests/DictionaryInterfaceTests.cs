// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace BBTree.Tests;

[Parallelizable(ParallelScope.All)]
public class DictionaryInterfaceTests
{
    [Test]
    public void Acts_like_a_sorted_dictionary_through_IDictionary([Values(1, 2, 3)] int seed)
    {
        RunDifferential(new BBTreeMap<ulong, int, UInt64InlineKey>(), Comparer<ulong>.Default, static random => (ulong)random.Next(2_000), seed);
        RunDifferential(new BBTreeMap<string, int, StringInlineKey>(), StringComparer.Ordinal, static random => random.Next(2_000).ToString("x"), seed);
    }

    [Test]
    public void Add_of_a_present_key_throws_and_keeps_the_value()
    {
        IDictionary<ulong, int> map = new BBTreeMap<ulong, int, UInt64InlineKey> { { 1, 10 } };
        Assert.That(() => map.Add(1, 20), Throws.ArgumentException);
        Assert.That(() => map.Add(new KeyValuePair<ulong, int>(1, 30)), Throws.ArgumentException);
        Assert.That(map[1], Is.EqualTo(10));
    }

    [Test]
    public void Indexer_get_of_a_missing_key_throws()
    {
        IDictionary<ulong, int> map = new BBTreeMap<ulong, int, UInt64InlineKey>();
        IReadOnlyDictionary<ulong, int> readOnly = (IReadOnlyDictionary<ulong, int>)map;
        Assert.That(() => map[1], Throws.TypeOf<KeyNotFoundException>());
        Assert.That(() => readOnly[1], Throws.TypeOf<KeyNotFoundException>());
    }

    [Test]
    public void Pairs_match_on_key_and_value([Values] bool sameValue)
    {
        ICollection<KeyValuePair<ulong, int>> map = new BBTreeMap<ulong, int, UInt64InlineKey> { { 1, 10 } };
        KeyValuePair<ulong, int> pair = new(1, sameValue ? 10 : 11);
        Assert.That(map.Contains(pair), Is.EqualTo(sameValue));
        Assert.That(map.Remove(pair), Is.EqualTo(sameValue));
        Assert.That(map.Count, Is.EqualTo(sameValue ? 0 : 1));
    }

    [Test]
    public void CopyTo_checks_the_array([Values(-1, 4, 6)] int index)
    {
        BBTreeMap<ulong, int, UInt64InlineKey> map = new() { { 1, 10 }, { 2, 20 } };
        ICollection<KeyValuePair<ulong, int>> entries = map;
        ICollection<ulong> keys = map.Keys;
        ICollection<int> values = map.Values;
        Type expected = index is < 0 or > 5 ? typeof(ArgumentOutOfRangeException) : typeof(ArgumentException);
        Assert.That(() => entries.CopyTo(new KeyValuePair<ulong, int>[5], index), Throws.TypeOf(expected));
        Assert.That(() => keys.CopyTo(new ulong[5], index), Throws.TypeOf(expected));
        Assert.That(() => values.CopyTo(new int[5], index), Throws.TypeOf(expected));
        Assert.That(() => keys.CopyTo(null!, 0), Throws.ArgumentNullException);
    }

    [Test]
    public void Keys_and_values_are_read_only_views_that_follow_the_map()
    {
        BBTreeMap<ulong, int, UInt64InlineKey> map = new() { { 2, 20 }, { 1, 10 } };
        ICollection<ulong> keys = map.Keys;
        ICollection<int> values = map.Values;
        map[3] = 30;
        Assert.That(keys, Is.EqualTo(new ulong[] { 1, 2, 3 }));
        Assert.That(values, Is.EqualTo(new[] { 10, 20, 30 }));
        Assert.That(keys.Contains(2UL), Is.True);
        Assert.That(values.Contains(30), Is.True);
        Assert.That(values.Contains(40), Is.False);
        Assert.That(keys.IsReadOnly && values.IsReadOnly, Is.True);
        Assert.That(((ICollection<KeyValuePair<ulong, int>>)map).IsReadOnly, Is.False);
        Assert.That(() => keys.Add(4), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => values.Remove(10), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => keys.Clear(), Throws.TypeOf<NotSupportedException>());
        Assert.That(map.Count, Is.EqualTo(3));
    }

    [Test]
    public void Concurrent_map_reads_through_IReadOnlyDictionary()
    {
        ConcurrentBBTreeMap<ulong, int, UInt64InlineKey> concurrent = new();
        for (ulong key = 100; key > 0; key--) concurrent.AddOrUpdate(key, (int)key * 10);
        IReadOnlyDictionary<ulong, int> map = concurrent;

        Assert.That(map.Count, Is.EqualTo(100));
        Assert.That(map[7], Is.EqualTo(70));
        Assert.That(() => map[1_000], Throws.TypeOf<KeyNotFoundException>());
        Assert.That(map.ContainsKey(50) && !map.ContainsKey(0), Is.True);
        Assert.That(map.TryGetValue(42, out int value) && value == 420, Is.True);
        Assert.That(map.Keys, Is.EqualTo(Enumerable.Range(1, 100).Select(static i => (ulong)i)));
        Assert.That(map.Values, Is.EqualTo(Enumerable.Range(1, 100).Select(static i => i * 10)));
    }

    private static void RunDifferential<TKey>(IDictionary<TKey, int> map, IComparer<TKey> comparer, Func<Random, TKey> nextKey, int seed)
        where TKey : notnull
    {
        Random random = new(seed);
        IDictionary<TKey, int> expected = new SortedDictionary<TKey, int>(comparer);
        for (int op = 0; op < 20_000; op++)
        {
            TKey key = nextKey(random);
            switch (random.Next(8))
            {
                case 0:
                    Assert.That(Catch(() => map.Add(key, op)), Is.EqualTo(Catch(() => expected.Add(key, op))));
                    break;
                case 1:
                    map[key] = op;
                    expected[key] = op;
                    break;
                case 2:
                    Assert.That(map.Remove(key), Is.EqualTo(expected.Remove(key)));
                    break;
                case 3:
                    KeyValuePair<TKey, int> pair = new(key, expected.TryGetValue(key, out int present) && random.Next(2) == 0 ? present : -1);
                    Assert.That(map.Contains(pair), Is.EqualTo(expected.Contains(pair)));
                    Assert.That(map.Remove(pair), Is.EqualTo(expected.Remove(pair)));
                    break;
                case 4:
                    Assert.That(Catch(() => map[key]), Is.EqualTo(Catch(() => expected[key])));
                    break;
                default:
                    Assert.That(map.TryGetValue(key, out int value), Is.EqualTo(expected.TryGetValue(key, out int expectedValue)));
                    Assert.That(value, Is.EqualTo(expectedValue));
                    Assert.That(map.ContainsKey(key), Is.EqualTo(expected.ContainsKey(key)));
                    break;
            }
        }

        Assert.That(map.Count, Is.EqualTo(expected.Count));
        Assert.That(map, Is.EqualTo(expected));
        Assert.That(map.Keys, Is.EqualTo(expected.Keys));
        Assert.That(map.Values, Is.EqualTo(expected.Values));

        KeyValuePair<TKey, int>[] copied = new KeyValuePair<TKey, int>[map.Count + 2];
        map.CopyTo(copied, 1);
        Assert.That(copied.Skip(1).Take(map.Count), Is.EqualTo(expected));

        IReadOnlyDictionary<TKey, int> readOnly = (IReadOnlyDictionary<TKey, int>)map;
        Assert.That(readOnly.Keys, Is.EqualTo(expected.Keys));
        Assert.That(readOnly.Values, Is.EqualTo(expected.Values));

        map.Clear();
        Assert.That(map.Count, Is.Zero);
        Assert.That(map, Is.Empty);
    }

    /// <summary>The value of <paramref name="action"/>, or the type of the exception it throws.</summary>
    private static object? Catch<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            return exception.GetType();
        }
    }

    private static object? Catch(Action action) => Catch(() =>
    {
        action();
        return true;
    });
}
