using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;
using PostgresStudio.Tests.Components;

using static PostgresStudio.Tests.Statements.StatementsTestFacts;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The drill-in's half of the Statements service (X1), with no database: the page's gate before anything is read or planned,
/// a statement the sweep did not read, a text withheld from the visitor, a text that names a schema the visitor may not see
/// (SEC-FIX), and the plan's own withholding rules - a relation in a withheld schema, a store the visitor may not see - over
/// hand-made plan JSON. The plan itself is the live suite's.
/// </summary>
public class StatementPlanServiceTests
{
    private sealed class CountingFacts(StatementFacts facts) : IStatementFacts
    {
        public int Reads { get; private set; }

        public Task<StatementFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(facts);
        }

        public StatementFacts? Latest(ResolvedSource resolved) => facts;
    }

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

    private static StatementFacts Two() => Facts(Read(
        null,
        Statement(1, "select * from public.orders where id = $1", 10, 100, [Orders]),
        Statement(3, "copy (select 'x') to stdout", 1, 100)));

    private static IEnumerable<StudioActionLogEntry> Plans(PostgresStudioComponentContext context) =>
        context.Services.GetRequiredService<StudioActionLogService>().GetLatest().Where(static x => x.Action == StatementsService.PlanAction);

    [Fact]
    public async Task With_ViewDatabaseActivity_off_nothing_is_read_planned_or_audited()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        CountingFacts facts = new(Two());

        StatementPlanView view = await Service(context, facts).PlanAsync(Scope, 1, Token);

        view.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        view.Reason.Should().Be(PostgresStudioText.Instance.StatementsCapabilityDenial);
        view.QueryId.Should().Be(1);
        facts.Reads.Should().Be(0);
        Plans(context).Should().BeEmpty("a closed gate refuses nothing a person did, as the page's own does");
    }

    [Fact]
    public async Task The_write_policys_refusal_is_the_pages_and_nothing_is_read()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.WriteAuthorizationPolicy = "write";
        context.AuthorizationService.Allow(static resource => resource.Capability is null);
        await context.ReadyAsync();
        CountingFacts facts = new(Two());

        StatementPlanView view = await Service(context, facts).PlanAsync(Scope with { TenantId = "acme" }, 1, Token);

        view.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        view.Reason.Should().Be(PostgresStudioText.Instance.StatementsWritePolicyDenial);
        facts.Reads.Should().Be(0);
        context.AuthorizationService.Calls.Should().Contain(static x => x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity));
    }

    [Fact]
    public async Task A_queryid_the_sweep_did_not_read_has_no_plan_and_says_so()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        StatementPlanView view = await Service(context, new CountingFacts(Two())).PlanAsync(Scope, 777, Token);

        view.Refusal.Should().Be(DatabaseRefusal.None);
        view.Statement.Should().BeNull();
        view.NoPlan.Should().Be(StatementsService.NotAmong);
        Plans(context).Should().BeEmpty("nothing was planned");
    }

    [Fact]
    public async Task A_text_withheld_at_read_gets_no_plan()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        StatementPlanView view = await Service(context, new CountingFacts(Two())).PlanAsync(Scope, 3, Token);

        view.Statement!.Text.Should().BeNull();
        view.Statement.TextNote.Should().Be(StatementReads.WithheldUtility);
        view.NoPlan.Should().Be(ExpensiveStatementsCheck.NotPlannedWithheld);
        view.Plan.Should().BeNull();
        Plans(context).Should().BeEmpty();
    }

    [Fact]
    public async Task A_text_whose_schemas_could_not_be_checked_gets_no_plan()
    {
        // No database browser gate behind the service: the visitor's withheld schemas cannot be read, so every text is withheld,
        // and with it every plan.
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        StatementPlanView view = await Service(context, new CountingFacts(Two())).PlanAsync(Scope, 1, Token);

        view.Statement!.Text.Should().BeNull();
        view.Statement.TextNote.Should().Be(SessionQueryText.SchemasUncheckedNote);
        view.NoPlan.Should().Be(ExpensiveStatementsCheck.NotPlannedWithheld);
        Plans(context).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The plan's own withholding, pure
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> Hr = new(StringComparer.Ordinal) { "hr" };

    private static StatementScreen Screen(IReadOnlyCollection<string>? refusedStoreTables = null) =>
        new(Hr, new DatabaseObjectClassifier([]), false, refusedStoreTables ?? []);

    [Fact]
    public void A_plan_that_reads_a_table_in_a_withheld_schema_is_not_shown()
    {
        const string Json = """
            [{"Plan": {"Node Type": "Hash Join", "Plans": [
                {"Node Type": "Seq Scan", "Schema": "public", "Relation Name": "v_people", "Alias": "p"},
                {"Node Type": "Seq Scan", "Schema": "hr", "Relation Name": "salaries", "Alias": "s"}]}}]
            """;

        StatementsService.WithheldPlan(Json, Screen(), Hr).Should().Be(StatementsService.PlanReadsWithheldSchema);
    }

    [Fact]
    public void A_plan_that_reads_only_what_the_visitor_may_see_is_shown()
    {
        const string Json = """[{"Plan": {"Node Type": "Seq Scan", "Schema": "public", "Relation Name": "orders", "Filter": "(hr_note <> ''::text)"}}]""";

        StatementsService.WithheldPlan(Json, Screen(), Hr).Should().BeNull("a column that merely starts with the schema's name is no table in it");
    }

    [Fact]
    public void A_plan_that_names_a_store_table_the_visitor_may_not_see_is_not_shown()
    {
        const string Json = """[{"Plan": {"Node Type": "Index Scan", "Schema": "public", "Relation Name": "mt_doc_payroll", "Index Name": "mt_doc_payroll_pkey"}}]""";

        StatementsService.WithheldPlan(Json, Screen(["mt_doc_payroll"]), Hr).Should().Contain("Marten store your account may not see");
    }

    [Fact]
    public void A_plan_for_a_visitor_whose_schemas_could_not_be_checked_is_not_shown()
    {
        const string Json = """[{"Plan": {"Node Type": "Result"}}]""";

        StatementsService.WithheldPlan(Json, StatementScreen.Unchecked(false), Hr).Should().StartWith("Not shown.").And.Contain(SessionQueryText.SchemasUncheckedNote);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The text's own withholding, pure (SEC-FIX): a text that names a withheld schema is never planned, because the plan
    // of a view or a SQL function there is made of that schema's definitions over tables the visitor may see.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("select v.note from hr.v_vip v where v.id = $1", "a bare qualifier")]
    [InlineData("select v.note from \"hr\".v_vip v where v.id = $1", "a double-quoted qualifier")]
    [InlineData("select v.note from \"hr\" . \"v_vip\" v", "a double-quoted qualifier with spaces before its dot")]
    [InlineData("select v.note from HR.v_vip v", "a bare qualifier in capitals, which Postgres folds to hr")]
    [InlineData("select v.note from hr/* ‹withheld› */.v_vip v", "a comment between the qualifier and its dot")]
    [InlineData("select v.note from hr\n.v_vip v", "a line break before the dot")]
    [InlineData("select o.note from public.orders o where o.id = $1 and hr.is_vip(o.tier)", "a function's qualifier")]
    [InlineData("select o.id from public.orders o where o.tier operator(hr.===) $1", "an operator's qualifier")]
    [InlineData("select $1::hr.grade", "a type's qualifier")]
    [InlineData("select v.note from app.hr.v_vip v", "a qualifier behind the database's name")]
    [InlineData("select v.note from hr.\"V vip\" v", "a qualifier in front of a quoted name")]
    public void A_text_that_names_a_withheld_schema_as_a_qualifier_is_not_planned(string text, string because)
    {
        StatementsService.TextNamesWithheld(text, Hr).Should().Be(StatementsService.NamesWithheldSchema, because);
    }

    [Fact]
    public void A_quoted_qualifier_naming_a_withheld_schema_with_capitals_or_a_space_is_not_planned()
    {
        HashSet<string> withheld = new(StringComparer.Ordinal) { "HR Data", "Payroll" };

        StatementsService.TextNamesWithheld("select s.amount from \"HR Data\".salaries s", withheld).Should().Be(StatementsService.NamesWithheldSchema);
        StatementsService.TextNamesWithheld("select s.amount from \"Payroll\".\"Run\" s", withheld).Should().Be(StatementsService.NamesWithheldSchema);
        StatementsService.TextNamesWithheld("select s.amount from public.salaries s", withheld).Should().BeNull();
    }

    /// <summary>
    /// Decided: a schema named only inside a string literal is refused too. The visitor's text shows <c>‹withheld›</c> there, and
    /// a literal can be a reference - <c>'hr.v_vip'::regclass</c>, <c>to_regclass</c>, <c>nextval</c> - which only Postgres'
    /// parse tells apart from a literal that is not.
    /// </summary>
    [Theory]
    [InlineData("select pg_catalog.to_regclass('hr.v_vip')")]
    [InlineData("select pg_catalog.nextval('hr.order_seq')")]
    [InlineData("select o.note from public.orders o where o.region = 'hr.north'")]
    public void A_withheld_schema_named_only_inside_a_string_literal_is_not_planned_either(string text)
    {
        StatementsService.TextNamesWithheld(text, Hr).Should().Be(StatementsService.NamesWithheldSchema);
    }

    /// <summary>
    /// A comment never reaches the predicate - the statement sweep masks every comment when it reads a text - and one that did,
    /// naming a withheld schema, would be refused too; the comment mask itself is not.
    /// </summary>
    [Fact]
    public void A_comment_that_names_a_withheld_schema_is_not_planned_and_the_reads_own_comment_mask_is()
    {
        StatementsService.TextNamesWithheld("/* reads hr.v_vip */ select 1", Hr).Should().Be(StatementsService.NamesWithheldSchema);
        StatementsService.TextNamesWithheld("select 1 -- hr\n", Hr).Should().Be(StatementsService.NamesWithheldSchema);

        StatementsService.TextNamesWithheld(
            StatementReads.CommentMask + " select o.note from public.orders o where o.id = $1", Hr).Should().BeNull(
            "the read's own mask names no schema");
    }

    /// <summary>The text's mask finds every whole word that spells the schema, and a text it changes is not planned.</summary>
    [Fact]
    public void A_word_that_spells_a_withheld_schema_is_masked_in_the_text_so_the_text_is_not_planned()
    {
        StatementsService.TextNamesWithheld("select o.hr from public.orders o where o.id = $1", Hr).Should().Be(StatementsService.NamesWithheldSchema);
    }

    [Theory]
    [InlineData("select o.note from public.orders o where o.id = $1")]
    [InlineData("select o.hr_note from public.orders o where o.thr = $1", "a name that merely starts or ends with the schema's")]
    [InlineData("select o.note from public.\"hr-archive\" o")]
    public void A_text_that_names_only_what_the_visitor_may_see_is_planned(string text, string because = "")
    {
        StatementsService.TextNamesWithheld(text, Hr).Should().BeNull(because);
    }

    [Fact]
    public void With_nothing_withheld_every_text_may_be_planned()
    {
        HashSet<string> none = new(StringComparer.Ordinal);

        StatementsService.TextNamesWithheld("select v.note from hr.v_vip v", none).Should().BeNull();
        StatementsService.TextNamesWithheld("select v.note from U&\"\\0068\\0072\".v_vip v", none).Should().BeNull();
    }

    /// <summary>Postgres decodes <c>U&amp;"\0068\0072"</c> to <c>hr</c>, and the mask does not: while anything is withheld, it is not planned.</summary>
    [Theory]
    [InlineData("select v.note from U&\"\\0068\\0072\".v_vip v")]
    [InlineData("select v.note from u&\"h\\0072\".v_vip v")]
    [InlineData("select v.note from U&\"!0068!0072\" UESCAPE '!'.v_vip v")]
    public void A_name_in_unicode_escapes_is_not_planned_while_a_schema_is_withheld(string text)
    {
        StatementsService.TextNamesWithheld(text, Hr).Should().Be(StatementsService.NamesEscapedIdentifier);
    }

    /// <summary>The reader - the one place that prepares - refuses it again before it opens a connection.</summary>
    [Fact]
    public async Task The_reader_refuses_a_text_that_names_a_withheld_schema_before_it_opens_a_connection()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        StatementPlanReader reader = new(context.Services.GetRequiredService<IStudioSettings>(), NullLogger<StatementPlanReader>.Instance);
        NoConnection database = new();

        StatementPlanRead read = await reader.ReadAsync(database, 1, "select v.note from hr.v_vip v where v.id = $1", truncated: false, Hr, Token);

        read.Json.Should().BeNull();
        read.NotPlanned.Should().Be(StatementsService.NamesWithheldSchema);
        read.Statements.Should().BeEmpty("nothing ran");

        HypotheticalIndex index = new()
        {
            Key = HypotheticalIndexCandidates.Key("public", "orders", ["id"]),
            Definition = "CREATE INDEX ON \"public\".\"orders\" (\"id\");",
            Caption = "an index on public.orders (id)",
            Schema = "public",
            Table = "orders",
            Columns = ["id"],
        };
        (HypotheticalIndexRead? hypothetical, string? notPlanned) = await reader.WhatIfAsync(
            database, 1, "select v.note from hr.v_vip v where v.id = $1", truncated: false, Hr, index, static _ => null, Token);

        hypothetical.Should().BeNull();
        notPlanned.Should().Be(StatementsService.NamesWithheldSchema);
        database.Opened.Should().Be(0, "nothing was prepared, so nothing was connected to");
    }

    /// <summary>A database no test may connect to.</summary>
    private sealed class NoConnection : IStudioDatabase
    {
        public int Opened { get; private set; }

        public string Identity => "default";

        public string Name => "app";

        public string Server => "nowhere";

        public Npgsql.NpgsqlDataSource? DataSource => null;

        public Npgsql.NpgsqlConnection CreateConnection(StudioConnectionUsage usage)
        {
            Opened++;
            throw new InvalidOperationException("No connection may be opened here.");
        }
    }

    [Fact]
    public void The_plans_search_path_drops_nothing_it_may_not_quote_and_is_never_empty()
    {
        StatementPlanQueries.SearchPath(["public", "app data"]).Should().Be("\"public\", \"app data\"");
        StatementPlanQueries.SearchPath([]).Should().Be("pg_catalog");
        StatementPlanQueries.SearchPath(["bad\"name", " "]).Should().Be("pg_catalog");
    }

    [Fact]
    public void The_plan_batch_is_t02s_shape_verbose_and_refuses_what_t02_refuses()
    {
        string batch = StatementPlanQueries.GenericPlan("select * from public.orders where id = $1 and note = $2", truncated: false)!;

        batch.Should().StartWith("prepare \"marten_studio_plan\" as select * from public.orders where id = $1 and note = $2\n;\n")
            .And.Contain("set local plan_cache_mode = force_generic_plan;\n")
            .And.Contain("explain (verbose, format json) execute \"marten_studio_plan\"(null, null);\n")
            .And.EndWith("deallocate \"marten_studio_plan\"");

        StatementPlanQueries.GenericPlan("update public.orders set note = $1", truncated: false).Should().BeNull("planning a write takes its lock");
        StatementPlanQueries.GenericPlan("select * from public.orders for update", truncated: false).Should().BeNull();
        StatementPlanQueries.GenericPlan("select 1", truncated: true).Should().BeNull("a cut text is not the statement");
        StatementPlanQueries.Shown(42, 1).Should().Contain("<the text pg_stat_statements recorded for queryid 42, shown masked above>")
            .And.NotContain("public.orders");
    }

    [Theory]
    [InlineData("42501", "may not read a table it names")]
    [InlineData("42P01", "does not resolve on the search_path the studio plans with")]
    [InlineData("55P03", "somebody holds a lock")]
    [InlineData("57014", "statement_timeout")]
    [InlineData("XX000", "(SQLSTATE XX000).")]
    public void A_plan_postgres_refused_is_the_studios_sentence_and_its_sqlstate(string sqlState, string says)
    {
        StatementPlanReader.NotPlanned(sqlState).Should().StartWith("Not planned: Postgres refused the plan (SQLSTATE " + sqlState + ")").And.Contain(says);
    }
}
