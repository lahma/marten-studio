using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Statements;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Integration.Tests.Diagnostics;

/// <summary>
/// The diagnostics API's statements section against a real <c>pg_stat_statements</c> (API design §5.4; W5-API-fix): on a server
/// of its own with the library preloaded, the section is read with <c>IncludeActivity</c> - a known statement listed, a
/// literal-bearing utility text withheld, a comment masked - and without it is listed under <c>Withheld</c> and names nothing.
/// </summary>
public class DiagnosticsStatementsLiveTests(StatementsPageServer server) : IClassFixture<StatementsPageServer>
{
    private const string Slow = "select pg_catalog.pg_sleep(0.02), o.note from public.orders o where o.id = @id";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_statements_section_is_read_with_IncludeActivity_withholds_literals_masks_comments_and_is_withheld_without_it()
    {
        string database = await server.CreateDatabaseAsync("dx_api_statements");
        string app = server.ConnectionString(database, StatementsPageServer.AppRole);
        await StatementsLive.SeedAsync(app);
        await StatementsLive.RepeatAsync(app, Slow, 25);

        // A COPY keeps its constants as written on PostgreSQL 17.
        await using (NpgsqlConnection connection = new(app))
        {
            await connection.OpenAsync(Token);
            using TextReader export = await connection.BeginTextExportAsync("copy (select 'alice@example.com' as who from public.orders) to stdout", Token);
            await export.ReadToEndAsync(Token);
        }

        await StatementsLive.RepeatAsync(app, "/* user='alice@example.com' */ select count(*) from public.orders where id > @id", 3);

        await using StudioHost host = StatementsLive.Host(app);
        IPostgresDiagnostics diagnostics = host.Services.GetRequiredService<IPostgresDiagnostics>();
        PostgresDatabaseRef target = new("default", "");

        PostgresDiagnosticsSnapshot without = await diagnostics.GetSnapshotAsync(
            target,
            new PostgresDiagnosticsRequest { Findings = false, Statements = true, RunDeeperChecks = true },
            Token);

        without.Statements.Should().BeNull("statement texts are Activity-grade");
        without.Withheld.Should().Contain("statements", HostGate.WithheldSection);
        without.ToJson().Should().NotContain("pg_sleep").And.NotContain("alice");

        PostgresDiagnosticsSnapshot with = await diagnostics.GetSnapshotAsync(
            target,
            new PostgresDiagnosticsRequest { Findings = false, Statements = true, IncludeActivity = true, RunDeeperChecks = true },
            Token);

        PostgresStatementsSection statements = with.Statements!;
        statements.Outcome.Should().Be(PostgresReadOutcome.Read, statements.Reason);
        with.Withheld.Should().NotContainKey("statements");
        statements.TotalMilliseconds.Should().BePositive();

        PostgresStatement slow = statements.Top.Should().Contain(static x => x.Text != null && x.Text.Contains("pg_sleep", StringComparison.Ordinal)).Subject;
        slow.Calls.Should().Be(25);
        slow.Text.Should().Contain("$1", "the text pg_stat_statements recorded, constants replaced");

        // The COPY, and the seed's own utility statements beside it: none is a plain query or write, so none has a text.
        PostgresStatement[] utilities = [.. statements.Top.Where(static x => x.TextWithheld == StatementReads.WithheldUtility)];
        utilities.Should().NotBeEmpty("the COPY is among them");
        utilities.Should().OnlyContain(static x => x.Text == null, "a COPY keeps its constants as written, so its text is withheld whoever asks");

        PostgresStatement commented = statements.Top.Should()
            .ContainSingle(static x => x.Text != null && x.Text.Contains("count(*) from public.orders where id >", StringComparison.Ordinal)).Subject;
        commented.Text.Should().StartWith(StatementReads.CommentMask).And.NotContain("alice");

        with.ToJson().Should().NotContain("alice", "neither the literal nor the comment reaches the JSON");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }
}
