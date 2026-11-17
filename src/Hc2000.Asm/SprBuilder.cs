namespace Hc2000.Asm;

public static class SprBuilder
{
    public static byte[] Create(ReadOnlySpan<byte> originZero, ReadOnlySpan<byte> originOne)
    {
        if (originZero.Length is 0 or > ushort.MaxValue || originZero.Length != originOne.Length)
            throw new ArgumentException("SPR origins must have equal nonempty image lengths of at most 65535 bytes.");
        var length = originZero.Length;
        var result = new byte[256 + length + (length + 7) / 8];
        result[1] = (byte)length;
        result[2] = (byte)(length >> 8);
        originZero.CopyTo(result.AsSpan(256, length));
        for (var index = 0; index < length; index++)
        {
            if (originZero[index] == originOne[index]) continue;
            if (originOne[index] != unchecked((byte)(originZero[index] + 1)))
                throw new ArgumentException($"SPR origin mismatch at image offset {index:X4}.");
            result[256 + length + (index >> 3)] |= (byte)(0x80 >> (index & 7));
        }
        return result;
    }
}
