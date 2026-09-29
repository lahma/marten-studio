using System.Security.Claims;

using JasperFx;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Database;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// A Marten store beside a Quartz-shaped schema and a legacy schema holding one of every kind of object
/// the database browser lists, built once per test class in schemas named after it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per class, like <see cref="MartenClassFixture" />.</b> Derive a nested
/// <c>public sealed class Fixture(PostgresFixture postgres) : DatabaseBrowserFixture(postgres)</c> in the test
/// class: the schemas and the role are named after the declaring class, so two classes running at once
/// never drop each other's tables.
/// </para>
/// <para>
/// Marten's own tables are created by Marten, once, with <c>AutoCreate.All</c>; every studio a test then
/// builds runs with <c>AutoCreate.None</c>, so nothing a test does can quietly create a table and make an
/// assertion about the catalog pass for the wrong reason. The non-Marten objects are plain DDL written here
/// - test code, where raw SQL belongs.
/// </para>
/// </remarks>
/// <param name="postgres">The assembly's shared container.</param>
public abstract class DatabaseBrowserFixture(PostgresFixture postgres) : IAsyncLifetime
{
    private string prefix = string.Empty;

    /// <summary>The shared container.</summary>
    public PostgresFixture Postgres { get; } = postgres;

    /// <summary>The store's document schema.</summary>
    public string DocumentSchema => prefix + "_doc";

    /// <summary>The store's event schema.</summary>
    public string EventSchema => prefix + "_doc_events";

    /// <summary>The Quartz-shaped schema.</summary>
    public string QuartzSchema => prefix + "_quartz";

    /// <summary>The legacy schema, with one of everything.</summary>
    public string LegacySchema => prefix + "_legacy";

    /// <summary>A role that may read everything here but <c>payroll</c>.</summary>
    public string Role => "ms_role_" + prefix;

    /// <summary>The hidden document type's table, as Marten names it.</summary>
    public string HiddenTable { get; private set; } = string.Empty;

    /// <summary>The visible document type's table, as Marten names it.</summary>
    public string VisibleTable { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        string name = (GetType().DeclaringType ?? GetType()).Name.ToLowerInvariant();
        prefix = name.Length > 40 ? name[..40] : name;

        await DropRoleAsync();

        foreach (string schema in new[] { DocumentSchema, EventSchema, QuartzSchema, LegacySchema })
        {
            await Postgres.CreateSchemaAsync(schema);
        }

        // Marten builds its own tables, once, the way an application would.
        await using (BrowserHost setup = Host(autoCreate: AutoCreate.All))
        {
            await setup.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

            HiddenTable = setup.Store.Options.FindOrResolveDocumentType(typeof(BrowserSecret)).TableName.Name;
            VisibleTable = setup.Store.Options.FindOrResolveDocumentType(typeof(BrowserCustomer)).TableName.Name;
        }

        await ExecuteAsync(FixtureSql
            .Replace("{d}", SqlIdentifier.Quote(DocumentSchema), StringComparison.Ordinal)
            .Replace("{q}", SqlIdentifier.Quote(QuartzSchema), StringComparison.Ordinal)
            .Replace("{l}", SqlIdentifier.Quote(LegacySchema), StringComparison.Ordinal)
            .Replace("{hidden}", SqlIdentifier.Quote(HiddenTable), StringComparison.Ordinal)
            .Replace("{role}", SqlIdentifier.Quote(Role), StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (DockerAvailability.IsAvailable && prefix.Length > 0)
        {
            await DropRoleAsync();
        }
    }

    /// <summary>
    /// A studio over the fixture's store: <c>BrowseDatabase</c> on, the Quartz and legacy schemas
    /// browsable, <see cref="BrowserSecret" /> hidden - then whatever <paramref name="configure" /> says.
    /// </summary>
    /// <param name="configure">Studio options, applied after those defaults.</param>
    /// <param name="authorization">An authorization service of the test's own, for the policy tests.</param>
    /// <param name="autoCreate">Marten's <c>AutoCreate</c>; none, unless the fixture itself is building the schema.</param>
    internal BrowserHost Host(
        Action<MartenStudioOptions>? configure = null,
        IAuthorizationService? authorization = null,
        AutoCreate autoCreate = AutoCreate.None)
    {
        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedInProvider>();

        string documentSchema = DocumentSchema;
        string eventSchema = EventSchema;
        string quartz = QuartzSchema;
        string legacy = LegacySchema;

        services.AddMarten(options =>
        {
            options.Connection(Postgres.ConnectionString);
            options.DatabaseSchemaName = documentSchema;
            options.Events.DatabaseSchemaName = eventSchema;
            options.AutoCreateSchemaObjects = autoCreate;

            options.Schema.For<BrowserCustomer>();
            options.Schema.For<BrowserSecret>();
            options.Events.AddEventType<BrowserEvent>();
        });

        services.AddMartenStudio(options =>
        {
            options.IsDocumentTypeVisible = static type => type != typeof(BrowserSecret);
            options.Capabilities.BrowseDatabase = true;
            options.BrowsableSchemas.Add(quartz);
            options.BrowsableSchemas.Add(legacy);

            configure?.Invoke(options);
        });

        if (authorization is not null)
        {
            services.AddSingleton(authorization);
        }

        return new BrowserHost(services.BuildServiceProvider());
    }

    /// <summary>Runs SQL on a connection of the test's own, outside the studio.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
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

        string quoted = SqlIdentifier.Quote(Role);

        await using var drop = new NpgsqlCommand($"drop owned by {quoted}; drop role {quoted};", connection);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// One of every kind of object, plus the grants the role test needs. <c>{d}</c>, <c>{q}</c>,
    /// <c>{l}</c>, <c>{hidden}</c> and <c>{role}</c> are replaced by quoted identifiers.
    /// </summary>
    private const string FixtureSql =
        """
        create table {d}.host_settings (key text primary key, value text);
        create function {d}.host_fn() returns int language sql as $$ select 1 $$;

        create table {q}.qrtz_job_details (
            sched_name varchar(120) not null,
            job_name varchar(200) not null,
            job_group varchar(200) not null,
            description varchar(250),
            constraint pk_qrtz_job_details primary key (sched_name, job_name, job_group));

        create table {q}.qrtz_triggers (
            sched_name varchar(120) not null,
            trigger_name varchar(200) not null,
            trigger_group varchar(200) not null,
            job_name varchar(200) not null,
            job_group varchar(200) not null,
            next_fire_time bigint,
            constraint pk_qrtz_triggers primary key (sched_name, trigger_name, trigger_group),
            constraint fk_qrtz_triggers_job_details foreign key (sched_name, job_name, job_group)
                references {q}.qrtz_job_details (sched_name, job_name, job_group));

        create index idx_qrtz_t_next_fire_time on {q}.qrtz_triggers (next_fire_time) include (trigger_group);

        insert into {q}.qrtz_job_details values ('scheduler', 'nightly', 'reports', 'the nightly report');
        insert into {q}.qrtz_triggers values ('scheduler', 'every-night', 'reports', 'nightly', 'reports', 638000000000000000);

        create table {l}.keyless_log (at timestamptz, message text);
        create table {l}.codes (code text not null, label text, constraint codes_code_key unique (code) include (label));

        create view {l}.job_summary as
            select sched_name, count(*) as triggers from {q}.qrtz_triggers group by sched_name;
        create materialized view {l}.populated_stats as select 1 as one;
        create materialized view {l}.empty_stats as select 1 as one with no data;

        create table {l}.measurements (id bigint not null, taken date not null, primary key (id, taken))
            partition by range (taken);
        create table {l}.measurements_2025 partition of {l}.measurements for values from ('2025-01-01') to ('2026-01-01');
        create table {l}.measurements_2026 partition of {l}.measurements for values from ('2026-01-01') to ('2027-01-01');

        create type {l}.order_status as enum ('pending', 'shipped', 'cancelled');
        create domain {l}.positive_amount as numeric(12,2) check (value > 0);
        create type {l}.address as (street text, city text);
        create type {l}.price_range as range (subtype = numeric);

        create table {l}.orders (
            id bigint generated by default as identity primary key,
            status {l}.order_status not null default 'pending',
            amount {l}.positive_amount,
            ship_to {l}.address,
            legacy_number serial);

        create table {l}.order_notes (id bigint primary key, order_id bigint references {l}.orders (id));

        create sequence {l}.standalone_numbers start 100;

        create function {l}.touch() returns trigger language plpgsql as $$ begin return new; end $$;
        create trigger orders_enabled before update on {l}.orders for each row execute function {l}.touch();
        create trigger orders_disabled after insert on {l}.orders for each row execute function {l}.touch();
        alter table {l}.orders disable trigger orders_disabled;

        create function {l}.recalculate(order_id bigint) returns numeric language plpgsql as $$ begin return 0; end $$;
        create procedure {l}.archive_orders(before date) language plpgsql as $$ begin null; end $$;
        create aggregate {l}.sum_amounts (numeric) (sfunc = numeric_add, stype = numeric);

        create table {l}."bad""name" (id int);
        create table {l}.payroll (id int primary key, salary numeric);

        create view {l}.secret_peek as select id from {d}.{hidden};

        create role {role} nologin;
        grant usage on schema {d}, {q}, {l} to {role};
        grant select on all tables in schema {d}, {q}, {l} to {role};
        revoke select on {l}.payroll from {role};
        """;

    /// <summary>Somebody is signed in; who they are is not what these tests are about.</summary>
    private sealed class SignedInProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}

/// <summary>One built studio over the fixture's store.</summary>
internal sealed class BrowserHost(ServiceProvider provider) : IAsyncDisposable
{
    /// <summary>The scope every call is made with: the default store, its only database, all tenants.</summary>
    public static StudioScope Scope { get; } = new("default", string.Empty, null);

    /// <summary>The container.</summary>
    public IServiceProvider Services => provider;

    /// <summary>The store.</summary>
    public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

    /// <summary>The process-wide audit ring.</summary>
    public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

    /// <summary>Calls the object service in a scope of its own, as a circuit would.</summary>
    public async Task<T> ObjectsAsync<T>(Func<IDatabaseObjectService, Task<T>> call)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<IDatabaseObjectService>());
    }

    /// <summary>Calls the gate in a scope of its own.</summary>
    public async Task<T> AccessAsync<T>(Func<DatabaseAccess, Task<T>> call)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<DatabaseAccess>());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await provider.DisposeAsync();
}

/// <summary>An authorization service that answers from a rule over the resource and records every question.</summary>
internal sealed class ResourcePolicy(Func<MartenStoreResource, bool> rule) : IAuthorizationService
{
    /// <summary>Every resource the studio asked about.</summary>
    public List<(string Policy, MartenStoreResource Resource)> Calls { get; } = [];

    public Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user,
        object? resource,
        IEnumerable<IAuthorizationRequirement> requirements) =>
        Task.FromResult(resource is MartenStoreResource store && rule(store)
            ? AuthorizationResult.Success()
            : AuthorizationResult.Failed());

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
    {
        if (resource is MartenStoreResource store)
        {
            lock (Calls)
            {
                Calls.Add((policyName, store));
            }
        }

        return Task.FromResult(resource is MartenStoreResource allowed && rule(allowed)
            ? AuthorizationResult.Success()
            : AuthorizationResult.Failed());
    }
}

/// <summary>A visible document type.</summary>
public class BrowserCustomer
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }

    /// <summary>A name.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>The document type the host hides.</summary>
public class BrowserSecret
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }
}

/// <summary>One event type, so the event store's tables exist.</summary>
/// <param name="Id">What it happened to.</param>
public record BrowserEvent(Guid Id);
