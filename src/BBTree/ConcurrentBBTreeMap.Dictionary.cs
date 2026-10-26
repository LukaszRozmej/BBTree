// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace BBTree;

public partial class ConcurrentBBTreeMap<TKey, TValue, TInlineKey> : IReadOnlyDictionary<TKey, TValue>
{
    TValue IReadOnlyDictionary<TKey, TValue>.this[TKey key] => this[key];

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys
    {
        get
        {
            foreach (KeyValuePair<TKey, TValue> entry in this) yield return entry.Key;
        }
    }

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values
    {
        get
        {
            foreach (KeyValuePair<TKey, TValue> entry in this) yield return entry.Value;
        }
    }

    bool IReadOnlyDictionary<TKey, TValue>.ContainsKey(TKey key) => ContainsKey(key);

    bool IReadOnlyDictionary<TKey, TValue>.TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => TryGetValue(key, out value);
}
