namespace Hc2000.Machine.Input;

// The first forty entries are row*5+column, A8..A15, D0..D4.
public enum HcKey
{
    CapsShift, Z, X, C, V,
    A, S, D, F, G,
    Q, W, E, R, T,
    D1, D2, D3, D4, D5,
    D0, D9, D8, D7, D6,
    P, O, I, U, Y,
    Enter, L, K, J, H,
    Space, SymbolShift, M, N, B,
    Edit, CapsLock, Left, Down, Up, Right, Graph, Delete, Control, Tab
}

[Flags]
public enum JoystickButtons { None = 0, Left = 1, Right = 2, Down = 4, Up = 8, Fire = 16 }

/// <summary>Logical key holds; expanded pairs are ORed, so releasing one source cannot release another.</summary>
public sealed class KeyboardMatrix
{
    private readonly bool[] pressed = new bool[50];
    private readonly JoystickButtons[] joysticks = new JoystickButtons[2];
    private static readonly HcKey[][] extras = [
        [HcKey.CapsShift, HcKey.D1], [HcKey.CapsShift, HcKey.D2],
        [HcKey.CapsShift, HcKey.D5], [HcKey.CapsShift, HcKey.D6],
        [HcKey.CapsShift, HcKey.D7], [HcKey.CapsShift, HcKey.D8],
        [HcKey.CapsShift, HcKey.D9], [HcKey.CapsShift, HcKey.D0],
        [HcKey.CapsShift, HcKey.SymbolShift],
        [HcKey.CapsShift, HcKey.SymbolShift, HcKey.I] // TAB=CTRL+I: drawing only, UNVERIFIED.
    ];
    // Sinclair: 1..5 = left/right/down/up/fire; 6..0 = left/right/down/up/fire.
    private static readonly HcKey[][] joystickKeys = [
        [HcKey.D1, HcKey.D2, HcKey.D3, HcKey.D4, HcKey.D5],
        [HcKey.D6, HcKey.D7, HcKey.D8, HcKey.D9, HcKey.D0]
    ];

    public void SetKey(HcKey key, bool down)
    {
        if (!Enum.IsDefined(key)) throw new ArgumentOutOfRangeException(nameof(key));
        pressed[(int)key] = down;
    }

    /// <param name="halfRow">0 selects the 1..5 row; 1 selects the 6..0 row.</param>
    public void SetJoystick(int halfRow, JoystickButtons buttons)
    {
        if (halfRow is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(halfRow));
        if (((int)buttons & ~31) != 0) throw new ArgumentOutOfRangeException(nameof(buttons));
        joysticks[halfRow] = buttons;
    }

    /// <returns>D0..D4; inactive rows float high. Multiple selected half-rows combine active-low.</returns>
    public byte Read(ushort port)
    {
        Span<byte> rows = stackalloc byte[8];
        rows.Clear();
        for (var i = 0; i < pressed.Length; i++)
        {
            if (!pressed[i]) continue;
            if (i < 40) Close(rows, (HcKey)i);
            else foreach (var key in extras[i - 40]) Close(rows, key);
        }
        for (var halfRow = 0; halfRow < 2; halfRow++)
            for (var column = 0; column < 5; column++)
                if (((int)joysticks[halfRow] & (1 << column)) != 0) Close(rows, joystickKeys[halfRow][column]);
        byte closed = 0;
        for (var row = 0; row < 8; row++)
            if ((port & (1 << (row + 8))) == 0) closed |= rows[row];
        return (byte)(~closed & 31);
    }

    private static void Close(Span<byte> rows, HcKey key) => rows[(int)key / 5] |= (byte)(1 << ((int)key % 5));

    /// <summary>Logical key state as raw 0/1 arrays (snapshot capture). First array: one 0/1 per
    /// HcKey in enum order; second: two joystick bit masks 0..31 (half rows 0 then 1).</summary>
    internal (int[] Pressed, int[] Joysticks) Snapshot()
    {
        var keys = new int[pressed.Length];
        for (var i = 0; i < pressed.Length; i++) keys[i] = pressed[i] ? 1 : 0;
        return (keys, new[] { (int)joysticks[0], (int)joysticks[1] });
    }

    internal void Restore(IReadOnlyList<int> pressed50, IReadOnlyList<int> joystickBits2)
    {
        if (pressed50.Count != 50) throw new ArgumentException("Keyboard snapshot must have 50 keys.", nameof(pressed50));
        if (joystickBits2.Count != 2) throw new ArgumentException("Keyboard snapshot must have 2 joystick halves.", nameof(joystickBits2));
        for (var i = 0; i < pressed.Length; i++)
        {
            if (pressed50[i] is not (0 or 1)) throw new ArgumentException("Keyboard snapshot key must be 0 or 1.", nameof(pressed50));
            pressed[i] = pressed50[i] == 1;
        }
        for (var half = 0; half < 2; half++)
        {
            if (joystickBits2[half] is < 0 or > 31) throw new ArgumentException("Keyboard snapshot joystick must be 0..31.", nameof(joystickBits2));
            joysticks[half] = (JoystickButtons)joystickBits2[half];
        }
    }
}
