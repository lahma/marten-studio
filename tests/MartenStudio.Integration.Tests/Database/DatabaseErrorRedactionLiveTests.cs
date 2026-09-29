using System.Text.Json;

using MartenStudio.Integration.Tests.Logging;
using MartenStudio.Services;
using MartenStudio.Services.Database;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// SEC-fix F1 and F7 against a real Postgres 17: what Postgres says about a failed row read names a withheld
/// schema only masked, and a visitor whose browser gate is closed is refused - audited, and logged as a
/// Warning - only for a specific object they asked for, never for using a page.
/// </summary>
/// <remarks>
/// The fixture's withheld schema (<see cref="DatabaseBrowserFixture.HrSchema" />) owns an enum, and two tables
/// in the browsable legacy schema have a column of it - the reviewer's probe: filtering such a column on a value
/// the enum does not have answered <c>invalid input value for enum hr.grade: "nope"</c> under "What Postgres
/// said", beside a Columns tab that printed <c>‹withheld›.grade</c>.
/// </remarks>
public class DatabaseErrorRedactionLiveTests(DatabaseErrorRedactionLiveTests.Fixture fixture)
    : IClassFixture<DatabaseErrorRedactionLiveTests.Fixture>
{
    private const string Mask = WithheldNames.Token;

    /// <summary>This class's schemas, and two legacy tables whose column is a withheld schema's enum.</summary>
    public sealed class Fixture(PostgresFixture postgres) : DatabaseBrowserFixture(postgres)
    {
        /// <inheritdoc />
        protected override string ExtraSql =>
            """
            create table {l}.emp (id int primary key, grade {hr}.grade not null);
            insert into {l}.emp values (1, 'a'), (2, 'b');

            create table {l}.graded (grade {hr}.grade primary key, label text);
            insert into {l}.graded values ('a', 'first');
            """;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // F1 - Postgres' own words
    // ---------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_filter_on_a_withheld_enum_says_what_Postgres_said_with_the_schema_masked()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectDetail emp = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.LegacySchema, "emp", Token));
        emp.Columns.Single(static x => x.Name == "grade").Type.Should().Be(Mask + ".grade", "the premise: the Columns tab masks it");

        TableRowPage page = await RowsAsync(host, x => x.ListRowsAsync(
            BrowserHost.Scope, fixture.LegacySchema, "emp", new TableRowRequest { Filter = "grade = nope" }, Token));

        page.State.Should().Be(TableRowPageState.Failed, page.Reason);
        page.Error!.SqlState.Should().Be("22P02");
        page.Error.PostgresMessage.Should().Be("invalid input value for enum " + Mask + ".grade: \"nope\"");
        JsonSerializer.Serialize(page).Should().NotContain(fixture.HrSchema, "nothing on the page names the withheld schema");

        TableRowCount count = await RowsAsync(host, x => x.CountExactAsync(
            BrowserHost.Scope, fixture.LegacySchema, "emp", "grade = nope", Token));

        count.Error!.PostgresMessage.Should().Contain(Mask + ".grade").And.NotContain(fixture.HrSchema);
    }

    [PostgresFact]
    public async Task A_row_key_of_a_withheld_enum_says_what_Postgres_said_with_the_schema_masked()
    {
        await using BrowserHost host = fixture.Host();

        TableRowDetail row = await RowsAsync(host, x => x.GetRowAsync(
            BrowserHost.Scope, fixture.LegacySchema, "graded", new Dictionary<string, string> { ["grade"] = "nope" }, Token));

        row.Error!.SqlState.Should().Be("22P02");
        row.Error.PostgresMessage.Should().Contain(Mask + ".grade").And.NotContain(fixture.HrSchema);
    }

    /// <summary>
    /// What the visitor typed comes back as typed. Masked, the echo would answer "is this a schema you are
    /// hiding?" - so the one place a withheld name may appear is inside the quotes of the visitor's own value.
    /// </summary>
    [PostgresFact]
    public async Task A_value_the_visitor_typed_is_echoed_as_typed()
    {
        await using BrowserHost host = fixture.Host();
        string typed = fixture.HrSchema + ".x";

        TableRowPage page = await RowsAsync(host, x => x.ListRowsAsync(
            BrowserHost.Scope,
            fixture.LegacySchema,
            "emp",
            new TableRowRequest { Filter = RowFilterGrammar.Format("id", RowFilterOperator.Equal, typed) },
            Token));

        page.Error!.PostgresMessage.Should().Be("invalid input syntax for type integer: \"" + typed + "\"");
    }

    // ---------------------------------------------------------------------------------------------
    // F7 - a closed gate is refused for what it asked, never for using a page
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A <c>viewer</c> - the store policy says yes, the write policy <c>BrowseDatabase</c> asks says no - makes
    /// every read the browser page and a store-schema table's page make to learn what they may show, and
    /// nothing is refused; then a link to a table in a browsable schema the gate keeps shut is, exactly once.
    /// </summary>
    [PostgresFact]
    public async Task A_closed_gate_is_refused_once_for_a_withheld_object_and_never_for_page_use()
    {
        var capture = new LogCapture();

        await using BrowserHost host = fixture.Host(
            static options =>
            {
                options.StoreAuthorizationPolicy = "store";
                options.WriteAuthorizationPolicy = "write";
            },
            new ResourcePolicy(static resource => resource.Capability is null),
            services: services => services.AddSingleton<ILoggerProvider>(capture));

        // The browser page, and the browser at the store's own schema.
        (await host.ObjectsAsync(x => x.GetOverviewAsync(BrowserHost.Scope, Token))).Refusal.Should().Be(DatabaseRefusal.None);
        await host.ObjectsAsync(x => x.ListAsync(BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables), Token));
        await host.ObjectsAsync(x => x.ListAsync(BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.DocumentSchema), Token));
        await host.ObjectsAsync(x => x.ListAsync(BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Views, fixture.DocumentSchema), Token));
        await host.ObjectsAsync(x => x.ListAsync(BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Functions), Token));

        // A store-schema table's page.
        DatabaseObjectDetail settings = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.DocumentSchema, "host_settings", Token));
        settings.Found.Should().BeTrue(settings.Reason);
        settings.Relation!.Rows.Allowed.Should().BeFalse("the premise: the gate is closed to this visitor");

        Refusals(capture).Should().BeEmpty("using a page whose gate is closed refuses nothing");
        host.Ring.GetLatest().Should().BeEmpty();

        // A pasted link to a table the gate keeps shut: one refusal.
        DatabaseObjectDetail triggers = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.QuartzSchema, "qrtz_triggers", Token));

        triggers.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        Refusals(capture).Should().ContainSingle().Which.EventId.Id.Should().Be(9203);

        StudioActionLogEntry entry = host.Ring.GetLatest().Should().ContainSingle().Which;
        entry.Target.Should().Be(fixture.QuartzSchema + ".qrtz_triggers");
        entry.Capability.Should().Be(nameof(StudioCapability.BrowseDatabase), "a browser refusal is shown only to a visitor who may browse (F2)");
    }

    private static IReadOnlyList<StudioLogLine> Refusals(LogCapture capture) =>
        [.. capture.Lines.Where(static x => x.EventId.Id is 9202 or 9203)];

    private static async Task<T> RowsAsync<T>(BrowserHost host, Func<ITableRowService, Task<T>> call)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<ITableRowService>());
    }
}
