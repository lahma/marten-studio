using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// The Overview's server read remembers a version for the circuit, but a failure only for <c>HealthInterval</c> - and
/// the page's Refresh asks again at once - so a server that comes back is seen again.
/// </summary>
public class PostgresServerReaderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Func<IStudioDatabase, CancellationToken, Task<PostgresServerFacts>> read)
        {
            ServiceCollection services = new();
            services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=app;Username=app"));
            Root = services.BuildServiceProvider();

            PostgresStudioOptions options = new() { HealthInterval = TimeSpan.FromSeconds(60) };
            IStudioSettings settings = PostgresStudioSettings.Of(options);
            PostgresStudioSourceProvider provider = new(
                Microsoft.Extensions.Options.Options.Create(options),
                new PostgresSourceRegistry(services, Root, Time),
                NullLogger<PostgresStudioSourceProvider>.Instance);
            StudioAuthorization authorization = new(
                settings, PostgresStudioDescriptor.Instance, new TestSourceAuthorizationService(), new TestAuthenticationStateProvider());

            Reader = new PostgresServerReader(
                new StudioSourceResolver(provider, authorization, PostgresStudioDescriptor.Instance),
                settings,
                NullLogger<PostgresServerReader>.Instance,
                throttle: null,
                Time,
                read);
        }

        public ManualTimeProvider Time { get; } = new();

        public ServiceProvider Root { get; }

        public PostgresServerReader Reader { get; }

        public async ValueTask DisposeAsync() => await Root.DisposeAsync();
    }

    private static readonly StudioScope Scope = new("default", "default", null);

    private static PostgresServerFacts Facts => new(DateTimeOffset.UnixEpoch, 170004, "17.4", PostgresPlatform.SelfHosted);

    [Fact]
    public async Task A_failure_is_remembered_for_the_health_interval_and_then_the_server_is_asked_again()
    {
        int reads = 0;
        bool down = true;
        await using Harness harness = new((_, _) =>
        {
            reads++;
            return down ? throw new NpgsqlException("connection refused") : Task.FromResult(Facts);
        });

        (await harness.Reader.ReadAsync(Scope, cancellationToken: Token)).Failure.Should().Contain("connection refused");

        down = false;
        harness.Time.Advance(TimeSpan.FromSeconds(59));
        (await harness.Reader.ReadAsync(Scope, cancellationToken: Token)).Facts.Should().BeNull("a failure is remembered for the interval");
        reads.Should().Be(1);

        harness.Time.Advance(TimeSpan.FromSeconds(2));
        (await harness.Reader.ReadAsync(Scope, cancellationToken: Token)).Facts!.Version.Should().Be("17.4");
        reads.Should().Be(2);

        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Reader.ReadAsync(Scope, cancellationToken: Token);
        reads.Should().Be(2, "a version read is kept for the circuit");
    }

    [Fact]
    public async Task Refresh_asks_again_at_once_a_remembered_failure_included()
    {
        int reads = 0;
        bool down = true;
        await using Harness harness = new((_, _) =>
        {
            reads++;
            return down ? throw new NpgsqlException("connection refused") : Task.FromResult(Facts);
        });

        await harness.Reader.ReadAsync(Scope, cancellationToken: Token);
        down = false;

        (await harness.Reader.ReadAsync(Scope, refresh: true, Token)).Facts.Should().NotBeNull();
        reads.Should().Be(2);
    }
}
