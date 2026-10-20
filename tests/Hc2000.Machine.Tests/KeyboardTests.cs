using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class KeyboardTests
{
    [Fact]
    public void AllFortyKeysHaveExactlyTheirSpectrumHalfRowAndColumn()
    {
        for (var key = 0; key < 40; key++)
        {
            var keyboard = new KeyboardMatrix();
            keyboard.SetKey((HcKey)key, true);
            for (var row = 0; row < 8; row++)
                Assert.Equal((byte)(31 & ~(row == key / 5 ? 1 << (key % 5) : 0)),
                    keyboard.Read((ushort)(0xfffe & ~(1 << (8 + row)))));
            Assert.Equal(31, keyboard.Read(0xfffe));
            Assert.Equal((byte)(31 & ~(1 << (key % 5))), keyboard.Read(0x00fe));
            keyboard.SetKey((HcKey)key, false);
            Assert.Equal(31, keyboard.Read(0x00fe));
        }
    }

    [Theory]
    [InlineData(HcKey.Edit, HcKey.D1)] [InlineData(HcKey.CapsLock, HcKey.D2)]
    [InlineData(HcKey.Left, HcKey.D5)] [InlineData(HcKey.Down, HcKey.D6)]
    [InlineData(HcKey.Up, HcKey.D7)] [InlineData(HcKey.Right, HcKey.D8)]
    [InlineData(HcKey.Graph, HcKey.D9)] [InlineData(HcKey.Delete, HcKey.D0)]
    [InlineData(HcKey.Control, HcKey.SymbolShift)]
    public void ExtraKeysCloseCapsShiftAndDocumentedKey(HcKey extra, HcKey normal)
    {
        var keyboard = new KeyboardMatrix();
        var expected = new KeyboardMatrix();
        keyboard.SetKey(extra, true);
        expected.SetKey(HcKey.CapsShift, true);
        expected.SetKey(normal, true);
        for (var row = 0; row < 8; row++)
        {
            var port = (ushort)(0xfffe & ~(1 << (8 + row)));
            Assert.Equal(expected.Read(port), keyboard.Read(port));
        }
    }

    [Fact]
    [Trait("hardware", "unverified")]
    public void TabUsesSelectedUnverifiedCtrlIReading()
    {
        var keyboard = new KeyboardMatrix();
        keyboard.SetKey(HcKey.Tab, true);
        Assert.Equal(30, keyboard.Read(0xfefe)); // CS
        Assert.Equal(29, keyboard.Read(0x7ffe)); // SS
        Assert.Equal(27, keyboard.Read(0xdffe)); // I
    }

    [Fact]
    public void OverlappingPairsDirectHoldsAndJoysticksDoNotReleaseOneAnother()
    {
        var keyboard = new KeyboardMatrix();
        keyboard.SetKey(HcKey.CapsShift, true);
        keyboard.SetKey(HcKey.Left, true);
        keyboard.SetKey(HcKey.Up, true);
        keyboard.SetKey(HcKey.Left, false);
        Assert.Equal(30, keyboard.Read(0xfefe));
        Assert.Equal(23, keyboard.Read(0xeffe)); // D7
        keyboard.SetKey(HcKey.Up, false);
        Assert.Equal(30, keyboard.Read(0xfefe));
        keyboard.SetKey(HcKey.CapsShift, false);
        Assert.Equal(31, keyboard.Read(0xfefe));
        keyboard.SetKey(HcKey.D5, true);
        keyboard.SetJoystick(0, JoystickButtons.Fire | JoystickButtons.Left);
        Assert.Equal(14, keyboard.Read(0xf7fe));
        keyboard.SetJoystick(0, JoystickButtons.None);
        Assert.Equal(15, keyboard.Read(0xf7fe));
        keyboard.SetJoystick(1, JoystickButtons.Left | JoystickButtons.Fire);
        Assert.Equal(14, keyboard.Read(0xeffe)); // D6 and D0
        Assert.Throws<ArgumentOutOfRangeException>(() => keyboard.SetKey((HcKey)50, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => keyboard.SetJoystick(2, JoystickButtons.None));
    }
}
