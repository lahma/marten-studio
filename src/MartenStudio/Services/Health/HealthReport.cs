using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Health;

/// <summary>Why a visitor was given no report.</summary>
internal enum HealthRefusal
{
    /// <summary>Nothing refused.</summary>
    None,

    /// <summary>The store policy refused the scope.</summary>
    StorePolicy,

    /// <summary>The store, or the database, could not be reached or does not exist.</summary>
    Unavailable,
}

/// <summary>Whether a report includes a costlier kind of sweep.</summary>
internal enum HealthDeeperChecks
{
    /// <summary>Only if one ran recently and is still cached - never start one.</summary>
    IfCached,

    /// <summary>
    /// Run one now (or take the one already running): what "Run deeper checks" asks for. Honoured only for a visitor
    /// through the tier's gate; anyone else is answered as <see cref="IfCached" />.
    /// </summary>
    Run,
}

/// <summary>What a page asks for.</summary>
/// <param name="Deeper">Whether to run the tier-2 checks, or only show a recent run.</param>
/// <param name="Statements">Whether to run the tier-3 checks, or only show a recent run - for a visitor through the activity gate.</param>
internal sealed record HealthReportRequest(
    HealthDeeperChecks Deeper = HealthDeeperChecks.IfCached,
    HealthDeeperChecks Statements = HealthDeeperChecks.IfCached)
{
    /// <summary>The regular sweep, with whatever deeper results are still cached.</summary>
    public static HealthReportRequest Regular { get; } = new();
}

/// <summary>One finding as a visitor is shown it.</summary>
/// <param name="Finding">The finding, as the shared sweep recorded it.</param>
/// <param name="Evidence">Its evidence, with every value this visitor may not see withheld.</param>
/// <param name="MuteKey">What "mute in this browser" stores: the store, the database and the finding's key.</param>
/// <param name="DocsUrl">The check's docs anchor.</param>
/// <param name="RemedyAvailable">
/// Whether this host can use the remedy as written - <see langword="false" /> when it needs a feature this
/// host's Marten does not have.
/// </param>
/// <param name="HostRuns">What this host runs, for a remedy it cannot use yet ("Marten 9.39.0").</param>
internal sealed record HealthReportFinding(
    HealthFinding Finding,
    IReadOnlyList<HealthEvidence> Evidence,
    string MuteKey,
    string DocsUrl,
    bool RemedyAvailable,
    string? HostRuns)
{
    /// <summary>The finding's code.</summary>
    public string Code => Finding.Code;

    /// <summary>How much it matters.</summary>
    public HealthSeverity Severity => Finding.Severity;

    /// <summary>
    /// The page anchor of the finding's card, unique within a report: the report suffixes one whose key reads
    /// like another's, because an element id is one per page.
    /// </summary>
    public string Anchor { get; init; } = "finding-" + HealthCheckInfo.Anchor(Finding.Key.Replace('|', '-'));
}

/// <summary>One check's status row: whether it ran, and what it found or why not.</summary>
/// <param name="Code">The code.</param>
/// <param name="Title">What it looks for.</param>
/// <param name="Area">Its area.</param>
/// <param name="Tier">Its cost tier.</param>
/// <param name="Outcome">How its last run ended.</param>
/// <param name="FindingCount">How many findings this visitor is shown.</param>
/// <param name="Note">What its evaluator said beside "no finding", or why it did not apply.</param>
/// <param name="Problem">
/// Why it did not check, in the studio's words - then, each under its own label, Postgres' own words once every
/// withheld schema's name is masked out of them for this visitor (D27), and a connection error's words for a
/// visitor through the database-wide gate. No other exception's message is shown.
/// </param>
/// <param name="SqlState">The SQLSTATE, when Postgres said something.</param>
/// <param name="DocsUrl">The check's docs anchor.</param>
/// <param name="Statements">What it ran, for "Show SQL".</param>
internal sealed record HealthCheckStatus(
    string Code,
    string Title,
    HealthArea Area,
    HealthCostTier Tier,
    HealthCheckOutcome Outcome,
    int FindingCount,
    string? Note,
    string? Problem,
    string? SqlState,
    string DocsUrl,
    IReadOnlyList<HealthStatement> Statements);

/// <summary>Everything the Health page draws for one visitor and one database.</summary>
internal sealed record HealthReport
{
    /// <summary>Why there is no report, or <see cref="HealthRefusal.None" />.</summary>
    public HealthRefusal Refusal { get; init; }

    /// <summary>The sentence for a refusal.</summary>
    public string? Reason { get; init; }

    /// <summary>The store's key.</summary>
    public string StoreKey { get; init; } = string.Empty;

    /// <summary>The database's identity.</summary>
    public string DatabaseId { get; init; } = string.Empty;

    /// <summary>When the regular sweep read its facts.</summary>
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>How long the regular sweep took.</summary>
    public TimeSpan? SweepDuration { get; init; }

    /// <summary>When the deeper checks last ran, if their results are still cached.</summary>
    public DateTimeOffset? DeeperMeasuredAt { get; init; }

    /// <summary>When the statement checks last ran, if their results are still cached and this visitor may see them.</summary>
    public DateTimeOffset? StatementsMeasuredAt { get; init; }

    /// <summary>Every finding this visitor may see, worst first. Muting is the page's, per browser.</summary>
    public IReadOnlyList<HealthReportFinding> Findings { get; init; } = [];

    /// <summary>How many findings exist that this visitor is not shown.</summary>
    public int WithheldCount { get; init; }

    /// <summary>What it would take to see them, naming the setting or the policy.</summary>
    public string? WithheldReason { get; init; }

    /// <summary>Every check considered, by area and code.</summary>
    public IReadOnlyList<HealthCheckStatus> Checks { get; init; } = [];

    /// <summary>"Watching for 4 min (5 samples)".</summary>
    public string? History { get; init; }

    /// <summary>
    /// What the studio itself spent in this database in the last five minutes, or <see langword="null" /> for a
    /// visitor short of the database-wide gate - it is a number about the database as a whole.
    /// </summary>
    public StudioCost? Cost { get; init; }

    /// <summary>Why <see cref="Cost" /> is not shown, when it is not.</summary>
    public string? CostWithheld { get; init; }

    /// <summary>Whether any tier-2 check exists in this version of the studio.</summary>
    public bool DeeperChecksExist { get; init; }

    /// <summary>
    /// Whether this visitor may start the tier-2 checks. They read a bounded slice of the data in every table they
    /// check, which is a database-wide act, so it takes the store policy for the database as a whole
    /// (<see cref="HealthGate.Database" />) - decided in the service, whatever the page's button says.
    /// </summary>
    public bool DeeperChecksAllowed { get; init; }

    /// <summary>
    /// What starting the tier-2 checks would take, naming the policy, when they exist and this visitor may not.
    /// </summary>
    public string? DeeperChecksRefused { get; init; }

    /// <summary>Whether any tier-3 check exists in this version of the studio.</summary>
    public bool StatementChecksExist { get; init; }

    /// <summary>Whether this visitor may run the statement checks (the activity gate).</summary>
    public bool StatementChecksAllowed { get; init; }

    /// <summary>
    /// What running the statement checks would take, naming the setting or the policy, when they exist and this
    /// visitor may not - the service's refusal, said on the page so a disabled button is not a mystery.
    /// </summary>
    public string? StatementChecksRefused { get; init; }

    /// <summary>The worst severity among the findings, or <see langword="null" /> for none.</summary>
    public HealthSeverity? Worst => Findings.Count == 0 ? null : Findings.Min(static x => x.Severity);

    /// <summary>A report that says why there is none.</summary>
    public static HealthReport Refused(HealthRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}

/// <summary>One Critical finding, as the sidebar and the toast know it.</summary>
/// <param name="MuteKey">The finding's mute key, which also identifies it across sweeps.</param>
/// <param name="Title">Its title.</param>
internal sealed record HealthGlanceItem(string MuteKey, string Title);

/// <summary>
/// What the navigation badge knows about a database's health: counts from the last sweep, for this visitor,
/// unmuted - read without starting a sweep.
/// </summary>
/// <param name="MeasuredAt">When the sweep behind it read its facts.</param>
/// <param name="Critical">How many Critical findings.</param>
/// <param name="Warning">How many Warnings.</param>
/// <param name="Criticals">The Critical findings, for the toast.</param>
internal sealed record HealthGlance(DateTimeOffset MeasuredAt, int Critical, int Warning, IReadOnlyList<HealthGlanceItem> Criticals)
{
    /// <summary>The worst severity worth a badge, or <see langword="null" /> when there is nothing to draw.</summary>
    public HealthSeverity? Worst => Critical > 0 ? HealthSeverity.Critical : Warning > 0 ? HealthSeverity.Warning : null;
}
