namespace PostgresStudio.Services.Health;

/// <summary>
/// A newer-than-floor feature of the library behind a studio's sources, as a check that needs it to run, or a remedy
/// whose line needs it, names it.
/// </summary>
/// <remarks>
/// <para>
/// The health engine only ever prints one and passes it back to <see cref="IStudioRuntime" />: whether this host has it,
/// and what this host runs instead, are the runtime's to say. MartenStudio's features are its probe table
/// (<c>RuntimeFeature</c>), whose private constructor is what keeps a remedy from claiming a version nothing checks.
/// </para>
/// <para>
/// An instance is compared by reference: a feature is one of a fixed table's static entries, never a value made up on
/// the spot.
/// </para>
/// </remarks>
internal abstract class StudioFeatureRequirement
{
    /// <summary>What a line that needs it prints beside it: <c>Marten 9.47+</c>.</summary>
    public abstract string Needs { get; }
}

/// <summary>
/// The libraries a studio's sources run on, as the health engine asks about them: which features a check may require,
/// whether this host has one, and what it runs instead.
/// </summary>
/// <remarks>
/// <para>
/// A singleton a studio registers. MartenStudio answers from the Marten, JasperFx and JasperFx.Events this process runs
/// (or a made-up runtime a test registers); a studio with no versioned library behind it registers none, and
/// <see cref="NoStudioRuntime" /> answers that nothing is required and nothing can be claimed.
/// </para>
/// <para>
/// <b>Pure and cheap.</b> It is asked for every check of every sweep and for every remedy shown, and it never connects:
/// what a host runs is known in-process.
/// </para>
/// </remarks>
internal interface IStudioRuntime
{
    /// <summary>
    /// The feature a check's <c>RequiresFeature</c> names, or <see langword="null" /> when the table has none by that name -
    /// which the registry refuses at build time.
    /// </summary>
    /// <param name="name">The feature's name, as the table spells its field.</param>
    StudioFeatureRequirement? FindFeature(string name);

    /// <summary>Whether this host has <paramref name="feature" />.</summary>
    /// <param name="feature">A feature of this runtime's table.</param>
    bool IsAvailable(StudioFeatureRequirement feature);

    /// <summary>What this host runs in place of what <paramref name="feature" /> needs: <c>Marten 9.39.0</c>.</summary>
    /// <param name="feature">A feature of this runtime's table.</param>
    string DescribeHost(StudioFeatureRequirement feature);
}

/// <summary>The runtime of a studio with no versioned library behind its sources: no feature exists, none is claimed.</summary>
internal sealed class NoStudioRuntime : IStudioRuntime
{
    /// <summary>The one instance.</summary>
    public static NoStudioRuntime Instance { get; } = new();

    private NoStudioRuntime()
    {
    }

    /// <inheritdoc />
    public StudioFeatureRequirement? FindFeature(string name) => null;

    /// <inheritdoc />
    /// <remarks>Never: a requirement nobody can confirm is not claimed.</remarks>
    public bool IsAvailable(StudioFeatureRequirement feature) => false;

    /// <inheritdoc />
    public string DescribeHost(StudioFeatureRequirement feature) => "a version the studio cannot read";
}
