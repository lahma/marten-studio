using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Sources;
using MartenStudio.Tests.Sql;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MartenStudio.Tests.Health;

/// <summary>
/// C06 labels the locks it reads with the database browser's classification, read afresh in the sweep's scope - and fails
/// closed, as it did in 0.4: a classification it cannot have costs the finding its relation names, never its row.
/// </summary>
public class LockWaitClassifierTests
{
    [Fact]
    public void A_declarations_source_that_cannot_be_built_leaves_C06_unlabelled_rather_than_unchecked()
    {
        ServiceCollection services = new();
        services.AddScoped<IDatabaseDeclarationsSource>(static _ => throw new InvalidOperationException("Not in a scope with no circuit."));

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        using DocumentStore store = Store();

        FluentActions.Invoking(() => LockWaitCheck.Classifier(Context(scope.ServiceProvider, store))).Should().NotThrow();
        LockWaitCheck.Classifier(Context(scope.ServiceProvider, store)).Should().BeNull();
    }

    [Fact]
    public void A_studio_that_registers_no_declarations_names_no_relation()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using DocumentStore store = Store();

        LockWaitCheck.Classifier(Context(provider, store)).Should().BeNull();
    }

    /// <summary>
    /// MartenStudio's declarations source resolves its authorization on the first policy question, not when it is built:
    /// the sweep's scope has no circuit, and C06's read asks no policy. A scope whose authorization graph does not build
    /// still classifies.
    /// </summary>
    [Fact]
    public async Task MartenStudios_declarations_are_read_in_a_scope_whose_authorization_graph_does_not_build()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMarten(static options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = "c06_classifier";
            options.Schema.For<HealthOrderDoc>();
        });
        services.AddMartenStudio();
        services.RemoveAll<AuthenticationStateProvider>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        FluentActions.Invoking(() => scope.ServiceProvider.GetRequiredService<StudioAuthorization>())
            .Should().Throw<InvalidOperationException>("the premise: this scope cannot build the authorization graph");

        IDocumentStore store = provider.GetRequiredService<IDocumentStore>();

        LockWaitCheck.Classifier(Context(scope.ServiceProvider, store))
            .Should().NotBeNull("the declarations are read from the store's options, and need no policy");
    }

    private static DocumentStore Store() => DocumentStore.For(static options =>
    {
        options.Connection(SqlTestStore.Unreachable);
        options.DatabaseSchemaName = "c06_classifier";
    });

    private static HealthCheckContext Context(IServiceProvider services, IDocumentStore store) => new(
        new ResolvedScope(
            new StudioScope("default", string.Empty, null),
            new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
            store,
            store.Storage.Database),
        MartenStudioSettings.Of(new MartenStudioOptions()),
        MartenStudioRuntime.Current,
        services,
        new HealthSqlRunner(new HealthConnection(MartenStudioDatabase.Of(store.Storage.Database)), new ReadOnlySqlOptions(), null),
        DateTimeOffset.UnixEpoch,
        new HealthSweepMemo(),
        CancellationToken.None);
}
