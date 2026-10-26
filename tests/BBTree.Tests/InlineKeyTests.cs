// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace BBTree.Tests;

[Parallelizable(ParallelScope.All)]
public class InlineKeyTests
{
    [Test]
    public void String_windows_order_like_the_strings([Values(1, 2, 3)] int seed)
    {
        Random random = new(seed);
        for (int i = 0; i < 50_000; i++)
        {
            string x = RandomString(random);
            string y = random.Next(2) == 0 ? RandomString(random) : x[..random.Next(x.Length + 1)] + RandomString(random);
            int shared = StringKey.CommonPrefixLength(x, y);
            Assert.That(shared, Is.EqualTo(x.AsSpan().CommonPrefixLength(y)));
            for (int offset = 0; offset <= shared; offset++)
            {
                ulong windowX = StringKey.WindowAt(x, offset);
                ulong windowY = StringKey.WindowAt(y, offset);
                if (windowX != windowY)
                {
                    Assert.That(Math.Sign(windowX.CompareTo(windowY)), Is.EqualTo(Math.Sign(string.CompareOrdinal(x, y))), $"\"{x}\" and \"{y}\" at {offset}");
                }
            }
        }
    }

    [Test]
    public void Byte_windows_are_the_bytes_big_endian_zero_past_the_end([Range(0, 20)] int length)
    {
        byte[] bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        for (int offset = 0; offset <= 20; offset++)
        {
            ulong expected = 0;
            for (int i = 0; i < 8 && offset + i < length; i++) expected |= (ulong)bytes[offset + i] << (56 - 8 * i);
            Assert.That(ByteWindows.At(bytes, offset), Is.EqualTo(expected), $"length {length}, offset {offset}");
        }
    }

    [Test]
    public void Number_windows_order_like_the_numbers()
    {
        CheckWindows<int, IntegerKey<int>>(Comparer<int>.Default);
        CheckWindows<short, IntegerKey<short>>(Comparer<short>.Default);
        CheckWindows<sbyte, IntegerKey<sbyte>>(Comparer<sbyte>.Default);
        CheckWindows<byte, IntegerKey<byte>>(Comparer<byte>.Default);
        CheckWindows<ushort, IntegerKey<ushort>>(Comparer<ushort>.Default);
        CheckWindows<uint, IntegerKey<uint>>(Comparer<uint>.Default);
        CheckWindows<ulong, IntegerKey<ulong>>(Comparer<ulong>.Default);
        CheckWindows<long, IntegerKey<long>>(Comparer<long>.Default);
        CheckWindows<char, IntegerKey<char>>(Comparer<char>.Default);
        CheckWindows<nint, IntegerKey<nint>>(Comparer<nint>.Default);
        CheckWindows<Int128, IntegerKey<Int128>>(Comparer<Int128>.Default);
        CheckWindows<UInt128, IntegerKey<UInt128>>(Comparer<UInt128>.Default);
        CheckWindows<double, FloatKey<double>>(KeySources.TotalOrder);
        CheckWindows<float, FloatKey<float>>(Comparer<float>.Create(static (x, y) => TotalOrderBits(BitConverter.SingleToInt32Bits(x)).CompareTo(TotalOrderBits(BitConverter.SingleToInt32Bits(y)))));
        CheckWindows<Half, FloatKey<Half>>(Comparer<Half>.Create(static (x, y) => TotalOrderBits(BitConverter.HalfToInt16Bits(x)).CompareTo(TotalOrderBits(BitConverter.HalfToInt16Bits(y)))));
    }

    [Test]
    public void Floats_follow_the_total_order()
    {
        double negativeNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0000));
        double positiveNaN = BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0000);
        double[] ascending =
        [
            negativeNaN, double.NegativeInfinity, double.MinValue, -1.0, -double.Epsilon, -0.0,
            0.0, double.Epsilon, 1.0, double.MaxValue, double.PositiveInfinity, positiveNaN,
        ];
        Assert.That(BitConverter.DoubleToInt64Bits(double.NaN), Is.EqualTo(BitConverter.DoubleToInt64Bits(negativeNaN)), "double.NaN is a negative NaN, so it orders first");
        for (int i = 1; i < ascending.Length; i++)
        {
            Assert.That(FloatKey<double>.Compare(ascending[i - 1], ascending[i]), Is.LessThan(0), $"{ascending[i - 1]} before {ascending[i]}");
        }

        FloatKey<double>.Map<int> map = new();
        foreach (double value in ascending.Reverse()) map.Add(value, 0);
        Assert.That(map.Keys.Select(BitConverter.DoubleToInt64Bits), Is.EqualTo(ascending.Select(BitConverter.DoubleToInt64Bits)));
    }

    [Test]
    public void Nested_map_types_are_the_maps_of_their_keys()
    {
        IntegerKey<int>.Map<string> numbers = new() { { 3, "c" }, { -1, "a" }, { 2, "b" } };
        Assert.That(numbers.Keys, Is.EqualTo(new[] { -1, 2, 3 }));
        Assert.That(numbers, Is.InstanceOf<BBTreeMap<int, string, IntegerKey<int>>>());

        StringKey.ConcurrentMap<int> words = new();
        Assert.That(words.TryAdd("b", 2) && words.TryAdd("a", 1) && !words.TryAdd("a", 3), Is.True);
        Assert.That(words.Select(static e => e.Key), Is.EqualTo(new[] { "a", "b" }));

        ComparableKey<Person, PersonPrefix>.Map<int> people = new() { [new Person("Bob", 2)] = 1, [new Person("Ann", 9)] = 2 };
        Assert.That(people.Keys.First(), Is.EqualTo(new Person("Ann", 9)));
    }

    /// <summary>
    /// Checks the inline key contract on random pairs, many of which share leading bytes: keys whose windows differ
    /// order like their windows, a shared prefix is shared, and equal keys of a fixed length share all their bytes.
    /// </summary>
    private static void CheckWindows<T, TKey>(IComparer<T> order)
        where T : unmanaged
        where TKey : struct, IInlineKey<T>
    {
        Random random = new(Marshal.SizeOf<T>() * 31 + typeof(T).Name.Length);
        int size = TKey.FixedLength;
        for (int i = 0; i < 20_000; i++)
        {
            T x = RandomOf<T>(random);
            T y = x;
            Span<byte> bytes = MemoryMarshal.AsBytes(new Span<T>(ref y));
            if (random.Next(2) == 0) random.NextBytes(bytes);
            else bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));

            int sign = Math.Sign(order.Compare(x, y));
            Assert.That(Math.Sign(TKey.Compare(x, y)), Is.EqualTo(sign), $"{typeof(TKey).Name}: {x} and {y}");
            int shared = TKey.CommonPrefixLength(x, y);
            Assert.That(shared == size, Is.EqualTo(sign == 0), $"{typeof(TKey).Name}: equal keys share all bytes, {x} and {y}");
            for (int offset = 0; offset <= Math.Min(shared, size - 1); offset++)
            {
                ulong windowX = TKey.WindowAt(x, offset);
                ulong windowY = TKey.WindowAt(y, offset);
                if (windowX != windowY) Assert.That(Math.Sign(windowX.CompareTo(windowY)), Is.EqualTo(sign), $"{typeof(TKey).Name}: {x} and {y} at {offset}");
                int sharedHere = Math.Min(shared - offset, sizeof(ulong));
                if (sharedHere > 0) Assert.That((windowX ^ windowY) >> (64 - 8 * sharedHere), Is.Zero, $"{typeof(TKey).Name}: {x} and {y} share {shared} bytes");
            }
        }
    }

    private static T RandomOf<T>(Random random) where T : unmanaged
    {
        T value = default;
        random.NextBytes(MemoryMarshal.AsBytes(new Span<T>(ref value)));
        return value;
    }

    private static long TotalOrderBits(long bits) => bits ^ (long)((ulong)(bits >> 63) >> 1);

    private static int TotalOrderBits(int bits) => bits ^ (int)((uint)(bits >> 31) >> 1);

    private static short TotalOrderBits(short bits) => (short)(bits ^ (short)((ushort)(bits >> 15) >> 1));

    private static string RandomString(Random random)
    {
        const string alphabet = "ab\0þÿzĀ中￿";
        char[] chars = new char[random.Next(0, 20)];
        for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }
}
