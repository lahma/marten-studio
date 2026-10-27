using System.Globalization;

using Npgsql;

using PostgresStudio.Integration.Tests.Versions;

using Testcontainers.PostgreSql;

namespace PostgresStudio.Integration.Tests.Query;

/// <summary>
/// X2's server: the official Debian <c>postgres:17.11</c> with <c>postgresql-17-hypopg</c> installed from the image's own PGDG
/// apt source - PROBE's recipe (hypopg 1.4.4, 2026-10-26) - and <c>pg_stat_statements</c> preloaded for the drill-in. One per
/// test class, never reused, labelled with this suite's reuse label so it is never mistaken for another suite's container.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each test in a database of its own</b> (the FLK-T25 lesson): a hypothetical index lives in a backend, but a table, its
/// statistics, a revoked grant and the extension are the database's, and a test that shared one with another would be
/// measuring both. <see cref="CreateDatabaseAsync" /> drops and creates it, and - as the superuser, never through the studio -
/// installs the extensions the test asks for. The studio never runs <c>CREATE EXTENSION</c>.
/// </para>
/// <para>
/// <b>D19's rule.</b> Locally a server that cannot start, or a package that cannot be installed (no network for apt), skips
/// every test that needs it with the reason; on GitHub Actions either is a failure, because a skipped live suite reported as a
/// pass is worse than one that failed.
/// </para>
/// </remarks>
public sealed class HypopgServer : IAsyncLifetime
{
    /// <summary>The image: Debian, because hypopg ships as a PGDG package, and the matrix's 17 is Alpine.</summary>
    public const string Image = "postgres:17.11";

    /// <summary>What installs hypopg inside the running container.</summary>
    public const string Install = "apt-get update -qq && apt-get install -y -qq postgresql-17-hypopg";

    private static readonly string RunId = Guid.NewGuid().ToString("N")[..8];

    private PostgreSqlContainer? container;

    /// <summary>Why the server is not there, or <see langword="null" />.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>Skips the calling test with <see cref="Unavailable" />'s reason, when there is one.</summary>
    public void SkipIfUnavailable()
    {
        if (Unavailable is { } why)
        {
            Assert.Skip(why);
        }
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            Unavailable = DockerAvailability.SkipReason;
            return;
        }

        container = new PostgreSqlBuilder(Image)
            .WithReuse(false)
            .WithName("ms-hypopg-" + RunId)
            .WithLabel(PostgresFixture.ReuseLabel, PostgresFixture.ReuseLabelValue)
            .WithLabel("ms-purpose", "hypopg")
            .WithCommand("-c", "shared_preload_libraries=pg_stat_statements")
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch (Exception exception) when (!DockerAvailability.OnContinuousIntegration)
        {
            await container.DisposeAsync();
            container = null;
            Unavailable = "The hypopg server could not start " + Image + " here (" + exception.GetType().Name + ": " + exception.Message
                + "); `docker pull " + Image + "` makes it run. On CI this is a failure, not a skip.";
            return;
        }

        var installed = await container.ExecAsync(["bash", "-c", Install]);

        if (installed.ExitCode != 0)
        {
            string why = "hypopg could not be installed in " + Image + " (`" + Install + "` exited "
                + Convert.ToString(installed.ExitCode, CultureInfo.InvariantCulture) + ": " + Tail(installed.Stderr) + ").";

            if (DockerAvailability.OnContinuousIntegration)
            {
                throw new InvalidOperationException(why + " On CI this is a failure, not a skip.");
            }

            Unavailable = why + " It needs the network for apt.postgresql.org.";
            return;
        }

        await ExecuteAsync(
            "create role app login password 'app'; "
            + "create role console_role nologin; "
            + "create role console_login login password 'console_login'; "
            + "grant console_role to console_login;",
            "postgres");
    }

    /// <summary>A connection string for <paramref name="role" /> (<c>postgres</c>, <c>app</c> or <c>console_login</c>) to <paramref name="database" />.</summary>
    public string ConnectionString(string role, string database, int? maxPoolSize = null)
    {
        NpgsqlConnectionStringBuilder builder = new((container ?? throw new InvalidOperationException(Unavailable)).GetConnectionString())
        {
            Database = database,
            ApplicationName = "ms-hypopg-" + role,
        };

        if (role != "postgres")
        {
            builder.Username = role;
            builder.Password = role;
        }

        if (maxPoolSize is { } most)
        {
            builder.MaxPoolSize = most;
        }

        return builder.ConnectionString;
    }

    /// <summary>Runs <paramref name="sql" /> as the superuser, on a connection of its own.</summary>
    public async Task ExecuteAsync(string sql, string database, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenSuperuserAsync(database, cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A superuser's connection, unpooled: a test drops and recreates its database.</summary>
    public async Task<NpgsqlConnection> OpenSuperuserAsync(string database, CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(ConnectionString("postgres", database)) { Pooling = false }.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A database of the test's own, owned by <c>app</c>, with <c>pg_stat_statements</c> and - unless the test asks for a
    /// database without it - hypopg installed by the superuser.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string name, bool hypopg = true, CancellationToken cancellationToken = default)
    {
        string quoted = PostgresStudio.Internal.Sql.SqlIdentifier.Quote(name);

        await ExecuteAsync("drop database if exists " + quoted + " with (force)", "postgres", cancellationToken);
        await ExecuteAsync("create database " + quoted + " owner app", "postgres", cancellationToken);
        await ExecuteAsync("create extension if not exists pg_stat_statements" + (hypopg ? "; create extension hypopg" : string.Empty), name, cancellationToken);

        return name;
    }

    /// <summary>The no-DDL fingerprint (<see cref="MatrixServer.FingerprintAsync" />).</summary>
    public async Task<IReadOnlySet<string>> SnapshotAsync(string database, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenSuperuserAsync(database, cancellationToken);
        return await MatrixServer.FingerprintAsync(connection, cancellationToken);
    }

    /// <summary><c>pg_class</c> itself: how many rows it has and its highest oid - a catalog row nobody named still moves both.</summary>
    public async Task<(long Rows, long HighestOid)> PgClassAsync(string database, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenSuperuserAsync(database, cancellationToken);
        await using NpgsqlCommand command = new("select count(*), max(oid::int8) from pg_catalog.pg_class", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>Whether a backend with <paramref name="processId" /> is still there.</summary>
    public async Task<bool> BackendExistsAsync(int processId, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenSuperuserAsync("postgres", cancellationToken);
        await using NpgsqlCommand command = new("select exists (select 1 from pg_catalog.pg_stat_activity where pid = @pid)", connection);
        command.Parameters.AddWithValue("pid", processId);
        return (bool) (await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    private static string Tail(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : "…" + trimmed[^300..];
    }
}
