// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Runtime.CompilerServices;

namespace BBTree;

/// <summary>
/// Inline keys of keys of any type that orders itself, such as a class of your own: the map orders the keys by
/// <see cref="IComparable{T}.CompareTo(T)"/>.
/// </summary>
/// <typeparam name="T">The key type. Keys must not be <see langword="null"/>.</typeparam>
/// <remarks>
/// <para>
/// The keys give the tree no bytes to summarize them by, so all keys of a node tie and a search compares full keys,
/// with a binary search in each node. Give the tree an order-preserving prefix with
/// <see cref="ComparableKey{T, TPrefix}"/>, or implement <see cref="IInlineKey{TKey}"/>, to search by inline keys.
/// </para>
/// <para>
/// In a <see cref="ConcurrentBBTreeMap{TKey, TValue, TInlineKey}"/>, a search may compare keys that a writer is
/// moving; the map retries a search whose comparison throws.
/// </para>
/// </remarks>
[InlineArray(BBTreeLayout.NodeCapacity)]
public struct ComparableKey<T> : IInlineKey<T> where T : IComparable<T>
{
    private ushort _slot;

    /// <inheritdoc/>
    public static int Compare(in T x, in T y) => x.CompareTo(y);

    /// <inheritdoc/>
    public static ulong WindowAt(in T key, int offset) => 0;

    /// <inheritdoc/>
    public static int CommonPrefixLength(in T x, in T y) => 0;

    /// <inheritdoc/>
    public static int Length(in T key) => 0;

    /// <inheritdoc/>
    public static int FixedLength => 0;

    /// <summary>A <see cref="BBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    public sealed class Map<TValue> : BBTreeMap<T, TValue, ComparableKey<T>>;

    /// <summary>A <see cref="ConcurrentBBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    public sealed class ConcurrentMap<TValue> : ConcurrentBBTreeMap<T, TValue, ComparableKey<T>>;
}

/// <summary>Gives keys a prefix that orders like them, so that a map of <see cref="ComparableKey{T, TPrefix}"/> searches by it.</summary>
/// <typeparam name="T">The key type.</typeparam>
public interface ISortPrefix<T>
{
    /// <summary>Gets the prefix of <paramref name="key"/>.</summary>
    /// <remarks>
    /// The contract: if <c>x.CompareTo(y) &lt; 0</c>, then <c>Of(x) &lt;= Of(y)</c>. Equal prefixes say nothing, and a
    /// search compares the full keys. Map the first fields that the order compares into the high bits; for example
    /// the first 8 bytes of a name, big-endian, or a timestamp.
    /// </remarks>
    static abstract ulong Of(in T key);
}

/// <summary>
/// Inline keys of keys of any type that orders itself, searched by a prefix that <typeparamref name="TPrefix"/> gives:
/// the map orders the keys by <see cref="IComparable{T}.CompareTo(T)"/>, and its nodes keep the prefix of each key.
/// </summary>
/// <typeparam name="T">The key type. Keys must not be <see langword="null"/>.</typeparam>
/// <typeparam name="TPrefix">Gives the prefix of each key; a struct, so that the JIT inlines it.</typeparam>
/// <remarks>
/// A search compares full keys only where prefixes tie, so the more keys the prefix tells apart, the faster the map.
/// Concurrent maps retry a search whose comparison throws, as with <see cref="ComparableKey{T}"/>.
/// </remarks>
[InlineArray(BBTreeLayout.NodeCapacity)]
public struct ComparableKey<T, TPrefix> : IInlineKey<T>
    where T : IComparable<T>
    where TPrefix : struct, ISortPrefix<T>
{
    private ulong _slot;

    /// <inheritdoc/>
    public static int Compare(in T x, in T y) => x.CompareTo(y);

    /// <inheritdoc/>
    /// <remarks>Keys share no units, so the tree asks only for the window at offset 0: the prefix.</remarks>
    public static ulong WindowAt(in T key, int offset) => offset == 0 ? TPrefix.Of(key) : 0;

    /// <inheritdoc/>
    public static int CommonPrefixLength(in T x, in T y) => 0;

    /// <inheritdoc/>
    public static int Length(in T key) => 0;

    /// <inheritdoc/>
    public static int FixedLength => 0;

    /// <summary>A <see cref="BBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    public sealed class Map<TValue> : BBTreeMap<T, TValue, ComparableKey<T, TPrefix>>;

    /// <summary>A <see cref="ConcurrentBBTreeMap{TKey, TValue, TInlineKey}"/> of these keys.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    public sealed class ConcurrentMap<TValue> : ConcurrentBBTreeMap<T, TValue, ComparableKey<T, TPrefix>>;
}
