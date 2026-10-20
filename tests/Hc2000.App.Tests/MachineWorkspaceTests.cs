using Avalonia.Input;
using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Workspace;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class MachineWorkspaceTests
{
    private static MachineTabContext Add(MachineWorkspace workspace, MachineConfiguration? configuration = null)
    {
        Assert.True(workspace.TryAddDraft(configuration ?? new(), out var tab, out var refusal));
        Assert.Null(refusal);
        return Assert.IsType<MachineTabContext>(tab);
    }

    private static bool Down(KeyboardMatrix matrix, HcKey key)
        => (matrix.Read((ushort)(0xffff ^ (1 << ((int)key / 5 + 8)))) & (1 << ((int)key % 5))) == 0;

    [Fact]
    public void EmptyWorkspaceDoesNotAcceptGuestInput()
    {
        var workspace = new MachineWorkspace();
        Assert.Null(workspace.Selected);
        Assert.False(workspace.KeyDown(Key.A));
        Assert.False(workspace.TextInput("a"));
        Assert.False(workspace.Paste("a"));
        workspace.KeyUp(Key.A);
        workspace.ReleaseFocus();
        Assert.False(workspace.Select(Guid.NewGuid()));
    }

    [Fact]
    public void FourDraftSlotsAreReservedAndFifthRefusalChangesNothing()
    {
        var workspace = new MachineWorkspace();
        var first = Add(workspace);
        Add(workspace); Add(workspace); Add(workspace);
        var before = workspace.Tabs.ToArray();
        Assert.False(workspace.TryAddDraft(new(), out var tab, out var refusal));
        Assert.Null(tab);
        Assert.Contains("four", refusal);
        Assert.Equal(before, workspace.Tabs);
        Assert.Same(first, workspace.Selected);
    }

    [Fact]
    public void DuplicateConfigurationDoesNotAllocateOrSelectAnotherTab()
    {
        var workspace = new MachineWorkspace();
        var first = Add(workspace);
        Assert.False(workspace.TryAddDraft(first.Configuration with { Name = "Duplicate" }, out var tab, out var refusal));
        Assert.Null(tab);
        Assert.Contains("already", refusal);
        Assert.Single(workspace.Tabs);
        Assert.Same(first, workspace.Selected);
    }

    [Fact]
    public void InvalidConfigurationDoesNotChangeExistingSelectionOrHeldInput()
    {
        var workspace = new MachineWorkspace();
        var first = Add(workspace);
        workspace.KeyDown(Key.A);
        Assert.Throws<ArgumentException>(() => workspace.TryAddDraft(new() { Name = "" }, out _, out _));
        Assert.Single(workspace.Tabs);
        Assert.Same(first, workspace.Selected);
        var matrix = new KeyboardMatrix();
        new InputController(first.InputQueue).ApplyPending(0, matrix);
        Assert.True(Down(matrix, HcKey.A));
    }

    [Fact]
    public void ContextsOwnDistinctQueuesAdaptersEvidenceAndConfigurationCopies()
    {
        var workspace = new MachineWorkspace();
        var configuration = new MachineConfiguration();
        var first = Add(workspace, configuration);
        var second = Add(workspace);
        Assert.Equal(configuration, first.Configuration);
        Assert.NotSame(configuration, first.Configuration);
        Assert.NotEqual(first.TabId, second.TabId);
        Assert.NotSame(first.InputQueue, second.InputQueue);
        Assert.NotSame(first.KeyboardInput, second.KeyboardInput);
        Assert.NotSame(first.KeyboardInput.Evidence, second.KeyboardInput.Evidence);
        Assert.Equal(0, second.InputQueue.BoundaryCount);
    }

    [Fact]
    public void SelectingAnotherTabReleasesOldHeldKeysAndCancelsItsPaste()
    {
        var workspace = new MachineWorkspace();
        var first = Add(workspace);
        var second = Add(workspace);
        var firstMatrix = new KeyboardMatrix(); var secondMatrix = new KeyboardMatrix();
        var firstController = new InputController(first.InputQueue);
        var secondController = new InputController(second.InputQueue);
        workspace.KeyDown(Key.A);
        workspace.Paste("bc");
        firstController.ApplyPending(0, firstMatrix);
        Assert.True(Down(firstMatrix, HcKey.A));
        Assert.True(firstController.IsPasting);
        Assert.True(workspace.Select(second.TabId));
        Assert.Equal(1, first.InputQueue.BoundaryCount); // old FIFO got the release
        Assert.Equal(0, second.InputQueue.BoundaryCount);
        workspace.KeyDown(Key.K);
        firstController.ApplyPending(1, firstMatrix);
        secondController.ApplyPending(1, secondMatrix);
        Assert.Equal(31, firstMatrix.Read(0x00fe));
        Assert.False(firstController.IsPasting);
        Assert.True(Down(secondMatrix, HcKey.K));
        Assert.False(Down(secondMatrix, HcKey.A));
        Assert.True(workspace.Select(first.TabId));
        workspace.KeyDown(Key.A); // old adapter's repeat bookkeeping was also cleared
        firstController.ApplyPending(2, firstMatrix);
        secondController.ApplyPending(2, secondMatrix);
        Assert.True(Down(firstMatrix, HcKey.A));
        Assert.Equal(31, secondMatrix.Read(0x00fe));
    }

    [Fact]
    public void InvalidOrSameSelectionDoesNotReleaseInput()
    {
        var workspace = new MachineWorkspace();
        var first = Add(workspace);
        workspace.KeyDown(Key.A);
        Assert.False(workspace.Select(Guid.NewGuid()));
        Assert.True(workspace.Select(first.TabId));
        Assert.Equal(1, first.InputQueue.BoundaryCount);
        var matrix = new KeyboardMatrix();
        new InputController(first.InputQueue).ApplyPending(0, matrix);
        Assert.True(Down(matrix, HcKey.A));
    }

    [Fact]
    public void DeactivationReleasesOnlySelectedAdapter()
    {
        var workspace = new MachineWorkspace();
        var first = Add(workspace); var second = Add(workspace);
        workspace.Select(second.TabId);
        var oldCount = first.InputQueue.BoundaryCount;
        workspace.KeyDown(Key.LeftShift);
        workspace.ReleaseFocus(InputReleaseOrigin.Deactivated);
        var matrix = new KeyboardMatrix();
        new InputController(second.InputQueue).ApplyPending(0, matrix);
        Assert.Equal(31, matrix.Read(0x00fe));
        Assert.Equal(oldCount, first.InputQueue.BoundaryCount);
    }

    [Fact]
    public void WorkspacesNeverShareSelectionOrReuseTabIdentityFromConfiguration()
    {
        var configuration = new MachineConfiguration();
        var left = new MachineWorkspace(); var right = new MachineWorkspace();
        var a = Add(left, configuration); var b = Add(right, configuration);
        Assert.NotEqual(a.TabId, b.TabId);
        left.KeyDown(Key.A);
        Assert.Equal(0, b.InputQueue.BoundaryCount);
        Assert.Same(b, right.Selected);
    }

    [Fact]
    public void CapturedDraftStaysWithOriginalTabAcrossSelectionAndRejectsStaleReply()
    {
        var workspace = new MachineWorkspace();
        var first = Add(workspace); var second = Add(workspace);
        var target = Assert.IsType<MachineDraftTarget>(workspace.CaptureSelectedDraft());
        workspace.Select(second.TabId);
        Assert.True(workspace.TryResolveDraft(target, out var original));
        Assert.Same(first, original);
        Assert.True(workspace.TryUpdateDraft(target, first.Configuration with { Name = "Updated original" }));
        Assert.Equal("Updated original", first.Configuration.Name);
        Assert.Equal("HC2000", second.Configuration.Name);
        Assert.Equal(1, first.DraftRevision);
        Assert.False(workspace.TryUpdateDraft(target, first.Configuration with { Name = "Stale" }));
        Assert.False(workspace.TryResolveDraft(target, out _));
        Assert.Equal("Updated original", first.Configuration.Name);
        Assert.Same(second, workspace.Selected);
    }

    [Fact]
    public void ForeignDraftTargetsAndInvalidEditsCannotMutateOrAdvanceRevision()
    {
        var workspace = new MachineWorkspace(); var other = new MachineWorkspace();
        var first = Add(workspace); Add(other, first.Configuration);
        var target = Assert.IsType<MachineDraftTarget>(workspace.CaptureSelectedDraft());
        Assert.False(other.TryUpdateDraft(target, first.Configuration with { Name = "Foreign" }));
        Assert.Throws<ArgumentException>(() => workspace.TryUpdateDraft(target, first.Configuration with { Name = "" }));
        Assert.Throws<ArgumentException>(() => workspace.TryUpdateDraft(target, first.Configuration with { Id = "changed" }));
        Assert.Equal(0, first.DraftRevision);
        Assert.Equal("HC2000", first.Configuration.Name);
        Assert.Null(new MachineWorkspace().CaptureSelectedDraft());
    }

    [Fact]
    public void DraftModeChangeReleasesOnlyOriginalKeyboardBeforeUsingNewMapping()
    {
        var workspace = new MachineWorkspace();
        var first = Add(workspace); var second = Add(workspace);
        var target = Assert.IsType<MachineDraftTarget>(workspace.CaptureSelectedDraft());
        workspace.KeyDown(Key.LeftShift);
        var matrix = new KeyboardMatrix();
        var controller = new InputController(first.InputQueue);
        controller.ApplyPending(0, matrix);
        Assert.True(Down(matrix, HcKey.CapsShift));
        Assert.True(workspace.TryUpdateDraft(target, first.Configuration with { KeyboardMode = "Symbolic" }));
        controller.ApplyPending(1, matrix);
        Assert.Equal(31, matrix.Read(0x00fe));
        Assert.Equal(KeyMappingMode.Symbolic, first.KeyboardInput.Mode);
        Assert.Equal(KeyMappingMode.Positional, second.KeyboardInput.Mode);
        Assert.Equal(0, second.InputQueue.BoundaryCount);
    }

    [Fact]
    public void EachDraftImportsItsOwnStoredKeyboardMode()
    {
        var workspace = new MachineWorkspace();
        var positional = Add(workspace);
        var symbolic = Add(workspace, new() { KeyboardMode = "Symbolic" });
        Assert.Equal(KeyMappingMode.Positional, positional.KeyboardInput.Mode);
        Assert.Equal(KeyMappingMode.Symbolic, symbolic.KeyboardInput.Mode);
        workspace.Select(symbolic.TabId);
        workspace.KeyDown(Key.A);
        Assert.True(workspace.TextInput("a"));
        workspace.KeyUp(Key.A);
        var matrix = new KeyboardMatrix();
        new InputController(symbolic.InputQueue).ApplyPending(0, matrix);
        Assert.Equal(31, matrix.Read(0x00fe));
        Assert.Equal(KeyMappingMode.Positional, positional.KeyboardInput.Mode);
    }
}
