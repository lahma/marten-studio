using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>
/// The diagnostics API's gate (API design §2.2, D31): the counterpart, for host code, of the Health page's visitor gate - with
/// no visitor, so no policy, capability or <c>ReadOnly</c> is asked, and one switch the host sets per call,
/// <see cref="PostgresDiagnosticsRequest.IncludeActivity" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same three gates, the same rule</b> (D29). <see cref="HealthGate.None" /> and <see cref="HealthGate.Database" /> pass:
/// the host's role reads the catalog and the statistics views itself. <see cref="HealthGate.Activity" /> - other people's
/// sessions and statements, application names, client addresses - passes only with <c>IncludeActivity</c>, and so does a
/// finding about one tenant, which names it (<see cref="HealthService.RequiredGate" />, the rule the page applies). A withheld
/// finding is counted, never described; a withheld check reads <see cref="PostgresReadOutcome.Withheld" />; a withheld piece
/// of evidence keeps its label and loses its value.
/// </para>
/// <para>
/// <b>What it does not apply</b>, because it is a visitor's view (API design §2.3): D27's masking of a schema the database
/// browser withholds, and <c>GatedSchemas</c> - the host's role reads <c>pg_class</c>.
/// </para>
/// <para>
/// <b>What it always applies</b>, because it is about the content, not the reader: a statement's text goes through
/// <see cref="SessionQueryText.Present(string?, bool, StatementScreen, int)" /> with the host's screen (<see cref="Screen" />) -
/// no withheld schema, every store's classifier, the document-type filter - so a hidden document type's table is never named;
/// T02's withholding and comment masking are applied where the text is read.
/// </para>
/// </remarks>
internal sealed class HostGate
{
    /// <summary>What a withheld piece of evidence, or a withheld section, says: the switch it would take.</summary>
    public const string WithheldValue = "withheld - Activity-grade: call with IncludeActivity = true";

    /// <summary>What <see cref="PostgresDiagnosticsSnapshot.Withheld" /> says of a section the request did not include.</summary>
    public const string WithheldSection = "Activity-grade: call with IncludeActivity = true";

    /// <param name="includeActivity">The request's switch.</param>
    /// <param name="screen">What a statement's text is checked against: the host's screen.</param>
    public HostGate(bool includeActivity, StatementScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        IncludeActivity = includeActivity;
        Screen = screen;
    }

    /// <summary>Whether Activity-grade data is included.</summary>
    public bool IncludeActivity { get; }

    /// <summary>The host's statement screen: no schema withheld, the content rules kept.</summary>
    public StatementScreen Screen { get; }

    /// <summary>
    /// The host's screen (API design §2.3): no withheld schema and no refused store - there is no visitor to refuse - with
    /// every store's classifier, so a hidden document type's table is withheld, and when the classifier could not be read
    /// while types may be hidden, every text is (fail closed, as for a visitor).
    /// </summary>
    /// <param name="classifier">Every registered store's classifier, or <see langword="null" /> when it could not be read.</param>
    /// <param name="hidesDocumentTypes">Whether the studio's document-type filter is set at all.</param>
    public static StatementScreen ScreenFor(DatabaseObjectClassifier? classifier, bool hidesDocumentTypes) =>
        new(new HashSet<string>(StringComparer.Ordinal), classifier, hidesDocumentTypes, []);

    /// <summary>Whether <paramref name="gate" /> passes.</summary>
    /// <param name="gate">The gate.</param>
    public bool Passes(HealthGate gate) => gate switch
    {
        HealthGate.None or HealthGate.Database => true,
        HealthGate.Activity => IncludeActivity,
        _ => false,
    };

    /// <summary>Whether <paramref name="finding" /> is included: its check's gate, raised to Activity for one about a tenant.</summary>
    /// <param name="finding">The finding.</param>
    public bool Shows(HealthFinding finding) => Passes(HealthService.RequiredGate(finding));

    /// <summary>
    /// One piece of evidence as the host is given it: its value at or below the gate; past it, its label and
    /// <see cref="WithheldValue" />; a statement's text through the host's screen, never the sweep's placeholder.
    /// </summary>
    /// <param name="evidence">The evidence, as the sweep recorded it.</param>
    public KeyValuePair<string, string> Evidence(HealthEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (!Passes(evidence.Gate))
        {
            return new KeyValuePair<string, string>(evidence.Label, WithheldValue);
        }

        if (evidence.Statement is { } statement)
        {
            SessionStatementText masked = SessionQueryText.Present(statement.Text, statement.Truncated, Screen);
            return new KeyValuePair<string, string>(evidence.Label, masked.Text ?? masked.Note ?? HealthEvidence.StatementPending);
        }

        return new KeyValuePair<string, string>(evidence.Label, evidence.Value);
    }
}
