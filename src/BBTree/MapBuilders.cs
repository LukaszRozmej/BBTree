// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;

namespace BBTree;

/// <summary>Builds maps from collection expressions, as in <c>StringKey.Map&lt;int&gt; map = [new("a", 1), new("b", 2)];</c>.</summary>
/// <remarks>A key that comes more than once takes its last value, as with the indexer of the map.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class BBTreeMapBuilder
{
    /// <summary>Builds a map of <paramref name="items"/>.</summary>
    public static BBTreeMap<TKey, TValue, TInlineKey> Create<TKey, TValue, TInlineKey>(ReadOnlySpan<KeyValuePair<TKey, TValue>> items)
        where TInlineKey : struct, IInlineKey<TKey>
    {
        BBTreeMap<TKey, TValue, TInlineKey> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a concurrent map of <paramref name="items"/>.</summary>
    public static ConcurrentBBTreeMap<TKey, TValue, TInlineKey> CreateConcurrent<TKey, TValue, TInlineKey>(ReadOnlySpan<KeyValuePair<TKey, TValue>> items)
        where TInlineKey : struct, IInlineKey<TKey>
    {
        ConcurrentBBTreeMap<TKey, TValue, TInlineKey> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a map of <paramref name="items"/>.</summary>
    public static IntegerKey<T>.Map<TValue> CreateIntegerMap<T, TValue>(ReadOnlySpan<KeyValuePair<T, TValue>> items)
        where T : unmanaged, IBinaryInteger<T>
    {
        IntegerKey<T>.Map<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a concurrent map of <paramref name="items"/>.</summary>
    public static IntegerKey<T>.ConcurrentMap<TValue> CreateIntegerConcurrentMap<T, TValue>(ReadOnlySpan<KeyValuePair<T, TValue>> items)
        where T : unmanaged, IBinaryInteger<T>
    {
        IntegerKey<T>.ConcurrentMap<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a map of <paramref name="items"/>.</summary>
    public static FloatKey<T>.Map<TValue> CreateFloatMap<T, TValue>(ReadOnlySpan<KeyValuePair<T, TValue>> items)
        where T : unmanaged, IBinaryFloatingPointIeee754<T>
    {
        FloatKey<T>.Map<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a concurrent map of <paramref name="items"/>.</summary>
    public static FloatKey<T>.ConcurrentMap<TValue> CreateFloatConcurrentMap<T, TValue>(ReadOnlySpan<KeyValuePair<T, TValue>> items)
        where T : unmanaged, IBinaryFloatingPointIeee754<T>
    {
        FloatKey<T>.ConcurrentMap<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a map of <paramref name="items"/>.</summary>
    public static StringKey.Map<TValue> CreateStringMap<TValue>(ReadOnlySpan<KeyValuePair<string, TValue>> items)
    {
        StringKey.Map<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a concurrent map of <paramref name="items"/>.</summary>
    public static StringKey.ConcurrentMap<TValue> CreateStringConcurrentMap<TValue>(ReadOnlySpan<KeyValuePair<string, TValue>> items)
    {
        StringKey.ConcurrentMap<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a map of <paramref name="items"/>.</summary>
    public static ByteArrayKey.Map<TValue> CreateByteArrayMap<TValue>(ReadOnlySpan<KeyValuePair<byte[], TValue>> items)
    {
        ByteArrayKey.Map<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a concurrent map of <paramref name="items"/>.</summary>
    public static ByteArrayKey.ConcurrentMap<TValue> CreateByteArrayConcurrentMap<TValue>(ReadOnlySpan<KeyValuePair<byte[], TValue>> items)
    {
        ByteArrayKey.ConcurrentMap<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a map of <paramref name="items"/>.</summary>
    public static BytesKey<T>.Map<TValue> CreateBytesMap<T, TValue>(ReadOnlySpan<KeyValuePair<T, TValue>> items)
        where T : unmanaged
    {
        BytesKey<T>.Map<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a concurrent map of <paramref name="items"/>.</summary>
    public static BytesKey<T>.ConcurrentMap<TValue> CreateBytesConcurrentMap<T, TValue>(ReadOnlySpan<KeyValuePair<T, TValue>> items)
        where T : unmanaged
    {
        BytesKey<T>.ConcurrentMap<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a map of <paramref name="items"/>.</summary>
    public static ComparableKey<T>.Map<TValue> CreateComparableMap<T, TValue>(ReadOnlySpan<KeyValuePair<T, TValue>> items)
        where T : IComparable<T>
    {
        ComparableKey<T>.Map<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a concurrent map of <paramref name="items"/>.</summary>
    public static ComparableKey<T>.ConcurrentMap<TValue> CreateComparableConcurrentMap<T, TValue>(ReadOnlySpan<KeyValuePair<T, TValue>> items)
        where T : IComparable<T>
    {
        ComparableKey<T>.ConcurrentMap<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a map of <paramref name="items"/>.</summary>
    public static ComparableKey<T, TPrefix>.Map<TValue> CreatePrefixedMap<T, TPrefix, TValue>(ReadOnlySpan<KeyValuePair<T, TValue>> items)
        where T : IComparable<T>
        where TPrefix : struct, ISortPrefix<T>
    {
        ComparableKey<T, TPrefix>.Map<TValue> map = new();
        Fill(map, items);
        return map;
    }

    /// <summary>Builds a concurrent map of <paramref name="items"/>.</summary>
    public static ComparableKey<T, TPrefix>.ConcurrentMap<TValue> CreatePrefixedConcurrentMap<T, TPrefix, TValue>(ReadOnlySpan<KeyValuePair<T, TValue>> items)
        where T : IComparable<T>
        where TPrefix : struct, ISortPrefix<T>
    {
        ComparableKey<T, TPrefix>.ConcurrentMap<TValue> map = new();
        Fill(map, items);
        return map;
    }

    private static void Fill<TKey, TValue, TInlineKey>(BBTreeMap<TKey, TValue, TInlineKey> map, ReadOnlySpan<KeyValuePair<TKey, TValue>> items)
        where TInlineKey : struct, IInlineKey<TKey>
    {
        foreach (KeyValuePair<TKey, TValue> item in items) map[item.Key] = item.Value;
    }

    private static void Fill<TKey, TValue, TInlineKey>(ConcurrentBBTreeMap<TKey, TValue, TInlineKey> map, ReadOnlySpan<KeyValuePair<TKey, TValue>> items)
        where TInlineKey : struct, IInlineKey<TKey>
    {
        foreach (KeyValuePair<TKey, TValue> item in items) map[item.Key] = item.Value;
    }
}

/// <summary>Shortcuts for the alternate lookups of the built-in keys.</summary>
public static class BBTreeMapExtensions
{
    /// <summary>Gets a view of <paramref name="map"/> that searches it by spans of chars, with no string allocation.</summary>
    public static BBTreeMap<string, TValue, StringKey>.AlternateLookup<ReadOnlySpan<char>, StringKey> GetSpanLookup<TValue>(
        this BBTreeMap<string, TValue, StringKey> map) => map.GetAlternateLookup<ReadOnlySpan<char>, StringKey>();

    /// <summary>Gets a view of <paramref name="map"/> that searches it by spans of bytes, with no array allocation.</summary>
    public static BBTreeMap<byte[], TValue, ByteArrayKey>.AlternateLookup<ReadOnlySpan<byte>, ByteArrayKey> GetSpanLookup<TValue>(
        this BBTreeMap<byte[], TValue, ByteArrayKey> map) => map.GetAlternateLookup<ReadOnlySpan<byte>, ByteArrayKey>();
}
