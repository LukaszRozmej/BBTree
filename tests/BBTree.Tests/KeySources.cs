// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BBTree.Tests;

/// <summary>A 32-byte key, as a hash, ordered by its bytes.</summary>
public struct Bytes32
{
    public ulong First;
    public ulong Second;
    public ulong Third;
    public ulong Fourth;

    public static Span<byte> Bytes(ref Bytes32 hash) => MemoryMarshal.AsBytes(new Span<Bytes32>(ref hash));
}

/// <summary>A key class of its own, ordered by name, then by age.</summary>
public sealed record Person(string Name, int Age) : IComparable<Person>
{
    public int CompareTo(Person? other)
    {
        int byName = string.CompareOrdinal(Name, other!.Name);
        return byName != 0 ? byName : Age.CompareTo(other.Age);
    }
}

/// <summary>The first 3 chars of a name, which order like the names: a prefix that ties often, past the shared starts.</summary>
public readonly struct PersonPrefix : ISortPrefix<Person>
{
    public static ulong Of(in Person key)
    {
        ulong prefix = 0;
        for (int i = 0; i < 3 && i < key.Name.Length; i++) prefix |= (ulong)key.Name[i] << (48 - 16 * i);
        return prefix;
    }
}

/// <summary>Keys that mix random, dense and prefix-sharing values, so that inline keys tie and prefixes change.</summary>
internal static class KeySources
{
    public static ulong UInt64(Random random) => random.Next(4) switch
    {
        0 => (ulong)random.NextInt64() << 1 ^ (uint)random.Next(2),
        1 => 0x1234_5678_9A00_0000UL | (uint)random.Next(50_000),
        2 => (ulong)random.Next(200_000),
        _ => ulong.MaxValue - (ulong)random.Next(1_000),
    };

    public static long Int64(Random random) => random.Next(3) switch
    {
        0 => random.NextInt64(long.MinValue, long.MaxValue),
        1 => random.Next(-50_000, 50_000),
        _ => long.MinValue + random.Next(1_000),
    };

    public static int Int32(Random random) => random.Next(3) switch
    {
        0 => random.Next(int.MinValue, int.MaxValue),
        1 => random.Next(-50_000, 50_000),
        _ => int.MaxValue - random.Next(1_000),
    };

    public static short Int16(Random random) => (short)random.Next(short.MinValue, short.MaxValue + 1);

    public static sbyte SByte(Random random) => (sbyte)random.Next(sbyte.MinValue, sbyte.MaxValue + 1);

    public static Int128 Int128(Random random) =>
        random.Next(2) == 0
            ? new Int128((ulong)random.NextInt64(), (ulong)random.NextInt64())
            : -(Int128)random.Next(100_000);

    /// <summary>Doubles of all classes: zeros of both signs, infinities, NaNs of both signs and two payloads, subnormals, and close values.</summary>
    public static double Double(Random random) => random.Next(10) switch
    {
        0 => random.Next(2) == 0 ? 0.0 : -0.0,
        1 => random.Next(2) == 0 ? double.PositiveInfinity : double.NegativeInfinity,
        2 => BitConverter.Int64BitsToDouble((random.Next(2) == 0 ? 0x7FF8_0000_0000_0000 : unchecked((long)0xFFF8_0000_0000_0000)) | (long)random.Next(2)),
        3 => double.Epsilon * random.Next(-100, 100),
        4 => 1.0 + random.Next(1_000) * double.Epsilon * (1L << 52),
        _ => (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-20, 20)),
    };

    /// <summary>The IEEE 754 total order of doubles, from the classic bit trick rather than from <see cref="FloatKey{T}"/>.</summary>
    public static readonly IComparer<double> TotalOrder = Comparer<double>.Create(static (x, y) =>
    {
        long a = BitConverter.DoubleToInt64Bits(x);
        long b = BitConverter.DoubleToInt64Bits(y);
        a ^= (long)((ulong)(a >> 63) >> 1);
        b ^= (long)((ulong)(b >> 63) >> 1);
        return a.CompareTo(b);
    });

    public static Person Person(Random random)
    {
        string[] starts = ["", "Ann", "Anna", "Annabel", "Bob", "Zoe"];
        string name = starts[random.Next(starts.Length)] + (char)('a' + random.Next(26)) + random.Next(100);
        return new Person(name, random.Next(3));
    }

    public static UInt128 UInt128(Random random) =>
        random.Next(2) == 0
            ? new UInt128((ulong)random.NextInt64(), (ulong)random.NextInt64())
            : new UInt128(0xABCD, (ulong)random.Next(100_000));

    public static string String(Random random)
    {
        const string alphabet = "ab\0ÿzĀ￿";
        string prefix = random.Next(4) switch
        {
            0 => "",
            1 => "shared/prefix/that/is/long/",
            2 => "shared/prefix/",
            _ => "s",
        };

        Span<char> chars = stackalloc char[random.Next(0, 12)];
        for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
        return prefix + new string(chars);
    }

    public static byte[] ByteArray(Random random)
    {
        byte[] bytes = new byte[random.Next(0, 14)];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(random.Next(3) == 0 ? 0 : random.Next(4) * 85);
        return random.Next(3) == 0 ? [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, .. bytes] : bytes;
    }

    public static Bytes32 Hash(Random random)
    {
        Bytes32 hash = default;
        Span<byte> bytes = Bytes32.Bytes(ref hash);
        random.NextBytes(bytes);
        if (random.Next(2) == 0) bytes[..28].Fill(0x42);
        return hash;
    }

    public static readonly IComparer<byte[]> ByteArrayComparer =
        Comparer<byte[]>.Create(static (x, y) => x.AsSpan().SequenceCompareTo(y));

    public static readonly IComparer<Bytes32> HashComparer =
        Comparer<Bytes32>.Create(static (x, y) => Bytes32.Bytes(ref x).SequenceCompareTo(Bytes32.Bytes(ref y)));
}
