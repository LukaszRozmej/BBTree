// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
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
            int shared = StringInlineKey.CommonPrefixLength(x, y);
            Assert.That(shared, Is.EqualTo(x.AsSpan().CommonPrefixLength(y)));
            for (int offset = 0; offset <= shared; offset++)
            {
                ulong windowX = StringInlineKey.WindowAt(x, offset);
                ulong windowY = StringInlineKey.WindowAt(y, offset);
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

    private static string RandomString(Random random)
    {
        const string alphabet = "ab\0þÿzĀ中￿";
        char[] chars = new char[random.Next(0, 20)];
        for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }
}
