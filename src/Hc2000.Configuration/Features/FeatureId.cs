namespace Hc2000.Features;

/// <summary>One release feature flag: an epic's slug, and whether this build compiled its implementation in. The only
/// instances are the generated members of <see cref="Features"/>, one per slug declared in build/feature-flags.json, so
/// code cannot gate on a flag that is not declared.</summary>
public sealed class FeatureId
{
    internal FeatureId(string slug, bool compiled)
    {
        Slug = slug;
        Compiled = compiled;
    }

    /// <summary>The epic's slug, as the release manifest names it.</summary>
    public string Slug { get; }

    /// <summary>True when this build carries the feature's implementation.</summary>
    public bool Compiled { get; }

    public override string ToString() => Slug;
}
