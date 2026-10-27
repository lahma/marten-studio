using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The diagnostics API through <c>AddMartenStudio()</c> (API design §1.9, §5.3), over the health harness's real container: an
/// unknown source or database is a value, never an exception; Activity-grade findings, evidence and findings about one tenant
/// need <c>IncludeActivity</c>; a hidden document type's finding never leaves the sweeper; the catalogue holds Marten's codes;
/// and a call logs at Debug and nowhere higher. The store never connects, so the sections a test asks for fail as values.
/// </summary>
public class DiagnosticsServiceTests
{
    private const string ActivitySentinel = "SENTINEL-ACTIVITY-FINDING";
    private const string EvidenceSentinel = "SENTINEL-EVIDENCE-pid-4242";
    private const string TenantSentinel = "SENTINEL-TENANT-acme";
    private const string HiddenSentinel = "SENTINEL-HIDDEN-TYPE";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IPostgresDiagnostics Diagnostics(HealthHarness harness) => harness.Services.GetRequiredService<IPostgresDiagnostics>();

    private static PostgresDatabaseRef Database(HealthHarness harness) => new("default", harness.Scope.DatabaseId);

    private static PostgresDiagnosticsRequest Including(bool activity) => PostgresDiagnosticsRequest.FindingsOnly with { IncludeActivity = activity };

    [Fact]
    public async Task An_unknown_source_or_database_is_NotFound_and_never_throws()
    {
        await using HealthHarness harness = HealthHarness.Create();

        PostgresDiagnosticsSnapshot source = await Diagnostics(harness).GetSnapshotAsync(new PostgresDatabaseRef("no-such-store", ""), PostgresDiagnosticsRequest.AllSections, Token);
        PostgresDiagnosticsSnapshot database = await Diagnostics(harness).GetSnapshotAsync(new PostgresDatabaseRef("default", "no-such-database"), PostgresDiagnosticsRequest.AllSections, Token);

        source.Outcome.Should().Be(PostgresReadOutcome.NotFound);
        source.Reason.Should().NotBeNullOrWhiteSpace();
        source.Findings.Should().BeEmpty();
        source.Storage.Should().BeNull("nothing is read for a database that is not there");

        database.Outcome.Should().Be(PostgresReadOutcome.NotFound);
        harness.Script.Runs.Should().BeEmpty("no sweep runs for a database nobody has");
    }

    [Fact]
    public async Task Listing_yields_the_stores_database_and_a_snapshot_names_it_by_the_stores_key()
    {
        await using HealthHarness harness = HealthHarness.Create();

        List<PostgresDatabaseRef> listed = [];
        await foreach (PostgresDatabaseRef database in Diagnostics(harness).ListDatabasesAsync(Token))
        {
            listed.Add(database);
        }

        listed.Should().ContainSingle().Which.Should().Be(Database(harness));

        PostgresDiagnosticsSnapshot snapshot = await Diagnostics(harness).GetSnapshotAsync(new PostgresDatabaseRef("DEFAULT", ""), Including(false), Token);

        snapshot.Outcome.Should().Be(PostgresReadOutcome.Read);
        snapshot.Database.Should().Be(Database(harness), "the source's own key and the database's identity, whatever the caller spelt");
        snapshot.Producer.Should().StartWith("MartenStudio ");
    }

    [Fact]
    public async Task Activity_grade_findings_and_evidence_need_IncludeActivity()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script
            .Finds("C90", HealthSeverity.Warning, ActivitySentinel)
            .Finds("T90", HealthSeverity.Warning, "A table is read whole", static x =>
            {
                x.Subject = "public.orders";
                x.Evidence.Add(new HealthEvidence("Rows", "300 million"));
                x.Evidence.Add(new HealthEvidence("Session 1", EvidenceSentinel, HealthGate.Activity));
            });

        PostgresDiagnosticsSnapshot without = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Including(false), Token);
        PostgresDiagnosticsSnapshot with = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Including(true), Token);

        string hidden = without.ToJson();
        hidden.Should().NotContain(ActivitySentinel).And.NotContain(EvidenceSentinel);
        without.FindingsWithheld.Should().Be(1);
        without.Checks.Single(static x => x.Code == "C90").Outcome.Should().Be(PostgresReadOutcome.Withheld);
        without.Findings.Single(static x => x.Code == "T90").Evidence.Should().Contain(new KeyValuePair<string, string>("Session 1", HostGate.WithheldValue))
            .And.Contain(new KeyValuePair<string, string>("Rows", "300 million"));

        with.ToJson().Should().Contain(ActivitySentinel).And.Contain(EvidenceSentinel);
        with.FindingsWithheld.Should().Be(0);
        with.Checks.Single(static x => x.Code == "C90").Outcome.Should().Be(PostgresReadOutcome.Read);
        harness.Script.Runs["C90"].Should().Be(1, "both calls share the one regular sweep");
    }

    [Fact]
    public async Task A_finding_about_one_tenant_needs_IncludeActivity()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("T90", HealthSeverity.Warning, "One tenant's table", static x => x.TenantId = TenantSentinel);

        PostgresDiagnosticsSnapshot without = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Including(false), Token);
        PostgresDiagnosticsSnapshot with = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Including(true), Token);

        without.ToJson().Should().NotContain(TenantSentinel);
        without.Findings.Should().BeEmpty();
        without.FindingsWithheld.Should().Be(1);

        with.Findings.Should().ContainSingle().Which.TenantId.Should().Be(TenantSentinel);
        with.Findings[0].Key.Should().Be("T90", "the key is the finding's, and the tenant is its own property");
    }

    [Fact]
    public async Task A_hidden_document_types_finding_is_never_returned_or_counted()
    {
        await using HealthHarness harness = HealthHarness.Create(studio: static x => x.IsDocumentTypeVisible = static type => type != typeof(HiddenHealthThing));
        harness.Script.Finds("T90", HealthSeverity.Critical, HiddenSentinel, static x => x.DocumentTypes.Add(typeof(HiddenHealthThing)));

        PostgresDiagnosticsSnapshot snapshot = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Including(true), Token);

        snapshot.ToJson().Should().NotContain(HiddenSentinel);
        snapshot.Findings.Should().BeEmpty();
        snapshot.FindingsWithheld.Should().Be(0, "the sweeper drops it before anything counts it");
    }

    [Fact]
    public async Task The_catalogue_holds_Martens_codes_with_their_variants()
    {
        Type[] every =
        [
            .. MartenStudio.Services.MartenStudioDescriptor.Instance.HealthCheckAssemblies
                .Select(static x => x.Assembly).Distinct().SelectMany(HealthCheckRegistry.Discover),
        ];

        await using HealthHarness harness = HealthHarness.Create(checks: every);
        IReadOnlyList<PostgresCheck> checks = Diagnostics(harness).Checks;

        checks.Select(static x => x.Code).Should().Contain(["D01", "E02", "K01", "T09", "C08", "T03"]).And.BeInAscendingOrder(StringComparer.Ordinal);
        checks.Single(static x => x.Code == "T03").Variants.Should().Equal("index", "partitions");
        checks.Single(static x => x.Code == "T02").RequiresActivity.Should().BeTrue();
        checks.Should().OnlyContain(static x => x.Outcome == null, "the catalogue is what the studio runs, with no outcome");
        harness.Script.Runs.Should().BeEmpty("reading the catalogue reads nothing");
    }

    [Fact]
    public async Task Sections_past_the_activity_gate_are_named_as_withheld_and_a_failing_read_is_a_value()
    {
        await using HealthHarness harness = HealthHarness.Create();

        PostgresDiagnosticsSnapshot snapshot = await Diagnostics(harness).GetSnapshotAsync(Database(harness), PostgresDiagnosticsRequest.AllSections, Token);

        snapshot.Statements.Should().BeNull();
        snapshot.Withheld.Should().ContainKey("statements").And.ContainKey("connections.byApplication");
        snapshot.Storage.Should().NotBeNull();
        snapshot.Storage!.Outcome.Should().NotBe(PostgresReadOutcome.Read, "the harness's store refuses every connection");
        snapshot.Storage.Reason.Should().NotBeNullOrWhiteSpace();
        snapshot.Settings!.Outcome.Should().NotBe(PostgresReadOutcome.Read);
        snapshot.Connections!.Outcome.Should().NotBe(PostgresReadOutcome.Read);
        snapshot.Wal.Should().NotBeNull("AllSections asks for the write-ahead log too (issue #31)");
        snapshot.Wal!.Outcome.Should().NotBe(PostgresReadOutcome.Read, "the same store refuses the Replication page's read");
        snapshot.Withheld.Should().NotContainKey("wal", "nothing in it is Activity-grade");
    }

    /// <summary>
    /// W5-API-fix F6: when every check that reads the catalog failed to reach the database, the sections asked for are not read
    /// - each says so - so a dead database costs no further failed connection and no 9251 Warning per section.
    /// </summary>
    [Fact]
    public async Task A_database_the_sweep_could_not_reach_is_asked_nothing_more()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: [typeof(ScriptedConfigurationCheck), typeof(ScriptedCatalogCheck), typeof(ScriptedDeeperCheck)]);
        harness.Script.Throws("T90", new Npgsql.NpgsqlException("SENTINEL-CONNECTION-WORDS", new System.Net.Sockets.SocketException(10061)));

        PostgresDiagnosticsSnapshot snapshot = await Diagnostics(harness).GetSnapshotAsync(
            Database(harness),
            PostgresDiagnosticsRequest.AllSections with { IncludeActivity = true, RunDeeperChecks = true },
            Token);

        snapshot.Outcome.Should().Be(PostgresReadOutcome.Failed, snapshot.Reason);
        snapshot.Checks.Single(static x => x.Code == "T90").Outcome.Should().Be(PostgresReadOutcome.Failed);

        foreach ((string Name, PostgresReadOutcome? Outcome, string? Reason) section in new (string, PostgresReadOutcome?, string?)[]
        {
            ("storage", snapshot.Storage?.Outcome, snapshot.Storage?.Reason),
            ("statements", snapshot.Statements?.Outcome, snapshot.Statements?.Reason),
            ("settings", snapshot.Settings?.Outcome, snapshot.Settings?.Reason),
            ("connections", snapshot.Connections?.Outcome, snapshot.Connections?.Reason),
            ("wal", snapshot.Wal?.Outcome, snapshot.Wal?.Reason),
        })
        {
            section.Outcome.Should().Be(PostgresReadOutcome.Failed, section.Name + " is there, and says it was not read");
            section.Reason.Should().Be(PostgresDiagnosticsService.NotReachedSection, section.Name);
        }

        snapshot.ToJson().Should().NotContain("SENTINEL-CONNECTION-WORDS");
        harness.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9251, "no section was read, so none failed");
        harness.Script.Runs.Should().NotContainKey("E90", "no costlier sweep is started for a database that cannot be reached");
    }

    [Fact]
    public async Task A_call_is_logged_at_Debug_with_codes_and_counts_and_never_higher()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("T90", HealthSeverity.Warning, "A table is read whole", static x => x.Subject = "public.orders");

        await Diagnostics(harness).GetSnapshotAsync(Database(harness), Including(false), Token);

        CapturedLogEntry read = harness.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9250).Subject;
        read.Level.Should().Be(LogLevel.Debug);
        read.Message.Should().Contain("1 findings").And.NotContain("public.orders", "a log line carries counts, never a finding's subject");
        harness.Logs.Entries.Where(static x => x.EventId.Id is >= 9250 and <= 9254).Should().OnlyContain(static x => x.Level == LogLevel.Debug);
    }
}
