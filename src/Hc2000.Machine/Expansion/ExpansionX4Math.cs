using System.Numerics;

namespace Hc2000.Machine.Expansion;

internal enum X4NumericStatus : byte
{
    Success = 0, DivideByZero = 1, Domain = 2, Overflow = 3, UnsupportedNonFinite = 4,
    PrecisionLoss = 5, NativeRange = 6, InternalFailure = 7, InvalidNumericEncoding = 8
}

[Flags]
internal enum X4NumericFlags : ushort
{
    None = 0, Inexact = 1, Underflow = 2, NegativeZeroLost = 4
}

internal sealed record X4NumericResult(X4NumericStatus Status, X4NumericFlags Flags, byte[] Bytes)
{
    internal static X4NumericResult Error(X4NumericStatus status) => new(status, X4NumericFlags.None, []);
}

internal static partial class ExpansionX4Math
{
    internal static X4NumericResult EvaluateInteger(byte operation, byte format, ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second)
    {
        var width = format switch { 1 => 4, 2 => 8, _ => throw new ArgumentOutOfRangeException(nameof(format)) };
        if (first.Length < width || second.Length < width) throw new ArgumentException("Incomplete integer operands.");
        var left = new BigInteger(first[..width], isUnsigned: false, isBigEndian: false);
        var right = new BigInteger(second[..width], isUnsigned: false, isBigEndian: false);
        BigInteger value;
        BigInteger remainder = BigInteger.Zero;
        switch (operation)
        {
            case 1: value = left + right; break;
            case 2: value = left - right; break;
            case 3: value = left * right; break;
            case 5:
                if (right.IsZero) return X4NumericResult.Error(X4NumericStatus.DivideByZero);
                value = BigInteger.DivRem(left, right, out remainder);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
        var limit = BigInteger.One << (width * 8 - 1);
        if (value < -limit || value >= limit) return X4NumericResult.Error(X4NumericStatus.Overflow);
        var result = new byte[operation == 5 ? 2 * width : width];
        WriteSigned(value, result.AsSpan(0, width));
        if (operation == 5) WriteSigned(remainder, result.AsSpan(width, width));
        return new(X4NumericStatus.Success, X4NumericFlags.None, result);
    }

    private static void WriteSigned(BigInteger value, Span<byte> output)
    {
        output.Fill(value.Sign < 0 ? (byte)0xff : (byte)0);
        if (!value.TryWriteBytes(output, out _, isUnsigned: false, isBigEndian: false))
            throw new InvalidOperationException("Checked integer did not fit its result.");
    }
}
