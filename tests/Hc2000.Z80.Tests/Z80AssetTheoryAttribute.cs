using Xunit;

namespace Hc2000.Z80.Tests;

public sealed class Z80AssetTheoryAttribute : TheoryAttribute
{
    public Z80AssetTheoryAttribute() => Skip = new Z80AssetFactAttribute().Skip;
}
