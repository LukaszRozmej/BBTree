// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Linq;
using NUnit.Framework;

namespace BBTree.Tests;

[Parallelizable(ParallelScope.All)]
public class InlineSlotsTests
{
    [Test]
    public void Count_matches_a_scalar_count([Values] bool wideKeys, [Values] bool leaf, [Values(0, 1, 7, 8, 9, 15, 16, 17, 31, 47, 48)] int count)
    {
        if (wideKeys) AssertCounts<ulong, IntegerKey<ulong>>(leaf, count);
        else AssertCounts<int, IntegerKey<int>>(leaf, count);
    }

    private static void AssertCounts<TKey, TSlots>(bool leaf, int count) where TSlots : struct, IInlineKey<TKey>
    {
        int width = InlineSlots<TKey, TSlots>.Width(leaf);
        ulong greatest = ulong.MaxValue >> (64 - 8 * width);
        Random random = new(count * 4 + width + (leaf ? 1 : 0));

        // Keys from the whole range, the top included, so a signed compare would misorder them; repeats test the equal run.
        ulong[] keys = new ulong[count];
        for (int i = 0; i < count; i++) keys[i] = random.Next(4) == 0 && i > 0 ? keys[i - 1] : (ulong)random.NextInt64() * 2 % greatest;
        if (count > 0) keys[^1] = greatest - 1;
        Array.Sort(keys);

        TSlots slots = InlineSlots<TKey, TSlots>.Empty;
        for (int i = 0; i < count; i++) InlineSlots<TKey, TSlots>.Set(leaf, ref slots, i, keys[i]);

        ulong[] targets = [.. keys, .. keys.Select(k => k + 1), 0, 1, greatest - 1, greatest, (ulong)random.NextInt64() % greatest];
        foreach (ulong target in targets)
        {
            int less = InlineSlots<TKey, TSlots>.Count(leaf, ref slots, count, target, out int equal);
            Assert.That(less, Is.EqualTo(keys.Count(k => k < target)), $"below {target:x}");
            Assert.That(equal, Is.EqualTo(keys.Count(k => k == target)), $"equal to {target:x}");
        }
    }
}
