#if HC2000_FEATURE_HC2000_FEATURE_FLAGS
namespace Hc2000.FeatureSample;

/// <summary>The sample feature behind the flag hc2000-feature-flags: one harmless line. It exists only in a build that
/// compiles the flag in.</summary>
internal static class FeatureSampleCommand
{
    public const string Line = "feature sample: the flag hc2000-feature-flags is on in this release";

    public static int Run(TextWriter stdout)
    {
        stdout.WriteLine(Line);
        return 0;
    }
}
#endif
