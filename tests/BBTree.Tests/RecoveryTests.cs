// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace BBTree.Tests;

[NonParallelizable]
public class RecoveryTests
{
    [Test]
    public void Failed_conversion_preserves_count_and_enumeration()
    {
        IntegerKey<int>.Map<int> map = [new(0, 0)];
        var range = map.EnumerateFrom(0);
        Assert.That(() => map.GetAlternateLookup<long, FailingConversion>().TryAdd(1, 1), Throws.InvalidOperationException);
        Assert.That(map.Count, Is.EqualTo(1));
        Assert.That(map.ToArray(), Is.EqualTo(new[] { new System.Collections.Generic.KeyValuePair<int, int>(0, 0) }));
        Assert.That(range.Select(static entry => entry.Key), Is.EqualTo(new[] { 0 }));
        Assert.That(map.TryAdd(1, 1), Is.True);
    }

    [Test]
    public void Failed_split_rolls_back_every_modified_node([Values(48, 1776)] int count)
    {
        int failures = 0;
        for (int failAt = 1; ; failAt++)
        {
            ComparableKey<int, FailingPrefix>.Map<int> map = new();
            for (int i = 0; i < count; i++) map[i] = i;
            var before = map.ToArray();
            bool failed = false;
            FailingPrefix.Remaining = failAt;
            try
            {
                map.TryAdd(count, count);
            }
            catch (InvalidOperationException)
            {
                failed = true;
                failures++;
            }
            finally
            {
                FailingPrefix.Remaining = 0;
            }

            TreeInvariants<int, int, ComparableKey<int, FailingPrefix>>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength);
            if (!failed) break;
            Assert.That(map.Count, Is.EqualTo(count));
            Assert.That(map.ToArray(), Is.EqualTo(before));
            for (int i = 0; i < count; i++) Assert.That(map[i], Is.EqualTo(i));
            Assert.That(map.TryAdd(count, count), Is.True);
        }

        Assert.That(failures, Is.GreaterThan(2));
    }

    [Test]
    public void Failed_rebalance_rolls_back_removal([Values] bool removeFirst)
    {
        int failures = 0;
        for (int failAt = 1; ; failAt++)
        {
            ComparableKey<int, FailingPrefix>.Map<int> map = new();
            for (int i = 0; i < 80; i++) map[i] = i;
            for (int i = 0; i < 30; i++) map.Remove(i);
            var before = map.ToArray();
            bool failed = false;
            FailingPrefix.Remaining = failAt;
            try
            {
                if (removeFirst) map.TryRemoveFirst(out _, out _);
                else map.Remove(30);
            }
            catch (InvalidOperationException)
            {
                failed = true;
                failures++;
            }
            finally { FailingPrefix.Remaining = 0; }

            TreeInvariants<int, int, ComparableKey<int, FailingPrefix>>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength);
            if (!failed) break;
            Assert.That(map.Count, Is.EqualTo(before.Length));
            Assert.That(map.ToArray(), Is.EqualTo(before));
            foreach (var entry in before) Assert.That(map[entry.Key], Is.EqualTo(entry.Value));
            Assert.That(map.Remove(30), Is.True);
        }

        Assert.That(failures, Is.GreaterThan(2));
    }

    [Test]
    public void Failed_prefix_shrink_can_be_retried()
    {
        BBTreeMap<int, int, FailingInteger> map = new();
        for (int i = 0; i < 120; i++) map[i] = i;
        int sharedLength = map.SharedLength;
        FailingInteger.FailKey = 119;
        try
        {
            Assert.That(() => map.TryAdd(int.MaxValue, -1), Throws.InvalidOperationException);
        }
        finally { FailingInteger.FailKey = null; }

        Assert.That(map.SharedLength, Is.EqualTo(sharedLength));
        Assert.That(map.Count, Is.EqualTo(120));
        for (int i = 0; i < 120; i++) Assert.That(map[i], Is.EqualTo(i));
        Assert.That(map.TryAdd(int.MaxValue, -1), Is.True);
        TreeInvariants<int, int, FailingInteger>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength);
    }

    [Test]
    public async Task Range_retries_comparison_against_keys_moved_by_a_split()
    {
        ComparableKey<BlockingKey>.ConcurrentMap<int> map = new();
        for (int i = 0; i < 48; i++) map[new BlockingKey(i)] = i;
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim completed = new();
        BlockingKey start = new(47, () =>
        {
            entered.Set();
            if (!completed.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        });
        Task writer = Task.Run(() =>
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            try { map[new BlockingKey(48)] = 48; }
            finally { completed.Set(); }
        });

        try
        {
            Assert.That(map.EnumerateFrom(start).Select(static entry => entry.Key.Value), Is.EqualTo(new[] { 47, 48 }));
        }
        finally
        {
            await writer;
        }
    }

    [Test]
    public void Range_propagates_persistent_comparison_failure()
    {
        ComparableKey<FailingKey>.ConcurrentMap<int> map = new();
        map[new FailingKey(false)] = 0;
        FailingKey query = new(true);
        Assert.That(() => map.EnumerateFrom(query).GetEnumerator(), Throws.InvalidOperationException);
        Assert.That(query.Calls, Is.EqualTo(8));
    }

    [Test]
    public void Churn_reclaims_empty_nodes_and_preserves_suspended_enumerators([Values] bool keepStableKeys)
    {
        IntegerKey<int>.ConcurrentMap<int> map = new();
        for (int round = 0; round < 4; round++)
        {
            int start = round * 10_000;
            for (int i = 0; i < 10_000; i++) map[start + i] = start + i;
            using var entries = map.GetEnumerator();
            Assert.That(entries.MoveNext(), Is.True);
            int last = entries.Current.Key;
            for (int i = 0; i < 10_000; i++)
            {
                if (!keepStableKeys || i % 1000 != 0) Assert.That(map.TryRemove(start + i, out _), Is.True);
            }

            int[] expected = map.Where(entry => entry.Key > last).Select(static entry => entry.Key).ToArray();
            System.Collections.Generic.List<int> remaining = [];
            while (entries.MoveNext()) remaining.Add(entries.Current.Key);
            Assert.That(remaining, Is.EqualTo(expected));
            Assert.That(CountNodes(map.Root), Is.LessThan(100));
            Assert.That(map.Count, Is.EqualTo(keepStableKeys ? (round + 1) * 10 : 0));
            TreeInvariants<int, int, IntegerKey<int>>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength, concurrent: true);
        }
    }

    [Test]
    public async Task Compaction_preserves_stable_keys_during_concurrent_churn()
    {
        IntegerKey<int>.ConcurrentMap<int> map = new();
        for (int i = 0; i < 100; i++) map[i] = i;
        using CancellationTokenSource finished = new();
        Task reader = Task.Run(() =>
        {
            while (!finished.IsCancellationRequested)
            {
                for (int i = 0; i < 100; i++) Assert.That(map[i], Is.EqualTo(i));
                Assert.That(map.Take(100).Select(static entry => entry.Key), Is.EqualTo(Enumerable.Range(0, 100)));
            }
        });
        Task[] writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
        {
            for (int round = 0; round < 8; round++)
            {
                int start = 100 + writer * 100_000 + round * 10_000;
                for (int i = 0; i < 3000; i++) Assert.That(map.TryAdd(start + i, i), Is.True);
                for (int i = 0; i < 3000; i++)
                {
                    Assert.That(map.TryRemove(start + i, out int value), Is.True);
                    Assert.That(value, Is.EqualTo(i));
                }
            }
        })).ToArray();
        try { await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { finished.Cancel(); }
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(map.Count, Is.EqualTo(100));
        Assert.That(map.Select(static entry => entry.Key), Is.EqualTo(Enumerable.Range(0, 100)));
        Assert.That(CountNodes(map.Root), Is.LessThan(100));
        TreeInvariants<int, int, IntegerKey<int>>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength, concurrent: true);
    }

    private static int CountNodes(Node<int, IntegerKey<int>> node)
    {
        if (node.IsLeaf) return 1;
        InternalNode<int, IntegerKey<int>> parent = (InternalNode<int, IntegerKey<int>>)node;
        int count = 1;
        for (int i = 0; i <= node.Count; i++) count += CountNodes(parent.ChildAt(i));
        return count;
    }

    [Test]
    public void Compacted_string_map_accepts_keys_outside_its_old_prefix()
    {
        StringKey.ConcurrentMap<string> map = new();
        for (int i = 0; i < 5000; i++) map[$"long/common/prefix/{i:D5}"] = i.ToString();
        for (int i = 0; i < 5000; i++)
        {
            if (i % 100 != 0) map.TryRemove($"long/common/prefix/{i:D5}", out _);
        }

        map["a"] = "first";
        map["z"] = "last";
        for (int i = 0; i < 5000; i += 100) Assert.That(map[$"long/common/prefix/{i:D5}"], Is.EqualTo(i.ToString()));
        Assert.That(map.Count, Is.EqualTo(52));
        Assert.That(map.First().Value, Is.EqualTo("first"));
        Assert.That(map.Last().Value, Is.EqualTo("last"));
        TreeInvariants<string, string, StringKey>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength, concurrent: true);
    }

    [Test]
    public void Failed_compaction_releases_locks_and_preserves_committed_removal()
    {
        ComparableKey<int, FailingPrefix>.ConcurrentMap<int> map = new();
        for (int i = 0; i < 3000; i++) map[i] = i;
        Node<int, ComparableKey<int, FailingPrefix>> node = map.Root;
        while (!node.IsLeaf) node = ((InternalNode<int, ComparableKey<int, FailingPrefix>>)node).ChildAt(node.Count);
        FailingPrefix.FailKey = ((Leaf<int, int, ComparableKey<int, FailingPrefix>>)node).EntryAt(0).Key;
        try
        {
            for (int i = 0; i < 2000; i++) Assert.That(map.TryRemove(i, out _), Is.True);
            Assert.That(FailingPrefix.FailKey, Is.Null, "compaction reached the injected callback failure");
        }
        finally { FailingPrefix.FailKey = null; }

        for (int i = 2000; i < 3000; i++) Assert.That(map[i], Is.EqualTo(i));
        Assert.That(map.Count, Is.EqualTo(1000));
        Assert.That(map.TryAdd(3000, 3000), Is.True);
        TreeInvariants<int, int, ComparableKey<int, FailingPrefix>>.Check(map.Root, map.Count, map.SharedKey, map.SharedLength, concurrent: true);
    }

    private struct FailingConversion : IKeyQuery<long, int>
    {
        public static int Compare(in long query, in int key) => query.CompareTo(key);
        public static ulong WindowAt(in long query, int offset) => IntegerKey<int>.WindowAt((int)query, offset);
        public static int CommonPrefixLength(in long query, in int key) => IntegerKey<int>.CommonPrefixLength((int)query, key);
        public static int Length(in long query) => sizeof(int);
        public static int ToKey(in long query) => throw new InvalidOperationException("Injected conversion failure.");
    }

    private struct FailingPrefix : ISortPrefix<int>
    {
        public static int Remaining;
        public static int? FailKey;
        public static ulong Of(in int key)
        {
            if (key == FailKey)
            {
                FailKey = null;
                throw new InvalidOperationException("Injected compaction failure.");
            }
            if (Remaining > 0 && --Remaining == 0) throw new InvalidOperationException("Injected prefix failure.");
            return (ulong)(uint)key << 32;
        }
    }

    [InlineArray(BBTreeLayout.NodeCapacity)]
    private struct FailingInteger : IInlineKey<int>
    {
        private ushort _slot;
        public static int? FailKey;
        public static int Compare(in int x, in int y) => x.CompareTo(y);
        public static int CommonPrefixLength(in int x, in int y) => IntegerKey<int>.CommonPrefixLength(x, y);
        public static int Length(in int key) => sizeof(int);
        public static int FixedLength => sizeof(int);
        public static ulong WindowAt(in int key, int offset) => key == FailKey
            ? throw new InvalidOperationException("Injected window failure.")
            : IntegerKey<int>.WindowAt(key, offset);
    }

    private sealed class BlockingKey(int value, Action? beforeCompare = null) : IComparable<BlockingKey>
    {
        public int Value { get; } = value;
        private Action? _beforeCompare = beforeCompare;
        public int CompareTo(BlockingKey? other)
        {
            Interlocked.Exchange(ref _beforeCompare, null)?.Invoke();
            return Value.CompareTo(other!.Value);
        }
    }

    private sealed class FailingKey(bool fail) : IComparable<FailingKey>
    {
        public int Calls;
        public int CompareTo(FailingKey? other)
        {
            Calls++;
            return fail ? throw new InvalidOperationException("Injected comparison failure.") : 0;
        }
    }
}
