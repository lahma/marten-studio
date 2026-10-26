using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;
using PostgresStudio.Tests.Components;

using static PostgresStudio.Tests.Statements.StatementsTestFacts;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The Statements page's per-visitor half (W5-Q1): the activity gate in Database › Sessions' order, with the statement sweep
/// never taken for a visitor short of it; every text masked for its visitor; a group naming a table in a schema they may not
/// see shown without it; the extension's status worded for the platform; the first read audited once per circuit.
/// </summary>
public class StatementsServiceTests
{
    /// <summary>The facts read, counted: a refused visitor must never cause one, and a poll never runs one.</summary>
    private sealed class CountingFacts(StatementFacts facts, StatementFacts? latest = null) : IStatementFacts
    {
        public int Reads { get; private set; }

        public int Lookups { get; private set; }

        public Task<StatementFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(facts);
        }

        public StatementFacts? Latest(ResolvedSource resolved)
        {
            Lookups++;
            return latest;
        }
    }

    private static StatementsService Service(PostgresStudioComponentContext context, IStatementFacts facts)
    {
        IServiceProvider services = context.Services;

        return new StatementsService(
            services.GetRequiredService<IStudioSettings>(),
            services.GetRequiredService<StudioCapabilityGuard>(),
            services.GetRequiredService<StudioAuthorization>(),
            ActivatorUtilities.CreateInstance<StudioSourceResolver>(services),
            facts,
            services.GetRequiredService<StudioActionLog>(),
            new StudioLogThrottle(TimeProvider.System),
            NullLogger<StatementsService>.Instance);
    }

    private static StatementFacts Some() => Facts(Read(null, Statement(1, "select * from public.orders where id = $1", 10, 100, [Orders])));

    private static readonly StudioScope Scope = new("default", "default", null);

    [Fact]
    public async Task With_ViewDatabaseActivity_off_the_page_says_so_and_reads_nothing()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        CountingFacts facts = new(Some());

        StatementsView view = await Service(context, facts).GetAsync(Scope, TestContext.Current.CancellationToken);

        view.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        view.Reason.Should().Be(PostgresStudioText.Instance.StatementsCapabilityDenial).And.Contain("PostgresStudioOptions.Capabilities.ViewDatabaseActivity");
        facts.Reads.Should().Be(0, "a visitor short of the gate never causes a statement sweep");
        context.AuthorizationService.Calls.Should().BeEmpty("nobody is asked who is asking about a capability the process does not have");
    }

    [Fact]
    public async Task Under_ReadOnly_the_page_says_ReadOnly_turned_it_off()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.ReadOnly = true;
        await context.ReadyAsync();
        CountingFacts facts = new(Some());

        StatementsView view = await Service(context, facts).GetAsync(Scope, TestContext.Current.CancellationToken);

        view.Refusal.Should().Be(DatabaseRefusal.ReadOnly);
        view.Reason.Should().Contain("ReadOnly is true");
        facts.Reads.Should().Be(0);
    }

    [Fact]
    public async Task The_write_policy_is_asked_with_the_capability_named_and_no_tenant_and_its_refusal_reads_nothing()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.SourceAuthorizationPolicy = "source";
        context.Options.WriteAuthorizationPolicy = "write";
        context.AuthorizationService.Allow(static resource => resource.Capability is null);
        await context.ReadyAsync();
        CountingFacts facts = new(Some());

        StatementsView view = await Service(context, facts).GetAsync(Scope with { TenantId = "acme" }, TestContext.Current.CancellationToken);

        view.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        view.Reason.Should().Be(PostgresStudioText.Instance.StatementsWritePolicyDenial);
        facts.Reads.Should().Be(0);
        context.AuthorizationService.Calls.Should().Contain(static x =>
            x.Policy == "write" && x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity));
        context.AuthorizationService.Calls.Should().Contain(static x => x.Policy == "source" && x.Resource.Capability == null,
            "the tenant is dropped: a statement belongs to no tenant");
    }

    [Fact]
    public async Task The_store_policy_refuses_the_database_as_a_whole_and_reads_nothing()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.SourceAuthorizationPolicy = "source";
        context.AuthorizationService.Allow(static _ => false);
        await context.ReadyAsync();
        CountingFacts facts = new(Some());

        StatementsView view = await Service(context, facts).GetAsync(Scope, TestContext.Current.CancellationToken);

        view.Refusal.Should().Be(DatabaseRefusal.StorePolicy);
        view.Reason.Should().Be(PostgresStudioText.Instance.StatementsStorePolicyDenial);
        facts.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Through_the_gate_the_statements_are_read_once_and_the_first_read_is_audited_once_per_circuit()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();
        CountingFacts facts = new(Some());
        StatementsService service = Service(context, facts);

        StatementsView first = await service.GetAsync(Scope, TestContext.Current.CancellationToken);
        await service.GetAsync(Scope, TestContext.Current.CancellationToken);

        first.Refusal.Should().Be(DatabaseRefusal.None);
        first.Rows.Should().ContainSingle();
        first.StudioCost.Should().NotBeNull("the studio's own line is the cost meter's");
        facts.Reads.Should().Be(2);
        service.RecordedReads.Should().Be(1, "the page's polls repeat the signature and record nothing");

        IReadOnlyList<StudioActionLogEntry> entries = context.Services.GetRequiredService<StudioActionLogService>().GetLatest();
        entries.Should().ContainSingle(static x => x.Action == StatementsService.Action)
            .Which.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
    }

    [Fact]
    public void A_text_is_masked_for_its_visitor_and_withheld_when_their_mask_cannot_be_read()
    {
        StatementsService service = new(new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions())),
            null!, null!, null!, null!, null!, new StudioLogThrottle(TimeProvider.System), NullLogger<StatementsService>.Instance);

        StatementFacts facts = Facts(Read(
            null,
            Statement(1, "select s.amount from hr.salaries s where s.id = $1", 10, 100, [Salaries]),
            Statement(2, "copy (select 'secret') to stdout", 1, 50)));

        StatementsView withheld = service.Show(facts, gate: null);
        withheld.TextsWithheld.Should().Be(StatementsService.TextsUnchecked);
        withheld.Rows.Single(static x => x.QueryId == 1).Text.Should().BeNull();
        withheld.Rows.Single(static x => x.QueryId == 1).TextNote.Should().Be(SessionQueryText.SchemasUncheckedNote);
        withheld.Rows.Single(static x => x.QueryId == 1).GroupLabel.Should().Be("select · a table in a schema you may not see",
            "a gated table is never named when the gate cannot be read");
        withheld.Rows.Single(static x => x.QueryId == 2).TextNote.Should().Be(StatementReads.WithheldUtility);

        DatabaseGateRead hrWithheld = new(
            new DatabaseGate(
                ["public"],
                [new CatalogSchema("public", true, false), new CatalogSchema("hr", true, false)],
                ["public"],
                capabilityEnabled: true,
                readOnly: false,
                authorized: true,
                new DatabaseObjectClassifier([])),
            DatabaseRefusal.None,
            null);

        StatementRowView masked = service.Show(facts, hrWithheld).Rows.Single(static x => x.QueryId == 1);
        masked.Text.Should().Be("select s.amount from ‹withheld›.salaries s where s.id = $1");
        masked.GroupLabel.Should().Be("select · a table in a schema you may not see");
        masked.GroupKey.Should().Be("select|withheld");
    }

    [Fact]
    public void A_statement_with_no_group_says_whether_its_text_was_read()
    {
        StatementsService service = new(new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions())),
            null!, null!, null!, null!, null!, new StudioLogThrottle(TimeProvider.System), NullLogger<StatementsService>.Instance);

        StatementsView view = service.Show(Facts(Read(null, Statement(1, "(select 1) union (select 2)", 1, 2), Statement(2, null, 1, 1))), gate: null);

        view.Rows.Single(static x => x.QueryId == 1).GroupLabel.Should().Be("not grouped");
        view.Rows.Single(static x => x.QueryId == 2).GroupLabel.Should().Be("its text was not read");
        view.Rows.Single(static x => x.QueryId == 2).TextNote.Should().Be(StatementsService.TextNotReturned);
    }

    [Theory]
    [InlineData(PostgresPlatformKind.SelfHosted, "postgresql.conf", "a superuser can run CREATE EXTENSION pg_stat_statements")]
    [InlineData(PostgresPlatformKind.AmazonAurora, "is \"typically\" loaded already, in AWS's word", "the master user (a member of rds_superuser) can run CREATE EXTENSION pg_stat_statements")]
    public void The_extensions_status_names_who_can_fix_it_on_this_platform(PostgresPlatformKind kind, string where, string who)
    {
        StatementFacts facts = StatementFacts.Build(
            "default", "default", Now,
            new StatementReadOutcome(StatementReadState.NotInstalled, new StatementExtension(170011, null, null), null) { Platform = new PostgresPlatform(kind, null) },
            null, HealthHistoryView.Empty, null, Tables, null);

        string status = StatementsService.Status(facts, PostgresStudioText.Instance)!;

        status.Should().StartWith("pg_stat_statements is not installed in this database").And.Contain(where).And.Contain(who);
        status.Should().NotContain("GRANT", "a grant is named in words, never as a statement to paste");
    }

    [Fact]
    public void A_status_names_the_sources_own_role_in_each_studios_words()
    {
        StatementFacts nothing = StatementFacts.Build(
            "default", "default", Now,
            new StatementReadOutcome(StatementReadState.NothingRecorded, Pg17, null), null, HealthHistoryView.Empty, null, Tables, null);

        StatementsService.Status(nothing, PostgresStudioText.Instance).Should().Contain("of the data source's own role");
        StatementsService.Status(nothing, StudioText.Marten).Should().Contain("of the store's own role");
        StatementsService.Status(Facts(Read(null)), PostgresStudioText.Instance).Should().BeNull("it read");
    }

    [Fact]
    public void The_window_is_said_as_watched_or_as_since_the_counters_began()
    {
        StatementFacts since = Facts(Read(null, Statement(1, "select 1", 1, 1)));
        StatementsService.WindowText(since).Should().StartWith("Each statement's totals since its counters began")
            .And.Contain("A window appears once the statement checks have run twice");

        StatementFacts watched = Facts(
            Read(2_000, Statement(1, "select 1", 20, 2_000)),
            History(Sample(TimeSpan.FromMinutes(25), 1_000, (1, 10, 1_000)), Sample(TimeSpan.Zero, 2_000, (1, 20, 2_000))));

        StatementsService.WindowText(watched).Should().StartWith("Over the last 25 min the statement checks watched (2026-10-26 11:35 UTC to 2026-10-26 12:00 UTC)");
    }

    /// <summary>
    /// W5-Q1-fix F6: the page's poll shows the last statement sweep that finished and never runs one - a sweep plans T02's
    /// statements, and a tab left open must not do that every five minutes on nobody's behalf - behind the same gate.
    /// </summary>
    [Fact]
    public async Task A_poll_shows_the_last_sweep_and_never_runs_one()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        CountingFacts none = new(Some(), latest: null);
        StatementsView notRun = await Service(context, none).GetLatestAsync(Scope, TestContext.Current.CancellationToken);

        notRun.Refusal.Should().Be(DatabaseRefusal.None);
        notRun.State.Should().Be(StatementsState.NotRun);
        notRun.Status.Should().Be(StatementsService.NotRun);
        notRun.Rows.Should().BeEmpty();
        notRun.StudioCost.Should().NotBeNull("the studio's own line is the meter's, read or not");
        (none.Reads, none.Lookups).Should().Be((0, 1), "a poll is a lookup, never a sweep");

        CountingFacts some = new(Some(), latest: Some());
        StatementsView shown = await Service(context, some).GetLatestAsync(Scope, TestContext.Current.CancellationToken);

        shown.State.Should().Be(StatementsState.Read);
        shown.Rows.Should().ContainSingle();
        (some.Reads, some.Lookups).Should().Be((0, 1));
    }

    [Fact]
    public async Task A_poll_is_gated_as_the_load_is_and_looks_nothing_up_for_a_refused_visitor()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        CountingFacts facts = new(Some(), latest: Some());

        StatementsView view = await Service(context, facts).GetLatestAsync(Scope, TestContext.Current.CancellationToken);

        view.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        (facts.Reads, facts.Lookups).Should().Be((0, 0));
    }

    /// <summary>
    /// W5-Q1-fix F2: which of the studio's own statements the page can list depends on the role each read runs as - with
    /// SqlConsoleRole set, the reads it narrows are not among them and the rest are, when they rank; with none set, all of them
    /// are, when they rank. Never "none is left out": only the statements kept are listed.
    /// </summary>
    [Fact]
    public void The_studio_line_says_which_of_the_studios_reads_this_page_can_list()
    {
        StatementsService Of(string? role) => new(
            new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions { SqlConsoleRole = role })),
            null!, null!, null!, null!, null!, new StudioLogThrottle(TimeProvider.System), NullLogger<StatementsService>.Instance);

        string narrowed = Of("studio_reader").StudioCostText();
        narrowed.Should().Contain("SqlConsoleRole is studio_reader. " + PostgresStudioText.Instance.StatementsStudioRoles);
        PostgresStudioText.Instance.StatementsStudioRoles.Should().Contain("but these reads of pg_stat_statements runs as it")
            .And.Contain("when they rank among the ones kept");

        string none = Of(null).StudioCostText();
        none.Should().Contain("With no SqlConsoleRole set, every statement the studio runs here runs as the data source's own role")
            .And.Contain("when they rank among the ones kept, and not otherwise");

        foreach (string line in new[] { narrowed, none })
        {
            line.Should().NotContain("none is left out").And.NotContain("only its reads of pg_stat_statements are listed");
        }

        // MartenStudio's reads of Marten's own tables run as the store's role whatever SqlConsoleRole is, and it says so.
        StudioText.Marten.StatementsStudioRoles.Should()
            .Contain("The document, event and projection reads, tenant discovery")
            .And.Contain("Marten's own sessions")
            .And.Contain("run as the store's own role");
    }

    /// <summary>W5-Q1-fix F1: on pg_stat_statements 1.8 a statement's nested runs are in its row, and the caveat says so.</summary>
    [Fact]
    public void The_caveat_says_what_a_statement_a_function_runs_counts_as_for_the_installed_version()
    {
        StatementRead read = Read(null, Statement(1, "select 1", 1, 1));

        StatementsService.Caveat(Facts(read), PostgresStudioText.Instance).Should()
            .Contain("Top-level statements only").And.Contain("with the most execution time per second since their counters began");

        StatementFacts old = StatementFacts.Build(
            "default", "default", Now, Outcome(read with { Extension = new StatementExtension(140000, "public", "1.8") }), null,
            HealthHistoryView.Empty, null, Tables, PostgresStatementClassifier.Instance);

        old.FoldsNested.Should().BeTrue();
        StatementsService.Caveat(old, PostgresStudioText.Instance).Should()
            .Contain("pg_stat_statements 1.8 has no toplevel column, so a statement a function runs is summed into its own row")
            .And.NotContain("Top-level statements only");
    }
}
