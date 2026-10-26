# BBTree

[![NuGet](https://img.shields.io/nuget/v/BBTree.svg)](https://www.nuget.org/packages/BBTree)
[![CI](https://github.com/LukaszRozmej/BBTree/actions/workflows/ci.yml/badge.svg)](https://github.com/LukaszRozmej/BBTree/actions/workflows/ci.yml)

Fast ordered maps for .NET: a C# port of [bbtree](https://github.com/XiangpengHao/bbtree) by Xiangpeng Hao, a B+-tree
whose nodes search inline keys with SIMD.

- `BBTreeMap<TKey, TValue, TInlineKey>` is the map for one thread, also named by its key type, as in
  `StringKey.Map<TValue>`. It gives `ref` access to values, ordered
  enumeration, range scans from a key, first and last entries, and lookups by an alternate key type, for example by
  `ReadOnlySpan<char>` in a map of strings.
- `ConcurrentBBTreeMap<TKey, TValue, TInlineKey>` is the map that threads share. Reads take no locks: each node has
  a version, and a read validates the versions of the nodes it read (optimistic lock coupling). Writers lock only
  the nodes they change. When empty leaves dominate, a removal compacts the tree under its node locks, reusing
  surviving leaves and rebuilding routing nodes. Operations can retry or wait during this occasional maintenance.

On the benchmark of the Rust crate, both maps are faster than the Rust crate in most cells; see
[Benchmarks](#benchmarks).

## Install

```
dotnet add package BBTree
```

The package targets .NET 10. It uses AVX2 where the CPU has it, and portable vector code elsewhere.

## What's new in 0.1.1

- Concurrent searches and range scans preserve their position across node splits, and range searches retry transient
  exceptions from custom comparisons.
- Failed key conversions and custom key callbacks preserve counts and existing entries. Splits and rebalancing
  roll back structural changes, and prefix changes publish only after the affected borders are ready.
- Adding or removing keys invalidates a single-threaded map's previously created ranges, including before enumeration starts.
- Concurrent maps reclaim empty leaves under churn. Compaction reuses surviving leaves, with occasional pauses and
  additional routing-node allocations; see the measurements below.

## Use

Each key type names its maps: `StringKey.Map<TValue>` is the map of `string` keys, `IntegerKey<int>.ConcurrentMap<TValue>`
the shared map of `int` keys.

```csharp
using BBTree;

IntegerKey<ulong>.Map<string> map = [new(7, "seven"), new(9, "nine")];
map[42] = "answer";

if (map.TryGetValue(42, out string? value)) Console.WriteLine(value);

// Values by reference: no second lookup to update one.
StringKey.Map<int> counts = new();
ref int count = ref counts.GetValueRefOrAddDefault("word", out _);
count++;

// Ordered scans.
foreach (KeyValuePair<ulong, string> entry in map) Console.WriteLine($"{entry.Key}: {entry.Value}");
foreach (KeyValuePair<ulong, string> entry in map.EnumerateFrom(10)) Console.WriteLine(entry.Key);
if (map.TryGetFirst(out ulong first, out _)) Console.WriteLine(first);

// Lookups by span, with no string allocation.
bool found = counts.GetSpanLookup().ContainsKey("word, and more".AsSpan(0, 4));

// Shared between threads.
IntegerKey<int>.ConcurrentMap<long> shared = new();
Parallel.For(0, 1_000, i => shared.AddOrUpdate(i, i * 2L));
foreach (KeyValuePair<int, long> entry in shared.EnumerateFrom(500)) Console.WriteLine(entry.Key);
```

Collection expressions build every map type; a key that comes more than once takes its last value, as with the
indexer. `GetSpanLookup()` searches maps of strings by `ReadOnlySpan<char>` and maps of byte arrays by
`ReadOnlySpan<byte>`; `GetAlternateLookup<TAlternate, TAlternateKey>()` serves other query types of your own keys.

`StringKey.Map<TValue>` is a `BBTreeMap<string, TValue, StringKey>`. Generic code over the key type takes the full
type, whose third type argument chooses how the tree orders and summarizes keys.

`BBTreeMap` implements `IDictionary<TKey, TValue>` and `IReadOnlyDictionary<TKey, TValue>`, with `Keys` and
`Values` as read-only views in key order, so it can stand in for a `SortedDictionary`. `ConcurrentBBTreeMap`
implements `IReadOnlyDictionary<TKey, TValue>`; there its `Count` is exact only when no write runs, and `Keys` and
`Values` are live enumerations. The interface members forward to the methods above, which take keys by `in` and
enumerate with struct enumerators, so use the map type itself on hot paths.

### Keys

| Key | Key type | Order |
|---|---|---|
| `IntegerKey<T>` | any integer type: `int`, `ulong`, `short`, `byte`, `Int128`, `nint`, `char`, … | numeric |
| `FloatKey<T>` | `double`, `float`, `Half` | IEEE 754 total order (see below) |
| `StringKey` | `string`, or `ReadOnlySpan<char>` as an alternate key | ordinal |
| `ByteArrayKey` | `byte[]`, or `ReadOnlySpan<byte>` as an alternate key | by its bytes |
| `BytesKey<T>` | any unmanaged `T`, such as a hash | by its bytes |
| `ComparableKey<T>` | any `T : IComparable<T>`, such as a class of your own | `CompareTo` |
| `ComparableKey<T, TPrefix>` | the same, with a prefix that orders like the keys | `CompareTo` |

`FloatKey<T>` orders `-NaN < -∞ < … < -0 < +0 < … < +∞ < +NaN`: `-0` and `+0` are two keys, as are NaNs of
different signs or payloads. `double.NaN` has its sign bit set, so it orders first.

### Keys of your own types

A type that implements `IComparable<T>` is a key with `ComparableKey<T>`. Such keys give the tree no bytes to
compare with vector instructions, so a search compares full keys, with a binary search in each node. Keys must not be
`null`.

```csharp
public sealed record Person(string Name, int Age) : IComparable<Person>
{
    public int CompareTo(Person? other)
    {
        int byName = string.CompareOrdinal(Name, other!.Name);
        return byName != 0 ? byName : Age.CompareTo(other.Age);
    }
}

ComparableKey<Person>.Map<string> people = new();
people[new Person("Ann", 42)] = "engineer";
```

To search by inline keys again, give the tree a prefix of each key that orders like the keys: if `x < y`, then
`Of(x) <= Of(y)`. A search then compares full keys only where prefixes tie.

```csharp
/// <summary>The first 4 chars of the name, which order like the names.</summary>
public readonly struct NamePrefix : ISortPrefix<Person>
{
    public static ulong Of(in Person key)
    {
        ulong prefix = 0;
        for (int i = 0; i < 4 && i < key.Name.Length; i++) prefix |= (ulong)key.Name[i] << (48 - 16 * i);
        return prefix;
    }
}

ComparableKey<Person, NamePrefix>.Map<string> faster = new();
```

For full control, implement `IInlineKey<TKey>`, as the built-in keys do; its documentation gives the contract. A
wrong implementation gives wrong answers, but never reads outside the map.

## How it works

- **Inline keys.** Each node keeps 2, 4 or 8 bytes of each key in a dense array beside the keys. A search counts the
  inline keys below its own with vector compares and no branches, and reads full keys only where inline keys tie.
- **Prefixes.** The inline keys skip the units (bytes, or chars of a string) that all keys of a node share, which the
  separators beside the node bound. Numbers and hashes keep 2 bytes past that prefix, strings and byte arrays 4, and
  keys with a sort prefix the 8 bytes of the prefix.
- **Length tags.** Leaves of strings and byte arrays also keep the length of each key, so a lookup proves a hit from
  the inline key alone when the key ends within it.
- **Strings.** A string window holds one byte for each of 4 chars: a char from `U+00FF` on saturates to `0xFF`, and
  the window ends there, so it still orders like the chars.

## Where it differs from the Rust crate

The runtime changes the trade-offs that shaped the Rust design:

- Separators are copies of keys kept in internal nodes. The Rust crate keeps each key once and walks down to a leaf
  to read a separator; a copy of a reference or of a small struct costs nothing here.
- Leaves link to the next leaf, so enumeration and range scans need no stack of ancestors.
- The concurrent map needs no epochs or free lists: a node that a reader still holds stays valid memory.
- Concurrent removals reclaim empty leaves once at least 32 leaves are empty and they make up about half the
  leaves, or when the tree becomes empty. Partly filled leaves remain; compaction trades occasional pauses and
  routing-node allocations for bounded retention under churn.
- The JIT shares one copy of generic code for all reference-type keys, where calls through the key interface become
  runtime lookups. Built-in string and byte-array keys are dispatched by type checks that the JIT folds, so their
  searches make direct calls. Their lookups run in a method of their own, which measured faster than inlined.
- The GC runs a write barrier for each reference that a move writes. Leaves whose entries hold references keep the
  entries in their slots and a byte array of their order: an insert or a removal shifts order bytes, not entries.
- Moves of slots, tags, order bytes and entries of unmanaged types are inlined overlapping vector copies, not calls.
- The enumerator of `BBTreeMap` implements no interfaces, so the JIT keeps it in registers; the interfaces get a box.
- The enumerator of `ConcurrentBBTreeMap` copies one entry at a time and validates its leaf after each. No method
  takes its address, so the JIT keeps it in registers too.
- Inserts and removals that change one leaf keep no path to the root; the few that split or merge nodes go down again.
- An enumeration prefetches the leaf after the one it reads, which the hardware prefetcher cannot guess. An insert or
  a removal prefetches the entries of its leaf as it reaches it, as the shift of entries reads lines that the search
  did not. Lookups prefetch nothing: there it measured slower.

## Benchmarks

`bench/BBTreeBench` runs the single-thread cells of the Rust crate's benchmark (`bbtree-bench ci`) with the same
keys, insertion orders, probe orders and operation counts, so that the two compare on one machine:

- **Keys.** `ulong` keys, and string keys with the length distribution of JSON object keys, made by the same
  SplitMix64 streams as the Rust bench. Values are `ulong`.
- **Operations.** Each operation runs on a map built with all keys, except the insert. `random insert` adds all keys
  in a shuffled order; `get, hit` and `get, miss` run 1,000,000 lookups of present and absent keys (copies, not the
  stored instances); `iterate` scans the whole map, per entry; `range of 64` reads 64 entries from a random start
  key; `random remove` removes all keys in a shuffled order.
- **Maps.** Rust `bbtree unsync` and `bbtree` (concurrent) against `BBTreeMap` and `ConcurrentBBTreeMap`, each on
  one thread.
- **Method.** The Rust and the C# bench ran alternately, 3 rounds; each cell is the fastest of its 3 runs, in
  nanoseconds per operation. Other load moves single runs by 5–10%, so treat differences below that as noise.
- **Machine.** AMD Ryzen 7 5800X, Windows 10, .NET 10.0.12 (x64, AVX2, workstation non-concurrent GC), Rust
  1.99.0-nightly (release), bbtree at commit `8adae97`, 2026-10-26.

### Summary

Ratios below 1 mean that C# is faster; **bold** ratios mean that C# is slower.

- **`ulong` keys:** C# is faster in all cells except full iteration of the single-thread map. At 10M keys (1.14)
  both maps wait for memory on the leaf chain. At 10k keys the Rust bench prints one decimal (0.4 ns), so that ratio
  is not exact. Lookups take 0.37–0.73 of the Rust time, inserts and removals 0.65–0.84.
- **`string` keys:** removals, iteration and ranges take 0.52–0.90 of the Rust time. On the single-thread map, hits
  are 6–13% slower and inserts up to 8% slower: .NET strings are UTF-16, so a window packs chars to bytes, and a key
  holds twice the bytes. The concurrent map is at or below Rust in all cells except inserts (1.07) and hits (1.01)
  at 10M keys.
- **`byte[]` keys,** the C# match of a Rust `String`, are at or below Rust except hits and inserts at 10k and 10M
  keys (1.02–1.05).

### Results

#### `ulong` keys

| Operation | Size | Rust bbtree unsync | C# `BBTreeMap` | C#/Rust | Rust bbtree | C# `ConcurrentBBTreeMap` | C#/Rust |
|---|---|--:|--:|--:|--:|--:|--:|
| random insert | 10k | 59.8 | 45.5 | 0.76 | 62.0 | 48.3 | 0.78 |
| random insert | 1M | 131 | 110 | 0.84 | 145 | 101 | 0.69 |
| random insert | 10M | 273 | 228 | 0.84 | 288 | 227 | 0.79 |
| get, hit | 10k | 41.5 | 21.7 | 0.52 | 39.5 | 21.8 | 0.55 |
| get, hit | 1M | 215 | 130 | 0.60 | 219 | 135 | 0.62 |
| get, hit | 10M | 280 | 190 | 0.68 | 296 | 216 | 0.73 |
| get, miss | 10k | 38.3 | 18.6 | 0.49 | 37.1 | 18.2 | 0.49 |
| get, miss | 1M | 199 | 73.3 | 0.37 | 190 | 87.0 | 0.46 |
| get, miss | 10M | 272 | 158 | 0.58 | 278 | 158 | 0.57 |
| iterate (per entry) | 10k | 0.40 | 0.49 | **1.22** | 1.10 | 0.76 | 0.69 |
| iterate (per entry) | 1M | 2.30 | 1.82 | 0.79 | 3.30 | 2.32 | 0.70 |
| iterate (per entry) | 10M | 2.90 | 3.31 | **1.14** | 4.50 | 3.38 | 0.75 |
| range of 64 | 10k | 129 | 73.6 | 0.57 | 124 | 97.4 | 0.79 |
| range of 64 | 1M | 471 | 337 | 0.72 | 466 | 357 | 0.77 |
| range of 64 | 10M | 721 | 636 | 0.88 | 722 | 637 | 0.88 |
| random remove | 10k | 62.3 | 43.2 | 0.69 | 66.3 | 43.0 | 0.65 |
| random remove | 1M | 150 | 112 | 0.75 | 174 | 123 | 0.71 |
| random remove | 10M | 298 | 250 | 0.84 | 302 | 248 | 0.82 |

#### `string` keys

| Operation | Size | Rust bbtree unsync | C# `BBTreeMap` | C#/Rust | Rust bbtree | C# `ConcurrentBBTreeMap` | C#/Rust |
|---|---|--:|--:|--:|--:|--:|--:|
| random insert | 10k | 80.9 | 84.4 | **1.04** | 91.2 | 85.8 | 0.94 |
| random insert | 1M | 257 | 257 | 1.00 | 268 | 255 | 0.95 |
| random insert | 10M | 383 | 414 | **1.08** | 393 | 421 | **1.07** |
| get, hit | 10k | 49.7 | 56.4 | **1.13** | 56.9 | 56.1 | 0.99 |
| get, hit | 1M | 284 | 301 | **1.06** | 316 | 314 | 0.99 |
| get, hit | 10M | 371 | 414 | **1.11** | 424 | 426 | **1.01** |
| get, miss | 10k | 39.3 | 36.6 | 0.93 | 38.8 | 37.5 | 0.97 |
| get, miss | 1M | 232 | 234 | **1.01** | 239 | 230 | 0.96 |
| get, miss | 10M | 294 | 295 | 1.00 | 316 | 294 | 0.93 |
| iterate (per entry) | 10k | 0.90 | 0.71 | 0.79 | 1.70 | 1.53 | 0.90 |
| iterate (per entry) | 1M | 4.80 | 2.79 | 0.58 | 7.60 | 3.97 | 0.52 |
| iterate (per entry) | 10M | 5.70 | 3.78 | 0.66 | 8.70 | 5.13 | 0.59 |
| range of 64 | 10k | 214 | 113 | 0.53 | 206 | 181 | 0.88 |
| range of 64 | 1M | 876 | 565 | 0.64 | 882 | 659 | 0.75 |
| range of 64 | 10M | 1162 | 891 | 0.77 | 1237 | 1009 | 0.82 |
| random remove | 10k | 101 | 80.9 | 0.80 | 118 | 72.5 | 0.61 |
| random remove | 1M | 442 | 284 | 0.64 | 488 | 284 | 0.58 |
| random remove | 10M | 637 | 450 | 0.71 | 678 | 419 | 0.62 |

#### `byte[]` keys (UTF-8 bytes of the same strings)

| Operation | Size | Rust bbtree unsync (`String`) | C# `BBTreeMap` (`byte[]`) | C#/Rust |
|---|---|--:|--:|--:|
| random insert | 10k | 81.0 | 82.9 | **1.02** |
| random insert | 1M | 254 | 245 | 0.96 |
| random insert | 10M | 385 | 398 | **1.03** |
| get, hit | 10k | 51.9 | 54.1 | **1.04** |
| get, hit | 1M | 292 | 288 | 0.99 |
| get, hit | 10M | 368 | 388 | **1.05** |
| get, miss | 10k | 40.4 | 34.7 | 0.86 |
| get, miss | 1M | 235 | 205 | 0.87 |
| get, miss | 10M | 302 | 260 | 0.86 |
| iterate (per entry) | 10k | 0.90 | 0.57 | 0.63 |
| iterate (per entry) | 1M | 4.70 | 2.84 | 0.60 |
| iterate (per entry) | 10M | 5.70 | 3.90 | 0.68 |
| range of 64 | 10k | 217 | 109 | 0.50 |
| range of 64 | 1M | 876 | 571 | 0.65 |
| range of 64 | 10M | 1155 | 894 | 0.77 |
| random remove | 10k | 99.6 | 80.4 | 0.81 |
| random remove | 1M | 444 | 285 | 0.64 |
| random remove | 10M | 633 | 442 | 0.70 |

### 0.1.1 churn measurements

Compared with commit `88f88f1`, before empty-leaf compaction, a moving window of 10,000 integer keys produced:

| Measurement | Before compaction | 0.1.1 |
|---|---:|---:|
| Median insert/remove time | 49.6 ns/op | 46.6 ns/op |
| Allocated during 100 window movements | 16.60 MB | 20.17 MB |
| Retained with 10,000 live keys | 16.72 MB | 172.27 KB |
| Retained after draining | 16.77 MB | 4.84 KB |

These are five-sample local measurements on Windows x64, Ryzen 7 5800X, .NET 10.0.9, AVX2 and workstation GC.
Memory units are decimal; retained memory is measured after full collections while keeping the map alive.
The workload performs two million timed operations. It retained about 99% less memory with live keys, with 21.5%
more allocations. Separate 10,000-key random-removal cells were 4–5% slower for the concurrent map. Compaction
tail latency and throughput with contending writers were not measured, so these results do not imply a general speedup.
See the [commands, comparison cells and raw samples](https://github.com/LukaszRozmej/BBTree/blob/v0.1.1/bench/results/review-2026-10-26.txt).

### Run it yourself

```
dotnet run -c Release --project bench/BBTreeBench -- --key u64,str-json --size 10k,1M,10M --map bbtree,bbtree-concurrent
```

Options: `--key` (`u64`, `str-json`, `bytes-json`), `--size` (`1k`, `10k`, `100k`, `1M`, `10M`), `--map` (`bbtree`,
`bbtree-concurrent`, `sorted-dictionary`) and `--op` (`random_insert`, `get_hit`, `get_miss`, `iterate`, `range64`,
`random_remove`). For the Rust side, run `cargo run --release -p bbtree-bench -- ci` in the bbtree repository.

To measure concurrent-map churn, including allocations and managed memory retained after full collections:

```
dotnet run -c Release --project bench/BBTreeBench -- --churn --reps 5
```

This moves a 10,000-key window through 100 batches of new integer keys, then drains the map. Timings cover the
window movements; retained memory is sampled with the final window present and again after draining.

## License

Licensed under either of the [MIT License or the Apache License, Version 2.0](LICENSE), at your option. BBTree is a
port of [bbtree](https://github.com/XiangpengHao/bbtree) by Xiangpeng Hao, which is licensed under the same terms.
