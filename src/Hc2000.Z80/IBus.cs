namespace Hc2000.Z80;

/// <summary>The CPU's memory and full 16-bit I/O bus.</summary>
public interface IBus
{
    byte Read(ushort addr);
    void Write(ushort addr, byte value);
    byte In(ushort port);
    void Out(ushort port, byte value);
    void Contend(ushort addr, int tstates);
    byte IntVector();
}
