using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Sql;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// POLISH P4: what Postgres says about the Schema screen's reads is masked against the schemas the visitor is not
/// shown - and when not even the schema list can be read, the mask fails closed. It used to hand the text back as
/// it was, on the reasoning that what failed then was the connection; but a failure that reaches the mask is not
/// always the connection's, and one that names an object in a schema the visitor may not see was then shown whole.
/// </summary>
/// <remarks>
/// No Postgres: the store is a host that does not resolve, so the read fails, the gate's schema list fails after
/// it, and the page gets the generic sentence. The whole message stays in the application's log.
/// </remarks>
public class SchemaFailureMaskingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_tab_whose_failure_cannot_be_checked_says_so_rather_than_what_Postgres_said()
    {
        await using Harness harness = await Harness.CreateAsync();

        SchemaTables tables = await harness.Schema.TablesAsync(harness.Scope, Token);

        tables.Reason.Should().Be(SchemaDataService.DetailsWithheld);
    }

    [Fact]
    public async Task A_preview_whose_failure_cannot_be_checked_withholds_it()
    {
        await using Harness harness = await Harness.CreateAsync();

        MigrationPreview preview = await harness.Schema.PreviewAsync(harness.Scope, Token);

        preview.Withheld.Should().BeNull("the visitor passes every gate; it is the database that is not there");
        preview.Notice.Should().Be(SchemaDataService.DetailsWithheld);
    }

    /// <summary>
    /// An apply's failure went back to the ApplySchemaChanges holder as the exception's own message, unmasked. It
    /// is masked like every other failure on the screen now - here, withheld whole, because the schema list the
    /// mask needs is on the same unreachable database - while the audit entry keeps what happened.
    /// </summary>
    [Fact]
    public async Task A_failed_apply_returns_the_masked_message_and_the_audit_keeps_the_whole_one()
    {
        await using Harness harness = await Harness.CreateAsync();

        SchemaApplyResult result = await harness.Schema.ApplyAsync(harness.Scope, harness.DatabaseId, Token);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be(SchemaDataService.DetailsWithheld);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should()
            .ContainSingle(static x => x.Action == "Apply schema changes").Which;
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().NotStartWith(SchemaDataService.DetailsWithheld, "the trail is where the difference is kept");
    }

    /// <summary>A store over an unreachable host, every capability, no policy to refuse anybody.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private Harness(ServiceProvider provider, string databaseId)
        {
            this.provider = provider;
            DatabaseId = databaseId;
            scope = provider.CreateAsyncScope();
            Schema = scope.ServiceProvider.GetRequiredService<ISchemaDataService>();
        }

        public string DatabaseId { get; }

        public ISchemaDataService Schema { get; }

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public StudioScope Scope => new(MartenStoreRegistry.DefaultStoreKey, DatabaseId, null);

        public static async Task<Harness> CreateAsync()
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(static options => options.Connection(SqlTestStore.Unreachable));
            services.AddMartenStudio(static options => options.Capabilities = MartenStudioCapabilities.All());
            services.AddSingleton<IAuthorizationService>(new TestStoreAuthorizationService());
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();

            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();

            return new Harness(provider, databases[0].Id.Identity);
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }
}
