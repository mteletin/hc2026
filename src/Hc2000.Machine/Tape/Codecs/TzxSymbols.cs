namespace Hc2000.Machine.Tape.Codecs;

public sealed partial class TzxTapeCodec
{
    private sealed record Symbol(byte Flags, ushort[] Widths);
    private static void Generalized(TzxCursor c, TzxSignalBuilder b, TapeLimits limits)
    {
        c.Dword(); uint pause = c.Word(), pilotTotal = c.Dword(); byte pilotPulses = c.Byte(), pilotAlphabet = c.Byte();
        uint dataTotal = c.Dword(); byte dataPulses = c.Byte(), dataAlphabet = c.Byte();
        if (pilotTotal > limits.RouteSteps || dataTotal > limits.VirtualTransitions)
            throw new TzxException(TapeError.BoundsExceeded, "Generalized symbol count exceeds budget.");
        Symbol[] Table(byte alphabet, byte pulses)
        {
            var table = new Symbol[alphabet == 0 ? 256 : alphabet];
            for (int i = 0; i < table.Length; i++)
            {
                byte flags = c.Byte();
                if (flags > 3) throw new TzxException(TapeError.InvalidFormat, "Reserved generalized symbol flag.");
                var widths = new ushort[pulses];
                for (int j = 0; j < widths.Length; j++) widths[j] = c.Word();
                table[i] = new(flags, widths);
            }
            return table;
        }
        void Emit(Symbol symbol)
        {
            // Level is the pending next pulse, i.e. the opposite of the last pulse.
            // Opposite/same flags are defined relative to that last played pulse.
            b.Level = symbol.Flags switch { 0 => b.Level, 1 => !b.Level, 2 => false, _ => true };
            foreach (ushort width in symbol.Widths) { if (width == 0) break; b.Pulse(width); }
        }
        if (pilotTotal > 0)
        {
            var table = Table(pilotAlphabet, pilotPulses);
            for (uint i = 0; i < pilotTotal; i++)
            {
                byte index = c.Byte(); uint count = c.Word();
                if (index >= table.Length) throw new TzxException(TapeError.InvalidFormat, "Generalized pilot index outside alphabet.");
                b.Repeat(count, () => Emit(table[index]));
            }
        }
        if (dataTotal > 0)
        {
            var table = Table(dataAlphabet, dataPulses);
            int bits = 0; while ((1 << bits) < table.Length) bits++;
            long bitCount = (long)bits * dataTotal;
            var packed = c.Take(checked((int)((bitCount + 7) / 8)));
            long bit = 0;
            if (bits == 0) b.Repeat(dataTotal, () => Emit(table[0]));
            else for (uint i = 0; i < dataTotal; i++)
            {
                int index = 0;
                for (int j = 0; j < bits; j++, bit++) index = index << 1 | ((packed[bit / 8] >> (7 - (int)(bit % 8))) & 1);
                if (index >= table.Length) throw new TzxException(TapeError.InvalidFormat, "Generalized data index outside alphabet.");
                Emit(table[index]);
            }
        }
        b.Pause(pause);
    }

    private static void Wave(TzxSignalBuilder b, uint width)
    {
        if (width == 0) return; // Omitted entire wave, not a zero-time phase change.
        b.Level = false; b.Tone(width, 2);
    }
    private static void C64Rom(TzxCursor c, TzxSignalBuilder b)
    {
        c.Dword(); uint pilot = c.Word(), count = c.Word(), sync1 = c.Word(), sync2 = c.Word();
        uint zero1 = c.Word(), zero2 = c.Word(), one1 = c.Word(), one2 = c.Word(); byte checksum = c.Byte();
        uint finishByte1 = c.Word(), finishByte2 = c.Word(), finishData1 = c.Word(), finishData2 = c.Word();
        uint trailer = c.Word(), trailerCount = c.Word(); int bits = c.Byte(); byte flags = c.Byte(); uint pause = c.Word();
        var data = c.Take(c.UInt24());
        if (bits is < 1 or > 8 || (checksum != 0 && checksum != 1 && checksum != 255) || flags > 1)
            throw new TzxException(TapeError.InvalidFormat, "Invalid C64 ROM flags/checksum/bits.");
        b.Repeat(count, () => Wave(b, pilot)); Wave(b, sync1); Wave(b, sync2);
        void Bit(bool bit) { Wave(b, bit ? one1 : zero1); Wave(b, bit ? one2 : zero2); }
        for (int i = 0; i < data.Length; i++)
        {
            bool xor = checksum == 1;
            int used = i == data.Length - 1 ? bits : 8;
            for (int j = 0; j < used; j++)
            {
                bool bit = (data[i] & 1 << ((flags & 1) != 0 ? 7 - j : 8 - used + j)) != 0;
                Bit(bit); xor ^= bit;
            }
            if (checksum != 255) Bit(xor);
            if (i != data.Length - 1) { Wave(b, finishByte1); Wave(b, finishByte2); }
            else { Wave(b, finishData1); Wave(b, finishData2); }
        }
        b.Repeat(trailerCount, () => Wave(b, trailer)); b.Pause(pause);
    }
    private static void C64Turbo(TzxCursor c, TzxSignalBuilder b)
    {
        c.Dword(); uint zero = c.Word(), one = c.Word(); byte additional = c.Byte();
        uint leadCount = c.Word(); byte lead = c.Byte(); int bits = c.Byte(); byte flags = c.Byte();
        uint trailCount = c.Word(); byte trail = c.Byte(); uint pause = c.Word(); var data = c.Take(c.UInt24());
        if (bits is < 1 or > 8 || flags > 1 || additional > 15)
            throw new TzxException(TapeError.InvalidFormat, "Invalid C64 turbo flags/bits.");
        void Extra() { for (int j = 0; j < (additional & 3); j++) Wave(b, (additional & 8) != 0 ? one : zero); }
        void Byte(byte value, int used)
        {
            if ((additional & 4) == 0) Extra();
            for (int j = 0; j < used; j++) Wave(b, (value & 1 << ((flags & 1) != 0 ? 7 - j : 8 - used + j)) != 0 ? one : zero);
            if ((additional & 4) != 0) Extra();
        }
        b.Repeat(leadCount, () => Byte(lead, 8));
        for (int i = 0; i < data.Length; i++) Byte(data[i], i == data.Length - 1 ? bits : 8);
        b.Repeat(trailCount, () => Byte(trail, 8)); b.Pause(pause);
    }
}
