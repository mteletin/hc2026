using Hc2000.Features;

namespace Hc2000.FeatureSample;

/// <summary>The flag mechanism's sample (epic hc2000-feature-flags), as the App (--feature-sample) and hc
/// (feature-sample) reach it: listed and run only when the release ships the flag on, refused otherwise. This gate is
/// always built; the sample itself, FeatureSampleCommand, is built only with HC2000_FEATURE_HC2000_FEATURE_FLAGS.</summary>
internal static class FeatureSampleGate
{
    public const string AppOption = "--feature-sample";
    public const string Subcommand = "feature-sample";

    public static bool Available(FeatureSet features) => features.IsAvailable(Features.Features.Hc2000FeatureFlags);

    public static int Run(FeatureSet features, string spelling, TextWriter stdout, TextWriter stderr)
    {
        if (Available(features))
        {
#if HC2000_FEATURE_HC2000_FEATURE_FLAGS
            return FeatureSampleCommand.Run(stdout);
#endif
        }
        stderr.WriteLine($"{spelling} is not available in this release.");
        return 2;
    }
}
