using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapeWriteReportTests
{
    [Fact]
    public void SharedReportPreservesExistingConstructorAndCollisionIsNotAPublicField()
    {
        var report = new TapeWriteReport(TapeFormat.Tzx, TapeSignalFidelity.Quantized, TapeByteFidelity.Transformed,
            TapeAmplitudeFidelity.NotPreserved, 1000, default, 2, []);
        Assert.Equal(2UL, report.EliminatedTransitions);
        Assert.DoesNotContain(typeof(TapeWriteReport).GetProperties(), p => p.Name == "CollisionCount");
    }
}
