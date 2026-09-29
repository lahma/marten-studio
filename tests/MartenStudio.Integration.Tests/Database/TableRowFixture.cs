using System.Diagnostics;
using System.Security.Claims;

using JasperFx;

using Marten;

using MartenStudio.Integration.Tests.Browser;
using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain;
using MartenStudio.Services;
using MartenStudio.Services.Database;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// The sample's store and its relational demo schemas - Quartz.NET's <c>quartz</c> and the hand-made
/// <c>legacy</c> - in a database of their own, seeded once, for the row reader's live tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>A database, not a schema</b>, for the reason <see cref="RelationalDemoFixture" /> gives:
/// <see cref="SampleDomain.Relational.RelationalDemoSchema" /> creates the fixed schemas <c>quartz</c> and
/// <c>legacy</c> and anchors to the sample's own <c>studio_sample</c>. Seeded through the sample's own
/// seeder, so the rows are the ones the demo host shows.
/// </para>
/// <para>
/// On top of the demo, the few things only these tests need, each written here as test code: a role
/// with <c>SELECT</c> on one column of <c>legacy.departments</c> and on every other table (the
/// <c>SqlConsoleRole</c> 42501 case - a role with no privilege at all is refused by the gate before
/// Postgres is asked), a foreign table (never read), and a view over <c>mt_doc_customer</c> (refused once
/// the customer type is hidden).
/// </para>
/// </remarks>
/// <param name="postgres">The assembly's container.</param>
public abstract class TableRowDemoFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The foreign table the gate refuses.</summary>
    public const string ForeignTable = "remote_orders";

    /// <summary>The view over the customer documents' table.</summary>
    public const string CustomerView = "customer_peek";

    /// <summary>The database this fixture owns.</summary>
    protected abstract string DatabaseName { get; }

    /// <summary>The assembly's container.</summary>
    public PostgresFixture Postgres { get; } = postgres;

    /// <summary>The connection string for <see cref="DatabaseName" />.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The role that may read one column of <c>legacy.departments</c> and every other table.</summary>
    public string Role => "ms_rows_" + DatabaseName;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        ConnectionString = await BrowserSuiteFixture.CreateDatabaseAsync(Postgres, DatabaseName);

        // The database was just dropped, and every privilege the role held in it with it.
        await DropRoleAsync();

        await using (MartenFixture marten = await MartenFixture.CreateAsync(ConnectionString, SampleStore.DocumentSchema))
        {
            await marten.SeedSampleDataAsync();
        }

        await ExecuteAsync(ExtraSql.Replace("{role}", SqlIdentifier.Quote(Role), StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A studio over the demo: <c>BrowseDatabase</c> on, <c>quartz</c> and <c>legacy</c> browsable, Marten
    /// never asked to create anything - then whatever <paramref name="configure" /> says.
    /// </summary>
    internal RowsHost Host(
        Action<MartenStudioOptions>? configure = null,
        IAuthorizationService? authorization = null,
        ILoggerProvider? logs = null) =>
        RowsHost.Create(ConnectionString, configure, authorization, logs);

    /// <summary>Runs SQL on a connection of the test's own, outside the studio.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A connection of the test's own to the demo database.</summary>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    /// <summary>Every row of a query, each column as text (NULL as <see langword="null" />).</summary>
    public async Task<List<string?[]>> RowsAsync(string sql)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        List<string?[]> rows = [];

        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new string?[reader.FieldCount];

            for (int i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>One number.</summary>
    public async Task<long> ScalarAsync(string sql)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task DropRoleAsync()
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync();

        await using (var exists = new NpgsqlCommand("select exists (select 1 from pg_roles where rolname = @role)", connection))
        {
            exists.Parameters.AddWithValue("role", Role);

            if (await exists.ExecuteScalarAsync() is not true)
            {
                return;
            }
        }

        await using var drop = new NpgsqlCommand("drop role " + SqlIdentifier.Quote(Role), connection);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>Somebody is signed in, by name, so the 9235 line has a user to name.</summary>
    internal sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "row-reader")], "test"))));
    }

    private const string ExtraSql =
        """
        create role {role} nologin;
        grant usage on schema legacy, quartz to {role};
        grant select on all tables in schema legacy, quartz to {role};
        revoke select on legacy.departments from {role};
        grant select (department_code) on legacy.departments to {role};

        create extension if not exists postgres_fdw;
        create server ms_rows_remote foreign data wrapper postgres_fdw options (host 'localhost', dbname 'nothing_here');
        create foreign table legacy.remote_orders (id int) server ms_rows_remote;

        create view legacy.customer_peek as select id from studio_sample.mt_doc_customer;
        """;
}

/// <summary>One built studio over a demo database.</summary>
internal sealed class RowsHost(ServiceProvider provider) : IAsyncDisposable
{
    /// <summary>
    /// A studio over the sample's store in <paramref name="connectionString" />'s database:
    /// <c>BrowseDatabase</c> on, <c>quartz</c> and <c>legacy</c> browsable, Marten never asked to create
    /// anything - then whatever <paramref name="configure" /> says.
    /// </summary>
    public static RowsHost Create(
        string connectionString,
        Action<MartenStudioOptions>? configure = null,
        IAuthorizationService? authorization = null,
        ILoggerProvider? logs = null)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(logs is null ? LogLevel.Warning : LogLevel.Trace);

            if (logs is not null)
            {
                builder.AddProvider(logs);
            }
        });

        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, TableRowDemoFixture.SignedIn>();

        services.AddMarten(options =>
        {
            SampleStore.Configure(options, connectionString);
            options.AutoCreateSchemaObjects = AutoCreate.None;
        });

        services.AddMartenStudio(options =>
        {
            options.Capabilities.BrowseDatabase = true;
            options.BrowsableSchemas.Add("quartz");
            options.BrowsableSchemas.Add("legacy");

            configure?.Invoke(options);
        });

        if (authorization is not null)
        {
            services.AddSingleton(authorization);
        }

        return new RowsHost(services.BuildServiceProvider());
    }

    /// <summary>The scope every call is made with: the default store, its only database, all tenants.</summary>
    public static StudioScope Scope { get; } = new("default", string.Empty, null);

    /// <summary>The container.</summary>
    public IServiceProvider Services => provider;

    /// <summary>The process-wide audit ring.</summary>
    public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

    /// <summary>Calls the row service in a scope of its own, as one circuit would.</summary>
    public async Task<T> RowsAsync<T>(Func<ITableRowService, Task<T>> call)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<ITableRowService>());
    }

    /// <summary>A circuit's scope, kept open across several calls - which is what the audit dedup is per.</summary>
    public AsyncServiceScope Circuit() => provider.CreateAsyncScope();

    /// <summary>
    /// Every page of a relation, walked by <see cref="TableRowPage.NextCursor" /> (or
    /// <see cref="TableRowPage.NextOffset" />) until there is no next page.
    /// </summary>
    public async Task<List<TableRowPage>> WalkAsync(string schema, string name, TableRowRequest first, int maxPages = 200)
    {
        List<TableRowPage> pages = [];
        TableRowRequest request = first;

        await using AsyncServiceScope scope = Circuit();
        ITableRowService rows = scope.ServiceProvider.GetRequiredService<ITableRowService>();

        while (pages.Count < maxPages)
        {
            TableRowPage page = await rows.ListRowsAsync(Scope, schema, name, request, TestContext.Current.CancellationToken);
            pages.Add(page);

            if (page.State != TableRowPageState.Loaded || !page.HasMore)
            {
                return pages;
            }

            request = page.NextCursor is { } cursor
                ? request with { Cursor = cursor, Offset = 0 }
                : request with { Offset = page.NextOffset ?? throw new InvalidOperationException("A page with more has no way to it.") };
        }

        throw new InvalidOperationException("The walk did not end in " + maxPages + " pages.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await provider.DisposeAsync();
}

/// <summary>
/// Every statement Npgsql runs under this log's activity, with its text - a statement log of the test's
/// own, read from Npgsql's tracing rather than from anything the studio reports about itself.
/// </summary>
/// <remarks>
/// Npgsql starts an <see cref="Activity" /> from its <c>Npgsql</c> source for every command and tags it
/// with <c>db.statement</c> whenever something listens. The log starts a root activity of its own and keeps
/// only the commands in that trace, so the rest of the suite, running at the same time, is not in it.
/// The tag is read when the activity stops, because Npgsql sets it after the activity has started.
/// </remarks>
internal sealed class StatementLog : IDisposable
{
    private readonly Activity root;
    private readonly ActivityListener listener;
    private readonly List<string> statements = [];
    private readonly Lock gate = new();

    public StatementLog()
    {
        root = new Activity("marten-studio-statement-log");
        root.SetIdFormat(ActivityIdFormat.W3C);
        root.Start();

        ActivityTraceId trace = root.TraceId;

        listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Parent.TraceId == trace ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == trace && activity.GetTagItem("db.statement") is string sql)
                {
                    lock (gate)
                    {
                        statements.Add(sql);
                    }
                }
            },
        };

        ActivitySource.AddActivityListener(listener);
    }

    /// <summary>Every statement run in this trace so far, in order.</summary>
    public IReadOnlyList<string> Statements
    {
        get
        {
            lock (gate)
            {
                return [.. statements];
            }
        }
    }

    public void Dispose()
    {
        listener.Dispose();
        root.Stop();
    }
}
