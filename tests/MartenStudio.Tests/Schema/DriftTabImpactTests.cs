using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Components.Pages.Schema;
using MartenStudio.Internal.Sql;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// 1.2-APPLY: the apply section draws what a previewed migration will lock and rewrite - table by table, with the estimated rows and
/// size, the sessions or the one line saying they are not shown, and the advice - and draws nothing for a script that locks no
/// existing table. Notes, never a refusal: the Apply button's state is the same either way.
/// </summary>
public class DriftTabImpactTests
{
    private const string Sql =
        "CREATE INDEX mt_doc_customer_idx_name ON studio.mt_doc_customer USING btree ((data ->> 'Name'));\n"
        + "alter table studio.mt_doc_order alter column total type numeric;";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static MigrationImpact Impact(bool withSessions)
    {
        IReadOnlyList<TableLockStatement> statements = MigrationRisk.LockStatements(Sql);
        RelationFootprintRow[] footprints =
        [
            new(0, 1, "studio", "mt_doc_customer", false, 1_200_000, 300_000_000, 380_000_000),
        ];

        return MigrationImpact.Assemble(
            statements,
            footprints,
            withSessions ? [new TableSessionRow(1, "idle in transaction", Granted: true, 1)] : null) with
        {
            SessionsNotice = withSessions ? null : "Sessions on these tables are not shown: they need MartenStudioOptions.Capabilities.ViewDatabaseActivity.",
        };
    }

    private static async Task<IRenderedComponent<DriftTab>> RenderAsync(StudioComponentContext context, MigrationPreview preview)
    {
        context.Options.Capabilities = MartenStudioCapabilities.All();
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.Single, "db");
        await context.State.EnsureInitializedAsync(Token);

        IRenderedComponent<DriftTab> tab = context.Render<DriftTab>();
        tab.FindAll("button").First(static x => x.TextContent.Contains("Preview migration", StringComparison.Ordinal)).Click();
        tab.WaitForAssertion(() => tab.Markup.Should().Contain("Migration preview"));

        return tab;
    }

    [Fact]
    public async Task The_apply_section_says_what_each_table_is_locked_with_its_rows_size_and_the_advice()
    {
        MigrationPreview preview = new(Sql, 2, [], "Update", null) { Impact = Impact(withSessions: true) };
        await using StudioComponentContext context = new();
        context.Services.AddSingleton<ISchemaDataService>(new FakeSchemaData(preview));

        IRenderedComponent<DriftTab> tab = await RenderAsync(context, preview);

        string text = tab.Find(".ms-schema-impact").TextContent;
        text.Should().Contain("studio.mt_doc_customer").And.Contain("SHARE").And.Contain("~1,200,000 rows").And.Contain("MiB");
        text.Should().Contain("studio.mt_doc_order", "a table the catalog has no figures for is still listed with what the script does to it")
            .And.Contain("ACCESS EXCLUSIVE").And.Contain("binary-coercible");
        text.Should().Contain("1 session holds a lock (1 idle in transaction)");
        text.Should().Contain("BuildIndexesConcurrentlyOnAlter").And.Contain("lock_timeout");
    }

    [Fact]
    public async Task A_visitor_short_of_the_activity_gate_gets_the_one_line_and_no_sessions_column()
    {
        MigrationPreview preview = new(Sql, 2, [], "Update", null) { Impact = Impact(withSessions: false) };
        await using StudioComponentContext context = new();
        context.Services.AddSingleton<ISchemaDataService>(new FakeSchemaData(preview));

        IRenderedComponent<DriftTab> tab = await RenderAsync(context, preview);

        string text = tab.Find(".ms-schema-impact").TextContent;
        text.Should().Contain("Sessions on these tables are not shown").And.Contain("ViewDatabaseActivity");
        text.Should().NotContain("Sessions now");

        // The sample script changes a column type, so the apply waits for the destructive tick - and for nothing else: the notes
        // add no gate of their own.
        tab.Find(".ms-schema-apply").HasAttribute("disabled").Should().BeTrue("the destructive statement has not been acknowledged");
        tab.Find(".ms-destructive-ack input").Change(true);
        tab.Find(".ms-schema-apply").HasAttribute("disabled").Should().BeFalse("the notes never gate the apply");
    }

    [Fact]
    public async Task A_script_that_locks_no_existing_table_draws_no_impact_block()
    {
        MigrationPreview preview = new("CREATE TABLE studio.mt_doc_new (id uuid not null);", 1, [], "Create", null);
        await using StudioComponentContext context = new();
        context.Services.AddSingleton<ISchemaDataService>(new FakeSchemaData(preview));

        IRenderedComponent<DriftTab> tab = await RenderAsync(context, preview);

        tab.Markup.Should().NotContain("What applying this will lock");
    }

    /// <summary>The schema service, answering the preview with a fixed value.</summary>
    private sealed class FakeSchemaData(MigrationPreview preview) : ISchemaDataService
    {
        public Task<SchemaCheck> CheckAsync(StudioScope scope, CancellationToken cancellationToken = default) => Task.FromResult(SchemaCheck.NotChecked);

        public Task<MigrationPreview> PreviewAsync(StudioScope scope, CancellationToken cancellationToken = default) => Task.FromResult(preview);

        public Task<SchemaApplyResult> ApplyAsync(StudioScope scope, string confirmation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SchemaTables> TablesAsync(StudioScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SchemaIndexes> IndexesAsync(StudioScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SchemaFunctions> FunctionsAsync(StudioScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DdlScript> DdlAsync(StudioScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> DatabaseIdentityAsync(StudioScope scope, CancellationToken cancellationToken = default) => Task.FromResult("db");
    }
}
