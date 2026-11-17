using System.Security.Claims;

using DataStudio.Kafka.Services;
using DataStudio.Marten.Tests.Support;
using DataStudio.Postgres;
using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Integration.Tests.Composition;

/// <summary>
/// D34 beside a database module: a Kafka audit entry is judged only by the module's own gate, never by the host's store policy, and a database entry
/// is judged as it always was. These two cases need both modules' gates in one ring, so they live in the project that references both; they run over the
/// dependency-injection container and its scoped Activity ring, with no circuit, no broker and no database connection.
/// </summary>
public class KafkaAuditBesidePostgresTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers the host's store policy no, and records every resource it was asked about.</summary>
    private sealed class RefuseTheHost : IAuthorizationService
    {
        public List<object?> Resources { get; } = [];

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            lock (Resources)
            {
                Resources.Add(resource);
            }

            return Task.FromResult(resource is KafkaTopicResource ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
    }

    private static (ServiceProvider Provider, RefuseTheHost Authorization) Host()
    {
        RefuseTheHost authorization = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none"));
        services.AddSingleton<IAuthorizationService>(authorization);
        services.AddSingleton<AuthenticationStateProvider, TestAuthenticationStateProvider>();
        services.AddDataStudio().AddPostgres(static options => options.SourceAuthorizationPolicy = "host-source");
        services.AddDataStudio().AddKafka(static options => options.ClusterAuthorizationPolicy = "kafka-topics");
        return (services.BuildServiceProvider(), authorization);
    }

    [Fact]
    public async Task A_module_entry_is_never_judged_by_the_hosts_store_policy()
    {
        (ServiceProvider provider, RefuseTheHost authorization) = Host();
        using ServiceProvider _ = provider;
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<KafkaActionLog>().Record(
            new KafkaClusterEntry("primary", "tenant-a", "Primary", null), ["orders.v1"], "SearchRecords", "orders.v1", succeeded: true, "done");

        List<StudioActionLogEntry> visible = await scope.ServiceProvider.GetRequiredService<StudioActivity>().GetVisibleAsync(cancellationToken: Ct);

        StudioActionLogEntry entry = visible.Should().ContainSingle("the host's policy refuses every source, and says nothing about a Kafka entry").Which;
        entry.Scope.Kind.Should().Be("kafka");
        entry.DatabaseScope().Should().BeNull("a module entry names no store, so no host policy can recognise it");
        authorization.Resources.Should().NotContain(static x => x is PostgresResource, "the host's policy is written for its own resource");
    }

    [Fact]
    public async Task A_host_entry_is_judged_as_before()
    {
        (ServiceProvider provider, RefuseTheHost authorization) = Host();
        using ServiceProvider _ = provider;
        using IServiceScope scope = provider.CreateScope();

        provider.GetRequiredService<StudioActionLogService>().Record(new StudioActionLogEntry(
            DateTimeOffset.UnixEpoch, "ops", new DatabaseAuditScope("default", "main", null).ToAuditScope(PostgresDescriptor.Key), "OpenRow", "public.orders", true, "done", null));

        List<StudioActionLogEntry> visible = await scope.ServiceProvider.GetRequiredService<StudioActivity>().GetVisibleAsync(cancellationToken: Ct);

        visible.Should().BeEmpty("the host's policy refuses the entry's store, as it did before the module existed");
        authorization.Resources.Should().Contain(static x => x is PostgresResource, "the database module's gate asked the host's policy about its own resource");
    }
}
