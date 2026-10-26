using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Query;

/// <summary>
/// Every statement the SQL console offers as an example runs, through the real console, on a real database - the half
/// <c>ConsoleExamplesTests</c> in the fast suite cannot show (the PS-3a review): that each one Postgres accepts in the
/// console's read-only transaction, not only that the console's guard lets it through.
/// </summary>
public class ConsoleExamplesLiveTests(PostgresFixture postgres)
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> Titles() => [.. ConsoleExamples.All.Select(static x => x.Title)];

    [PostgresTheory]
    [MemberData(nameof(Titles))]
    public async Task Every_example_runs_in_the_console_and_returns_rows(string title)
    {
        string snippet = ConsoleExamples.All.Single(x => x.Title == title).Snippet;

        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_console_" + ConsoleExamples.All.ToList().FindIndex(x => x.Title == title)], Token);
        await OwnedDatabases.ExecuteAsync(owned[0], "create table public.orders (id int primary key); insert into public.orders select g from generate_series(1, 50) g; analyze public.orders", Token);

        await using StudioHost host = StudioHost.Create(
            services => services.AddSingleton(_ => NpgsqlDataSource.Create(owned[0])),
            static o => o.Capabilities.RunSql = true);
        await using StudioCircuit circuit = host.Circuit();

        SqlConsoleResult result = await circuit.Get<ISqlConsoleService>().RunSqlAsync(Scope, new SqlConsoleRequest(snippet), Token);

        result.Rejection.Should().BeNull("the console's guard sends it: " + result.Rejection);
        result.Error.Should().BeNull("Postgres runs it: " + result.Error);
        result.Succeeded.Should().BeTrue();
        result.Columns.Should().NotBeEmpty();
        result.Rows.Should().NotBeEmpty("{0} reads the catalog of a database that has a schema and a table in it", title);
    }
}
