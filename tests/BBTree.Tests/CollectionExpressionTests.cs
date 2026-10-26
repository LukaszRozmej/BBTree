// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace BBTree.Tests;

[Parallelizable(ParallelScope.All)]
public class CollectionExpressionTests
{
    [Test]
    public void Every_map_type_builds_from_a_collection_expression()
    {
        BBTreeMap<ulong, string, IntegerKey<ulong>> general = [new(2, "b"), new(1, "a")];
        ConcurrentBBTreeMap<ulong, string, IntegerKey<ulong>> generalConcurrent = [new(2, "b"), new(1, "a")];
        IntegerKey<int>.Map<string> integers = [new(2, "b"), new(-1, "a")];
        IntegerKey<int>.ConcurrentMap<string> integersConcurrent = [new(2, "b"), new(-1, "a")];
        FloatKey<double>.Map<string> floats = [new(2.5, "b"), new(-1.5, "a")];
        FloatKey<double>.ConcurrentMap<string> floatsConcurrent = [new(2.5, "b"), new(-1.5, "a")];
        StringKey.Map<int> strings = [new("b", 2), new("a", 1)];
        StringKey.ConcurrentMap<int> stringsConcurrent = [new("b", 2), new("a", 1)];
        ByteArrayKey.Map<int> arrays = [new([2], 2), new([1], 1)];
        ByteArrayKey.ConcurrentMap<int> arraysConcurrent = [new([2], 2), new([1], 1)];
        BytesKey<Bytes32>.Map<int> hashes = [new(default, 0)];
        BytesKey<Bytes32>.ConcurrentMap<int> hashesConcurrent = [new(default, 0)];
        ComparableKey<Person>.Map<int> people = [new(new Person("Bob", 1), 2), new(new Person("Ann", 1), 1)];
        ComparableKey<Person>.ConcurrentMap<int> peopleConcurrent = [new(new Person("Bob", 1), 2), new(new Person("Ann", 1), 1)];
        ComparableKey<Person, PersonPrefix>.Map<int> prefixed = [new(new Person("Bob", 1), 2), new(new Person("Ann", 1), 1)];
        ComparableKey<Person, PersonPrefix>.ConcurrentMap<int> prefixedConcurrent = [new(new Person("Bob", 1), 2), new(new Person("Ann", 1), 1)];

        Assert.That(general.Values, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(generalConcurrent.Select(static e => e.Value), Is.EqualTo(new[] { "a", "b" }));
        Assert.That(integers.Keys, Is.EqualTo(new[] { -1, 2 }));
        Assert.That(integersConcurrent.Select(static e => e.Key), Is.EqualTo(new[] { -1, 2 }));
        Assert.That(floats.Keys, Is.EqualTo(new[] { -1.5, 2.5 }));
        Assert.That(floatsConcurrent.Select(static e => e.Key), Is.EqualTo(new[] { -1.5, 2.5 }));
        Assert.That(strings.Keys, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(stringsConcurrent.Select(static e => e.Key), Is.EqualTo(new[] { "a", "b" }));
        Assert.That(arrays.Values, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(arraysConcurrent.Select(static e => e.Value), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(hashes.Count + hashesConcurrent.Count, Is.EqualTo(2));
        Assert.That(people.Values, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(peopleConcurrent.Select(static e => e.Value), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(prefixed.Values, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(prefixedConcurrent.Select(static e => e.Value), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void A_repeated_key_takes_its_last_value([Values] bool concurrent)
    {
        IEnumerable<KeyValuePair<int, string>> map = concurrent
            ? (IntegerKey<int>.ConcurrentMap<string>)[new(1, "first"), new(2, "two"), new(1, "last")]
            : (IntegerKey<int>.Map<string>)[new(1, "first"), new(2, "two"), new(1, "last")];
        Assert.That(map, Is.EqualTo(new KeyValuePair<int, string>[] { new(1, "last"), new(2, "two") }));
    }

    [Test]
    public void Empty_and_spread_expressions_build_maps()
    {
        StringKey.Map<int> empty = [];
        StringKey.Map<int> source = [new("b", 2), new("a", 1)];
        StringKey.Map<int> copy = [.. source, new("c", 3)];
        Assert.That(empty, Is.Empty);
        Assert.That(copy.Keys, Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void Span_lookups_search_without_allocating_keys()
    {
        StringKey.Map<int> words = [new("word", 1)];
        ByteArrayKey.Map<int> blobs = [new([1, 2, 3], 7)];
        Assert.That(words.GetSpanLookup().ContainsKey("word and more".AsSpan(0, 4)), Is.True);
        Assert.That(blobs.GetSpanLookup().TryGetValue([1, 2, 3], out int value) && value == 7, Is.True);
    }
}
