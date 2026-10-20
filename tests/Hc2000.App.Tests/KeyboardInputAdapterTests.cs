using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Hc2000.App.Input;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class KeyboardInputAdapterTests
{
    private static bool Down(KeyboardMatrix matrix, HcKey key)
        => (matrix.Read((ushort)(0xffff ^ (1 << ((int)key / 5 + 8)))) & (1 << ((int)key % 5))) == 0;

    [AvaloniaFact]
    public void FreshWindowSelectsPositionalAndSymbolicRemainsSelectable()
    {
        var window = new MainWindow();
        try
        {
            var box = window.FindControl<ComboBox>("InputModeBox")!;
            Assert.Equal(KeyMappingMode.Positional, window.KeyboardInput.Mode);
            Assert.Equal(1, box.SelectedIndex);
            Assert.Equal("Positional (physical keys)", Assert.IsType<ComboBoxItem>(box.SelectedItem).Content);
            box.SelectedIndex = 0;
            Assert.Equal(KeyMappingMode.Symbolic, window.KeyboardInput.Mode);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void StartupAltKAndShiftAltProducePhysicalChords()
    {
        var queue = new InputQueue(); var input = new KeyboardInputAdapter(queue);
        var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        Assert.Equal(KeyMappingMode.Positional, input.Mode);
        input.KeyDown(Key.LeftAlt, KeyModifiers.Alt);
        input.KeyDown(Key.K, KeyModifiers.Alt);
        controller.ApplyPending(0, matrix);
        Assert.True(Down(matrix, HcKey.SymbolShift)); Assert.True(Down(matrix, HcKey.K));
        Assert.False(Down(matrix, HcKey.CapsShift));
        input.KeyUp(Key.K); input.KeyUp(Key.LeftAlt); controller.ApplyPending(1, matrix);
        Assert.Equal(31, matrix.Read(0x00fe));
        input.KeyDown(Key.LeftShift, KeyModifiers.Shift);
        input.KeyDown(Key.LeftAlt, KeyModifiers.Shift | KeyModifiers.Alt);
        controller.ApplyPending(2, matrix);
        Assert.True(Down(matrix, HcKey.CapsShift)); Assert.True(Down(matrix, HcKey.SymbolShift));
        input.KeyUp(Key.LeftShift); input.KeyUp(Key.LeftAlt); controller.ApplyPending(3, matrix);
        Assert.Equal(31, matrix.Read(0x00fe));
    }

    [Fact]
    public void SymbolicQuoteUsesHostTextWithoutPhysicalShiftAndReleasesOnKeyUp()
    {
        var queue = new InputQueue(); var input = new KeyboardInputAdapter(queue) { Mode = KeyMappingMode.Symbolic };
        var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        input.KeyDown(Key.LeftShift); input.KeyDown(Key.D2, KeyModifiers.Shift); input.TextInput("\"");
        Assert.Equal(31, matrix.Read(0x00fe)); // UI cannot mutate the matrix.
        controller.ApplyPending(0, matrix);
        Assert.True(Down(matrix, HcKey.SymbolShift)); Assert.True(Down(matrix, HcKey.P));
        Assert.False(Down(matrix, HcKey.CapsShift)); Assert.False(Down(matrix, HcKey.D2));
        input.KeyUp(Key.D2); controller.ApplyPending(1, matrix);
        Assert.Equal(31, matrix.Read(0x00fe));
    }

    [Fact]
    public void AltGrSymbolicTextDoesNotBecomeAControlLetter()
    {
        var queue = new InputQueue(); var input = new KeyboardInputAdapter(queue) { Mode = KeyMappingMode.Symbolic };
        var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        input.KeyDown(Key.LeftCtrl, KeyModifiers.Control);
        input.KeyDown(Key.RightAlt, KeyModifiers.Control | KeyModifiers.Alt);
        input.KeyDown(Key.Q, KeyModifiers.Control | KeyModifiers.Alt); input.TextInput("@");
        controller.ApplyPending(0, matrix);
        Assert.True(Down(matrix, HcKey.SymbolShift)); Assert.True(Down(matrix, HcKey.D2));
        Assert.False(Down(matrix, HcKey.CapsShift)); Assert.False(Down(matrix, HcKey.Q));
        input.ReleaseFocus(); controller.ApplyPending(1, matrix); Assert.Equal(31, matrix.Read(0x00fe));
    }

    [Fact]
    public void PositionalModeIgnoresTextAndPreservesPhysicalShift()
    {
        var queue = new InputQueue(); var input = new KeyboardInputAdapter(queue) { Mode = KeyMappingMode.Positional };
        var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        input.KeyDown(Key.LeftShift); input.KeyDown(Key.D2, KeyModifiers.Shift);
        Assert.False(input.TextInput("\"")); controller.ApplyPending(0, matrix);
        Assert.True(Down(matrix, HcKey.CapsShift)); Assert.True(Down(matrix, HcKey.D2));
        Assert.False(Down(matrix, HcKey.SymbolShift));
        input.KeyUp(Key.D2); input.KeyUp(Key.LeftShift); controller.ApplyPending(1, matrix);
        Assert.Equal(31, matrix.Read(0x00fe));
    }

    [Fact]
    public void HostRepeatAndControlLetterTextDoNotDuplicateOrReplaceChord()
    {
        var queue = new InputQueue(); var input = new KeyboardInputAdapter(queue) { Mode = KeyMappingMode.Symbolic };
        var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        input.KeyDown(Key.C, KeyModifiers.Control); input.TextInput("c");
        input.KeyDown(Key.C, KeyModifiers.Control); input.TextInput("c");
        controller.ApplyPending(0, matrix);
        Assert.True(Down(matrix, HcKey.CapsShift)); Assert.True(Down(matrix, HcKey.SymbolShift));
        Assert.True(Down(matrix, HcKey.C));
        input.KeyUp(Key.C); controller.ApplyPending(1, matrix); Assert.Equal(31, matrix.Read(0x00fe));
    }

    [Fact]
    public void FocusLossAndModeChangeExplicitlyReleaseAndPasteRejectsWithoutBlocking()
    {
        var queue = new InputQueue(); var input = new KeyboardInputAdapter(queue) { Mode = KeyMappingMode.Symbolic };
        var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        var errors = new List<string>(); input.Rejected += errors.Add;
        input.KeyDown(Key.A); input.TextInput("a"); controller.ApplyPending(0, matrix);
        input.Mode = KeyMappingMode.Positional; controller.ApplyPending(1, matrix);
        Assert.Equal(31, matrix.Read(0x00fe));
        Assert.False(input.Paste("a€b")); Assert.Single(errors);
        Assert.True(input.Paste("DIR\r\n")); input.ReleaseFocus(); controller.ApplyPending(2, matrix);
        Assert.False(controller.IsPasting); Assert.Equal(31, matrix.Read(0x00fe));
    }

    [Fact]
    public void TextOnlyImeInputUsesRateLimitedPaste()
    {
        var queue = new InputQueue(); var input = new KeyboardInputAdapter(queue) { Mode = KeyMappingMode.Symbolic };
        var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        Assert.True(input.TextInput("ab")); controller.ApplyPending(0, matrix);
        Assert.True(Down(matrix, HcKey.A)); Assert.False(Down(matrix, HcKey.B));
        controller.ApplyPending(1, matrix); Assert.True(Down(matrix, HcKey.A));
    }

    [AvaloniaFact]
    public void MainWindowEventsAndPasteUiOnlyEnqueueUntilBoundary()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var matrix = new KeyboardMatrix(); var controller = new InputController(window.InputQueue);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A });
            window.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "a" });
            Assert.Equal(31, matrix.Read(0x00fe)); controller.ApplyPending(0, matrix);
            Assert.True(Down(matrix, HcKey.A));
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.A });
            controller.ApplyPending(1, matrix); Assert.Equal(31, matrix.Read(0x00fe));
            window.FindControl<TextBox>("PasteTextBox")!.Text = "bc";
            window.FindControl<Button>("PasteButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(31, matrix.Read(0x00fe)); controller.ApplyPending(2, matrix);
            Assert.True(Down(matrix, HcKey.B)); Assert.False(Down(matrix, HcKey.C));
        }
        finally { window.Close(); }
    }

    private static MainWindow OpenInputWindow()
    {
        var window = new MainWindow();
        // Raw headless input pumps the dispatcher. Prevent auto-boot from installing a
        // competing consumer of InputQueue: this paused worker owns a separate fixture.
        window.AttachRuntime(new Runtime.EmulationWorker(
            () => new RuntimeTests.Fixture().Machine, context => context.Pause()));
        window.Show(); window.Focus();
        return window;
    }

    [AvaloniaTheory]
    [InlineData(Key.A, "a", HcKey.A)]
    [InlineData(Key.D2, "2", HcKey.D2)]
    [InlineData(Key.Space, " ", HcKey.Space)]
    [InlineData(Key.OemComma, ",", HcKey.N)]
    public void SymbolicRawInputLeavesTextKeyUnhandledAndReleases(Key key, string text, HcKey expected)
    {
        var window = OpenInputWindow();
        window.FindControl<ComboBox>("InputModeBox")!.SelectedIndex = 0;
        try
        {
            var matrix = new KeyboardMatrix(); var controller = new InputController(window.InputQueue);
            bool? handled = null;
            window.AddHandler(InputElement.KeyDownEvent, (_, e) => handled = e.Handled,
                RoutingStrategies.Bubble, handledEventsToo: true);
            window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, text);
            // Native backends only deliver the character when KeyDown was not handled.
            Assert.False(handled);
            controller.ApplyPending(0, matrix); Assert.Equal(31, matrix.Read(0x00fe));
            if (handled == false) window.KeyTextInput(text);
            controller.ApplyPending(1, matrix); Assert.True(Down(matrix, expected));
            window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, text);
            controller.ApplyPending(2, matrix); Assert.Equal(31, matrix.Read(0x00fe));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SpaceAfterToolbarInteractionDoesNotClickPasteButton()
    {
        var window = OpenInputWindow();
        try
        {
            var button = window.FindControl<Button>("PasteButton")!;
            var clicks = 0; button.Click += (_, _) => clicks++;
            Assert.False(button.Focus()); // Toolbar must not steal machine input.
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyTextInput(" ");
            var matrix = new KeyboardMatrix(); var controller = new InputController(window.InputQueue);
            controller.ApplyPending(0, matrix); Assert.True(Down(matrix, HcKey.Space));
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            controller.ApplyPending(1, matrix); Assert.Equal(31, matrix.Read(0x00fe));
            Assert.Equal(0, clicks);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(KeyMappingMode.Positional, RawInputModifiers.None)]
    [InlineData(KeyMappingMode.Symbolic, RawInputModifiers.Control)]
    public void RawInputPhysicalAndControlChordsStillHandleKeyDown(KeyMappingMode mode, RawInputModifiers modifiers)
    {
        var window = OpenInputWindow();
        try
        {
            window.KeyboardInput.Mode = mode;
            bool? handled = null;
            window.AddHandler(InputElement.KeyDownEvent, (_, e) => handled = e.Handled,
                RoutingStrategies.Bubble, handledEventsToo: true);
            window.KeyPressQwerty(PhysicalKey.C, modifiers); Assert.True(handled);
            window.KeyTextInput("c"); // Even a stray platform character cannot replace the chord.
            var matrix = new KeyboardMatrix(); var controller = new InputController(window.InputQueue);
            controller.ApplyPending(0, matrix); Assert.True(Down(matrix, HcKey.C));
            Assert.Equal(modifiers == RawInputModifiers.Control, Down(matrix, HcKey.CapsShift));
            Assert.Equal(modifiers == RawInputModifiers.Control, Down(matrix, HcKey.SymbolShift));
            window.KeyReleaseQwerty(PhysicalKey.C, modifiers);
            controller.ApplyPending(1, matrix); Assert.Equal(31, matrix.Read(0x00fe));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClipboardShortcutDoesNotStealCpmControlV()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var matrix = new KeyboardMatrix(); var controller = new InputController(window.InputQueue);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.V, KeyModifiers = KeyModifiers.Control });
            controller.ApplyPending(0, matrix);
            Assert.True(Down(matrix, HcKey.CapsShift));
            Assert.True(Down(matrix, HcKey.SymbolShift)); Assert.True(Down(matrix, HcKey.V));
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.V });
            controller.ApplyPending(1, matrix); Assert.Equal(31, matrix.Read(0x00fe));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TextEditorIsNotMachineInputAndCloseEnqueuesShutdownAfterRelease()
    {
        var window = new MainWindow(); window.Show();
        var matrix = new KeyboardMatrix(); var controls = new List<InputControlKind>();
        var controller = new InputController(window.InputQueue, (cmd, _) => controls.Add(cmd.Kind));
        var editor = window.FindControl<TextBox>("PasteTextBox")!;
        editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A });
        editor.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "a" });
        controller.ApplyPending(0, matrix); Assert.Equal(31, matrix.Read(0x00fe));
        window.KeyboardInput.KeyDown(Key.B); window.KeyboardInput.TextInput("b"); controller.ApplyPending(1, matrix);
        Assert.True(Down(matrix, HcKey.B));
        window.Close(); controller.ApplyPending(2, matrix);
        Assert.Equal(31, matrix.Read(0x00fe)); Assert.Equal(InputControlKind.Shutdown, Assert.Single(controls));
    }
}
