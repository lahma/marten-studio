using System.Text.RegularExpressions;

namespace PostgresStudio.Services.Health;

/// <summary>One fact behind a finding: a number, a setting, a name.</summary>
/// <param name="Label">What it is, in the studio's words.</param>
/// <param name="Value">What it was, already formatted.</param>
/// <param name="Gate">
/// Who may see the value. A visitor short of it sees the label and that the value is withheld, and why - so a
/// check can say "3 sessions idle in transaction" to everybody and name them only behind
/// <see cref="HealthGate.Activity" />.
/// </param>
internal sealed record HealthEvidence(string Label, string Value, HealthGate Gate = HealthGate.None)
{
    /// <summary>What <see cref="Value" /> says in a sweep, for a statement's text not yet masked for anybody.</summary>
    public const string StatementPending = "withheld - a statement's text is shown only once it is masked for you";

    /// <summary>
    /// Another program's statement, carried apart from <see cref="Value" />: it is shown only after the report masks it
    /// for the visitor it is drawn for, and <see cref="Value" /> holds <see cref="StatementPending" /> until then.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A finding is shared by every visitor of the database, and what one of them may read of a statement depends on
    /// the schemas, the document types and the stores their own gates let them see (<c>StatementScreen</c>). So the
    /// sweep never puts the text where a page draws it: the report replaces <see cref="Value" /> with
    /// <c>SessionQueryText.Present</c>'s answer for that visitor - masked, withheld or cut - and drops this. Anything
    /// that shows evidence without going through the report shows the placeholder, which is the safe failure.
    /// </para>
    /// </remarks>
    public HealthStatementText? Statement { get; init; }

    /// <summary>
    /// A statement's text as evidence: behind <see cref="HealthGate.Activity" />, and masked per visitor when shown.
    /// </summary>
    /// <param name="label">What it is.</param>
    /// <param name="text">The text, as read.</param>
    /// <param name="truncated">Whether the read cut it.</param>
    public static HealthEvidence StatementText(string label, string text, bool truncated) =>
        new(label, StatementPending, HealthGate.Activity) { Statement = new HealthStatementText(text, truncated) };
}

/// <summary>A statement's text, unmasked, inside a sweep - never on a page as it is.</summary>
/// <param name="Text">The text.</param>
/// <param name="Truncated">Whether the read cut it.</param>
internal sealed record HealthStatementText(string Text, bool Truncated);

/// <summary>One statement a check ran, for "Show SQL" (D14): the text and its parameter names, never values.</summary>
/// <param name="Sql">The statement, exactly as it was sent.</param>
/// <param name="ParameterNames">Its parameters as the statement spells them (<c>@schemas</c>).</param>
internal sealed record HealthStatement(string Sql, IReadOnlyList<string> ParameterNames);

/// <summary>
/// What to change: a <c>StoreOptions</c> line the host pastes into its own registration - never DDL.
/// </summary>
/// <remarks>
/// <para>
/// The <c>IndexAdvice</c> rule, for the same reason: an index, a storage parameter or a setting the studio
/// applied itself is schema the host's configuration does not know about, and the next
/// <c>CreateOrUpdate</c> takes it away again. So the remedy is the line, and the host's own deploy applies
/// it. The constructor refuses text that begins a DDL statement, so an evaluator that tried to suggest one
/// fails its check - a "could not check" row - rather than putting it on the page.
/// </para>
/// <para>
/// A line that needs a newer library than the floor names the <see cref="StudioFeatureRequirement" /> it needs, and
/// the page prints the feature's requirement ("Marten 9.47+") beside it and, on a host that lacks it, what this
/// host runs (<see cref="IStudioRuntime" />). A feature can only come from the studio's probe table, so a remedy
/// cannot claim a version the probe does not check.
/// </para>
/// <para>
/// <b>The one exception is a relational table's migration SQL</b> (<see cref="ForMigration" />, issue #8 G12). A table
/// the host's own migrations own has no configuration that reconciles it, so the reason above does not hold - and
/// without DDL a plain data source's index and storage findings would have no remedy at all. Such a remedy is marked
/// <see cref="IsMigration" />, drawn under "For your own migrations - the studio never runs this", and only ever made by
/// a studio's <c>IHealthTableRemedies</c> for a <c>Relational</c> table, from SQL built under <c>Internal/Sql</c>.
/// </para>
/// </remarks>
internal sealed record HealthRemedy
{
    /// <summary>What a migration remedy says of itself, after its own explanation.</summary>
    public const string HandOverNote =
        "It is text for whoever owns the schema to put in a migration; the studio never runs it.";

    /// <summary>The heading a migration remedy is drawn under.</summary>
    public const string MigrationHeading = "For your own migrations - the studio never runs this";

    /// <summary>
    /// A remedy that is SQL for the host's own migrations - <c>ALTER TABLE ... SET (...)</c>,
    /// <c>CREATE INDEX CONCURRENTLY ...</c> - which the DDL rule of the constructor does not apply to.
    /// </summary>
    /// <param name="sql">The statement, built under <c>Internal/Sql</c> with every identifier quoted.</param>
    /// <param name="explanation">What it changes and what it costs; <see cref="HandOverNote" /> is added after it.</param>
    public static HealthRemedy ForMigration(string sql, string explanation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);

        return new HealthRemedy(sql, explanation + " " + HandOverNote, isMigration: true);
    }

    private HealthRemedy(string code, string explanation, bool isMigration)
    {
        Code = code;
        Explanation = explanation;
        IsMigration = isMigration;
    }

    /// <param name="code">The line, or lines, to paste.</param>
    /// <param name="explanation">What it changes, and what it costs.</param>
    /// <param name="requires">The newer-than-floor feature the line needs, or <see langword="null" />.</param>
    /// <exception cref="ArgumentException"><paramref name="code" /> is empty, or begins a DDL statement.</exception>
    public HealthRemedy(string code, string? explanation = null, StudioFeatureRequirement? requires = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (HealthRemedyRules.DdlStatement().IsMatch(code))
        {
            throw new ArgumentException(
                "A health remedy is a StoreOptions line, never DDL: an object the studio created would be one the host's configuration does not declare.",
                nameof(code));
        }

        Code = code;
        Explanation = explanation;
        Requires = requires;
    }

    /// <summary>The line, or lines, to paste.</summary>
    public string Code { get; }

    /// <summary>What it changes, and what it costs.</summary>
    public string? Explanation { get; }

    /// <summary>The newer-than-floor feature the line needs, or <see langword="null" />.</summary>
    public StudioFeatureRequirement? Requires { get; }

    /// <summary>
    /// Whether it is SQL for the host's own migrations (<see cref="ForMigration" />) rather than a configuration line.
    /// </summary>
    public bool IsMigration { get; }
}

/// <summary>What a remedy may not be.</summary>
internal static partial class HealthRemedyRules
{
    /// <summary>A line that starts a DDL statement, a privilege change or a maintenance command.</summary>
    [GeneratedRegex(
        @"(^|;)\s*(create|alter|drop|truncate|grant|revoke|vacuum|analyze|reindex|cluster|comment\s+on)\s",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    internal static partial Regex DdlStatement();
}

/// <summary>
/// One thing the advisor has to say about a database.
/// </summary>
/// <remarks>
/// <para>
/// An evaluator fills in what only it knows - severity, title, evidence, caveat, remedy, the subject and the
/// document types it is about. The sweep stamps the rest from the check's own metadata: the code's area, its
/// gate, its docs anchor, when it was measured and the statements its gatherer ran. A finding is shared by
/// every visitor of the database; what each one is shown of it is decided per visitor, after
/// (<see cref="HealthGate" />).
/// </para>
/// <para>
/// <b>Names go in gated evidence, never in the title.</b> A title is shown to everybody the check's gate
/// passes, so "a session has been idle in transaction for 12 minutes" is a title and its process id is a
/// piece of evidence behind <see cref="HealthGate.Activity" />. The same goes for a tenant: a finding about
/// one tenant sets <see cref="TenantId" />, and only a visitor who may see that tenant, through the activity
/// gate, is shown it at all.
/// </para>
/// </remarks>
internal sealed record HealthFinding
{
    /// <summary>The check's code - <c>K01</c>, <c>D02</c>.</summary>
    public required string Code { get; init; }

    /// <summary>How much it matters.</summary>
    public required HealthSeverity Severity { get; init; }

    /// <summary>One sentence, naming nothing a gate would hide.</summary>
    public required string Title { get; init; }

    /// <summary>Why it matters: what goes wrong, and when.</summary>
    public required string WhyItMatters { get; init; }

    /// <summary>
    /// What the finding is about within its code - a shard, a table - or <see langword="null" /> for a
    /// finding there can only be one of. Part of <see cref="Key" />, so muting one shard's finding does not
    /// mute another's.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>The tenant the finding is about, or <see langword="null" /> for the database as a whole.</summary>
    public string? TenantId { get; init; }

    /// <summary>The facts behind it.</summary>
    public IReadOnlyList<HealthEvidence> Evidence { get; init; } = [];

    /// <summary>
    /// How sure it is: the statistics reset time, a privilege that hides other roles' sessions, the window a
    /// rate was measured over.
    /// </summary>
    public string? Caveat { get; init; }

    /// <summary>What to change, or <see langword="null" /> when the finding is the answer in itself.</summary>
    public HealthRemedy? Remedy { get; init; }

    /// <summary>
    /// A screen of the studio that helps with this finding - the index advisor, the dead letters filtered to a
    /// projection - drawn beside the remedy, or <see langword="null" />. Shown to whoever is shown the finding,
    /// so it names nothing a gate would hide.
    /// </summary>
    public HealthLink? Link { get; init; }

    /// <summary>
    /// The document types it is about. A type the studio's document-type filter hides
    /// (<c>IStudioSettings.IsDocumentTypeVisible</c>) takes the whole finding with it: a hidden type never appears in a
    /// finding.
    /// </summary>
    public IReadOnlyList<Type> DocumentTypes { get; init; } = [];

    /// <summary>
    /// The schemas of the tables it names that are not one of the source's own schemas - an
    /// <c>ExtendedSchemaObjects</c> table in a schema of its own (<see cref="PostgresStudio.Services.Sources.StudioTable.GatedSchemas" />).
    /// Empty for everything else, which is nearly everything.
    /// </summary>
    /// <remarks>
    /// The database browser shows such a schema only past its own gate (<c>Capabilities.BrowseDatabase</c> and a
    /// <c>BrowsableSchemas</c> entry), and masks its name everywhere else (D27), so a finding that names a table in
    /// one is shown only to a visitor that gate does not withhold it from, and counted as withheld for anybody else.
    /// A read that cannot ask the gate withholds it.
    /// </remarks>
    public IReadOnlyList<string> GatedSchemas { get; init; } = [];

    // ---- Stamped by the sweep from the check's metadata ------------------------------------------------

    /// <summary>The check's area.</summary>
    public HealthArea Area { get; init; }

    /// <summary>The check's gate: who may see the finding at all.</summary>
    public HealthGate Gate { get; init; }

    /// <summary>The check's title, which is also its docs heading.</summary>
    public string CheckTitle { get; init; } = string.Empty;

    /// <summary>When the facts behind it were read.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>What the check's gatherer ran, for "Show SQL".</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>What identifies this finding among the others of its database: the code, then the subject.</summary>
    public string Key => Subject is null ? Code : Code + "|" + Subject;
}

/// <summary>
/// A link from a finding to the studio screen that helps with it: a route below the studio root and its query,
/// to which the page adds the visitor's scope (D9).
/// </summary>
/// <param name="Label">What the link says - "Open the index advisor".</param>
/// <param name="Route">The page, relative to the studio root, as the screens' own link helpers name it.</param>
/// <param name="Query">The page's own parameters; an entry whose value is blank is left out.</param>
internal sealed record HealthLink(string Label, string Route, IReadOnlyList<KeyValuePair<string, string?>> Query)
{
    /// <summary>A link with no parameters of its own.</summary>
    public HealthLink(string label, string route) : this(label, route, [])
    {
    }
}

/// <summary>What a check's evaluator concludes: its findings, and a note for the check's own status row.</summary>
/// <param name="Findings">Zero or more findings, each with a key of its own and evidence labels of its own.</param>
/// <param name="Note">
/// What the status row says when there is no finding and "no finding" alone would not be the whole truth -
/// "the host registers no IHostEnvironment, so the environment is not known". It is shown at its check's gate,
/// like the findings: a visitor short of the gate sees the row's outcome and not its note, so a note may say
/// what a finding would have said.
/// </param>
internal sealed record HealthVerdict(IReadOnlyList<HealthFinding> Findings, string? Note = null)
{
    /// <summary>Nothing to say, and perhaps why.</summary>
    public static HealthVerdict Quiet(string? note = null) => new([], note);

    /// <summary>These findings.</summary>
    public static HealthVerdict Of(params HealthFinding[] findings) => new(findings);
}
