// SPDX-FileCopyrightText: 2026 Lukasz Rozmej
// SPDX-License-Identifier: MIT OR Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace BBTree;

/// <summary>Moves a run of elements of a node, which hold no references, one element up or down.</summary>
/// <remarks>
/// The runs are at most a node long, and a call to a general copy costs more than the move. Each move loads a run in
/// overlapping vectors and stores each one only after the bytes it overwrites are loaded, so it writes exactly the
/// bytes of the destination: a node keeps the greatest inline key in the slots past its count.
/// </remarks>
internal static class ElementShift
{
    /// <summary>Moves the <paramref name="length"/> bytes at <paramref name="start"/> <paramref name="step"/> bytes up.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Up(ref byte start, int length, int step)
    {
        if (length <= 32)
        {
            Small(ref start, ref Unsafe.Add(ref start, step), length);
            return;
        }

        // The lowest vector is loaded first, as the stores above it overwrite its bytes; then the rest moves from the top.
        Vector256<byte> lowest = Vector256.LoadUnsafe(ref start);
        for (int offset = length - 32; offset > 0; offset -= 32)
        {
            Vector256.LoadUnsafe(ref start, (nuint)offset).StoreUnsafe(ref start, (nuint)(offset + step));
        }

        lowest.StoreUnsafe(ref start, (nuint)step);
    }

    /// <summary>Moves the <paramref name="length"/> bytes at <paramref name="start"/> <paramref name="step"/> bytes down.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Down(ref byte start, int length, int step)
    {
        ref byte destination = ref Unsafe.Subtract(ref start, step);
        if (length <= 32)
        {
            Small(ref start, ref destination, length);
            return;
        }

        Vector256<byte> highest = Vector256.LoadUnsafe(ref start, (nuint)(length - 32));
        for (int offset = 0; offset < length - 32; offset += 32)
        {
            Vector256.LoadUnsafe(ref start, (nuint)offset).StoreUnsafe(ref destination, (nuint)offset);
        }

        highest.StoreUnsafe(ref destination, (nuint)(length - 32));
    }

    /// <summary>Copies up to 32 bytes in two loads that overlap, both taken before either store.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Small(ref byte source, ref byte destination, int length)
    {
        if (length >= 16)
        {
            Vector128<byte> low = Vector128.LoadUnsafe(ref source);
            Vector128<byte> high = Vector128.LoadUnsafe(ref source, (nuint)(length - 16));
            low.StoreUnsafe(ref destination);
            high.StoreUnsafe(ref destination, (nuint)(length - 16));
        }
        else if (length >= 8)
        {
            ulong low = Unsafe.ReadUnaligned<ulong>(ref source);
            ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, length - 8));
            Unsafe.WriteUnaligned(ref destination, low);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, length - 8), high);
        }
        else if (length >= 4)
        {
            uint low = Unsafe.ReadUnaligned<uint>(ref source);
            uint high = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, length - 4));
            Unsafe.WriteUnaligned(ref destination, low);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, length - 4), high);
        }
        else if (length >= 2)
        {
            ushort low = Unsafe.ReadUnaligned<ushort>(ref source);
            ushort high = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref source, length - 2));
            Unsafe.WriteUnaligned(ref destination, low);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, length - 2), high);
        }
        else if (length == 1)
        {
            destination = source;
        }
    }
}
