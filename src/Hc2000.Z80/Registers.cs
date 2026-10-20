namespace Hc2000.Z80;

/// <summary>Mutable CPU state. Pair and byte views always refer to the same storage.</summary>
public struct Registers
{
    public ushort AF, BC, DE, HL, IX, IY, SP, PC;
    public ushort AFShadow, BCShadow, DEShadow, HLShadow;
    public byte I, R, IM;
    public bool IFF1, IFF2;
    public ushort WZ;
    public byte Q;

    public ushort MEMPTR { readonly get => WZ; set => WZ = value; }
    public byte A { readonly get => (byte)(AF >> 8); set => AF = (ushort)((value << 8) | (AF & 0xff)); }
    public byte F { readonly get => (byte)AF; set => AF = (ushort)((AF & 0xff00) | value); }
    public byte B { readonly get => (byte)(BC >> 8); set => BC = (ushort)((value << 8) | (BC & 0xff)); }
    public byte C { readonly get => (byte)BC; set => BC = (ushort)((BC & 0xff00) | value); }
    public byte D { readonly get => (byte)(DE >> 8); set => DE = (ushort)((value << 8) | (DE & 0xff)); }
    public byte E { readonly get => (byte)DE; set => DE = (ushort)((DE & 0xff00) | value); }
    public byte H { readonly get => (byte)(HL >> 8); set => HL = (ushort)((value << 8) | (HL & 0xff)); }
    public byte L { readonly get => (byte)HL; set => HL = (ushort)((HL & 0xff00) | value); }
    public byte IXH { readonly get => (byte)(IX >> 8); set => IX = (ushort)((value << 8) | (IX & 0xff)); }
    public byte IXL { readonly get => (byte)IX; set => IX = (ushort)((IX & 0xff00) | value); }
    public byte IYH { readonly get => (byte)(IY >> 8); set => IY = (ushort)((value << 8) | (IY & 0xff)); }
    public byte IYL { readonly get => (byte)IY; set => IY = (ushort)((IY & 0xff00) | value); }
}
