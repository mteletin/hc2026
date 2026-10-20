using Avalonia.Input;
using Hc2000.App.Input;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>
/// Three input-path checks per arch seam-split (626978) for hc2000-app-guest-screen-gl-grey:
/// (1) InputQueue drained at HALT boundary
/// (2) Chord mapping for x and Return
/// (3) KeyboardMatrix reflection after ApplyPending
/// </summary>
public sealed class InputPathChecks
{
    private static bool KeyDown(KeyboardMatrix matrix, HcKey key)
        => (matrix.Read((ushort)(0xffff ^ (1 << ((int)key / 5 + 8)))) & (1 << ((int)key % 5))) == 0;

    // ── Check 1: InputQueue drained at HALT (instruction boundary) ──────────────────
    //
    // EmulationWorker.Run loop: while (!cancelled && !stopping) {
    //   if (!Machine.TryRunFrame(Boundary, ...)) { Rebase; wait(2ms); continue; }
    //   ...pace...
    // }
    // Boundary() = { boundary(context); return !cancelled && context.PermitStep(); }
    // boundary(context) = input.ApplyPending(context.Machine.MasterTick, context.Machine.Keyboard)
    //
    // FrameRunner.TryRunFrame: while (clock.CompletedFrames < requested) {
    //   if (!boundary()) return false;   ← called BEFORE each CPU Step
    //   step();
    // }
    //
    // HALT: Z80.Step() advances TStates by 4 (M1 fetch), then early-returns (Z80.cs L72-76).
    // After HALT, the CPU keeps stepping (4 T-states each), and at every instruction
    // boundary the boundary() callback fires → ApplyPending drains the queue.
    // Even if the machine is paused (PermitStep returns false), the EmulationWorker
    // loop keeps calling TryRunFrame → Boundary() → ApplyPending.
    [Fact]
    public void Check1_InputQueueDrainedAtInstructionBoundary()
    {
        var queue = new InputQueue();
        var matrix = new KeyboardMatrix();
        var controller = new InputController(queue);

        // Enqueue: press 'x' (HcKey.X), then press Enter (HcKey.Enter)
        queue.Enqueue(new KeyTransition("pc:x", new KeyChord(HcKey.X), true));
        queue.Enqueue(new KeyTransition("pc:enter", new KeyChord(HcKey.Enter), true));

        // Before boundary: nothing applied
        Assert.False(KeyDown(matrix, HcKey.X));
        Assert.False(KeyDown(matrix, HcKey.Enter));

        // At the boundary (what EmulationWorker does each frame):
        controller.ApplyPending(1000, matrix);

        // Both keys are now in the matrix
        Assert.True(KeyDown(matrix, HcKey.X), "Check1: 'x' should be pressed after ApplyPending");
        Assert.True(KeyDown(matrix, HcKey.Enter), "Check1: Enter should be pressed after ApplyPending");

        // Release
        queue.Enqueue(new KeyTransition("pc:x", new KeyChord(HcKey.X), false));
        queue.Enqueue(new KeyTransition("pc:enter", new KeyChord(HcKey.Enter), false));
        controller.ApplyPending(1001, matrix);
        Assert.False(KeyDown(matrix, HcKey.X), "Check1: 'x' released");
        Assert.False(KeyDown(matrix, HcKey.Enter), "Check1: Enter released");
    }

    // ── Check 2: Chord mapping for x and Return ─────────────────────────────────────
    //
    // Physical path: OnKeyDown → KeyboardInputAdapter.KeyDown(Key.X)
    //   → IsTextKey(X)=true → return true (suppressed, no chord from KeyDown)
    //   → Avalonia fires TextInput("x") → KeyboardInputAdapter.TextInput("x")
    //   → KeyMap.Character('x') → HcKey.X (KeyMap.cs L44: 'a'..'z' → Letter)
    //   → queue.Enqueue(KeyTransition(source, chord{X}, down:true))
    //
    // Physical path: OnKeyDown → KeyboardInputAdapter.KeyDown(Key.Enter)
    //   → IsTextKey(Enter)=false → KeyMap.Physical(Key.Enter) → HcKey.Enter
    //   → queue.Enqueue(KeyTransition(source, chord{Enter}, down:true))
    //
    // TextInput path: '\r' or '\n' → HcKey.Enter (KeyMap.cs L33)
    [Fact]
    public void Check2_ChordMappingForXAndReturn()
    {
        // Character path (what TextInput delivers for 'x')
        var chordX = KeyMap.Character('x');
        Assert.NotNull(chordX);
        Assert.Equal(new HcKey[] { HcKey.X }, chordX!.Keys.ToArray());

        // Character path (what TextInput delivers for '\n')
        var chordNl = KeyMap.Character('\n');
        Assert.NotNull(chordNl);
        Assert.Equal(new HcKey[] { HcKey.Enter }, chordNl!.Keys.ToArray());

        // Character path for '\r'
        var chordCr = KeyMap.Character('\r');
        Assert.NotNull(chordCr);
        Assert.Equal(new HcKey[] { HcKey.Enter }, chordCr!.Keys.ToArray());

        // Physical path (what OnKeyDown delivers for Key.Enter)
        var chordPhysEnter = KeyMap.Physical(Key.Enter);
        Assert.NotNull(chordPhysEnter);
        Assert.Equal(new HcKey[] { HcKey.Enter }, chordPhysEnter!.Keys.ToArray());

        // Physical path: Key.X goes through the text path, not Physical.
        // But verify Physical would also work if called:
        var chordPhysX = KeyMap.Physical(Key.X);
        Assert.NotNull(chordPhysX);
        Assert.Equal(new HcKey[] { HcKey.X }, chordPhysX!.Keys.ToArray());
    }

    // ── Check 3: KeyboardMatrix reflects the key after ApplyPending ─────────────────
    //
    // After the chord is enqueued (Check 2) and ApplyPending runs (Check 1),
    // InputController.Change() calls matrix.SetKey(HcKey.X, true).
    // KeyboardMatrix.Read() must reflect the closed switch:
    //   row = (int)HcKey.X / 5 = 1, col = (int)HcKey.X % 5 = 2
    //   Read(0xFFFF & ~(1<<9)) → bit 2 must be clear (active-low = pressed)
    [Fact]
    public void Check3_KeyboardMatrixReflectsAppliedKey()
    {
        var queue = new InputQueue();
        var matrix = new KeyboardMatrix();
        var controller = new InputController(queue);

        // Simulate what the adapter does for 'x' via TextInput
        queue.Enqueue(new KeyTransition("pc:x", new KeyChord(HcKey.X), true));
        // Simulate what the adapter does for Enter via OnKeyDown
        queue.Enqueue(new KeyTransition("pc:enter", new KeyChord(HcKey.Enter), true));
        controller.ApplyPending(500, matrix);

        // Verify X: row 1, col 2
        Assert.True(KeyDown(matrix, HcKey.X),
            "Check3: KeyboardMatrix must reflect 'x' as pressed after ApplyPending");

        // Verify Enter: row 7, col 0
        Assert.True(KeyDown(matrix, HcKey.Enter),
            "Check3: KeyboardMatrix must reflect Enter as pressed after ApplyPending");

        // Release and verify
        queue.Enqueue(new KeyTransition("pc:x", new KeyChord(HcKey.X), false));
        queue.Enqueue(new KeyTransition("pc:enter", new KeyChord(HcKey.Enter), false));
        controller.ApplyPending(501, matrix);
        Assert.False(KeyDown(matrix, HcKey.X), "Check3: 'x' released");
        Assert.False(KeyDown(matrix, HcKey.Enter), "Check3: Enter released");
    }

    // ── Full round-trip: adapter → queue → controller → matrix ──────────────────────
    [Fact]
    public void FullRoundTrip_AdapterToMatrix()
    {
        var queue = new InputQueue();
        var matrix = new KeyboardMatrix();
        var adapter = new KeyboardInputAdapter(queue);
        var controller = new InputController(queue);

        // Press 'x' via the text-input path (as Avalonia does)
        adapter.KeyDown(Key.X);           // returns true (suppressed, IsTextKey)
        adapter.TextInput("x");           // enqueues chord{X}

        // Press Enter via the physical path (as Avalonia does)
        adapter.KeyDown(Key.Enter);       // enqueues chord{Enter}

        // Apply at the boundary
        controller.ApplyPending(100, matrix);

        Assert.True(KeyDown(matrix, HcKey.X));
        Assert.True(KeyDown(matrix, HcKey.Enter));

        // Release
        adapter.KeyUp(Key.X);
        adapter.KeyUp(Key.Enter);
        controller.ApplyPending(101, matrix);

        Assert.False(KeyDown(matrix, HcKey.X));
        Assert.False(KeyDown(matrix, HcKey.Enter));
    }
}
