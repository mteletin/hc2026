using Xunit;

namespace Hc2000.Tools.Tests;

public sealed class FactoryHddCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hc-factory-cli-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FactoryWritesOneDeterministicContainerAndReceiptAndRefusesReplacement()
    {
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, StorageCommand.Run("hdd", ["factory", first], output, error));
        Assert.Equal("", error.ToString());
        Assert.Equal(new[] { "HC2000-HDD.hdd", "factory-hdd.sha256" }.Order(), Directory.GetFiles(first).Select(Path.GetFileName).Order());
        Assert.Contains("entries=3", output.ToString());
        foreach (var slot in new[] { "D", "E", "F" }) Assert.Contains("name=\"" + slot + ".DSK\"", output.ToString());
        Assert.DoesNotContain("C.DSK", output.ToString());
        var container = File.ReadAllBytes(Path.Combine(first, "HC2000-HDD.hdd"));
        var receipt = File.ReadAllText(Path.Combine(first, "factory-hdd.sha256"));
        Assert.Equal(1, StorageCommand.Run("hdd", ["factory", first], new StringWriter(), error));
        Assert.Contains("Refusing to replace", error.ToString());
        Assert.Equal(container, File.ReadAllBytes(Path.Combine(first, "HC2000-HDD.hdd")));
        Assert.Equal(receipt, File.ReadAllText(Path.Combine(first, "factory-hdd.sha256")));
        Assert.Empty(Directory.GetFiles(first, "*.hc-factory-*"));
        Assert.Equal(0, StorageCommand.Run("hdd", ["factory", second], new StringWriter(), new StringWriter()));
        Assert.Equal(container, File.ReadAllBytes(Path.Combine(second, "HC2000-HDD.hdd")));
        Assert.Equal(receipt, File.ReadAllText(Path.Combine(second, "factory-hdd.sha256")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
