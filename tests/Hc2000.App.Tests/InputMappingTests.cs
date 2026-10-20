using Avalonia.Input;
using Hc2000.App.Input;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class InputMappingTests
{
    [Theory]
    [InlineData('"', HcKey.SymbolShift, HcKey.P)]
    [InlineData('@', HcKey.SymbolShift, HcKey.D2)]
    [InlineData('a', HcKey.A, HcKey.A)]
    [InlineData('A', HcKey.CapsShift, HcKey.A)]
    [InlineData('\r', HcKey.Enter, HcKey.Enter)]
    [InlineData('\t', HcKey.Tab, HcKey.Tab)]
    [InlineData('\u0003', HcKey.Control, HcKey.C)]
    public void CharactersMapToImmutableLogicalChords(char c, HcKey first, HcKey last)
    {
        var chord = KeyMap.Character(c);
        Assert.NotNull(chord);
        Assert.Equal(new[] { first, last }.Distinct(), chord.Keys);
    }

    [Theory]
    [InlineData(Key.Left, HcKey.Left, 0x13)]
    [InlineData(Key.Down, HcKey.Down, 0x18)]
    [InlineData(Key.Up, HcKey.Up, 0x05)]
    [InlineData(Key.Right, HcKey.Right, 0x04)]
    [InlineData(Key.Delete, HcKey.Delete, 0x7f)]
    [InlineData(Key.Back, HcKey.Delete, 0x7f)]
    [InlineData(Key.Enter, HcKey.Enter, 0x0d)]
    [InlineData(Key.Tab, HcKey.Tab, 0x09)]
    [InlineData(Key.Escape, HcKey.Edit, 0x1b)]
    public void CpmSpecialKeysUseRomCodesNotPcOrAnsiCodes(Key pc, HcKey hc, int code)
    {
        Assert.Equal(hc, Assert.Single(KeyMap.Physical(pc)!.Keys));
        Assert.Equal((byte)code, KeyMap.CpmCode(pc));
    }

    [Fact]
    public void AllControlLettersAndPhysicalPositionsMap()
    {
        for (var i = 0; i < 26; i++)
        {
            var key = (Key)((int)Key.A + i);
            var hc = Enum.Parse<HcKey>(((char)('A' + i)).ToString());
            Assert.Equal(hc, Assert.Single(KeyMap.Physical(key)!.Keys));
            Assert.Equal(new[] { HcKey.Control, hc }, KeyMap.Physical(key, KeyModifiers.Control)!.Keys);
            Assert.Equal((byte)(i + 1), KeyMap.CpmCode(key, KeyModifiers.Control));
        }
    }

    [Theory]
    [InlineData(Key.F2, HcKey.Edit)]
    [InlineData(Key.CapsLock, HcKey.CapsLock)]
    [InlineData(Key.F3, HcKey.Graph)]
    [InlineData(Key.LeftCtrl, HcKey.Control)]
    [InlineData(Key.RightCtrl, HcKey.Control)]
    [InlineData(Key.LeftShift, HcKey.CapsShift)]
    [InlineData(Key.RightAlt, HcKey.SymbolShift)]
    public void ExtraKeysAndModifiersReuseMatrixExpansion(Key pc, HcKey hc)
        => Assert.Equal(hc, Assert.Single(KeyMap.Physical(pc)!.Keys));

    [Fact]
    public void EveryPrintableAsciiCharacterAndDigitPositionIsMapped()
    {
        for (var c = ' '; c <= '~'; c++) Assert.NotNull(KeyMap.Character(c));
        for (var i = 0; i < 10; i++)
        {
            var expected = Enum.Parse<HcKey>("D" + i);
            Assert.Equal(expected, Assert.Single(KeyMap.Physical((Key)((int)Key.D0 + i))!.Keys));
            Assert.Equal(expected, Assert.Single(KeyMap.Physical((Key)((int)Key.NumPad0 + i))!.Keys));
        }
    }

    [Theory]
    [InlineData('\\', HcKey.D)]
    [InlineData('[', HcKey.Y)]
    [InlineData(']', HcKey.U)]
    [InlineData(':', HcKey.Z)]
    [InlineData('?', HcKey.C)]
    [InlineData('/', HcKey.V)]
    [InlineData('`', HcKey.X)]
    [InlineData('_', HcKey.D0)]
    public void RomPunctuationTableHasCorrectIndex(char value, HcKey key)
        => Assert.Equal(new[] { HcKey.SymbolShift, key }, KeyMap.Character(value)!.Keys);

    [Fact]
    public void UnsupportedInputIsExplicitAndChordCopiesItsArray()
    {
        Assert.Null(KeyMap.Character('€'));
        Assert.Null(KeyMap.Physical(Key.F12));
        var array = new[] { HcKey.A };
        var chord = new KeyChord(array); array[0] = HcKey.B;
        Assert.Equal(HcKey.A, Assert.Single(chord.Keys));
    }
}
