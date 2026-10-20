using Hc2000.Tools;
using Xunit;

namespace Hc2000.Tools.Tests;

public class DispatchTests : IDisposable
{
    private readonly NativeImageAdmissionTests.Domain nativeDomain = new();
    private readonly IDisposable nativeScope;
    public DispatchTests() => nativeScope = nativeDomain.Enter();
    public void Dispose() { nativeScope.Dispose(); nativeDomain.Dispose(); }
    [Fact]
    public void TapeCapabilitiesDispatchIsImplemented()
        => Assert.Equal(0, Program.Dispatch("tape", ["info", "--capabilities", "--json"]));

    [Theory]
    [InlineData("asm")]
    [InlineData("disasm")]
    [InlineData("dsk")]
    [InlineData("fs")]
    [InlineData("rom")]
    [InlineData("ASM")]
    public void RecognisedSubcommandIsAccepted(string sub)
    {
        var rc = Program.Dispatch(sub, []);
        Assert.Equal(1, rc); // stub: accepted, not implemented
    }
}
