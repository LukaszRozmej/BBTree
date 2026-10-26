// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Runtime.CompilerServices;

namespace BBTree;

/// <summary>Calls the members of <see cref="IKeyQuery{TQuery, TKey}"/>, directly for the built-in keys behind references.</summary>
/// <remarks>
/// The JIT shares one copy of generic code among all reference types. There, a call through
/// <c>IKeyQuery&lt;string, string&gt;</c> becomes <c>IKeyQuery&lt;__Canon, __Canon&gt;</c>: a runtime lookup and an
/// indirect call that is never inlined, on every level of every search. <typeparamref name="TQueryKey"/> is a struct,
/// so the type checks here fold at JIT time and leave a direct call for <see cref="StringKey"/> and
/// <see cref="ByteArrayKey"/>. Other key types take the interface call.
/// </remarks>
internal static class KeyDispatch<TQuery, TQueryKey, TKey>
    where TQuery : allows ref struct
    where TQueryKey : IKeyQuery<TQuery, TKey>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong WindowAt(in TQuery query, int offset)
    {
        if (typeof(TQueryKey) == typeof(StringKey))
        {
            return typeof(TQuery) == typeof(ReadOnlySpan<char>)
                ? CharWindows.At(Unsafe.As<TQuery, ReadOnlySpan<char>>(ref Unsafe.AsRef(in query)), offset)
                : CharWindows.At(Unsafe.As<TQuery, string>(ref Unsafe.AsRef(in query)), offset);
        }

        if (typeof(TQueryKey) == typeof(ByteArrayKey))
        {
            return typeof(TQuery) == typeof(ReadOnlySpan<byte>)
                ? ByteWindows.At(Unsafe.As<TQuery, ReadOnlySpan<byte>>(ref Unsafe.AsRef(in query)), offset)
                : ByteWindows.At(Unsafe.As<TQuery, byte[]>(ref Unsafe.AsRef(in query)), offset);
        }

        return TQueryKey.WindowAt(query, offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compare(in TQuery query, in TKey key)
    {
        if (typeof(TQueryKey) == typeof(StringKey))
        {
            return typeof(TQuery) == typeof(ReadOnlySpan<char>)
                ? Unsafe.As<TQuery, ReadOnlySpan<char>>(ref Unsafe.AsRef(in query)).SequenceCompareTo(Unsafe.As<TKey, string>(ref Unsafe.AsRef(in key)))
                : string.CompareOrdinal(Unsafe.As<TQuery, string>(ref Unsafe.AsRef(in query)), Unsafe.As<TKey, string>(ref Unsafe.AsRef(in key)));
        }

        if (typeof(TQueryKey) == typeof(ByteArrayKey))
        {
            return typeof(TQuery) == typeof(ReadOnlySpan<byte>)
                ? Unsafe.As<TQuery, ReadOnlySpan<byte>>(ref Unsafe.AsRef(in query)).SequenceCompareTo(Unsafe.As<TKey, byte[]>(ref Unsafe.AsRef(in key)))
                : Unsafe.As<TQuery, byte[]>(ref Unsafe.AsRef(in query)).AsSpan().SequenceCompareTo(Unsafe.As<TKey, byte[]>(ref Unsafe.AsRef(in key)));
        }

        return TQueryKey.Compare(query, key);
    }

    /// <summary>Whether <paramref name="query"/> equals <paramref name="key"/>: cheaper than an ordered comparison for byte strings.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreEqual(in TQuery query, in TKey key)
    {
        if (typeof(TQueryKey) == typeof(StringKey))
        {
            return typeof(TQuery) == typeof(ReadOnlySpan<char>)
                ? Unsafe.As<TQuery, ReadOnlySpan<char>>(ref Unsafe.AsRef(in query)).SequenceEqual(Unsafe.As<TKey, string>(ref Unsafe.AsRef(in key)))
                : string.Equals(Unsafe.As<TQuery, string>(ref Unsafe.AsRef(in query)), Unsafe.As<TKey, string>(ref Unsafe.AsRef(in key)));
        }

        if (typeof(TQueryKey) == typeof(ByteArrayKey))
        {
            return typeof(TQuery) == typeof(ReadOnlySpan<byte>)
                ? Unsafe.As<TQuery, ReadOnlySpan<byte>>(ref Unsafe.AsRef(in query)).SequenceEqual(Unsafe.As<TKey, byte[]>(ref Unsafe.AsRef(in key)))
                : Unsafe.As<TQuery, byte[]>(ref Unsafe.AsRef(in query)).AsSpan().SequenceEqual(Unsafe.As<TKey, byte[]>(ref Unsafe.AsRef(in key)));
        }

        return TQueryKey.Compare(query, key) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Length(in TQuery query)
    {
        if (typeof(TQueryKey) == typeof(StringKey))
        {
            return typeof(TQuery) == typeof(ReadOnlySpan<char>)
                ? Unsafe.As<TQuery, ReadOnlySpan<char>>(ref Unsafe.AsRef(in query)).Length
                : Unsafe.As<TQuery, string>(ref Unsafe.AsRef(in query)).Length;
        }

        if (typeof(TQueryKey) == typeof(ByteArrayKey))
        {
            return typeof(TQuery) == typeof(ReadOnlySpan<byte>)
                ? Unsafe.As<TQuery, ReadOnlySpan<byte>>(ref Unsafe.AsRef(in query)).Length
                : Unsafe.As<TQuery, byte[]>(ref Unsafe.AsRef(in query)).Length;
        }

        return TQueryKey.Length(query);
    }

    /// <summary>Gets the length tag of <paramref name="query"/>: its length, saturated at 255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Tag(in TQuery query) => (byte)Math.Min(Length(query), byte.MaxValue);

    /// <summary>
    /// Whether the <paramref name="width"/> bytes of an inline key hold its units exactly, one byte each,
    /// so that equal inline keys of keys of one length that end within them prove equal keys.
    /// </summary>
    /// <remarks>A string window saturates chars from <c>U+00FF</c> on, so it holds them exactly only without a <c>0xFF</c> byte.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HoldsUnits(ulong inlineKey, int width)
    {
        if (typeof(TQueryKey) == typeof(ByteArrayKey)) return true;
        if (typeof(TQueryKey) != typeof(StringKey)) return false;

        // A zero byte in the complement, within the kept bytes, is a 0xFF byte.
        ulong complement = ~inlineKey | (width == sizeof(ulong) ? 0 : ulong.MaxValue << (8 * width));
        return ((complement - 0x0101_0101_0101_0101) & ~complement & 0x8080_8080_8080_8080) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CommonPrefixLength(in TQuery query, in TKey key)
    {
        if (typeof(TQueryKey) == typeof(StringKey))
        {
            return typeof(TQuery) == typeof(ReadOnlySpan<char>)
                ? Unsafe.As<TQuery, ReadOnlySpan<char>>(ref Unsafe.AsRef(in query)).CommonPrefixLength(Unsafe.As<TKey, string>(ref Unsafe.AsRef(in key)))
                : Unsafe.As<TQuery, string>(ref Unsafe.AsRef(in query)).AsSpan().CommonPrefixLength(Unsafe.As<TKey, string>(ref Unsafe.AsRef(in key)));
        }

        if (typeof(TQueryKey) == typeof(ByteArrayKey))
        {
            return typeof(TQuery) == typeof(ReadOnlySpan<byte>)
                ? Unsafe.As<TQuery, ReadOnlySpan<byte>>(ref Unsafe.AsRef(in query)).CommonPrefixLength(Unsafe.As<TKey, byte[]>(ref Unsafe.AsRef(in key)))
                : Unsafe.As<TQuery, byte[]>(ref Unsafe.AsRef(in query)).AsSpan().CommonPrefixLength(Unsafe.As<TKey, byte[]>(ref Unsafe.AsRef(in key)));
        }

        return TQueryKey.CommonPrefixLength(query, key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TKey ToKey(in TQuery query)
    {
        if (typeof(TQuery) == typeof(TKey) || (!typeof(TQuery).IsValueType && typeof(TQueryKey) == typeof(StringKey)) ||
            (!typeof(TQuery).IsValueType && typeof(TQueryKey) == typeof(ByteArrayKey)))
        {
            return Unsafe.As<TQuery, TKey>(ref Unsafe.AsRef(in query));
        }

        return TQueryKey.ToKey(query);
    }
}
