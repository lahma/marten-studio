using System.Security.Claims;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Documents;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Logging;

/// <summary>
/// DB-0-fix-2, F8: an exact count that cannot get a connection is "could not read this", not "the count
/// ran out of time".
/// </summary>
/// <remarks>
/// <para>
/// The count's own statement timing out is the visitor's business: the badge draws "?", keeps the "="
/// button, and logs Debug. <c>PostgresFailure.IsTimeout</c> recognises it as 57014 or as an
/// <c>NpgsqlException</c> wrapping a <c>TimeoutException</c> - which is also exactly what Npgsql throws
/// when its pool is dry and the connect timeout expires. The exact count opened its connection with a
/// plain <c>OpenAsync</c>, so a database that was not answering at all was drawn as a slow count and
/// logged at Debug. It opens through <c>PostgresFailure.OpenAsync</c> now, which marks a failure to
/// connect so the timeout filter passes it by.
/// </para>
/// <para>
/// A real pool of one connection, held by the test: the studio's open waits one second and fails the way
/// a dry production pool fails.
/// </para>
/// </remarks>
public class ExactCountConnectionLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task An_exact_count_that_cannot_get_a_connection_is_Unavailable_and_a_Warning()
    {
        string schema = GetType().Name.ToLowerInvariant();
        await postgres.CreateSchemaAsync(schema, Token);

        var logs = new LogCapture();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(logs);
        });

        services.AddMarten(options =>
        {
            options.Connection(new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
            {
                MaxPoolSize = 1,
                Timeout = 1,
            }.ConnectionString);

            options.DatabaseSchemaName = schema;
            options.Schema.For<ExactCountThing>();
        });

        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, AnonymousVisitor>();
        services.AddMartenStudio(static options => options.DiscoverTenantIds = false);

        await using ServiceProvider provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IDocumentStore>();
        IMartenDatabase database = (await store.Storage.AllDatabases())[0];

        // The pool's one connection, held for the length of the count.
        await using NpgsqlConnection held = database.CreateConnection(ConnectionUsage.Read);
        await held.OpenAsync(Token);

        using IServiceScope scope = provider.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentDataService>();

        var count = await documents.CountExactAsync(
            new StudioScope(MartenStoreRegistry.DefaultStoreKey, database.Id.Identity, null),
            store.Options.FindOrResolveDocumentType(typeof(ExactCountThing)).Alias,
            Token);

        count.IsUnavailable.Should().BeTrue();
        count.IsUnknown.Should().BeFalse("a pool that could not hand out a connection is not a count that ran out of time");

        logs.Lines.Should().Contain(
            static x => x.Level == LogLevel.Warning && x.Message.Contains("exactly", StringComparison.Ordinal),
            "a database the studio cannot reach is worth a Warning; a slow count of the visitor's own is not");
    }

    /// <summary>A document type, so the count has a collection to name.</summary>
    public sealed class ExactCountThing
    {
        public Guid Id { get; set; }
    }

    /// <summary>Who a generic host's circuit belongs to: nobody.</summary>
    private sealed class AnonymousVisitor : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
