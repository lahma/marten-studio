using System.Security.Claims;

using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Integration.Tests.Health;
using MartenStudio.Integration.Tests.Tenancy;
using MartenStudio.SampleDomain;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using Npgsql;

using PostgresStudio;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Integration.Tests.Diagnostics;

/// <summary>
/// The diagnostics API through <c>AddMartenStudio()</c>, live (API design §1.9, §5.4): host A's shape - a conjoined store whose
/// database the API lists, whose catalogue holds Marten's codes, and whose one tenant's hot stream (E03) is returned only with
/// <c>IncludeActivity</c> - and a master-table tenancy that holds no database yet, which lists nothing and creates nothing.
/// </summary>
public class MartenDiagnosticsLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_conjoined_stores_tenant_finding_needs_IncludeActivity_and_its_catalogue_holds_Martens_codes()
    {
        const string Schema = "dx_api_conjoined";

        await using ServiceProvider provider = await StoreHostAsync(Schema, static options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Policies.AllDocumentsAreMultiTenanted();
        });

        IDocumentStore store = provider.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        await EventHealthData.AppendAsync(store, streams: 200, eventsPerStream: 3, tenant: "globex");
        Guid hot = (await EventHealthData.AppendAsync(store, streams: 1, eventsPerStream: 1, tenant: "acme"))[0];
        await EventHealthData.AppendToAsync(store, hot, events: 900, tenant: "acme");

        IPostgresDiagnostics diagnostics = provider.GetRequiredService<IPostgresDiagnostics>();

        List<PostgresDatabaseRef> listed = [];
        await foreach (PostgresDatabaseRef database in diagnostics.ListDatabasesAsync(Token))
        {
            listed.Add(database);
        }

        PostgresDatabaseRef target = listed.Should().ContainSingle().Subject;
        target.Should().Be(new PostgresDatabaseRef("default", store.Storage.Database.Id.Identity));

        diagnostics.Checks.Select(static x => x.Code).Should().Contain(["D01", "D02", "E03", "K01", "T09", "C02"], "the registry is MartenStudio's");

        PostgresDiagnosticsSnapshot closed = await diagnostics.GetSnapshotAsync(
            target,
            PostgresDiagnosticsRequest.FindingsOnly with { RunDeeperChecks = true },
            Token);

        closed.Outcome.Should().Be(PostgresReadOutcome.Read, closed.Reason);
        closed.Findings.Should().NotContain(static x => x.Code == "E03", "the finding names a tenant");
        closed.FindingsWithheld.Should().BePositive();
        closed.ToJson().Should().NotContain("acme").And.NotContain(hot.ToString());

        PostgresDiagnosticsSnapshot open = await diagnostics.GetSnapshotAsync(
            target,
            PostgresDiagnosticsRequest.FindingsOnly with { RunDeeperChecks = true, IncludeActivity = true },
            Token);

        PostgresFinding e03 = open.Findings.Should().ContainSingle(static x => x.Code == "E03").Subject;
        e03.TenantId.Should().Be("acme");
        e03.Category.Should().Be(PostgresFindingCategory.Events);
        e03.Key.Should().StartWith("E03|");
    }

    [PostgresFact]
    public async Task A_master_table_tenancy_that_holds_nothing_lists_nothing_and_creates_nothing()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["dx_api_master"], Token);
        ServerSnapshot before = await ServerSnapshot.ReadAsync(owned.ConnectionStrings, "dx_api_", lookupTable: null, Token);

        ServiceCollection services = Basics();
        services.AddMarten(options =>
        {
            options.MultiTenantedDatabasesWithMasterDatabaseTable(tenancy =>
            {
                tenancy.ConnectionString = owned[0];
                tenancy.SchemaName = "tenants";
            });

            options.DatabaseSchemaName = "dx_api_master";
            options.Schema.For<TenancyNote>().MultiTenanted();
        });
        services.AddMartenStudio(static options => options.AuthorizationPolicy = null);

        await using (ServiceProvider provider = services.BuildServiceProvider())
        {
            IPostgresDiagnostics diagnostics = provider.GetRequiredService<IPostgresDiagnostics>();

            List<PostgresDatabaseRef> listed = [];
            await foreach (PostgresDatabaseRef database in diagnostics.ListDatabasesAsync(Token))
            {
                listed.Add(database);
            }

            listed.Should().BeEmpty("the tenancy holds no database, and listing never asks it to build one (hard rule 14, D27)");

            PostgresDiagnosticsSnapshot snapshot = await diagnostics.GetSnapshotAsync(
                new PostgresDatabaseRef("default", "acme"), PostgresDiagnosticsRequest.AllSections, Token);

            snapshot.Outcome.Should().Be(PostgresReadOutcome.NotFound, snapshot.Reason);
        }

        // The master database's objects are in the snapshot: the lookup table, which enumerating the tenancy applies, would show.
        before.ShouldBeUnchangedIn(await ServerSnapshot.ReadAsync(owned.ConnectionStrings, "dx_api_", lookupTable: null, Token));
    }

    private async Task<ServiceProvider> StoreHostAsync(string schema, Action<StoreOptions> store)
    {
        foreach (string name in new[] { schema, schema + "_events" })
        {
            await using NpgsqlConnection connection = new(postgres.ConnectionString);
            await connection.OpenAsync(Token);
            string quoted = SqlIdentifier.Quote(name);
            await using NpgsqlCommand command = new($"drop schema if exists {quoted} cascade; create schema {quoted};", connection);
            await command.ExecuteNonQueryAsync(Token);
        }

        ServiceCollection services = Basics();
        services.AddMarten(options =>
        {
            SampleStore.Configure(options, postgres.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema + "_events";
            options.Events.AddEventType<HealthPing>();
            store(options);
        });
        services.AddMartenStudio();

        return services.BuildServiceProvider();
    }

    private static ServiceCollection Basics()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, Anonymous>();
        services.AddSingleton<IHostEnvironment>(new Development());
        return services;
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class Development : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "MartenStudio.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
