using System.Security.Claims;
using System.Text.Json;

using JasperFx;

using Marten;
using Marten.Schema;

using MartenStudio.Integration.Tests.Database;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Schema;

/// <summary>
/// DB-7-fix against a real Postgres, over a store whose documents Marten partitions per tenant
/// (<c>PartitionMultiTenantedDocumentsUsingMartenManagement</c>) with three tenants in it - the shape in which
/// the DDL tab printed every tenant's name, one <c>CREATE TABLE … partition of … for values in ('tenant')</c>
/// per tenant, to a visitor the store policy let see one.
/// </summary>
/// <remarks>
/// <para>
/// Items 1, 2, 4 and 5 of the packet: the check, the preview and the script need the store policy for the
/// database as a whole and - while a document type is hidden - the database browser's gate; the apply is
/// refused to a visitor who may not see what it runs; the partition count of a Marten table and the row
/// figures of <c>mt_tenant_partitions</c> are withheld while the gate is shut; and the sentence names the
/// policy that actually refused.
/// </para>
/// <para>
/// One store per test, in a schema of the test's own. Every call goes through the studio's own services, so
/// the gate is exercised rather than bypassed.
/// </para>
/// </remarks>
public class SchemaScriptGateLiveTests(PostgresFixture fixture)
{
    private const string StorePolicy = "gate-store";

    private const string WritePolicy = "gate-write";

    private static readonly string[] Tenants = ["acme", "globex", "initech"];

    private static readonly StudioScope WholeDatabase = new("default", string.Empty, null);

    private static readonly StudioScope AcmeOnly = new("default", string.Empty, "acme");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------------------
    // (a) The store policy for the database as a whole
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The measured leak: a visitor the store policy lets see one tenant, who may even apply schema changes
    /// for that tenant, is refused the script, the check, the preview and the apply - and nothing is run.
    /// </summary>
    [PostgresFact]
    public async Task A_visitor_the_store_policy_restricts_to_one_tenant_is_refused_every_script_and_the_apply()
    {
        var policy = new ResourcePolicy(static resource => resource.TenantId == "acme");

        await using Host host = await StartAsync("db7f_tenant", hideSecret: false, policy, static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.StoreAuthorizationPolicy = StorePolicy;
            options.WriteAuthorizationPolicy = WritePolicy;
        });

        DdlScript ddl = await host.SchemaAsync(x => x.DdlAsync(AcmeOnly, Token));
        ddl.Withheld!.Kind.Should().Be(DatabaseRefusal.StorePolicy);
        ddl.Withheld.Reason.Should().Contain("spans every tenant").And.Contain("MartenStudioOptions.StoreAuthorizationPolicy");
        ddl.Text.Should().BeEmpty();

        SchemaCheck check = await host.SchemaAsync(x => x.CheckAsync(AcmeOnly, Token));
        check.Status.Should().Be(SchemaCheckStatus.Withheld);
        check.Withheld!.Kind.Should().Be(DatabaseRefusal.StorePolicy);

        MigrationPreview preview = await host.SchemaAsync(x => x.PreviewAsync(AcmeOnly, Token));
        preview.Withheld!.Kind.Should().Be(DatabaseRefusal.StorePolicy);
        preview.HasSql.Should().BeFalse();

        string identity = await host.SchemaAsync(x => x.DatabaseIdentityAsync(AcmeOnly, Token));
        Func<Task> apply = () => host.SchemaAsync(x => x.ApplyAsync(AcmeOnly, identity, Token));

        StudioNotAuthorizedException refusedApply = (await apply.Should().ThrowAsync<StudioNotAuthorizedException>()).Which;
        refusedApply.Scope.TenantId.Should().BeNull("an apply migrates the database as a whole, and that is what was refused");

        foreach (object refused in new object[] { ddl, check, preview })
        {
            string json = JsonSerializer.Serialize(refused);
            json.Should().NotContain("globex").And.NotContain("initech", "a refusal carries nothing of the script");
        }

        // Audited, against the database as a whole - the question that was refused.
        IReadOnlyList<StudioActionLogEntry> ring = host.Audit.GetLatest();
        ring.Should().Contain(static x => x.Action == "Generate schema script" && !x.Succeeded && x.TenantId == null);
        ring.Should().Contain(static x => x.Action == "Check schema" && !x.Succeeded);
        ring.Should().Contain(static x => x.Action == "Preview schema migration" && !x.Succeeded);
        ring.Should().Contain(static x => x.Action == "Apply schema changes" && !x.Succeeded && x.TenantId == null);
        ring.Should().NotContain(static x => x.Action == "Apply schema changes" && x.Succeeded);

        policy.Calls.Should().Contain(static x => x.Policy == StorePolicy && x.Resource.TenantId == null,
            "the store policy is asked about the database with no tenant");
    }

    /// <summary>
    /// The re-review of DB-0-fix-2 (R2): a write policy that lets the visitor apply schema changes for one
    /// tenant does not let them migrate the database - <c>CreateOrUpdate</c> can drop indexes and columns and
    /// rewrite a partitioned table for every tenant in it. The write policy is asked with no tenant, refuses,
    /// and the refusal is audited against the database as a whole; nothing runs.
    /// </summary>
    [PostgresFact]
    public async Task An_apply_from_a_tenant_scope_asks_the_write_policy_about_the_whole_database_and_runs_nothing_when_refused()
    {
        var policy = new ResourcePolicy(static resource => resource.Capability is null || resource.TenantId == "acme");

        await using Host host = await StartAsync("db7f_apply_tenant", hideSecret: false, policy, static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.StoreAuthorizationPolicy = StorePolicy;
            options.WriteAuthorizationPolicy = WritePolicy;
        });

        IReadOnlyList<string> before = await Host.IndexNamesAsync(fixture, "db7f_apply_tenant");
        string identity = await host.SchemaAsync(x => x.DatabaseIdentityAsync(AcmeOnly, Token));

        Func<Task> apply = () => host.SchemaAsync(x => x.ApplyAsync(AcmeOnly, identity, Token));

        StudioNotAuthorizedException refused = (await apply.Should().ThrowAsync<StudioNotAuthorizedException>()).Which;
        refused.Scope.TenantId.Should().BeNull();

        policy.Calls.Should().Contain(static x =>
            x.Policy == WritePolicy && x.Resource.Capability == nameof(StudioCapability.ApplySchemaChanges) && x.Resource.TenantId == null);
        policy.Calls.Should().NotContain(static x => x.Resource.Capability == nameof(StudioCapability.ApplySchemaChanges) && x.Resource.TenantId != null,
            "the visitor's tenant is never the question an apply asks");

        host.Audit.GetLatest().Should().Contain(static x => x.Action == "Apply schema changes" && !x.Succeeded && x.TenantId == null);
        (await Host.IndexNamesAsync(fixture, "db7f_apply_tenant")).Should().Equal(before, "nothing ran");
    }

    /// <summary>No policy and no hidden type: every visitor sees what they always saw, every tenant's partition included.</summary>
    [PostgresFact]
    public async Task With_no_policy_and_no_hidden_type_the_script_is_shown_as_before()
    {
        await using Host host = await StartAsync("db7f_open", hideSecret: false);

        DdlScript ddl = await host.SchemaAsync(x => x.DdlAsync(AcmeOnly, Token));

        ddl.Withheld.Should().BeNull();
        ddl.Reason.Should().BeNull();
        foreach (string tenant in Tenants)
        {
            ddl.Text.Should().Contain("for values in ('" + tenant + "')", "Marten writes each tenant's partition into the script");
        }

        ddl.Text.Should().Contain("mt_doc_gatesecret");

        SchemaCheck check = await host.SchemaAsync(x => x.CheckAsync(WholeDatabase, Token));
        check.Status.Should().NotBe(SchemaCheckStatus.Withheld);
        check.Status.Should().NotBe(SchemaCheckStatus.Unavailable, check.Reason);

        MigrationPreview preview = await host.SchemaAsync(x => x.PreviewAsync(WholeDatabase, Token));
        preview.Withheld.Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------------
    // (b) A hidden document type: the database browser's gate as well
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task With_a_hidden_type_and_the_capability_off_every_script_is_refused_naming_BrowseDatabase()
    {
        await using Host host = await StartAsync("db7f_hid_off", hideSecret: true);

        DdlScript ddl = await host.SchemaAsync(x => x.DdlAsync(WholeDatabase, Token));
        SchemaCheck check = await host.SchemaAsync(x => x.CheckAsync(WholeDatabase, Token));
        MigrationPreview preview = await host.SchemaAsync(x => x.PreviewAsync(WholeDatabase, Token));

        foreach (SchemaScriptRefusal? refusal in new[] { ddl.Withheld, check.Withheld, preview.Withheld })
        {
            refusal!.Kind.Should().Be(DatabaseRefusal.CapabilityOff);
            refusal.Reason.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase")
                .And.Contain("document types the host hides");
        }

        JsonSerializer.Serialize(new object[] { ddl, check, preview }).Should().NotContain("gatesecret");

        host.Audit.GetLatest().Should().Contain(static x =>
            x.Action == "Generate schema script" && !x.Succeeded && x.Capability == nameof(StudioCapability.BrowseDatabase));
    }

    /// <summary>
    /// The capability on and the write policy refusing <c>BrowseDatabase</c>: refused, naming the write policy -
    /// and an apply the write policy allows is refused too, because it would run what the visitor may not read.
    /// </summary>
    [PostgresFact]
    public async Task With_a_hidden_type_the_capability_on_and_the_write_policy_refusing_it_every_script_and_the_apply_are_refused()
    {
        var policy = new ResourcePolicy(static resource => resource.Capability != nameof(StudioCapability.BrowseDatabase));

        await using Host host = await StartAsync("db7f_hid_wp", hideSecret: true, policy, static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.StoreAuthorizationPolicy = StorePolicy;
            options.WriteAuthorizationPolicy = WritePolicy;
        });

        DdlScript ddl = await host.SchemaAsync(x => x.DdlAsync(WholeDatabase, Token));
        ddl.Withheld!.Kind.Should().Be(DatabaseRefusal.WritePolicy);
        ddl.Withheld.Reason.Should().Contain("MartenStudioOptions.WriteAuthorizationPolicy").And.Contain("BrowseDatabase");

        (await host.SchemaAsync(x => x.PreviewAsync(WholeDatabase, Token))).Withheld!.Kind.Should().Be(DatabaseRefusal.WritePolicy);
        (await host.SchemaAsync(x => x.CheckAsync(WholeDatabase, Token))).Withheld!.Kind.Should().Be(DatabaseRefusal.WritePolicy);

        string identity = await host.SchemaAsync(x => x.DatabaseIdentityAsync(WholeDatabase, Token));
        SchemaApplyResult apply = await host.SchemaAsync(x => x.ApplyAsync(WholeDatabase, identity, Token));

        apply.Succeeded.Should().BeFalse();
        apply.Withheld!.Kind.Should().Be(DatabaseRefusal.WritePolicy);

        policy.Calls.Where(static x => x.Resource.Capability == nameof(StudioCapability.BrowseDatabase))
            .Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.TenantId == null);
    }

    [PostgresFact]
    public async Task With_a_hidden_type_a_visitor_past_the_gate_reads_the_whole_script()
    {
        await using Host host = await StartAsync("db7f_hid_open", hideSecret: true, configure: static options =>
            options.Capabilities = MartenStudioCapabilities.All());

        DdlScript ddl = await host.SchemaAsync(x => x.DdlAsync(WholeDatabase, Token));

        ddl.Withheld.Should().BeNull();
        ddl.Text.Should().Contain("mt_doc_gatesecret", "past the gate the hidden type's table is part of what Marten would run");
        ddl.Text.Should().Contain("for values in ('acme')");

        (await host.SchemaAsync(x => x.PreviewAsync(WholeDatabase, Token))).Withheld.Should().BeNull();
        (await host.SchemaAsync(x => x.CheckAsync(WholeDatabase, Token))).Withheld.Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------------
    // F9 on Marten's own per-tenant partitions, and the tenancy table
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// F9 closed on a Marten table, not only a host's: the tenant-partitioned document table says it is
    /// partitioned and not how many times, and <c>mt_tenant_partitions</c> - a row per tenant - shows no row
    /// figure at all. No partition's name is anywhere in the answer.
    /// </summary>
    [PostgresFact]
    public async Task While_the_gate_is_shut_a_Marten_tables_partition_count_and_the_tenancy_tables_rows_are_withheld()
    {
        await using Host host = await StartAsync("db7f_f9_shut", hideSecret: false);

        SchemaTables tables = await host.SchemaAsync(x => x.TablesAsync(WholeDatabase, Token));
        tables.Reason.Should().BeNull();

        TableStats invoices = tables.Tables.Single(static x => x.Table == "mt_doc_gateinvoice");
        invoices.IsPartitioned.Should().BeTrue();
        invoices.PartitionCount.Should().BeNull("a per-tenant partition count is the number of tenants");

        TableStats registry = tables.Tables.Single(static x => x.Table == "mt_tenant_partitions");
        registry.FiguresWithheld.Should().BeTrue();
        registry.EstimatedRows.Should().Be(-1);
        registry.LiveRows.Should().BeNull();
        registry.DeadRows.Should().BeNull();
        registry.SequentialScans.Should().BeNull();
        registry.IndexScans.Should().BeNull();

        tables.PartitionCountsWithheld.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase");

        string json = JsonSerializer.Serialize(tables);
        foreach (string tenant in Tenants)
        {
            json.Should().NotContain(tenant, "a partition is named after its tenant and is never listed");
        }
    }

    [PostgresFact]
    public async Task Past_the_gate_the_partition_count_and_the_tenancy_tables_rows_are_shown()
    {
        await using Host host = await StartAsync("db7f_f9_open", hideSecret: false, configure: static options =>
            options.Capabilities = MartenStudioCapabilities.All());

        SchemaTables tables = await host.SchemaAsync(x => x.TablesAsync(WholeDatabase, Token));

        tables.PartitionCountsWithheld.Should().BeNull();
        tables.Tables.Single(static x => x.Table == "mt_doc_gateinvoice").PartitionCount.Should().Be(Tenants.Length);

        TableStats registry = tables.Tables.Single(static x => x.Table == "mt_tenant_partitions");
        registry.FiguresWithheld.Should().BeFalse();
        registry.EstimatedRows.Should().Be(Tenants.Length, "ANALYZE counted one row per tenant");

        // n_live_tup is whatever the statistics collector has been told so far: ANALYZE sets it, and the
        // inserting backend's own pending counts may be flushed after that and added on top (seen: 6 for 3).
        // That it is shown at all is the point here; its exact value is the collector's.
        registry.LiveRows.Should().NotBeNull().And.BeGreaterThanOrEqualTo(Tenants.Length);
    }

    /// <summary>
    /// Item 5: the sentence names the policy that refused - capability on, the store policy refusing the
    /// database as a whole to a visitor it lets see one tenant, and then the write policy refusing
    /// <c>BrowseDatabase</c> to one the store policy lets see everything.
    /// </summary>
    [PostgresFact]
    public async Task The_withheld_sentence_names_the_store_policy_or_the_write_policy_whichever_refused()
    {
        var storeRefuses = new ResourcePolicy(static resource => resource.TenantId == "acme");

        await using (Host host = await StartAsync("db7f_names_sp", hideSecret: false, storeRefuses, static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.StoreAuthorizationPolicy = StorePolicy;
            options.WriteAuthorizationPolicy = WritePolicy;
        }))
        {
            SchemaTables tables = await host.SchemaAsync(x => x.TablesAsync(AcmeOnly, Token));

            tables.Reason.Should().BeNull();
            tables.PartitionCountsWithheld.Should().Contain("The store policy (MartenStudioOptions.StoreAuthorizationPolicy)")
                .And.NotContain("WriteAuthorizationPolicy");
            tables.Tables.Single(static x => x.Table == "mt_doc_gateinvoice").PartitionCount.Should().BeNull();
        }

        var writeRefuses = new ResourcePolicy(static resource => resource.Capability != nameof(StudioCapability.BrowseDatabase));

        await using (Host host = await StartAsync("db7f_names_wp", hideSecret: false, writeRefuses, static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.StoreAuthorizationPolicy = StorePolicy;
            options.WriteAuthorizationPolicy = WritePolicy;
        }))
        {
            SchemaTables tables = await host.SchemaAsync(x => x.TablesAsync(WholeDatabase, Token));

            tables.PartitionCountsWithheld.Should().Contain("The write policy (MartenStudioOptions.WriteAuthorizationPolicy");
            tables.Tables.Single(static x => x.Table == "mt_tenant_partitions").FiguresWithheld.Should().BeTrue();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // The store
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A store with every document partitioned per tenant by Marten, three tenants with a document each, and
    /// everything analysed - built, applied and filled by Marten, then handed to a studio.
    /// </summary>
    private async Task<Host> StartAsync(
        string schema,
        bool hideSecret,
        IAuthorizationService? authorization = null,
        Action<MartenStudioOptions>? configure = null)
    {
        await fixture.CreateSchemaAsync(schema, Token);

        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedInProvider>();

        services.AddMarten(options =>
        {
            options.Connection(fixture.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.AutoCreateSchemaObjects = AutoCreate.None;

            options.Policies.AllDocumentsAreMultiTenanted();
            options.Policies.PartitionMultiTenantedDocumentsUsingMartenManagement(schema);

            options.Schema.For<GateInvoice>().Index(static x => x.Name);
            options.Schema.For<GateSecret>();
        });

        services.AddMartenStudio(options =>
        {
            options.AuthorizationPolicy = null;

            foreach (string tenant in Tenants)
            {
                options.KnownTenantIds.Add(tenant);
            }

            if (hideSecret)
            {
                options.IsDocumentTypeVisible = static type => type != typeof(GateSecret);
            }

            configure?.Invoke(options);
        });

        if (authorization is not null)
        {
            services.AddSingleton(authorization);
        }

        ServiceProvider provider = services.BuildServiceProvider();
        var host = new Host(provider);

        try
        {
            IDocumentStore store = provider.GetRequiredService<IDocumentStore>();
            await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await store.Advanced.AddMartenManagedTenantsAsync(Token, Tenants);

            foreach (string tenant in Tenants)
            {
                await using IDocumentSession session = store.LightweightSession(tenant);
                session.Store(new GateInvoice { Id = Guid.NewGuid(), Name = "n" });
                await session.SaveChangesAsync(Token);
            }

            // Only this test's own schema. A bare ANALYZE analyses every other class's tables in the shared
            // database, and several of them are about a table that has never been analysed - which a
            // parallel run then quietly turned into a test of the other branch (and a failure).
            await using NpgsqlConnection connection = await fixture.OpenAsync(Token);
            List<string> statements = [];
            await using (var list = new NpgsqlCommand(
                "select pg_catalog.format('analyze %I.%I', n.nspname, c.relname) from pg_catalog.pg_class c " +
                "join pg_catalog.pg_namespace n on n.oid = c.relnamespace " +
                "where n.nspname = @schema and c.relkind in ('r', 'p') and not c.relispartition",
                connection))
            {
                list.Parameters.AddWithValue("schema", schema);
                await using NpgsqlDataReader reader = await list.ExecuteReaderAsync(Token);
                while (await reader.ReadAsync(Token))
                {
                    statements.Add(reader.GetString(0));
                }
            }

            foreach (string statement in statements)
            {
                await using var analyze = new NpgsqlCommand(statement, connection);
                await analyze.ExecuteNonQueryAsync(Token);
            }

            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>One built studio.</summary>
    private sealed class Host(ServiceProvider provider) : IAsyncDisposable
    {
        public StudioActionLogService Audit => provider.GetRequiredService<StudioActionLogService>();

        public async Task<T> SchemaAsync<T>(Func<ISchemaDataService, Task<T>> action)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<ISchemaDataService>());
        }

        /// <summary>Every index in <paramref name="schema" />, by name, read outside the studio.</summary>
        public static async Task<IReadOnlyList<string>> IndexNamesAsync(PostgresFixture postgres, string schema)
        {
            await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
            await using var command = new NpgsqlCommand(
                "select indexname from pg_indexes where schemaname = @schema order by 1", connection);
            command.Parameters.AddWithValue("schema", schema);

            List<string> names = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        public async ValueTask DisposeAsync() => await provider.DisposeAsync();
    }

    /// <summary>Somebody is signed in; who they are is not what these tests are about.</summary>
    private sealed class SignedInProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}

/// <summary>A document Marten partitions per tenant.</summary>
[DocumentAlias("gateinvoice")]
public class GateInvoice
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>The document type the host hides, in the tests that hide one.</summary>
[DocumentAlias("gatesecret")]
public class GateSecret
{
    public Guid Id { get; set; }
}
