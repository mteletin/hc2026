using Xunit;

namespace Hc2000.Z80.Tests;

// Timing gates cannot overlap allocation-heavy independent-corpus replay collections.
[CollectionDefinition("Z80 timing gates", DisableParallelization = true)]
public class TimingGatesCollection { }
