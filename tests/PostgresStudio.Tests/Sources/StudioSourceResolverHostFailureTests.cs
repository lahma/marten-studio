using System.Diagnostics.CodeAnalysis;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.Logging;

using PostgresStudio.Services;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// 1.0-HARD, item 1, for any <see cref="IStudioSource" /> - Marten's tenancy as much as a host's database source: whatever
/// the source's own code throws from listing its databases or completing a scope reaches the caller as a
/// <see cref="StudioStoreUnavailableException" /> that names the source, says the host's words under H1b's rule (escaped and
/// bounded; Postgres' own withheld) and keeps the original as its inner exception, while the studio's own refusals (a tenant the
/// source does not have) pass through as they are.
/// </summary>
public class StudioSourceResolverHostFailureTests
{
    private const string HostWords = "tenancy lookup failed\r\nforged: all clear";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_source_whose_list_throws_KeyNotFound_is_wrapped_and_the_original_is_logged_once()
    {
        CapturingLoggerProvider logs = new();
        using ILoggerFactory factory = logs.CreateFactory();
        ThrowingSource source = new() { List = new KeyNotFoundException(HostWords) };
        StudioSourceResolver resolver = Resolver(source, factory);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            Func<Task> resolve = async () => await resolver.ResolveAsync(new StudioScope("tenants", "acme", null), cancellationToken: Token);
            StudioStoreUnavailableException wrapped = (await resolve.Should().ThrowAsync<StudioStoreUnavailableException>()).Which;

            wrapped.Message.Should().Contain("tenants").And.Contain(StudioLogValue.CapText(HostWords)).And.NotContain("\n");
            wrapped.InnerException.Should().BeOfType<KeyNotFoundException>();
        }

        logs.Entries.Where(static x => x.EventId.Id == 9213).Select(static x => x.Level)
            .Should().Equal([LogLevel.Warning, LogLevel.Debug], "the second occurrence in the window is Debug");
    }

    [Fact]
    public async Task A_source_whose_completion_throws_anything_is_wrapped_and_the_original_is_logged_once()
    {
        CapturingLoggerProvider logs = new();
        using ILoggerFactory factory = logs.CreateFactory();
        ThrowingSource source = new() { Complete = new InvalidOperationException(HostWords) };
        StudioSourceResolver resolver = Resolver(source, factory);

        Func<Task> resolve = async () => await resolver.ResolveAsync(new StudioScope("tenants", "acme", null), cancellationToken: Token);
        StudioStoreUnavailableException wrapped = (await resolve.Should().ThrowAsync<StudioStoreUnavailableException>()).Which;

        wrapped.Message.Should().Contain("tenants").And.Contain(StudioLogValue.CapText(HostWords)).And.NotContain("\n");
        wrapped.InnerException.Should().BeOfType<InvalidOperationException>();

        logs.Entries.Where(static x => x.EventId.Id == 9219).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task A_source_that_completes_with_its_own_KeyNotFound_for_a_missing_tenant_is_not_rewritten()
    {
        ThrowingSource source = new() { Complete = new KeyNotFoundException("'t9' is not a tenant of database 'acme'.") };
        StudioSourceResolver resolver = Resolver(source, null);

        Func<Task> resolve = async () => await resolver.ResolveAsync(new StudioScope("tenants", "acme", "t9"), cancellationToken: Token);
        await resolve.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*not a tenant*");
    }

    [Fact]
    public async Task A_build_failures_reason_is_bounded_and_escaped()
    {
        ThrowingSource source = new() { BuildFailure = "down\r\nforged line " + new string('x', 6_000) };
        StudioSourceResolver resolver = Resolver(source, null);

        Func<Task> resolve = async () => await resolver.ResolveAsync(new StudioScope("tenants", "acme", null), cancellationToken: Token);
        string message = (await resolve.Should().ThrowAsync<StudioStoreUnavailableException>()).Which.Message;

        message.Any(StudioLogValue.IsEscaped).Should().BeFalse();
        message.Length.Should().BeLessThan(StudioLogValue.MaxTextLength + 200);
    }

    private static StudioSourceResolver Resolver(ThrowingSource source, ILoggerFactory? factory)
    {
        IStudioSettings settings = PostgresStudioSettings.Of(new PostgresStudioOptions());
        StudioAuthorization authorization = new(settings, PostgresStudioDescriptor.Instance, new TestSourceAuthorizationService(), new TestAuthenticationStateProvider());
        return new StudioSourceResolver(
            new ThrowingProvider(source),
            authorization,
            PostgresStudioDescriptor.Instance,
            factory?.CreateLogger<StudioSourceResolver>(),
            new StudioLogThrottle(),
            settings);
    }

    private sealed class ThrowingProvider(ThrowingSource source) : IStudioSourceProvider
    {
        private readonly StudioSourceRegistration registration = new("tenants", "Tenants", StudioSourceKind.HostProvider);

        public long Generation => 0;

        public IReadOnlyList<StudioSourceRegistration> Registrations() => [registration];

        public StudioSourceRegistration? Find(string? key) =>
            string.Equals(key, registration.Key, StringComparison.OrdinalIgnoreCase) ? registration : null;

        public StoreAvailability TryResolve(StudioSourceRegistration requested, [NotNullWhen(true)] out IStudioSource? resolved)
        {
            source.Registration = requested;
            resolved = source;
            return source.BuildFailure is { } failure
                ? StoreAvailability.Unavailable(failure, DateTimeOffset.UnixEpoch)
                : StoreAvailability.Available;
        }

        public void Invalidate()
        {
        }

        public IAsyncEnumerable<StudioListedDatabase> ListUnattendedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<StudioUnattendedResolution> ResolveUnattendedAsync(string sourceKey, string databaseId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingSource : IStudioSource
    {
        public Exception? List { get; init; }

        public Exception? Complete { get; init; }

        public string? BuildFailure { get; init; }

        public StudioSourceRegistration Registration { get; set; } = new("tenants", "Tenants", StudioSourceKind.HostProvider);

        public StudioDatabaseCardinality Cardinality => StudioDatabaseCardinality.StaticMultiple;

        public bool ShowsTenantSelector => true;

        public ValueTask<IReadOnlyList<IStudioDatabase>> DatabasesAsync(CancellationToken cancellationToken) =>
            List is { } failure ? throw failure : ValueTask.FromResult<IReadOnlyList<IStudioDatabase>>([new Database()]);

        public ValueTask<TenantList> DiscoverTenantsAsync(IStudioDatabase database, IReadOnlyList<IStudioDatabase> all, CancellationToken cancellationToken) =>
            ValueTask.FromResult(TenantList.Unavailable);

        public ValueTask<ResolvedSource> CompleteAsync(
            StudioScope scope,
            IStudioDatabase database,
            IReadOnlyList<IStudioDatabase> all,
            CancellationToken cancellationToken) =>
            Complete is { } failure ? throw failure : ValueTask.FromResult(new ResolvedSource(scope, Registration, database));
    }

    private sealed class Database : IStudioDatabase
    {
        public string Identity => "acme";

        public string Name => "acme";

        public string Server => "localhost";

        public Npgsql.NpgsqlDataSource? DataSource => null;

        public Npgsql.NpgsqlConnection CreateConnection(StudioConnectionUsage usage) => throw new NotSupportedException();
    }
}
