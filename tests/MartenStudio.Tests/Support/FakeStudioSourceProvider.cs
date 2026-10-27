using System.Diagnostics.CodeAnalysis;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Support;

/// <summary>
/// Sources for a test that needs the shared resolver (<see cref="StudioSourceResolver" />) - its two questions, by the
/// visitor's spelling and by the registered names - and nothing behind it: no Marten store, no data source, no database.
/// </summary>
/// <remarks>
/// Registrations by key, each with databases by identity, found and matched as the real providers find them: a key and an
/// identity ignoring case, an empty identity the first database. Any tenant completes, because the tests that use this are
/// about who is asked what, not about tenancy. Nothing here opens anything; what a sweep with no visitor would list is not
/// supported.
/// </remarks>
internal sealed class FakeStudioSourceProvider : IStudioSourceProvider
{
    private readonly List<FakeSource> sources = [];

    /// <summary>Adds a source with <paramref name="databaseIdentities" />, in order: the first is an empty <c>?db=</c>'s.</summary>
    public FakeStudioSourceProvider WithSource(string key, params string[] databaseIdentities)
    {
        sources.Add(new FakeSource(
            new StudioSourceRegistration(key, key, StudioSourceKind.DataSource),
            [.. databaseIdentities.Select(static x => (IStudioDatabase) new FakeDatabase(x))]));

        return this;
    }

    /// <summary>How many times a source was asked for its databases - what a resolution costs past the policy.</summary>
    public int DatabaseListings => sources.Sum(static x => x.Listings);

    /// <summary>The resolver a circuit over these sources asks with <paramref name="authorization" />.</summary>
    public StudioSourceResolver Resolver(StudioAuthorization authorization) => new(this, authorization, MartenSpine.Descriptor);

    /// <inheritdoc />
    public long Generation { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<StudioSourceRegistration> Registrations() => [.. sources.Select(static x => x.Registration)];

    /// <inheritdoc />
    public StudioSourceRegistration? Find(string? key) =>
        sources.FirstOrDefault(x => string.Equals(x.Registration.Key, key, StringComparison.OrdinalIgnoreCase))?.Registration;

    /// <inheritdoc />
    public StoreAvailability TryResolve(StudioSourceRegistration registration, [NotNullWhen(true)] out IStudioSource? source)
    {
        source = sources.FirstOrDefault(x => x.Registration == registration);
        return source is null ? StoreAvailability.Unavailable("not one of this fake's sources", DateTimeOffset.UnixEpoch) : StoreAvailability.Available;
    }

    /// <inheritdoc />
    /// <remarks>Nothing is cached here, so the scope selector's refresh only moves <see cref="Generation" />.</remarks>
    public void Invalidate() => Generation++;

    /// <inheritdoc />
    public IAsyncEnumerable<StudioListedDatabase> ListUnattendedAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("A visitor's resolution never lists unattended.");

    /// <inheritdoc />
    public ValueTask<StudioUnattendedResolution> ResolveUnattendedAsync(string sourceKey, string databaseId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A visitor's resolution never resolves unattended.");

    private sealed class FakeSource(StudioSourceRegistration registration, IReadOnlyList<IStudioDatabase> databases) : IStudioSource
    {
        public int Listings { get; private set; }

        public StudioSourceRegistration Registration { get; } = registration;

        public StudioDatabaseCardinality Cardinality => databases.Count > 1 ? StudioDatabaseCardinality.StaticMultiple : StudioDatabaseCardinality.Single;

        public bool ShowsTenantSelector => true;

        public ValueTask<IReadOnlyList<IStudioDatabase>> DatabasesAsync(CancellationToken cancellationToken)
        {
            Listings++;
            return ValueTask.FromResult(databases);
        }

        public ValueTask<TenantList> DiscoverTenantsAsync(IStudioDatabase database, IReadOnlyList<IStudioDatabase> all, CancellationToken cancellationToken) =>
            ValueTask.FromResult(TenantList.Unavailable);

        public ValueTask<ResolvedSource> CompleteAsync(
            StudioScope scope,
            IStudioDatabase database,
            IReadOnlyList<IStudioDatabase> all,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ResolvedSource(scope, Registration, database));
    }

    private sealed class FakeDatabase(string identity) : IStudioDatabase
    {
        public string Identity { get; } = identity;

        public string Name => Identity;

        public string Server => "localhost";

        public NpgsqlDataSource? DataSource => null;

        public NpgsqlConnection CreateConnection(StudioConnectionUsage usage) =>
            throw new NotSupportedException("This fake's databases are never opened.");
    }
}
