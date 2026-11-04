using MartenStudio.Tests.Health;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.2-INDEX-B: T15's "What if it had an index?" and T01's "Try indexes against this table's statements", through the real runner
/// over fakes - so no database is read. The runner refuses T01's action below the Activity gate and T15's below the Database gate;
/// a press is one audit entry, carrying the advisor's counts; the advisor's own gate is asked again and its own entry is not written;
/// the lookup the studio composes is quoted, a read of the catalog and never an index it runs.
/// </summary>
public sealed class IndexWhatIfActionTests : IAsyncDisposable
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private readonly PostgresStudioComponentContext context = new();

    public ValueTask DisposeAsync() => context.DisposeAsync();

    // ---- Which finding offers which action -------------------------------------------------------------------------------

    [Fact]
    public void T01_offers_the_advisor_and_T15_the_lookup_and_no_other_finding_offers_either()
    {
        FakeStatements statements = new();
        HealthActionService service = Service(statements);

        service.OffersFor(Finding("T01", "public.orders")).Should().ContainSingle().Which.Id.Should().Be(SequentialScanWhatIfAction.ActionId);
        service.OffersFor(Finding("T15", "public.orders.fk")).Should().ContainSingle().Which.Id.Should().Be(ForeignKeyWhatIfAction.ActionId);
        service.OffersFor(Finding("T02", null)).Should().BeEmpty();
    }

    [Fact]
    public void The_two_actions_declare_the_gates_the_design_names_and_no_capability()
    {
        SequentialScanWhatIfAction t01 = new(new FakeStatements());
        ForeignKeyWhatIfAction t15 = Action15();

        t01.Gate.Should().Be(HealthGate.Activity, "it plans statement texts, which are other programs' source text");
        t01.Capability.Should().BeNull();
        t01.Id.Should().Be("t01.try-indexes");
        t01.Label.Should().Be("Try indexes against this table's statements");
        t01.AppliesTo(Finding("T01", "x").Finding).Should().BeTrue();
        t01.AppliesTo(Finding("T15", "x").Finding).Should().BeFalse();

        t15.Gate.Should().Be(HealthGate.Database, "the finding's own: what it shows is what the finding already shows");
        t15.Capability.Should().BeNull();
        t15.Id.Should().Be("t15.what-if");
        t15.Label.Should().Be("What if it had an index?");
        t15.AppliesTo(Finding("T15", "x").Finding).Should().BeTrue();
        t15.AppliesTo(Finding("T01", "x").Finding).Should().BeFalse();
    }

    [Fact]
    public void A_finding_about_a_schema_the_visitor_may_not_see_offers_neither()
    {
        HealthActionService service = Service(new FakeStatements());

        foreach (string code in new[] { "T01", "T15" })
        {
            HealthReportFinding finding = Finding(code, "hr.salaries");
            service.OffersFor(finding with { Finding = finding.Finding with { GatedSchemas = ["hr"] } }).Should().BeEmpty("fail closed");
        }
    }

    // ---- The runner's gates ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_runner_refuses_T01s_action_below_the_Activity_gate_and_the_advisor_is_never_asked()
    {
        FakeStatements statements = new();

        HealthActionResult result = await Service(statements).RunAsync(Scope, Finding("T01", "public.orders"), SequentialScanWhatIfAction.ActionId, Token);

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().Contain("ViewDatabaseActivity");
        statements.Calls.Should().BeEmpty();
        Audited("Try indexes for a table").Should().ContainSingle().Which.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task The_runner_refuses_T15s_action_below_the_Database_gate()
    {
        context.Options.SourceAuthorizationPolicy = "source";
        int before = context.AuthorizationService.Calls.Count;
        await Resolver().ResolveAsync(Scope, null, Token);
        int resolverQuestions = context.AuthorizationService.Calls.Count - before;

        int asked = 0;
        context.AuthorizationService.Allow(_ => ++asked <= resolverQuestions);

        HealthActionResult result = await Service(new FakeStatements()).RunAsync(Scope, Finding("T15", "public.orders.fk"), ForeignKeyWhatIfAction.ActionId, Token);

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().StartWith("This action needs ");
        Audited("What-if foreign key lookup").Should().ContainSingle().Which.Succeeded.Should().BeFalse();
    }

    // ---- T01: the advisor, narrowed ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_press_is_one_audit_entry_carrying_the_advisors_counts_and_the_advisor_is_told_not_to_write_its_own()
    {
        context.WithAllCapabilities();
        FakeStatements statements = new() { View = Advised() };

        HealthActionResult result = await Service(statements).RunAsync(Scope, Finding("T01", "public.orders"), SequentialScanWhatIfAction.ActionId, Token);

        result.Outcome.Should().Be(HealthActionOutcome.Done);
        statements.Calls.Should().ContainSingle().Which.Should().Match<(StudioScope Scope, IndexAdviceRequest Request, bool RecordAudit)>(
            x => x.Request.Table == "public.orders" && x.Request.Columns.Count == 0 && !x.RecordAudit && x.Scope.TenantId == null
                && x.Scope.StoreKey == "default" && x.Scope.DatabaseId == "default");

        StudioActionLogEntry entry = Audited("Try indexes for a table").Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
        entry.Message.Should().Be("Planned 18 of 20 statements; tried 9 of 9 indexes in 40 plans; 1 would help.");
        entry.Target.Should().Be("the table's statements");
        context.ActionLog.GetLatest().Should().NotContain(static x => x.Action == StatementsService.IndexAdviceAction, "the advisor's own entry is the runner's");
    }

    [Fact]
    public async Task The_result_lists_the_recommendation_with_its_remedy_a_copy_and_a_link_to_the_panel_for_the_table()
    {
        context.WithAllCapabilities();
        FakeStatements statements = new() { View = Advised() };

        HealthActionResult result = await Service(statements).RunAsync(Scope, Finding("T01", "public.orders"), SequentialScanWhatIfAction.ActionId, Token);

        result.Summary.Should().Be("Add this index: saves ~38% of the planned cost of the 20 statements tried, weighted by calls, across 4 statements.");
        result.Columns.Should().Equal("Index", "Helped", "Planned cost, weighted by calls", "Saved", "Remedy", "Details");
        HealthActionRow row = result.Rows.Should().ContainSingle().Subject;
        row.Cells[0].Text.Should().Be("An index on public.orders (customer_id)");
        row.Cells[1].Text.Should().Be("4");
        row.Cells[2].Text.Should().Be("5,000 → 500");
        row.Cells[3].Text.Should().Be("~38%");
        row.Cells[4].Should().Match<HealthActionCell>(static x => x.Mono && x.Copy && x.Text.StartsWith("CREATE INDEX CONCURRENTLY", StringComparison.Ordinal));
        HealthLink link = row.Cells[5].Link.Should().NotBeNull().And.Subject.As<HealthLink>();
        link.Label.Should().Be("Details on Statements");
        link.Route.Should().Be("statements");
        link.Query.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string?>("advise", "public.orders"));
        result.Notes.Should().Contain(IndexAdvisor.Caveat);
    }

    [Fact]
    public async Task At_most_five_recommendations_are_listed_and_none_says_so()
    {
        context.WithAllCapabilities();
        IndexAdviceItem item = Advised().Recommended[0];
        FakeStatements statements = new() { View = Advised() with { Recommended = [.. Enumerable.Range(0, 8).Select(i => item with { Index = item.Index with { Key = "k" + i } })] } };

        (await Service(statements).RunAsync(Scope, Finding("T01", "public.orders"), SequentialScanWhatIfAction.ActionId, Token)).Rows.Should().HaveCount(5);

        statements.View = Advised() with { Recommended = [] };
        HealthActionResult none = await Service(statements).RunAsync(Scope, Finding("T01", "public.orders"), SequentialScanWhatIfAction.ActionId, Token);

        none.Rows.Should().BeEmpty();
        none.Summary.Should().Be("No index these plans suggest would help the 20 statements on this table that you may see.");
    }

    [Fact]
    public async Task The_advisors_refusals_hypopgs_absence_and_a_failure_map_to_the_actions_outcomes()
    {
        context.WithAllCapabilities();
        FakeStatements statements = new() { View = new IndexAdviceView { Unavailable = HypotheticalIndexAvailability.NotInstalledSentence, AuditOutcome = "Not run: hypopg is not installed." } };
        HealthActionService service = Service(statements);

        HealthActionResult unavailable = await service.RunAsync(Scope, Finding("T01", "public.orders"), SequentialScanWhatIfAction.ActionId, Token);
        unavailable.Outcome.Should().Be(HealthActionOutcome.Refused);
        unavailable.Reason.Should().Be(HypotheticalIndexAvailability.NotInstalledSentence);

        statements.View = new IndexAdviceView { Reason = IndexAdvisor.AlreadyRunningForDatabase };
        (await service.RunAsync(Scope, Finding("T01", "public.orders"), SequentialScanWhatIfAction.ActionId, Token)).Outcome.Should().Be(HealthActionOutcome.Refused);

        statements.View = new IndexAdviceView { Reason = "Index advice failed (InvalidOperationException).", AuditOutcome = "Index advice failed (InvalidOperationException)." };
        (await service.RunAsync(Scope, Finding("T01", "public.orders"), SequentialScanWhatIfAction.ActionId, Token)).Outcome.Should().Be(HealthActionOutcome.Failed);

        statements.View = new IndexAdviceView { Refusal = DatabaseRefusal.WritePolicy, Reason = "Your write policy refused it." };
        (await service.RunAsync(Scope, Finding("T01", "public.orders"), SequentialScanWhatIfAction.ActionId, Token)).Outcome.Should().Be(HealthActionOutcome.Refused);
    }

    // ---- T15: the lookup the studio composes -------------------------------------------------------------------------------

    [Fact]
    public void The_lookup_is_quoted_names_the_table_only_and_has_one_placeholder_per_column_and_no_lock_clause()
    {
        ForeignKeyLookupQueries.Lookup("public", "order_lines", ["order_id"]).Should().Be("select 1 from only \"public\".\"order_lines\" x where x.\"order_id\" = $1");
        ForeignKeyLookupQueries.Lookup("public", "order_lines", ["order_id", "tenant_id"]).Should()
            .Be("select 1 from only \"public\".\"order_lines\" x where x.\"order_id\" = $1 and x.\"tenant_id\" = $2")
            .And.NotContainEquivalentOf("for key share").And.NotContainEquivalentOf("for update");
        ForeignKeyLookupQueries.Lookup("My Schema", "Line.Items", ["Mixed Case", "a.b"]).Should()
            .Be("select 1 from only \"My Schema\".\"Line.Items\" x where x.\"Mixed Case\" = $1 and x.\"a.b\" = $2");
    }

    [Fact]
    public void A_name_that_cannot_be_quoted_honestly_gets_no_lookup()
    {
        ForeignKeyLookupQueries.Lookup("public", "t\"; drop table x; --", ["a"]).Should().BeNull();
        ForeignKeyLookupQueries.Lookup("public", "t", ["a\"b"]).Should().BeNull();
        ForeignKeyLookupQueries.Lookup("pub\"lic", "t", ["a"]).Should().BeNull();
        ForeignKeyLookupQueries.Lookup("public", "t", []).Should().BeNull();
        ForeignKeyLookupQueries.Lookup("public", "t‮", ["a"]).Should().BeNull("a right-to-left override makes the text say something else");
    }

    [Fact]
    public void The_lookups_batch_is_a_plain_select_the_drill_ins_own_guard_accepts()
    {
        string lookup = ForeignKeyLookupQueries.Lookup("public", "order_lines", ["order_id"])!;

        StatementPlanQueries.GenericPlan(lookup, truncated: false).Should().NotBeNull().And.Contain("explain (verbose, format json) execute");
        HealthStatementQueries.Refusal(lookup, truncated: false).Should().Be(StatementPlanRefusal.None);
    }

    [Fact]
    public void The_key_is_read_again_from_pg_catalog_alone_with_the_subject_a_parameter()
    {
        string sql = ForeignKeyLookupQueries.KeySql;

        sql.Should().Contain("pg_catalog.pg_constraint k").And.Contain("= @subject").And.Contain("k.contype = 'f'").And.Contain("k.conparentid = 0");
        sql.Should().NotContainEquivalentOf("information_schema").And.NotContainEquivalentOf("to_regclass");
        ForeignKeyLookupQueries.SubjectParameter.Should().Be("subject");
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------------

    private ForeignKeyWhatIfAction Action15()
    {
        PostgresStudio.Services.Settings.IStudioSettings settings = context.Services.GetRequiredService<PostgresStudio.Services.Settings.IStudioSettings>();

        return new ForeignKeyWhatIfAction(
            new HypotheticalIndexReader(settings, NullLogger<HypotheticalIndexReader>.Instance),
            new StatementPlanReader(settings, NullLogger<StatementPlanReader>.Instance));
    }

    private HealthActionService Service(FakeStatements statements)
    {
        IEnumerable<IHealthFindingAction> actions = [new SequentialScanWhatIfAction(statements), Action15()];

        return ActivatorUtilities.CreateInstance<HealthActionService>(
            context.Services,
            Resolver(),
            new HealthActionFlights(),
            actions,
            NullLogger<HealthActionService>.Instance);
    }

    private StudioSourceResolver Resolver() => ActivatorUtilities.CreateInstance<StudioSourceResolver>(context.Services);

    private StudioActionLogEntry[] Audited(string action) =>
        [.. context.ActionLog.GetLatest().Where(x => x.Action == action)];

    private static HealthReportFinding Finding(string code, string? subject)
    {
        HealthFinding finding = FakeHealthService.Finding(code, HealthSeverity.Warning, "A finding", subject: subject);

        return new HealthReportFinding(finding, finding.Evidence, HealthService.MuteKeyOf("default", "default", finding), "https://example.invalid/", true, null);
    }

    private static IndexAdviceView Advised()
    {
        HypotheticalIndex index = PlanWhatIfCandidates.Candidate("public", "orders", ["customer_id"])!;

        return new IndexAdviceView
        {
            StatementsTried = 20,
            StatementsSelected = 20,
            AuditOutcome = "Planned 18 of 20 statements; tried 9 of 9 indexes in 40 plans; 1 would help.",
            Recommended =
            [
                new IndexAdviceItem
                {
                    Index = index,
                    TableKey = "public.orders",
                    Remedy = "CREATE INDEX CONCURRENTLY ON \"public\".\"orders\" (\"customer_id\");",
                    Share = 0.38,
                    Helped = 4,
                    Saved = 4_500,
                    Statements = [new IndexAdviceStatement(1, 10, 500, 50, true)],
                },
            ],
        };
    }

    /// <summary>The Statements service, recording how it was asked and answering with a view a test sets.</summary>
    private sealed class FakeStatements : IStatementsService
    {
        public IndexAdviceView View { get; set; } = new();

        public List<(StudioScope Scope, IndexAdviceRequest Request, bool RecordAudit)> Calls { get; } = [];

        public Task<StatementsView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task<StatementsView> GetLatestAsync(StudioScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task<StatementPlanView> PlanAsync(StudioScope scope, long queryId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task<WhatIfResult> WhatIfAsync(StudioScope scope, long queryId, HypotheticalIndex index, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task<IndexAdviceView> AdviseIndexesAsync(StudioScope scope, IndexAdviceRequest request, CancellationToken cancellationToken = default) =>
            AdviseIndexesAsync(scope, request, recordAudit: true, cancellationToken);

        public Task<IndexAdviceView> AdviseIndexesAsync(StudioScope scope, IndexAdviceRequest request, bool recordAudit, CancellationToken cancellationToken = default)
        {
            Calls.Add((scope, request, recordAudit));
            return Task.FromResult(View);
        }
    }
}
