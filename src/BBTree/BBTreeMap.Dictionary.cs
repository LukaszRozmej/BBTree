// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace BBTree;

public sealed partial class BBTreeMap<TKey, TValue, TInlineKey> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
{
    /// <summary>Gets the keys, in key order.</summary>
    public KeyCollection Keys => new(this);

    /// <summary>Gets the values, in the order of their keys.</summary>
    public ValueCollection Values => new(this);

    /// <summary>Adds <paramref name="key"/> and <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentException">The map already has the key.</exception>
    public void Add(in TKey key, TValue value)
    {
        if (!TryAdd(key, value)) ThrowDuplicateKey(key);
    }

    ICollection<TKey> IDictionary<TKey, TValue>.Keys => Keys;

    ICollection<TValue> IDictionary<TKey, TValue>.Values => Values;

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

    TValue IDictionary<TKey, TValue>.this[TKey key]
    {
        get => this[key];
        set => this[key] = value;
    }

    TValue IReadOnlyDictionary<TKey, TValue>.this[TKey key] => this[key];

    void IDictionary<TKey, TValue>.Add(TKey key, TValue value) => Add(key, value);

    bool IDictionary<TKey, TValue>.ContainsKey(TKey key) => ContainsKey(key);

    bool IReadOnlyDictionary<TKey, TValue>.ContainsKey(TKey key) => ContainsKey(key);

    bool IDictionary<TKey, TValue>.Remove(TKey key) => Remove(key);

    bool IDictionary<TKey, TValue>.TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => TryGetValue(key, out value);

    bool IReadOnlyDictionary<TKey, TValue>.TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => TryGetValue(key, out value);

    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item) =>
        TryGetValue(item.Key, out TValue? value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);

    bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
    {
        ref TValue value = ref GetValueRefOrNullRef(item.Key);
        return !Unsafe.IsNullRef(ref value) &&
            EqualityComparer<TValue>.Default.Equals(value, item.Value) && Remove(item.Key);
    }

    void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        CheckCopyTo(array, arrayIndex, _count);
        foreach (KeyValuePair<TKey, TValue> entry in this) array[arrayIndex++] = entry;
    }

    private static void CheckCopyTo<T>(T[] array, int arrayIndex, int count)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(arrayIndex, array.Length);
        if (array.Length - arrayIndex < count) throw new ArgumentException("The array is too small for the entries.", nameof(array));
    }

    [DoesNotReturn]
    private static void ThrowDuplicateKey(in TKey key) => throw new ArgumentException($"The key '{key}' is already in the map.", nameof(key));

    [DoesNotReturn]
    private static void ThrowReadOnly() => throw new NotSupportedException("The collection is a read-only view of the map.");

    /// <summary>The keys of a map, in key order: a read-only view that follows the changes of the map.</summary>
    public readonly struct KeyCollection : ICollection<TKey>, IReadOnlyCollection<TKey>
    {
        private readonly BBTreeMap<TKey, TValue, TInlineKey> _map;

        internal KeyCollection(BBTreeMap<TKey, TValue, TInlineKey> map) => _map = map;

        /// <summary>Gets the number of keys.</summary>
        public int Count => _map.Count;

        /// <summary>Gets an enumerator of the keys in key order.</summary>
        public Enumerator GetEnumerator() => new(_map.GetEnumerator());

        /// <summary>Whether the map has <paramref name="item"/>.</summary>
        public bool Contains(TKey item) => _map.ContainsKey(item);

        /// <summary>Copies the keys, in key order, into <paramref name="array"/> from <paramref name="arrayIndex"/> on.</summary>
        public void CopyTo(TKey[] array, int arrayIndex)
        {
            CheckCopyTo(array, arrayIndex, _map.Count);
            foreach (TKey key in this) array[arrayIndex++] = key;
        }

        bool ICollection<TKey>.IsReadOnly => true;

        void ICollection<TKey>.Add(TKey item) => ThrowReadOnly();

        void ICollection<TKey>.Clear() => ThrowReadOnly();

        bool ICollection<TKey>.Remove(TKey item)
        {
            ThrowReadOnly();
            return false;
        }

        IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => new BoxedEnumerator<TKey, Enumerator>(GetEnumerator());

        IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator<TKey, Enumerator>(GetEnumerator());

        /// <summary>Enumerates the keys in key order.</summary>
        public struct Enumerator : IStructEnumerator<TKey>
        {
            private BBTreeMap<TKey, TValue, TInlineKey>.Enumerator _entries;

            internal Enumerator(BBTreeMap<TKey, TValue, TInlineKey>.Enumerator entries) => _entries = entries;

            /// <summary>Gets the key at the current position.</summary>
            public readonly TKey Current => _entries.Current.Key;

            /// <summary>Moves to the next key.</summary>
            /// <returns>Whether there is a next key.</returns>
            public bool MoveNext() => _entries.MoveNext();
        }
    }

    /// <summary>The values of a map, in the order of their keys: a read-only view that follows the changes of the map.</summary>
    public readonly struct ValueCollection : ICollection<TValue>, IReadOnlyCollection<TValue>
    {
        private readonly BBTreeMap<TKey, TValue, TInlineKey> _map;

        internal ValueCollection(BBTreeMap<TKey, TValue, TInlineKey> map) => _map = map;

        /// <summary>Gets the number of values.</summary>
        public int Count => _map.Count;

        /// <summary>Gets an enumerator of the values in the order of their keys.</summary>
        public Enumerator GetEnumerator() => new(_map.GetEnumerator());

        /// <summary>Whether the map has a value equal to <paramref name="item"/>.</summary>
        /// <remarks>Scans all values.</remarks>
        public bool Contains(TValue item)
        {
            EqualityComparer<TValue> comparer = EqualityComparer<TValue>.Default;
            foreach (TValue value in this)
            {
                if (comparer.Equals(value, item)) return true;
            }

            return false;
        }

        /// <summary>Copies the values, in the order of their keys, into <paramref name="array"/> from <paramref name="arrayIndex"/> on.</summary>
        public void CopyTo(TValue[] array, int arrayIndex)
        {
            CheckCopyTo(array, arrayIndex, _map.Count);
            foreach (TValue value in this) array[arrayIndex++] = value;
        }

        bool ICollection<TValue>.IsReadOnly => true;

        void ICollection<TValue>.Add(TValue item) => ThrowReadOnly();

        void ICollection<TValue>.Clear() => ThrowReadOnly();

        bool ICollection<TValue>.Remove(TValue item)
        {
            ThrowReadOnly();
            return false;
        }

        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => new BoxedEnumerator<TValue, Enumerator>(GetEnumerator());

        IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator<TValue, Enumerator>(GetEnumerator());

        /// <summary>Enumerates the values in the order of their keys.</summary>
        public struct Enumerator : IStructEnumerator<TValue>
        {
            private BBTreeMap<TKey, TValue, TInlineKey>.Enumerator _entries;

            internal Enumerator(BBTreeMap<TKey, TValue, TInlineKey>.Enumerator entries) => _entries = entries;

            /// <summary>Gets the value at the current position.</summary>
            public readonly TValue Current => _entries.Current.Value;

            /// <summary>Moves to the next value.</summary>
            /// <returns>Whether there is a next value.</returns>
            public bool MoveNext() => _entries.MoveNext();
        }
    }
}

/// <summary>An enumerator that a box can serve through <see cref="IEnumerator{T}"/>.</summary>
/// <remarks>
/// The struct enumerators of the maps implement no standard interfaces, so that <c>foreach</c> keeps them in
/// registers; see <see cref="BBTreeMap{TKey, TValue, TInlineKey}.Enumerator"/>. This one is internal to the boxes.
/// </remarks>
internal interface IStructEnumerator<out T>
{
    T Current { get; }

    bool MoveNext();
}

/// <summary>Serves a struct enumerator through <see cref="IEnumerator{T}"/>.</summary>
internal sealed class BoxedEnumerator<T, TEnumerator>(TEnumerator enumerator) : IEnumerator<T>
    where TEnumerator : struct, IStructEnumerator<T>
{
    private TEnumerator _enumerator = enumerator;

    public T Current => _enumerator.Current;

    object? IEnumerator.Current => Current;

    public bool MoveNext() => _enumerator.MoveNext();

    public void Reset() => throw new NotSupportedException();

    public void Dispose() { }
}
