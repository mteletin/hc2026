using System.Buffers.Binary;
using System.Numerics;

namespace Hc2000.Machine.Expansion;

internal static partial class ExpansionX4Math
{
    private readonly record struct Rational(bool Negative, BigInteger Numerator, BigInteger Denominator);
    private readonly record struct Format(int Bytes, int Precision, int MinimumExponent, int MaximumExponent, bool Native);
    private sealed class NumericLimitException : Exception;

    private static Format GetFormat(byte format) => format switch
    {
        3 => new(4, 24, -126, 127, false),
        4 => new(8, 53, -1022, 1023, false),
        5 => new(4, 24, -128, 126, true),
        6 => new(8, 56, -128, 126, true),
        7 => new(6, 40, -128, 126, true),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    internal static X4NumericResult EvaluateFloat(byte operation, byte sourceFormat, byte destinationFormat,
        bool allowRounding, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        try
        {
            var source = GetFormat(sourceFormat);
            var destination = GetFormat(destinationFormat);
            var status = Decode(sourceFormat, source, first, out var left);
            if (status != X4NumericStatus.Success) return X4NumericResult.Error(status);
            var right = default(Rational);
            if (operation is >= 1 and <= 4)
            {
                status = Decode(sourceFormat, source, second, out right);
                if (status != X4NumericStatus.Success) return X4NumericResult.Error(status);
            }
            Rational value;
            switch (operation)
            {
                case 1:
                case 2:
                    var effectiveRightSign = right.Negative ^ (operation == 2);
                    var signedLeft = left.Negative ? -left.Numerator : left.Numerator;
                    var signedRight = effectiveRightSign ? -right.Numerator : right.Numerator;
                    var sum = signedLeft * right.Denominator + signedRight * left.Denominator;
                    CheckLimit(sum, left.Denominator * right.Denominator);
                    value = new(sum.Sign < 0 || sum.IsZero && left.Numerator.IsZero && right.Numerator.IsZero &&
                        left.Negative && effectiveRightSign, BigInteger.Abs(sum), left.Denominator * right.Denominator);
                    break;
                case 3:
                    value = new(left.Negative ^ right.Negative, left.Numerator * right.Numerator,
                        left.Denominator * right.Denominator);
                    break;
                case 4:
                    if (right.Numerator.IsZero) return X4NumericResult.Error(X4NumericStatus.DivideByZero);
                    value = new(left.Negative ^ right.Negative, left.Numerator * right.Denominator,
                        left.Denominator * right.Numerator);
                    break;
                case 6:
                    if (left.Negative && !left.Numerator.IsZero) return X4NumericResult.Error(X4NumericStatus.Domain);
                    return Quantize(destinationFormat, destination, left, true, true);
                case 7: value = left; break;
                default: throw new ArgumentOutOfRangeException(nameof(operation));
            }
            CheckLimit(value.Numerator, value.Denominator);
            return Quantize(destinationFormat, destination, value, false, operation != 7 || allowRounding);
        }
        catch (NumericLimitException)
        {
            return X4NumericResult.Error(X4NumericStatus.InternalFailure);
        }
    }

    private static X4NumericStatus Decode(byte code, Format format, ReadOnlySpan<byte> bytes, out Rational value)
    {
        if (bytes.Length < format.Bytes) throw new ArgumentException("Incomplete floating operand.");
        bytes = bytes[..format.Bytes];
        bool negative;
        BigInteger fraction;
        int exponent;
        if (!format.Native)
        {
            var bits = format.Bytes == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            var fractionBits = format.Precision - 1;
            var exponentBits = format.Bytes == 4 ? 8 : 11;
            negative = (bits >> (fractionBits + exponentBits)) != 0;
            var encodedExponent = (int)((bits >> fractionBits) & ((1ul << exponentBits) - 1));
            if (encodedExponent == (1 << exponentBits) - 1)
            {
                value = default;
                return X4NumericStatus.UnsupportedNonFinite;
            }
            fraction = bits & ((1ul << fractionBits) - 1);
            if (encodedExponent == 0) exponent = format.MinimumExponent - fractionBits;
            else
            {
                fraction += BigInteger.One << fractionBits;
                exponent = encodedExponent + format.MinimumExponent - 1 - fractionBits;
            }
        }
        else
        {
            var exponentByte = code == 7 ? bytes[0] : bytes[^1];
            var fractions = (code == 7 ? bytes[1..] : bytes[..^1]).ToArray();
            negative = (fractions[^1] & 0x80) != 0;
            fractions[^1] &= 0x7f;
            if (exponentByte == 0)
            {
                value = new(false, BigInteger.Zero, BigInteger.One);
                return bytes.IndexOfAnyExcept((byte)0) < 0 ? X4NumericStatus.Success : X4NumericStatus.InvalidNumericEncoding;
            }
            fraction = new BigInteger(fractions, isUnsigned: true, isBigEndian: false);
            fraction += BigInteger.One << (format.Precision - 1);
            exponent = exponentByte - 129 - (format.Precision - 1);
        }
        value = exponent >= 0
            ? new(negative, fraction << exponent, BigInteger.One)
            : new(negative, fraction, BigInteger.One << -exponent);
        return X4NumericStatus.Success;
    }

    private static X4NumericResult Quantize(byte code, Format format, Rational value, bool squareRoot, bool allowRounding)
    {
        if (value.Numerator.IsZero)
        {
            if (format.Native && value.Negative && !allowRounding) return X4NumericResult.Error(X4NumericStatus.PrecisionLoss);
            var signLost = format.Native && value.Negative ? X4NumericFlags.NegativeZeroLost : X4NumericFlags.None;
            return new(X4NumericStatus.Success, signLost, Encode(code, format, BigInteger.Zero, format.MinimumExponent, value.Negative));
        }
        var exponent = FloorLog2(value.Numerator, value.Denominator);
        if (squareRoot) exponent >>= 1;
        if (format.Native && !allowRounding && !squareRoot)
        {
            var maximumSignificand = (BigInteger.One << format.Precision) - 1;
            var maximumShift = format.MaximumExponent - (format.Precision - 1);
            var left = value.Numerator;
            var right = value.Denominator * maximumSignificand;
            if (maximumShift >= 0) right <<= maximumShift;
            else left <<= -maximumShift;
            CheckLimit(left, right);
            if (left > right) return X4NumericResult.Error(X4NumericStatus.NativeRange);
        }
        if (format.Native && exponent < format.MinimumExponent)
        {
            if (!allowRounding) return X4NumericResult.Error(X4NumericStatus.NativeRange);
            var comparison = ComparePowerOfTwo(value.Numerator, value.Denominator, format.MinimumExponent - 1);
            var rounded = comparison > 0 ? BigInteger.One << (format.Precision - 1) : BigInteger.Zero;
            var flags = X4NumericFlags.Inexact;
            if (rounded.IsZero) flags |= X4NumericFlags.Underflow;
            if (rounded.IsZero && value.Negative) flags |= X4NumericFlags.NegativeZeroLost;
            return new(X4NumericStatus.Success, flags, Encode(code, format, rounded, format.MinimumExponent, value.Negative));
        }
        var workingExponent = Math.Max(exponent, format.MinimumExponent);
        var unitExponent = workingExponent - (format.Precision - 1);
        var numerator = value.Numerator;
        var denominator = value.Denominator;
        var scale = squareRoot ? checked(unitExponent * 2) : unitExponent;
        if (scale >= 0) denominator <<= scale;
        else numerator <<= -scale;
        CheckLimit(numerator, denominator);
        BigInteger quotient;
        bool inexact;
        if (squareRoot)
        {
            quotient = IntegerSqrt(numerator / denominator);
            inexact = quotient * quotient * denominator != numerator;
            var twicePlusOne = 2 * quotient + 1;
            var comparison = (4 * numerator).CompareTo(twicePlusOne * twicePlusOne * denominator);
            if (comparison > 0 || comparison == 0 && !quotient.IsEven) quotient++;
        }
        else
        {
            quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
            inexact = !remainder.IsZero;
            var comparison = (2 * remainder).CompareTo(denominator);
            if (comparison > 0 || comparison == 0 && !quotient.IsEven) quotient++;
        }
        if (quotient >= BigInteger.One << format.Precision)
        {
            quotient >>= 1;
            workingExponent++;
        }
        if (workingExponent > format.MaximumExponent)
            return X4NumericResult.Error(format.Native ? X4NumericStatus.NativeRange : X4NumericStatus.Overflow);
        if (!allowRounding && inexact) return X4NumericResult.Error(X4NumericStatus.PrecisionLoss);
        var flagsResult = inexact ? X4NumericFlags.Inexact : X4NumericFlags.None;
        if (!format.Native && inexact && workingExponent == format.MinimumExponent && quotient < BigInteger.One << (format.Precision - 1))
            flagsResult |= X4NumericFlags.Underflow;
        if (format.Native && quotient.IsZero && value.Negative) flagsResult |= X4NumericFlags.NegativeZeroLost;
        return new(X4NumericStatus.Success, flagsResult, Encode(code, format, quotient, workingExponent, value.Negative));
    }

    private static byte[] Encode(byte code, Format format, BigInteger significand, int exponent, bool negative)
    {
        var result = new byte[format.Bytes];
        if (format.Native)
        {
            if (significand.IsZero) return result;
            var nativeFraction = significand - (BigInteger.One << (format.Precision - 1));
            var offset = code == 7 ? 1 : 0;
            if (!nativeFraction.TryWriteBytes(result.AsSpan(offset, format.Bytes - 1), out _, isUnsigned: true, isBigEndian: false))
                throw new NumericLimitException();
            if (negative) result[offset + format.Bytes - 2] |= 0x80;
            result[code == 7 ? 0 : format.Bytes - 1] = checked((byte)(exponent + 129));
            return result;
        }
        var fractionBits = format.Precision - 1;
        var normal = significand >= BigInteger.One << fractionBits;
        var exponentBits = format.Bytes == 4 ? 8 : 11;
        var encodedExponent = normal ? (ulong)(exponent - format.MinimumExponent + 1) : 0ul;
        var fraction = normal ? significand - (BigInteger.One << fractionBits) : significand;
        var bits = (ulong)fraction | (encodedExponent << fractionBits) |
            (negative ? 1ul << (fractionBits + exponentBits) : 0ul);
        if (format.Bytes == 4) BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)bits);
        else BinaryPrimitives.WriteUInt64LittleEndian(result, bits);
        return result;
    }

    private static int FloorLog2(BigInteger numerator, BigInteger denominator)
    {
        var exponent = checked((int)(numerator.GetBitLength() - denominator.GetBitLength()));
        return ComparePowerOfTwo(numerator, denominator, exponent) < 0 ? exponent - 1 : exponent;
    }

    private static int ComparePowerOfTwo(BigInteger numerator, BigInteger denominator, int exponent) => exponent >= 0
        ? numerator.CompareTo(denominator << exponent) : (numerator << -exponent).CompareTo(denominator);

    private static BigInteger IntegerSqrt(BigInteger value)
    {
        if (value.IsZero) return BigInteger.Zero;
        var guess = BigInteger.One << checked((int)((value.GetBitLength() + 1) / 2));
        for (var iteration = 0; iteration < 8192; iteration++)
        {
            var next = (guess + value / guess) >> 1;
            if (next >= guess) return guess;
            guess = next;
        }
        throw new NumericLimitException();
    }

    private static void CheckLimit(BigInteger first, BigInteger second)
    {
        if (first.GetBitLength() > 8192 || second.GetBitLength() > 8192) throw new NumericLimitException();
    }
}
