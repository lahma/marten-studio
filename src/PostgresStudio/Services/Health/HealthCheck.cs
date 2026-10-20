using System.Globalization;
using System.Reflection;
using System.Text;

namespace PostgresStudio.Services.Health;

/// <summary>
/// The static metadata of one health check, on the check's own class - which is all it takes to add one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HealthCheckRegistry" /> finds every concrete <see cref="IHealthCheck" /> in the studio's
/// assembly and reads this off it, so a check is added by adding its class: no list to append to, no switch
/// to extend, no registration. The conventions tests read the same attribute to hold each check to its docs
/// heading, its live tests and the probe table.
/// </para>
/// <para>
/// Named arguments are plain settable properties because that is what C# allows in an attribute.
/// </para>
/// </remarks>
/// <param name="code">
/// The code: the area's letter (<c>D</c>aemon, <c>E</c>vents, <c>T</c>ables, <c>C</c>onnections,
/// <c>R</c>eplication, <c>S</c>erver, configuration <c>K</c>) and two digits. Unique across the studio.
/// </param>
/// <param name="area">The area, which must be the one the code's letter names.</param>
/// <param name="tier">What it costs, and so when it runs.</param>
/// <param name="title">
/// What it looks for, as a short phrase. It is also the docs heading: <c>## {code} {title}</c> in the area's
/// file under <c>docs/health/</c>.
/// </param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class HealthCheckAttribute(string code, HealthArea area, HealthCostTier tier, string title) : Attribute
{
    /// <summary>The code.</summary>
    public string Code { get; } = code;

    /// <summary>The area.</summary>
    public HealthArea Area { get; } = area;

    /// <summary>The cost tier.</summary>
    public HealthCostTier Tier { get; } = tier;

    /// <summary>What it looks for.</summary>
    public string Title { get; } = title;

    /// <summary>
    /// Who may see its findings. <see cref="HealthGate.Database" /> unless the check says otherwise: most
    /// checks read database-wide numbers, and the conservative default is the one a check that forgot to
    /// think about it gets.
    /// </summary>
    public HealthGate Gate { get; set; } = HealthGate.Database;

    /// <summary>
    /// The feature of the studio's runtime (<see cref="IStudioRuntime.FindFeature" />) the check needs to run at all, by
    /// its name - in MartenStudio a field of its probe table, <c>nameof(RuntimeFeature.X)</c> - or <see langword="null" />
    /// for one that runs on the floor. On a host without it the check is listed as not applicable, with the version it
    /// needs, and never run.
    /// </summary>
    public string? RequiresFeature { get; set; }
}

/// <summary>A check's metadata, read once from its <see cref="HealthCheckAttribute" />.</summary>
/// <param name="Code">The code.</param>
/// <param name="Area">The area.</param>
/// <param name="Tier">The cost tier.</param>
/// <param name="Gate">Who may see its findings.</param>
/// <param name="Title">What it looks for, and its docs heading.</param>
/// <param name="Requires">The feature it needs to run, or <see langword="null" />.</param>
/// <param name="CheckType">The class.</param>
internal sealed record HealthCheckInfo(
    string Code,
    HealthArea Area,
    HealthCostTier Tier,
    HealthGate Gate,
    string Title,
    StudioFeatureRequirement? Requires,
    Type CheckType)
{
    /// <summary>
    /// Where the published docs live for this build of the studio; the area's file and anchor are appended.
    /// </summary>
    /// <remarks>
    /// Pinned to the release's own tag (<c>blob/v0.4.0</c>), read from the studio assembly's informational
    /// version, so a host running one release is sent to that release's description of its checks - a later
    /// release may reword, renumber or drop one. A prerelease or unversioned build links to <c>main</c>.
    /// </remarks>
    internal static string DocsRoot { get; } = DocsRootFor(
        typeof(HealthCheckInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The area's docs file, relative to the repository root.</summary>
    public string DocsFile => "docs/health/" + AreaFileName(Area);

    /// <summary>The heading the docs file must carry for this check.</summary>
    public string DocsHeading => "## " + Code + " " + Title;

    /// <summary>The heading's anchor, as GitHub derives it.</summary>
    public string DocsAnchor => Anchor(Code + " " + Title);

    /// <summary>The published page and anchor, for the finding's "Docs" link.</summary>
    public string DocsUrl => DocsRoot + AreaFileName(Area) + "#" + DocsAnchor;

    /// <summary>The docs file of an area.</summary>
    public static string AreaFileName(HealthArea area) => area.ToString().ToLowerInvariant() + ".md";

    /// <summary>
    /// The docs root for an informational version: its release tag when it names a release
    /// (<c>0.4.0+abc</c>), <c>main</c> for a prerelease (<c>0.4.0-rc.1</c>) or anything that is not a version.
    /// </summary>
    internal static string DocsRootFor(string? informationalVersion)
    {
        string reference = "main";

        if (informationalVersion is { Length: > 0 })
        {
            string core = informationalVersion.Split('+')[0].Trim();

            if (!core.Contains('-', StringComparison.Ordinal) && Version.TryParse(core, out Version? version) && version.Build >= 0)
            {
                reference = "v" + version.ToString(3);
            }
        }

        return "https://github.com/lahma/marten-studio/blob/" + reference + "/docs/health/";
    }

    /// <summary>
    /// A heading's anchor as GitHub derives it: lower case, every character that is not a letter, a digit, a
    /// space, a hyphen or an underscore dropped, and each space a hyphen.
    /// </summary>
    internal static string Anchor(string heading)
    {
        ArgumentNullException.ThrowIfNull(heading);

        StringBuilder anchor = new(heading.Length);

        foreach (char c in heading.Trim().ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                anchor.Append(c);
            }
            else if (c == ' ')
            {
                anchor.Append('-');
            }
        }

        return anchor.ToString();
    }
}

/// <summary>
/// What the sweep holds a check by: three steps, the first the only one with I/O.
/// </summary>
/// <remarks>
/// Implemented by <see cref="HealthCheck{TFacts}" /> and nothing else; a check derives from that, which is
/// what keeps its gatherer and its evaluator two separate methods over one typed facts record.
/// </remarks>
internal interface IHealthCheck
{
    /// <summary>Reads the facts. All of the check's I/O, and nothing else.</summary>
    Task<object> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken);

    /// <summary>What the facts contribute to the history, keyed by series.</summary>
    IEnumerable<HealthSamplePart> Sample(object facts);

    /// <summary>Judges the facts. Pure.</summary>
    HealthVerdict Evaluate(object facts, HealthEvaluation evaluation);
}

/// <summary>
/// The base every health check derives from: an async fact gatherer and a pure evaluator over a facts
/// record of the check's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>The split is the design.</b> <see cref="GatherAsync" /> does every bit of I/O the check needs - SQL only
/// through <see cref="HealthCheckContext.Sql" />, whose statements come from the builders under
/// <c>Internal/Sql/</c> and run in a read-only transaction under <c>statement_timeout</c> and
/// <c>lock_timeout</c> - and returns plain data. <see cref="Evaluate" /> turns that data into findings and
/// touches nothing: not the clock (the time is <see cref="HealthEvaluation.Now" />), not the store, not a
/// service. That is the <c>IndexAdvice.Join</c> precedent, and it is what lets every threshold and every
/// sentence be unit-tested against stated facts with no Postgres.
/// </para>
/// <para>
/// <b>Stateless.</b> One instance serves every sweep of every database, so a check holds no fields; the
/// conventions test refuses one that does. Anything a check wants to remember between sweeps goes through
/// <see cref="Sample" /> into <see cref="HealthHistory" />, and comes back as
/// <see cref="HealthEvaluation.History" />.
/// </para>
/// <para>
/// <b>Expected states are values.</b> A gatherer that finds the thing it needs missing for a reason that is
/// the host's choice - no extension, no privilege, an old Postgres, an externally managed daemon - throws
/// <see cref="HealthCheckUnavailableException" />; the check is listed as not checked, with that reason,
/// and the log hears about it at Debug. Anything else it throws is a "could not check" row and, at most once
/// per database and kind of failure in ten minutes, a Warning.
/// </para>
/// </remarks>
/// <typeparam name="TFacts">What the gatherer reads and the evaluator judges.</typeparam>
internal abstract class HealthCheck<TFacts> : IHealthCheck
    where TFacts : notnull
{
    /// <summary>Reads the facts. All of the check's I/O.</summary>
    /// <param name="context">The database being swept, and the only way to query it.</param>
    /// <param name="cancellationToken">The check's own budget.</param>
    public abstract Task<TFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken);

    /// <summary>Judges the facts. Pure: everything it may use is in its two arguments.</summary>
    /// <param name="facts">What <see cref="GatherAsync" /> read.</param>
    /// <param name="evaluation">The time, the runtime and the history.</param>
    public abstract HealthVerdict Evaluate(TFacts facts, HealthEvaluation evaluation);

    /// <summary>
    /// What these facts add to <see cref="HealthHistory" />, for a rate the next sweep can compute. Nothing,
    /// unless a check says otherwise.
    /// </summary>
    public virtual IEnumerable<HealthSamplePart> Sample(TFacts facts) => [];

    /// <inheritdoc />
    async Task<object> IHealthCheck.GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        await GatherAsync(context, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    IEnumerable<HealthSamplePart> IHealthCheck.Sample(object facts) => Sample((TFacts) facts);

    /// <inheritdoc />
    HealthVerdict IHealthCheck.Evaluate(object facts, HealthEvaluation evaluation) => Evaluate((TFacts) facts, evaluation);
}

/// <summary>
/// What a gatherer throws for an expected state: the check cannot run here, for a reason that is the host's
/// choice rather than a fault.
/// </summary>
/// <remarks>
/// "<c>pg_stat_statements</c> is not installed", "this role cannot read other roles' sessions", "the event
/// store has no tables yet". The status row prints <see cref="Exception.Message" /> as it is, so it is written
/// for the person reading the page and names nothing a gate would hide.
/// </remarks>
internal sealed class HealthCheckUnavailableException : Exception
{
    /// <param name="reason">Why, for the status row.</param>
    public HealthCheckUnavailableException(string reason) : base(reason)
    {
    }

    /// <param name="reason">Why, for the status row.</param>
    /// <param name="innerException">What said so.</param>
    public HealthCheckUnavailableException(string reason, Exception innerException) : base(reason, innerException)
    {
    }
}
