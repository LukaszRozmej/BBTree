// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

namespace BBTree;

/// <summary>
/// Searches the keys of a map with a <typeparamref name="TQuery"/>: compares it with keys and gives its inline keys.
/// </summary>
/// <typeparam name="TQuery">The type that a search takes, such as a span for string keys.</typeparam>
/// <typeparam name="TKey">The key type of the map.</typeparam>
/// <remarks>
/// A query has the units and windows of the key that it equals, so that its windows compare with the inline keys
/// of the map. See <see cref="IInlineKey{TKey}"/>.
/// </remarks>
public interface IKeyQuery<TQuery, TKey> where TQuery : allows ref struct
{
    /// <summary>Compares <paramref name="query"/> with <paramref name="key"/>.</summary>
    static abstract int Compare(in TQuery query, in TKey key);

    /// <summary>Gets the window of <paramref name="query"/> at unit <paramref name="offset"/>; see <see cref="IInlineKey{TKey}"/>.</summary>
    static abstract ulong WindowAt(in TQuery query, int offset);

    /// <summary>Gets the number of units of <paramref name="query"/>.</summary>
    static abstract int Length(in TQuery query);

    /// <summary>Gets the number of leading units that <paramref name="query"/> and <paramref name="key"/> share.</summary>
    static abstract int CommonPrefixLength(in TQuery query, in TKey key);

    /// <summary>Makes the key that a map stores when an insert adds <paramref name="query"/>.</summary>
    static abstract TKey ToKey(in TQuery query);
}

/// <summary>
/// Orders the keys of a map and gives each key its inline keys: order-preserving windows of the key.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <remarks>
/// <para>
/// Every key is a sequence of units, such as the big-endian bytes of an integer or the chars of a string, that orders
/// like the key, a shorter sequence first. <see cref="IKeyQuery{TQuery, TKey}.WindowAt"/> summarizes the units from
/// <c>offset</c> on in a big-endian word, zero past the end of the key. The contract: for two keys that share their
/// first <c>offset</c> units, a smaller window means a smaller key. Equal windows say nothing, and a search compares
/// the full keys.
/// </para>
/// <para>
/// A node keeps the first 2, 4 or 8 bytes of the window of each key, taken past the units that all keys of the node
/// share, which <see cref="IKeyQuery{TQuery, TKey}.CommonPrefixLength"/> gives exactly. A search compares these inline
/// keys first and reads the full keys only where the inline keys tie.
/// </para>
/// <para>
/// The implementing struct is also the storage of the inline keys of one node, so it must be an
/// <see cref="System.Runtime.CompilerServices.InlineArrayAttribute"/> of <see cref="BBTreeLayout.NodeCapacity"/>
/// elements of <see cref="ushort"/>, <see cref="uint"/> or <see cref="ulong"/>. The element size is the inline
/// key width. Narrow inline keys pack more keys into one cache line but tie more often. Elements for one more byte
/// per key keep the length of each leaf key as a tag. Use 2 bytes for keys stored in the node (integers, fixed byte
/// arrays) and 4 bytes with tags for keys behind a reference (strings, byte arrays).
/// </para>
/// <para>
/// A wrong implementation gives wrong answers, but never reads outside of the map.
/// </para>
/// </remarks>
public interface IInlineKey<TKey> : IKeyQuery<TKey, TKey>
{
    /// <summary>
    /// Gets the number of units when all keys have one length and a window holds one byte of each unit, or 0.
    /// </summary>
    /// <remarks>
    /// Then equal inline keys that cover the last unit prove equal keys, so a search does not read the full keys.
    /// </remarks>
    static abstract int FixedLength { get; }

    static TKey IKeyQuery<TKey, TKey>.ToKey(in TKey query) => query;
}

/// <summary>Marks the built-in inline keys, whose comparisons never throw, even on keys that a writer is moving.</summary>
internal interface ITrustedInlineKey;
