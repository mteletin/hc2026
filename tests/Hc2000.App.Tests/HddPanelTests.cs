using System.Collections.Immutable;
using Avalonia.Headless.XUnit;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddPanelTests
{
    [AvaloniaFact]
    public async Task ColdStartIsOneUseAndCannotBeRearmedByDisconnectOrReset()
    {
        var panel = new HddPanel(); var called = 0;
        Assert.False(panel.ColdConfigurationAvailable);
        panel.ConfigureCold(images => { Assert.Empty(images); called++; return Task.CompletedTask; });
        Assert.True(panel.ColdConfigurationAvailable);
        await panel.StartColdAsync();
        Assert.Equal(1, called); Assert.False(panel.ColdConfigurationAvailable);
        panel.Disconnect();
        Assert.Throws<InvalidOperationException>(() => panel.ConfigureCold(_ => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.StartColdAsync());
    }

    [AvaloniaFact]
    public async Task PersistFailureCancelsCaptureWithoutAcknowledgingOrSwitching()
    {
        using var nativeDomain = new HddColdAdmissionTests.PrivateScopedDomain();
        var panel = new HddPanel(); var actions = new List<HddAction>();
        var capture = new HddSaveCapture(1, 2, 1, ImmutableArray.CreateRange(new byte[HddMediaAdapter.ImageLength]));
        panel.Attach(command =>
        {
            var action = command.Hdd!.Action; actions.Add(action);
            var view = new HddDriveView(2, "C.raw", true, 1, false, true);
            return Task.FromResult(new MediaReply(command.RequestId, new(null, false, false, 0, false, 0),
                new(null, false, false, 0, false, 0), Hdd: new([view], action == HddAction.CaptureSave ? capture : null)));
        });
        var nonexistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "copy.raw");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => panel.SaveCopyAsync(nonexistent, 2));
        Assert.Equal(new[] { HddAction.CaptureSave, HddAction.CancelSave }, actions);
        Assert.False(panel.CanCloseSafely);
    }
}
