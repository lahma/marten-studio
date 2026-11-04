using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

using Testcontainers.PostgreSql;

namespace PostgresStudio.Integration.Tests.Query;

/// <summary>
/// 1.2-VEC in the core, with no Marten: the read-only session describes a statement and asks Postgres for the columns Npgsql
/// has no handler for - pgvector's three types first - in text form. This project runs at Npgsql 10.0.3 as well
/// (<c>-p:PostgresStudioNpgsqlVersion=10.0.3</c>), which the Marten suite cannot: it takes Marten's own Npgsql.
/// </summary>
/// <remarks>
/// <c>postgres:17-alpine</c> has no pgvector, so the class runs on a server of its own, the image
/// <c>PgVectorLiveTests</c> uses, never reused. When it cannot be started here the class skips with the reason; on CI that
/// fails the run (D19).
/// </remarks>
public sealed class PgVectorConsoleLiveTests : IAsyncLifetime
{
    private const string Image = "pgvector/pgvector:0.8.1-pg17";

    private PostgreSqlContainer? container;
    private string? unavailable;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            unavailable = DockerAvailability.SkipReason;
            return;
        }

        try
        {
            container = new PostgreSqlBuilder(Image).WithReuse(false).Build();
            await container.StartAsync(Token);
        }
        catch (Exception exception) when (!DockerAvailability.OnContinuousIntegration)
        {
            unavailable = "The " + Image + " image could not be started here (" + exception.Message + "); `docker pull " + Image +
                          "` makes this class run. On CI this is a failure, not a skip.";
            container = null;
            return;
        }

        await using NpgsqlConnection connection = await OpenAsync();
        await using var seed = new NpgsqlCommand(
            """
            create extension vector;
            create extension seg;
            create schema pgv;
            create type pgv.vector as enum ('up', 'down');
            create table pgv.items (id int primary key, embedding vector(3), half halfvec(3), sparse sparsevec(5), n numeric);
            insert into pgv.items values (1, '[1,2,3]', '[0.5,0.25,0.125]', '{1:1.5,3:2}/5', 1.5), (2, null, null, null, null);
            """,
            connection);
        await seed.ExecuteNonQueryAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(container!.GetConnectionString());
        await connection.OpenAsync(Token);
        return connection;
    }

    [PostgresFact]
    public async Task A_pgvector_column_arrives_as_the_text_Postgres_prints_and_is_marked_as_pgvector_s()
    {
        Assert.SkipWhen(unavailable is not null, unavailable ?? string.Empty);

        await using NpgsqlConnection connection = await OpenAsync();
        SqlResultSet result = await new ReadOnlySqlSession().ExecuteAsync(
            connection, "select id, embedding, half, sparse, n from pgv.items order by id");

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);
        result.Columns.Select(static x => x.Vector?.Kind).Should().Equal(
            new PgVectorKind?[] { null, PgVectorKind.Vector, PgVectorKind.HalfVector, PgVectorKind.SparseVector, null });

        result.Rows[0].Select(static x => x.Text).Should().Equal("1", "[1,2,3]", "[0.5,0.25,0.125]", "{1:1.5,3:2}/5", "1.5");
        result.Rows[0][0].Kind.Should().Be(SqlCellKind.Number);
        result.Rows[0][4].Kind.Should().Be(SqlCellKind.Number, "a column the driver can read stays typed");
        result.Rows[1][1].Kind.Should().Be(SqlCellKind.Null);
    }

    [PostgresFact]
    public async Task Another_unknown_type_is_text_and_a_look_alike_named_vector_is_not_a_vector()
    {
        Assert.SkipWhen(unavailable is not null, unavailable ?? string.Empty);

        await using NpgsqlConnection connection = await OpenAsync();
        var session = new ReadOnlySqlSession();

        // seg, not cube: no Npgsql version has a handler for it (Npgsql 10 reads cube).
        SqlResultSet seg = await session.ExecuteAsync(connection, "select '1 .. 2'::seg as c");
        seg.Succeeded.Should().BeTrue(seg.Error?.MessageText);
        seg.Columns[0].Vector.Should().BeNull();
        seg.Rows.Single()[0].Text.Should().Be("1 .. 2");

        SqlResultSet lookalike = await session.ExecuteAsync(connection, "select 'up'::pgv.vector as v");
        lookalike.Succeeded.Should().BeTrue(lookalike.Error?.MessageText);
        lookalike.Columns[0].Vector.Should().BeNull("the vector extension does not own pgv.vector");
        lookalike.Rows.Single()[0].Text.Should().Be("up");
    }

    /// <summary>
    /// Npgsql keys an auto-prepared statement by its SQL text alone and fixes its result formats into it, so asking for text on
    /// such a connection would disagree with the cached formats from the second or third run - and leave a text-format prepared
    /// statement on the host's pooled connection. There the console asks for no text: each statement runs four times with no
    /// exception and the same cells, and afterwards a plain command with the same text on that one pooled connection reads
    /// its binary values.
    /// </summary>
    [PostgresFact]
    public async Task On_a_connection_that_prepares_statements_nothing_is_asked_for_as_text_and_the_pool_is_left_as_it_was()
    {
        Assert.SkipWhen(unavailable is not null, unavailable ?? string.Empty);

        string connectionString = container!.GetConnectionString() + ";Max Auto Prepare=50;Auto Prepare Min Usages=2;Maximum Pool Size=1";
        const string Vector = "select id, embedding, n from pgv.items order by id";
        const string Unknown = "select '1 .. 2'::seg as c, 7 as n";

        // The vector has a binary output function and no Npgsql handler: "(cannot read ...)", as before 1.2. seg has no binary
        // output function, so Postgres refuses to send it in binary: an error, as before 1.2. Either way the same every run.
        foreach (string statement in new[] { Vector, Unknown })
        {
            string first = string.Empty;

            for (int run = 0; run < 4; run++)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(Token);

                SqlResultSet result = await new ReadOnlySqlSession().ExecuteAsync(connection, statement);

                result.Columns.Should().OnlyContain(static x => x.Vector == null);

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

            if (statement == Vector)
            {
                first.Should().Contain("(cannot read").And.Contain("::text");
            }
            else
            {
                first.Should().StartWith("error ").And.Contain("seg");
            }
        }

        // The same text, as the host's own command on the same pooled connection (Maximum Pool Size=1): the statement the
        // console ran must not have left a text-format description behind.
        await using var host = new NpgsqlConnection(connectionString);
        await host.OpenAsync(Token);

        for (int run = 0; run < 3; run++)
        {
            await using var command = new NpgsqlCommand(Vector, host);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

            (await reader.ReadAsync(Token)).Should().BeTrue();
            reader.GetInt32(0).Should().Be(1);
            reader.GetDecimal(2).Should().Be(1.5m);
        }
    }

    [PostgresFact]
    public async Task The_describe_runs_inside_the_read_only_transaction_and_a_write_is_still_refused()
    {
        Assert.SkipWhen(unavailable is not null, unavailable ?? string.Empty);

        await using NpgsqlConnection connection = await OpenAsync();
        var session = new ReadOnlySqlSession();

        SqlResultSet refused = await session.ExecuteAsync(
            connection, "with x as (delete from pgv.items returning embedding) select embedding from x");

        refused.Succeeded.Should().BeFalse();
        refused.Error!.SqlState.Should().Be("25006");

        SqlResultSet missing = await session.ExecuteAsync(connection, "select embedding from pgv.no_such_table");
        missing.Error!.SqlState.Should().Be("42P01", "the describe's error is the statement's own");

        (await session.ExecuteAsync(connection, "select count(*) from pgv.items")).Rows.Single()[0].Text.Should().Be("2");
    }
}
