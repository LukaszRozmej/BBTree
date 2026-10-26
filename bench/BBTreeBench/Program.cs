// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

// The single-map cells of bbtree's own benchmark (bbtree/bench, operations/single_map.rs),
// with the same key generators, orders and operation counts, so that the numbers compare
// with the Rust crate's on one machine.

using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using BBTree;

string[] keyKinds = Option("--key", "u64,str-json");
string[] sizes = Option("--size", "1M,10M");
string[] maps = Option("--map", "bbtree,sorted-dictionary");
string[] operations = Option("--op", "random_insert,get_hit,get_miss,iterate,range64,random_remove");
int threads = int.Parse(Option("--threads", "1")[0]);
int reps = int.Parse(Option("--reps", "1")[0]);

Console.WriteLine($"{RuntimeInformationText()}");
foreach (string key in keyKinds)
{
    foreach (string size in sizes)
    {
        int count = size switch { "1k" => 1_000, "10k" => 10_000, "100k" => 100_000, "1M" => 1_000_000, "10M" => 10_000_000, _ => throw new ArgumentException(size) };
        if (key == "u64")
        {
            Cells<ulong> cells = CellMaker.UInt64(count);
            foreach (string map in maps) RunMap(cells, key, size, map, operations, threads, reps);
        }
        else if (key == "bytes-json")
        {
            Cells<byte[]> cells = CellMaker.JsonBytes(count);
            foreach (string map in maps) RunMap(cells, key, size, map, operations, threads, reps);
        }
        else
        {
            Cells<string> cells = CellMaker.JsonStrings(count);
            foreach (string map in maps) RunMap(cells, key, size, map, operations, threads, reps);
        }
    }
}

return;

string[] Option(string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return (index >= 0 ? args[index + 1] : fallback).Split(',');
}

static string RuntimeInformationText() =>
    $"# {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, " +
    $"Vector512 {System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated}, Vector256 {System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}, " +
    $"server GC {GCSettings.IsServerGC}";

static void RunMap<TKey>(Cells<TKey> cells, string key, string size, string map, string[] operations, int threads, int reps) where TKey : notnull
{
    foreach (string operation in operations)
    {
        double[] samples = new double[reps];
        for (int rep = 0; rep < reps; rep++) samples[rep] = Cell();
        Array.Sort(samples);
        double nanoseconds = samples[reps / 2];
        Console.WriteLine($"{map} {key}_u64_{operation}_{size}: {Format(nanoseconds)} ns/op (min {Format(samples[0])}, max {Format(samples[^1])}; {Report.GcNote})");
        continue;

        double Cell() => (key, map) switch
        {
            ("u64", "bbtree") => Measure<ulong, BBTreeAdapter<ulong, UInt64InlineKey>>((Cells<ulong>)(object)cells, operation, threads),
            ("u64", "bbtree-concurrent") => Measure<ulong, ConcurrentAdapter<ulong, UInt64InlineKey>>((Cells<ulong>)(object)cells, operation, threads),
            ("u64", "sorted-dictionary") => Measure<ulong, SortedDictionaryAdapter<ulong>>((Cells<ulong>)(object)cells, operation, threads),
            ("str-json", "bbtree") => Measure<string, BBTreeAdapter<string, StringInlineKey>>((Cells<string>)(object)cells, operation, threads),
            ("str-json", "bbtree-concurrent") => Measure<string, ConcurrentAdapter<string, StringInlineKey>>((Cells<string>)(object)cells, operation, threads),
            ("bytes-json", "bbtree") => Measure<byte[], BBTreeAdapter<byte[], ByteArrayInlineKey>>((Cells<byte[]>)(object)cells, operation, threads),
            ("str-json", "sorted-dictionary") => Measure<string, SortedDictionaryAdapter<string>>((Cells<string>)(object)cells, operation, threads),
            _ => throw new ArgumentException($"{key} {map}"),
        };
    }
}

static double Measure<TKey, TMap>(Cells<TKey> cells, string operation, int threads)
    where TKey : notnull
    where TMap : struct, IBenchMap<TKey, TMap>
{
    // Tier every method of the cell up before timing it: run it on a small map until the JIT has
    // compiled the optimized code, as the Rust bench measures compiled code.
    Cells<TKey> warmup = cells.Small();
    for (int round = 0; round < 3; round++)
    {
        for (int i = 0; i < 40; i++) Time<TKey, TMap>(warmup, operation, threads);
        Thread.Sleep(200);
    }

    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    TimeSpan pausesBefore = GC.GetTotalPauseDuration();
    int gen0Before = GC.CollectionCount(0), gen1Before = GC.CollectionCount(1), gen2Before = GC.CollectionCount(2);
    (long ticks, long ops) = Time<TKey, TMap>(cells, operation, threads);
    double pauseNanoseconds = (GC.GetTotalPauseDuration() - pausesBefore).TotalNanoseconds;
    Report.GcNote = $"gc {pauseNanoseconds / ops:F1} ns/op, collections {GC.CollectionCount(0) - gen0Before}/{GC.CollectionCount(1) - gen1Before}/{GC.CollectionCount(2) - gen2Before}";
    return ticks * (1e9 / Stopwatch.Frequency) / ops;
}

// Iteration costs under a nanosecond an entry: two decimals tell such cells apart.
static string Format(double nanoseconds) => nanoseconds < 10 ? nanoseconds.ToString("F2") : nanoseconds.ToString("F1");

static (long Ticks, long Ops) Time<TKey, TMap>(Cells<TKey> cells, string operation, int threads)
    where TKey : notnull
    where TMap : struct, IBenchMap<TKey, TMap>
{
    switch (operation)
    {
        case "random_insert":
            {
                // Small maps are built again until the budget, as the Rust bench does.
                int rounds = (cells.Budget + cells.Present.Length - 1) / cells.Present.Length;
                long ticks = 0;
                for (int round = 0; round < rounds; round++)
                {
                    long start = Stopwatch.GetTimestamp();
                    TMap map = Build<TKey, TMap>(cells.Present);
                    ticks += Stopwatch.GetTimestamp() - start;
                    Check(map.Count == cells.Present.Length);
                }

                return (ticks, (long)rounds * cells.Present.Length);
            }
        case "get_hit" or "get_miss":
            {
                TMap map = Build<TKey, TMap>(cells.Present);
                bool hit = operation == "get_hit";
                int[] probes = hit ? cells.HitProbes : cells.MissProbes;
                TKey[] pool = hit ? cells.HitPool : cells.MissPool;
                if (threads == 1)
                {
                    long start = Stopwatch.GetTimestamp();
                    (int hits, ulong _) = Lookups(map, pool, probes);
                    long ticks = Stopwatch.GetTimestamp() - start;
                    Check(hits == (hit ? probes.Length : 0));
                    return (ticks, probes.Length);
                }

                // Every thread runs all probes from an offset of its own; the time is the wall time of all of them.
                using Barrier barrier = new(threads + 1);
                int[] hitsPerThread = new int[threads];
                Thread[] workers = new Thread[threads];
                for (int t = 0; t < threads; t++)
                {
                    int thread = t;
                    int offset = thread * probes.Length / threads;
                    int[] rotated = [.. probes[offset..], .. probes[..offset]];
                    workers[t] = new Thread(() =>
                    {
                        barrier.SignalAndWait();
                        hitsPerThread[thread] = Lookups(map, pool, rotated).Hits;
                        barrier.SignalAndWait();
                    });
                    workers[t].Start();
                }

                barrier.SignalAndWait();
                long parallelStart = Stopwatch.GetTimestamp();
                barrier.SignalAndWait();
                long parallelTicks = Stopwatch.GetTimestamp() - parallelStart;
                foreach (Thread worker in workers) worker.Join();
                Check(hitsPerThread.Sum() == (hit ? (long)probes.Length * threads : 0));
                return (parallelTicks, (long)probes.Length * threads);
            }
        case "update":
            {
                TMap map = Build<TKey, TMap>(cells.Present);
                long start = Stopwatch.GetTimestamp();
                foreach (int probe in cells.HitProbes) map.Insert(cells.HitPool[probe], 1);
                long ticks = Stopwatch.GetTimestamp() - start;
                Check(map.Count == cells.Present.Length);
                return (ticks, cells.HitProbes.Length);
            }
        case "iterate":
            {
                TMap map = Build<TKey, TMap>(cells.Present);
                int passes = (cells.Budget + cells.Present.Length - 1) / cells.Present.Length;
                long start = Stopwatch.GetTimestamp();
                ulong sum = 0;
                for (int i = 0; i < passes; i++) sum += map.ChecksumAll();
                long ticks = Stopwatch.GetTimestamp() - start;
                GC.KeepAlive(sum);
                return (ticks, (long)passes * cells.Present.Length);
            }
        case "range64":
            {
                TMap map = Build<TKey, TMap>(cells.Present);
                long start = Stopwatch.GetTimestamp();
                ulong sum = 0;
                foreach (int probe in cells.RangeProbes) sum += map.ChecksumRange(cells.RangePool[probe], 64);
                long ticks = Stopwatch.GetTimestamp() - start;
                GC.KeepAlive(sum);
                return (ticks, cells.RangeProbes.Length);
            }
        case "random_remove":
            {
                // Small maps are built again until the budget, as the Rust bench does.
                int rounds = (cells.Budget + cells.Present.Length - 1) / cells.Present.Length;
                long ticks = 0;
                for (int round = 0; round < rounds; round++)
                {
                    TMap map = Build<TKey, TMap>(cells.Present);
                    long start = Stopwatch.GetTimestamp();
                    int removed = 0;
                    foreach (TKey key in cells.RemovalOrder) removed += map.Remove(key) ? 1 : 0;
                    ticks += Stopwatch.GetTimestamp() - start;
                    Check(removed == cells.Present.Length);
                }

                return (ticks, (long)rounds * cells.Present.Length);
            }
        default:
            throw new ArgumentException(operation);
    }
}

static TMap Build<TKey, TMap>(TKey[] keys)
    where TKey : notnull
    where TMap : struct, IBenchMap<TKey, TMap>
{
    TMap map = TMap.Create();
    for (int i = 0; i < keys.Length; i++) map.Insert(keys[i], (ulong)i);
    return map;
}

static (int Hits, ulong Sum) Lookups<TKey, TMap>(TMap map, TKey[] pool, int[] probes)
    where TKey : notnull
    where TMap : struct, IBenchMap<TKey, TMap>
{
    int hits = 0;
    ulong sum = 0;
    foreach (int probe in probes)
    {
        if (map.TryGet(pool[probe], out ulong value))
        {
            hits++;
            sum += value;
        }
    }

    return (hits, sum);
}

static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("the cell's result is wrong");
}

/// <summary>A cell's inputs, made as bbtree's bench makes them.</summary>
internal sealed class Cells<TKey>
{
    public required TKey[] Present { get; init; }
    public required TKey[] HitPool { get; init; }
    public required TKey[] MissPool { get; init; }
    public required TKey[] RangePool { get; init; }

    /// <summary>Indexes into a pool: the Rust bench probes through references into its pool, so each probe reads the pool.</summary>
    public required int[] HitProbes { get; init; }
    public required int[] MissProbes { get; init; }
    public required int[] RangeProbes { get; init; }
    public required TKey[] RemovalOrder { get; init; }
    public required int Budget { get; init; }
    public required Func<Cells<TKey>> Small { get; init; }
}

/// <summary>Makes cells as bbtree's bench makes them.</summary>
internal static class CellMaker
{
    public const int DefaultBudget = 1_000_000;

    public static Cells<ulong> UInt64(int count, int budget = DefaultBudget)
    {
        ulong[] present = new ulong[count];
        for (int i = 0; i < count; i++) present[i] = SeededRng.Mix(2 * (ulong)i);
        int missCount = Math.Min(budget, count);
        int stride = count / missCount;
        ulong[] absent = new ulong[missCount];
        for (int i = 0; i < missCount; i++) absent[i] = SeededRng.Mix(2 * (ulong)(i * stride) + 1);
        return Make(present, absent, budget, Comparer<ulong>.Default, () => UInt64(2_000, 2_000), static key => key);
    }

    public static Cells<string> JsonStrings(int count, int budget = DefaultBudget)
    {
        JsonKeys keys = new();
        string[] present = new string[count];
        for (int i = 0; i < count; i++) present[i] = keys.Next();
        string[] absent = new string[Math.Min(budget, count)];
        for (int i = 0; i < absent.Length; i++) absent[i] = keys.Next();
        return Make(present, absent, budget, StringComparer.Ordinal, () => JsonStrings(2_000, 2_000), static key => new string(key));
    }

    public static Cells<byte[]> JsonBytes(int count, int budget = DefaultBudget)
    {
        JsonKeys keys = new();
        byte[][] present = new byte[count][];
        for (int i = 0; i < count; i++) present[i] = System.Text.Encoding.UTF8.GetBytes(keys.Next());
        byte[][] absent = new byte[Math.Min(budget, count)][];
        for (int i = 0; i < absent.Length; i++) absent[i] = System.Text.Encoding.UTF8.GetBytes(keys.Next());
        IComparer<byte[]> comparer = Comparer<byte[]>.Create(static (x, y) => x.AsSpan().SequenceCompareTo(y));
        return Make(present, absent, budget, comparer, () => JsonBytes(2_000, 2_000), static key => (byte[])key.Clone());
    }

    private static Cells<TKey> Make<TKey>(TKey[] present, TKey[] absent, int budget, IComparer<TKey> comparer, Func<Cells<TKey>> small, Func<TKey, TKey> clone)
    {
        new SeededRng(SeededRng.InsertionOrder).Shuffle(present);

        int sampleCount = Math.Min(budget, present.Length);
        int stride = present.Length / sampleCount;
        TKey[] sample = new TKey[sampleCount];
        // Probes use keys of their own, as the Rust bench clones its pools.
        for (int i = 0; i < sampleCount; i++) sample[i] = clone(present[i * stride]);

        TKey[] starts = (TKey[])present.Clone();
        Array.Sort(starts, comparer);
        Array.Resize(ref starts, present.Length - 63);

        TKey[] removalOrder = (TKey[])present.Clone();
        new SeededRng(SeededRng.RemovalOrder).Shuffle(removalOrder);

        return new Cells<TKey>
        {
            Present = present,
            HitPool = sample,
            MissPool = absent,
            RangePool = starts,
            HitProbes = Probes(sample.Length, budget),
            MissProbes = Probes(absent.Length, budget),
            RangeProbes = Probes(starts.Length, budget),
            RemovalOrder = removalOrder,
            Budget = budget,
            Small = small,
        };
    }

    private static int[] Probes(int poolLength, int budget)
    {
        SeededRng random = new(SeededRng.LookupOrder);
        int[] probes = new int[budget];
        for (int i = 0; i < budget; i++) probes[i] = random.Below(poolLength);
        return probes;
    }
}

/// <summary>SplitMix64, with the streams of bbtree's bench.</summary>
internal sealed class SeededRng(int stream)
{
    public const int KeyContent = 0;
    public const int InsertionOrder = 1;
    public const int LookupOrder = 2;
    public const int RemovalOrder = 3;

    private ulong _state = Mix((ulong)stream);

    public ulong Next()
    {
        _state += 0x9E37_79B9_7F4A_7C15;
        return Mix(_state);
    }

    public int Below(int bound) => (int)Math.BigMul(Next(), (ulong)bound, out _);

    public void Shuffle<T>(T[] items)
    {
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = Below(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    public static ulong Mix(ulong x)
    {
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EB;
        return x ^ (x >> 31);
    }
}

/// <summary>JSON object keys with the field study's lengths, made distinct as bbtree's bench makes them.</summary>
internal sealed class JsonKeys
{
    private const string Characters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

    private static readonly (int Length, int Weight)[] LengthHistogram =
    [
        (1, 80), (2, 1440), (3, 2628), (4, 8867), (5, 6693), (6, 13055), (7, 3573), (8, 12255), (9, 2747),
        (10, 2597), (11, 4126), (12, 2288), (13, 1785), (14, 2797), (15, 1039), (16, 638), (17, 607), (18, 584),
        (19, 449), (20, 242), (21, 1254), (22, 175), (23, 739), (24, 207), (25, 173), (26, 173), (27, 1),
        (28, 520), (34, 173),
    ];

    private readonly SeededRng _random = new(SeededRng.KeyContent);
    private readonly HashSet<string> _seen = [];
    private readonly int _totalWeight = LengthHistogram.Sum(static entry => entry.Weight);

    public string Next()
    {
        string key = Text(Length());
        while (!_seen.Add(key)) key += Characters[_random.Below(Characters.Length)];
        return key;
    }

    private int Length()
    {
        int ticket = _random.Below(_totalWeight);
        foreach ((int length, int weight) in LengthHistogram)
        {
            if (ticket < weight) return length;
            ticket -= weight;
        }

        throw new UnreachableException();
    }

    private string Text(int length) => string.Create(length, _random, static (chars, random) =>
    {
        for (int i = 0; i < chars.Length; i++) chars[i] = Characters[random.Below(Characters.Length)];
    });
}

internal interface IBenchMap<TKey, TSelf> where TSelf : struct, IBenchMap<TKey, TSelf>
{
    static abstract TSelf Create();
    int Count { get; }
    void Insert(TKey key, ulong value);
    bool TryGet(TKey key, out ulong value);
    bool Remove(TKey key);
    ulong ChecksumAll();
    ulong ChecksumRange(TKey start, int length);
}

internal readonly struct BBTreeAdapter<TKey, TInlineKey>(BBTreeMap<TKey, ulong, TInlineKey> map) : IBenchMap<TKey, BBTreeAdapter<TKey, TInlineKey>>
    where TKey : notnull
    where TInlineKey : struct, IInlineKey<TKey>
{
    public static BBTreeAdapter<TKey, TInlineKey> Create() => new(new BBTreeMap<TKey, ulong, TInlineKey>());
    public int Count => map.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Insert(TKey key, ulong value) => map[key] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, out ulong value) => map.TryGetValue(key, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(TKey key) => map.Remove(key);

    public ulong ChecksumAll()
    {
        ulong sum = 0;
        foreach (KeyValuePair<TKey, ulong> entry in map) sum += entry.Value;
        return sum;
    }

    public ulong ChecksumRange(TKey start, int length)
    {
        ulong sum = 0;
        foreach (KeyValuePair<TKey, ulong> entry in map.EnumerateFrom(start))
        {
            sum += entry.Value;
            if (--length == 0) break;
        }

        return sum;
    }
}

internal readonly struct ConcurrentAdapter<TKey, TInlineKey>(ConcurrentBBTreeMap<TKey, ulong, TInlineKey> map) : IBenchMap<TKey, ConcurrentAdapter<TKey, TInlineKey>>
    where TKey : notnull
    where TInlineKey : struct, IInlineKey<TKey>
{
    public static ConcurrentAdapter<TKey, TInlineKey> Create() => new(new ConcurrentBBTreeMap<TKey, ulong, TInlineKey>());
    public int Count => map.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Insert(TKey key, ulong value) => map[key] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, out ulong value) => map.TryGetValue(key, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(TKey key) => map.TryRemove(key, out _);

    public ulong ChecksumAll()
    {
        ulong sum = 0;
        foreach (KeyValuePair<TKey, ulong> entry in map) sum += entry.Value;
        return sum;
    }

    public ulong ChecksumRange(TKey start, int length)
    {
        ulong sum = 0;
        foreach (KeyValuePair<TKey, ulong> entry in map.EnumerateFrom(start))
        {
            sum += entry.Value;
            if (--length == 0) break;
        }

        return sum;
    }
}


internal readonly struct SortedDictionaryAdapter<TKey>(SortedDictionary<TKey, ulong> map) : IBenchMap<TKey, SortedDictionaryAdapter<TKey>>
    where TKey : notnull
{
    public static SortedDictionaryAdapter<TKey> Create() =>
        new(typeof(TKey) == typeof(string) ? new SortedDictionary<TKey, ulong>((IComparer<TKey>)StringComparer.Ordinal) : []);

    public int Count => map.Count;
    public void Insert(TKey key, ulong value) => map[key] = value;
    public bool TryGet(TKey key, out ulong value) => map.TryGetValue(key, out value);
    public bool Remove(TKey key) => map.Remove(key);

    public ulong ChecksumAll()
    {
        ulong sum = 0;
        foreach (KeyValuePair<TKey, ulong> entry in map) sum += entry.Value;
        return sum;
    }

    // SortedDictionary has no range view.
    public ulong ChecksumRange(TKey start, int length) => throw new NotSupportedException("SortedDictionary has no range scan");
}

internal static class Report
{
    /// <summary>The GC pauses and collections of the last timed cell.</summary>
    public static string GcNote = "";
}
