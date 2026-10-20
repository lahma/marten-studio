using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Configuration;

/// <summary>
/// A probe-table feature is the shared health engine's <see cref="StudioFeatureRequirement" />: what a check's
/// <c>RequiresFeature</c> names and what a remedy's line needs, printed beside it as its <see cref="Requirement" />.
/// Whether a host has it is <see cref="MartenStudioRuntime" />'s to say.
/// </summary>
internal sealed partial class RuntimeFeature : StudioFeatureRequirement
{
    /// <inheritdoc />
    public override string Needs => Requirement;
}

/// <summary>
/// MartenStudio's <see cref="IStudioRuntime" />: the probe table (<see cref="RuntimeFeature" />) and the Marten, JasperFx and
/// JasperFx.Events this host runs - this process's own, or a made-up one a test registers.
/// </summary>
/// <param name="Runtime">The runtime the features are judged against.</param>
internal sealed record MartenStudioRuntime(MartenRuntime Runtime) : IStudioRuntime
{
    /// <summary>This process's own runtime.</summary>
    public static MartenStudioRuntime Current { get; } = new(MartenRuntime.Current);

    /// <inheritdoc />
    public StudioFeatureRequirement? FindFeature(string name) => RuntimeFeature.Find(name);

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="feature" /> is not one of the probe table's.</exception>
    public bool IsAvailable(StudioFeatureRequirement feature) => Feature(feature).IsAvailable(Runtime);

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="feature" /> is not one of the probe table's.</exception>
    public string DescribeHost(StudioFeatureRequirement feature) => Runtime.DescribeHost(Feature(feature).Package);

    private static RuntimeFeature Feature(StudioFeatureRequirement feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        return feature as RuntimeFeature
            ?? throw new ArgumentException("MartenStudio's runtime judges the features of its own probe table only.", nameof(feature));
    }
}
