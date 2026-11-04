using System.Security.Claims;

using Marten;

using MartenStudio.Integration.Tests.Database;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Integration.Tests.Query;

/// <summary>
/// 1.2-VEC against a real pgvector: the SQL console - the service the Marten Query page's SQL tab and PostgresStudio's Query
/// page both run through - shows <c>vector</c>, <c>halfvec</c> and <c>sparsevec</c> columns as the text Postgres prints, knows
/// they are pgvector's from the extension that owns their type, and leaves every other column as it was.
/// </summary>
/// <remarks>
/// The host in these tests never calls <c>UseVector()</c> (the <c>Pgvector</c> package is outside the budget), which is the
/// point: Npgsql has no handler for the three types, and the console used to answer "(cannot read vector)". The image is
/// <see cref="PgVectorServerFixture.Image" />, the one <see cref="PgVectorLiveTests" /> runs on.
/// </remarks>
public class PgVectorConsoleLiveTests(PgVectorServerFixture server) : IClassFixture<PgVectorServerFixture>
{
    private const string Embeddings = PgVectorServerFixture.DocumentSchema + "." + PgVectorServerFixture.Table;

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_select_of_pgvector_columns_comes_back_as_text_marked_as_pgvector_columns()
    {
        SkipIfUnavailable();

        await using Host host = Host.Build(server);
        SqlConsoleResult result = await host.RunAsync(
            $"select embedding, summary, sparse, unsized, typed, wide from {Embeddings} order by id limit 1");

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);
        result.Columns.Select(static x => x.Vector?.TypeName).Should().Equal("vector", "halfvec", "sparsevec", "vector", "vector", "vector");
        result.Columns.Select(static x => x.Vector?.Dimensions).Should().OnlyContain(static x => x == null,
            "a result column carries a type, not the declaration of a table's column");

        IReadOnlyList<SqlCell> row = result.Rows.Single();
        row.Select(static x => x.Text).Take(5).Should().Equal("[1,2,3]", "[1,2,3]", "{1:1.5,3:2}/5", "[1,2]", "[0.5,0.25,0.125]");
        row.Should().OnlyContain(static x => x.Kind == SqlCellKind.Text);
        row.Select(static x => x.Text).Should().NotContain(static x => x.Contains("cannot read", StringComparison.Ordinal));

        SqlCell wide = row[5];
        wide.IsTruncated.Should().BeFalse("a vector gets a cap of its own, not the kilobyte of an ordinary cell");
        wide.Text.Length.Should().BeGreaterThan(1024);
        PgVector.Preview(wide.Text, wide.IsTruncated)!.Total.Should().Be(400, "the whole value was read");

        PgVector.Preview(row[2].Text, false)!.Text.Should().Be("{1:1.5, 3:2}/5");
    }

    [PostgresFact]
    public async Task A_mixed_statement_keeps_its_typed_columns_typed()
    {
        SkipIfUnavailable();

        await using Host host = Host.Build(server);
        SqlConsoleResult result = await host.RunAsync(
            $"select id, embedding, id > 1 as later, 'x'::text as label, timestamptz '2026-01-02 03:04:05+00' as at, '{{\"a\":1}}'::jsonb as doc from {Embeddings} order by id limit 1");

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);
        result.Columns.Select(static x => x.Vector is not null).Should().Equal(false, true, false, false, false, false);

        IReadOnlyList<SqlCell> row = result.Rows.Single();
        row[0].Should().Be(new SqlCell("1", SqlCellKind.Number, false, 1));
        row[1].Text.Should().Be("[1,2,3]");
        row[2].Kind.Should().Be(SqlCellKind.Boolean);
        row[3].Kind.Should().Be(SqlCellKind.Text);
        row[4].Kind.Should().Be(SqlCellKind.Timestamp);
        row[5].Kind.Should().Be(SqlCellKind.Json);
    }

    [PostgresFact]
    public async Task A_null_vector_is_a_null_cell_and_an_aggregate_over_vectors_is_a_plain_number()
    {
        SkipIfUnavailable();

        await using Host host = Host.Build(server);

        SqlConsoleResult nulls = await host.RunAsync($"select summary, sparse from {Embeddings} where id = 2");
        nulls.Succeeded.Should().BeTrue(nulls.Error?.MessageText);
        nulls.Rows.Single()[0].Text.Should().Be("[0.5,-0.25,4]");

        SqlConsoleResult missing = await host.RunAsync($"select unsized from {Embeddings} where id = 2");
        missing.Rows.Single()[0].Kind.Should().Be(SqlCellKind.Null);
        missing.Columns[0].Vector.Should().NotBeNull("the column is a vector whether or not this row has a value");

        SqlConsoleResult distance = await host.RunAsync($"select embedding <-> '[1,2,3]' as d, vector_dims(embedding) as dims from {Embeddings} order by id");
        distance.Succeeded.Should().BeTrue(distance.Error?.MessageText);
        distance.Columns.Select(static x => x.Vector).Should().OnlyContain(static x => x == null, "a distance and a dimension count are not vectors");
        distance.Columns[0].PostgresType.Should().Be("double precision");
        distance.Rows[0][0].Kind.Should().Be(SqlCellKind.Number);
        distance.Rows[0][1].Text.Should().Be("3");
    }

    [PostgresFact]
    public async Task An_unknown_type_that_is_not_pgvector_s_arrives_as_text_and_a_look_alike_is_not_a_vector()
    {
        SkipIfUnavailable();

        await using NpgsqlConnection setup = await server.OpenAsync();
        await using (var command = new NpgsqlCommand(
                         """
                         create extension if not exists seg;
                         drop schema if exists vec_console cascade;
                         create schema vec_console;
                         create type vec_console.vector as enum ('up', 'down');
                         create table vec_console.lookalike (id int primary key, v vec_console.vector);
                         insert into vec_console.lookalike values (1, 'up');
                         """,
                         setup))
        {
            await command.ExecuteNonQueryAsync(Token);
        }

        await using Host host = Host.Build(server);

        // seg, not cube: no Npgsql version has a handler for it (Npgsql 10 reads cube).
        SqlConsoleResult seg = await host.RunAsync("select '1 .. 2'::seg as c, 7 as n");
        seg.Succeeded.Should().BeTrue(seg.Error?.MessageText);
        seg.Columns[0].Vector.Should().BeNull("seg is not pgvector's");
        seg.Rows.Single()[0].Text.Should().Be("1 .. 2", "the text Postgres prints, not \"(cannot read seg)\"");
        seg.Rows.Single()[1].Kind.Should().Be(SqlCellKind.Number);

        SqlConsoleResult lookalike = await host.RunAsync("select v, id from vec_console.lookalike");
        lookalike.Succeeded.Should().BeTrue(lookalike.Error?.MessageText);
        lookalike.Columns[0].PostgresType.Should().Contain("vector");
        lookalike.Columns[0].Vector.Should().BeNull("a type named vector that the vector extension does not own is not pgvector's");
        lookalike.Rows.Single()[0].Text.Should().Be("up");
    }

    /// <summary>
    /// The describe is Parse and Describe only: a function that raises a notice every time it runs raises it once for a
    /// statement the console describes and then runs.
    /// </summary>
    [PostgresFact]
    public async Task The_describe_executes_nothing()
    {
        SkipIfUnavailable();

        await using NpgsqlConnection setup = await server.OpenAsync();
        await using (var command = new NpgsqlCommand(
                         """
                         create schema if not exists vec_console;
                         create or replace function vec_console.beat() returns int language plpgsql volatile as
                         $$ begin raise notice 'beat'; return 1; end $$;
                         """,
                         setup))
        {
            await command.ExecuteNonQueryAsync(Token);
        }

        await using Host host = Host.Build(server);
        SqlConsoleResult result = await host.RunAsync($"select vec_console.beat() as beat, embedding from {Embeddings} where id = 1");

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);
        result.Columns[1].Vector.Should().NotBeNull("the describe ran, and found a vector");
        result.Notices.Count(static x => x.Contains("beat", StringComparison.Ordinal)).Should().Be(1, "the statement ran once");
    }

    [PostgresFact]
    public async Task A_statement_Postgres_rejects_while_describing_comes_back_as_its_own_error()
    {
        SkipIfUnavailable();

        await using Host host = Host.Build(server);

        SqlConsoleResult missing = await host.RunAsync("select embedding from pgv_docs.no_such_table");

        missing.Succeeded.Should().BeFalse();
        missing.Error!.SqlState.Should().Be("42P01", "the describe's error is the statement's own, not 25P02 (the transaction was aborted)");
        missing.Error.Position.Should().BeGreaterThan(0);

        SqlConsoleResult syntax = await host.RunAsync("select from from");
        syntax.Error!.SqlState.Should().Be("42601");

        (await host.RunAsync("select 1")).Succeeded.Should().BeTrue("the next run is on a transaction of its own");
    }

    [PostgresFact]
    public async Task The_guard_and_the_read_only_transaction_are_unchanged_for_a_statement_over_vectors()
    {
        SkipIfUnavailable();

        await using Host host = Host.Build(server);

        SqlConsoleResult guarded = await host.RunAsync($"delete from {Embeddings}");
        guarded.Succeeded.Should().BeFalse();
        guarded.Rejection.Should().NotBeNull("the parser still refuses it before anything is sent");

        // Past the parser, straight to the session: Postgres refuses the write, as before.
        await using NpgsqlConnection connection = await server.OpenAsync();
        SqlResultSet refused = await new ReadOnlySqlSession().ExecuteAsync(connection, $"with x as (delete from {Embeddings} returning embedding) select embedding from x");

        refused.Succeeded.Should().BeFalse();
        refused.Error!.SqlState.Should().Be("25006");

        (await host.RunAsync($"select count(*) from {Embeddings}")).Rows.Single()[0].Text.Should().Be("2");
    }

    [PostgresFact]
    public async Task A_vector_past_its_cap_is_cut_and_says_so()
    {
        SkipIfUnavailable();

        await using NpgsqlConnection connection = await server.OpenAsync();
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions { Formatting = new SqlValueFormatterOptions { MaxVectorLength = 40 } });

        SqlResultSet result = await session.ExecuteAsync(connection, $"select embedding, wide from {Embeddings} where id = 1");

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);
        result.Rows.Single()[0].IsTruncated.Should().BeFalse("seven characters are under the cap");
        SqlCell wide = result.Rows.Single()[1];
        wide.IsTruncated.Should().BeTrue();
        wide.Text.Should().EndWith("…");
        wide.FullLength.Should().BeGreaterThan(1024);
        PgVector.Preview(wide.Text, wide.IsTruncated)!.Total.Should().BeNull("a cut value is never counted");
    }

    /// <summary>
    /// Postgres raises some notices when it parses a statement - an identifier it will truncate - and again when it runs it.
    /// The describe's are dropped just before the statement executes, so each is listed once.
    /// </summary>
    [PostgresFact]
    public async Task A_notice_raised_at_parse_time_is_listed_once_and_a_failed_describe_keeps_its_own()
    {
        SkipIfUnavailable();

        await using Host host = Host.Build(server);
        string longName = new('a', 70);

        SqlConsoleResult result = await host.RunAsync($"select 1 as {longName}");

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);
        result.Notices.Should().ContainSingle(static x => x.Contains("will be truncated", StringComparison.Ordinal));

        SqlConsoleResult failed = await host.RunAsync($"select 1 as {longName} from pgv_docs.no_such_table");

        failed.Succeeded.Should().BeFalse();
        failed.Notices.Should().ContainSingle(static x => x.Contains("will be truncated", StringComparison.Ordinal),
            "the describe failed, nothing ran after it, and what it raised is all there is");
    }

    /// <summary>
    /// 1.2-VEC-FIX: Npgsql keys an auto-prepared statement by its SQL text alone and fixes its result formats into it, so on a
    /// connection that prepares statements the console asks for no text at all (exactly the 1.1 behaviour): the vector says
    /// how to see it, a type Npgsql cannot read says so, nothing throws, and every run is the same.
    /// </summary>
    [PostgresFact]
    public async Task On_a_connection_that_prepares_statements_the_console_asks_for_no_text_and_every_run_is_the_same()
    {
        SkipIfUnavailable();

        await using NpgsqlConnection setup = await server.OpenAsync();
        await using (var command = new NpgsqlCommand("create extension if not exists seg", setup))
        {
            await command.ExecuteNonQueryAsync(Token);
        }

        await using Host host = Host.Build(server, ";Max Auto Prepare=50;Auto Prepare Min Usages=2;Maximum Pool Size=1");

        // The vector has a binary output function and no Npgsql handler: "(cannot read ...)", as before 1.2. seg has no binary
        // output function, so Postgres refuses to send it in binary: an error, as before 1.2. Either way the same every run.
        foreach (string statement in new[]
                 {
                     $"select id, embedding from {Embeddings} order by id",
                     "select '1 .. 2'::seg as c, 7 as n",
                 })
        {
            string first = string.Empty;

            for (int run = 0; run < 4; run++)
            {
                SqlConsoleResult result = await host.RunAsync(statement);

                result.Columns.Should().OnlyContain(static x => x.Vector == null, "the console asked for no text, so it knows no vector");

                string outcome = result.Succeeded
                    ? string.Join(" | ", result.Rows.SelectMany(static x => x).Select(static x => x.Text))
                    : "error " + result.Error!.SqlState + " " + result.Error.MessageText;

                if (run == 0)
                {
                    first = outcome;
                }
                else
                {
                    outcome.Should().Be(first, "run " + run + " of " + statement);
                }
            }

            if (statement.Contains("embedding", StringComparison.Ordinal))
            {
                first.Should().Contain("(cannot read").And.Contain("::text");
            }
            else
            {
                first.Should().StartWith("error ").And.Contain("seg");
            }
        }
    }

    [PostgresFact]
    public async Task Many_vector_columns_share_a_budget_and_the_cells_past_it_are_cut_to_the_kilobyte()
    {
        SkipIfUnavailable();

        await using NpgsqlConnection connection = await server.OpenAsync();
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions
        {
            Formatting = new SqlValueFormatterOptions { MaxVectorLength = 100_000, MaxVectorResultLength = 3_000 },
        });

        SqlResultSet result = await session.ExecuteAsync(
            connection, $"select wide, wide as w2, wide as w3, embedding from {Embeddings} where id = 1");

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);
        IReadOnlyList<SqlCell> row = result.Rows.Single();

        row[0].IsTruncated.Should().BeFalse("the first fits whole");
        row[1].IsTruncated.Should().BeTrue("the second would pass the budget");
        row[1].Text.Length.Should().Be(1025);
        row[2].IsTruncated.Should().BeTrue();
        row[3].Text.Should().Be("[1,2,3]", "a value that is under the kilobyte is whole whatever the budget");
    }

    [PostgresFact]
    public async Task The_export_carries_the_whole_text_of_a_vector()
    {
        SkipIfUnavailable();

        await using Host host = Host.Build(server);
        SqlConsoleResult result = await host.RunAsync($"select id, embedding, wide from {Embeddings} where id = 1");

        string csv = QueryExport.ToCsv(result.Columns, result.Rows);
        string json = QueryExport.ToJson(result.Columns, result.Rows);

        csv.Should().Contain("\"[1,2,3]\"").And.Contain("0.4]");
        json.Should().Contain("\"[1,2,3]\"").And.Contain(",0.4]");
        json.Should().NotContain("…");
    }

    private void SkipIfUnavailable() =>
        Assert.SkipWhen(server.Unavailable is not null, server.Unavailable ?? string.Empty);

    /// <summary>A Marten host on the pgvector server with the SQL console on, and no <c>UseVector()</c>.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private Host(ServiceProvider provider)
        {
            this.provider = provider;
            scope = provider.CreateAsyncScope();
        }

        public static Host Build(PgVectorServerFixture server, string connectionSuffix = "")
        {
            var services = new ServiceCollection();

            services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddAuthorization();
            services.AddSingleton<AuthenticationStateProvider, SignedInProvider>();

            services.AddMarten(options =>
            {
                options.Connection(server.ConnectionString + connectionSuffix);
                options.DatabaseSchemaName = PgVectorServerFixture.DocumentSchema;
                options.Events.DatabaseSchemaName = PgVectorServerFixture.EventSchema;
                options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
            });

            services.AddMartenStudio(options => options.Capabilities.RunSql = true);

            return new Host(services.BuildServiceProvider());
        }

        public Task<SqlConsoleResult> RunAsync(string statement) =>
            scope.ServiceProvider.GetRequiredService<ISqlConsoleService>()
                .RunSqlAsync(Scope, new SqlConsoleRequest(statement), Token);

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class SignedInProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}
