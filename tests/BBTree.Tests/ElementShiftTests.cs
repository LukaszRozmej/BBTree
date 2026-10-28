// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace BBTree.Tests;

[Parallelizable(ParallelScope.All)]
public class ElementShiftTests
{
    private const int MaxStep = 64;
    private const int MaxLength = 200;

    [Test]
    public void Moves_like_an_overlap_safe_copy_and_writes_only_the_destination(
        [Values] bool up,
        [Values(1, 2, 3, 4, 8, 12, 16, 24, 32, 40, 48, 64)] int step)
    {
        for (int length = 0; length <= MaxLength; length++)
        {
            byte[] actual = new byte[MaxStep + MaxLength + MaxStep];
            for (int i = 0; i < actual.Length; i++) actual[i] = (byte)(i * 7 + 1);
            byte[] expected = (byte[])actual.Clone();

            int start = MaxStep;
            int destination = up ? start + step : start - step;
            expected.AsSpan(start, length).CopyTo(expected.AsSpan(destination, length));
            if (up) ElementShift.Up(ref actual[start], length, step);
            else ElementShift.Down(ref actual[start], length, step);

            Assert.That(actual, Is.EqualTo(expected), $"{(up ? "up" : "down")} length {length} step {step}");
        }
    }

    [Test]
    public void Inserting_below_existing_keys_keeps_them([Values] bool concurrent)
    {
        long[] keys = [0, 2, 4, 6, 8, 10, 12, 14, 9];
        IReadOnlyDictionary<long, long> map;
        if (concurrent)
        {
            ConcurrentBBTreeMap<long, long, IntegerKey<long>> concurrentMap = new();
            foreach (long key in keys) concurrentMap[key] = key;
            map = concurrentMap;
        }
        else
        {
            BBTreeMap<long, long, IntegerKey<long>> singleMap = new();
            foreach (long key in keys) singleMap[key] = key;
            map = singleMap;
        }

        Assert.That(map.Keys, Is.EqualTo(new long[] { 0, 2, 4, 6, 8, 9, 10, 12, 14 }));
        Assert.That(map.TryGetValue(14, out long value) && value == 14);
    }
}
