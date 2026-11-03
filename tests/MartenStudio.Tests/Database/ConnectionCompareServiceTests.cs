using System.Collections.Concurrent;
using System.Text.Json;

using Marten;

using MartenStudio.Tests.Health;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Services;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Database;

/// <summary>
/// "Compare a connection string" (1.1-PASTE), through a real studio container: the page's gate asked first and with no tenant, the
/// text never looked at for a visitor it refused, nothing audited, nothing logged, and no sentinel in anything that comes back.
/// The same service serves Postgres Studio; this is the Marten Studio wording of it.
/// </summary>
public class ConnectionCompareServiceTests
{
    private const string Sentinel = "S3NT1NEL";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static readonly JsonSerializerOptions Dump = new() { IncludeFields = true, MaxDepth = 64 };

    private const string Pasted = "Host=other.example;Database=app;Username=svc;SSL Mode=VerifyFull;Application Name=billing-api;"
        + "Password=" + Sentinel + "-pw;PWD=" + Sentinel + "-pwd;Pwd=" + Sentinel + "-Pwd;SSL Password=" + Sentinel + "-ssl;"
        + "Options='-c app.secret=" + Sentinel + "-opt -c search_path=" + Sentinel + "-sp'";

    [Fact]
    public async Task A_visitor_the_store_policy_refuses_is_told_so_and_nothing_is_read_or_parsed()
    {
        await using Harness harness = Harness.Create(policies: true);
        harness.Policies.Allow(static resource => resource.TenantId is not null);

        ConnectionCompareView view = await harness.Service.CompareAsync(Scope with { TenantId = "acme" }, Pasted, Token);

        view.Outcome.Should().Be(ConnectionCompareOutcome.Refused, "a connection belongs to no tenant, so the policy is asked without one");
        view.Reason.Should().Contain("MartenStudioOptions.StoreAuthorizationPolicy");
        view.Rows.Should().BeEmpty();
        harness.Facts.Reads.Should().Be(0, "nothing is read for a visitor the gate refused");
        harness.Ring.Should().BeEmpty();
    }

    [Fact]
    public async Task A_visitor_through_the_gate_gets_the_comparison_and_the_policy_is_asked_with_no_tenant()
    {
        await using Harness harness = Harness.Create(policies: true);

        ConnectionCompareView view = await harness.Service.CompareAsync(Scope with { TenantId = "acme" }, Pasted, Token);

        view.Outcome.Should().Be(ConnectionCompareOutcome.Compared);
        harness.Policies.Calls.Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.TenantId == null);
        harness.Facts.Reads.Should().Be(1, "the connection in effect is the page's own cached read");

        view.Rows.Single(static x => x.Keyword == "Host").Pasted.Value.Should().Be("other.example");
        view.Rows.Single(static x => x.Keyword == "Host").Differs.Should().BeTrue();
        view.Rows.Single(static x => x.Keyword == "SSL Mode").Pasted.Value.Should().Be("VerifyFull");
        view.RemovedSecrets.Should().Equal("Password", "SSL Password");
    }

    [Fact]
    public async Task Nothing_to_compare_is_said_before_the_server_is_asked_anything()
    {
        await using Harness harness = Harness.Create();

        (await harness.Service.CompareAsync(Scope, "   ", Token)).Outcome.Should().Be(ConnectionCompareOutcome.Empty);
        (await harness.Service.CompareAsync(Scope, null, Token)).Outcome.Should().Be(ConnectionCompareOutcome.Empty);
        (await harness.Service.CompareAsync(Scope, new string('x', PastedConnectionString.MaxCharacters + 1), Token)).Outcome.Should().Be(ConnectionCompareOutcome.TooLong);
        harness.Facts.Reads.Should().Be(0);
    }

    [Fact]
    public async Task A_current_connection_that_cannot_be_read_is_a_value_in_the_studios_words_and_never_the_exceptions()
    {
        await using Harness harness = Harness.Create();
        harness.Facts.Throws = new Npgsql.NpgsqlException("password authentication failed for user svc with " + Sentinel + "-live");

        ConnectionCompareView view = await harness.Service.CompareAsync(Scope, Pasted, Token);

        view.Outcome.Should().Be(ConnectionCompareOutcome.Unavailable);
        view.Reason.Should().NotContain(Sentinel).And.NotContain("svc");
    }

    [Fact]
    public async Task Refusals_name_a_keyword_and_the_unreadable_say_only_that_npgsql_could_not_read_it()
    {
        await using Harness harness = Harness.Create();

        ConnectionCompareView rejected = await harness.Service.CompareAsync(Scope, "Host=a;Bogus Keyword=" + Sentinel + ";Password=" + Sentinel, Token);
        rejected.Outcome.Should().Be(ConnectionCompareOutcome.Rejected);
        rejected.Reason.Should().Be("Npgsql does not know the keyword \"Bogus Keyword\".");

        ConnectionCompareView unreadable = await harness.Service.CompareAsync(Scope, "postgresql://u:" + Sentinel + "@db.example/app?sslmode=require", Token);
        unreadable.Outcome.Should().Be(ConnectionCompareOutcome.Unreadable);
        unreadable.Reason.Should().Be("Npgsql could not read this connection string.");

        Harness.Everything(rejected, unreadable).Should().NotContain(Sentinel);
    }

    [Fact]
    public async Task The_sentinel_is_in_no_model_no_log_line_at_trace_and_no_audit_entry()
    {
        await using Harness harness = Harness.Create(policies: true);
        harness.Facts.CurrentPasswordIsInTheText = true;

        ConnectionCompareView compared = await harness.Service.CompareAsync(Scope, Pasted, Token);
        ConnectionCompareView rejected = await harness.Service.CompareAsync(Scope, Pasted + ";Not A Keyword=" + Sentinel + "-bad", Token);
        ConnectionCompareView badValue = await harness.Service.CompareAsync(Scope, Pasted + ";Port=" + Sentinel + "-port", Token);
        ConnectionCompareView unreadable = await harness.Service.CompareAsync(Scope, Pasted + ";" + Sentinel + "-no-equals", Token);

        compared.Outcome.Should().Be(ConnectionCompareOutcome.Compared);
        rejected.Outcome.Should().Be(ConnectionCompareOutcome.Rejected);
        badValue.Outcome.Should().Be(ConnectionCompareOutcome.Rejected);
        unreadable.Outcome.Should().Be(ConnectionCompareOutcome.Unreadable);

        string models = Harness.Everything(compared, rejected, badValue, unreadable);
        models.Should().NotContain(Sentinel);
        models.Should().Contain("other.example", "the dump is not vacuous: a value that may be shown is in it");
        models.Should().Contain("Not A Keyword", "a refusal names the keyword");

        // The capture is wired, at Trace: a line logged through the container's own factory lands in it.
        harness.Provider.GetRequiredService<ILoggerFactory>().CreateLogger("probe").LogTrace("probe line");
        harness.Log.Lines.Should().Contain("probe: probe line");
        harness.Log.Lines.Should().NotContain(static x => x.Contains(Sentinel, StringComparison.Ordinal), "no level, Trace included, ever sees the text");
        harness.Ring.Should().BeEmpty("a comparison reads nothing and runs nothing, so it is not audited");
    }

    // ----------------------------------------------------------------------------------------------

    private sealed class CapturingLogger : ILoggerProvider
    {
        public ConcurrentQueue<string> Queue { get; } = new();

        public IReadOnlyList<string> Lines => [.. Queue];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLogger owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Queue.Enqueue(category + ": " + formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }

    private sealed class FakeFacts : IConnectionFactsSource
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public Exception? Throws { get; set; }

        public bool CurrentPasswordIsInTheText { get; set; }

        public Task<ConnectionFacts> GetAsync(ResolvedSource resolved, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref reads);

            if (Throws is not null)
            {
                return Task.FromException<ConnectionFacts>(Throws);
            }

            return Task.FromResult(new ConnectionFacts
            {
                Settings = NpgsqlSettingsReader.Read(
                    "Host=db.example;Database=app;Username=svc;Application Name=billing-api;SSL Mode=Prefer;"
                    + (CurrentPasswordIsInTheText ? "Password=" + Sentinel + "-live;Persist Security Info=true" : string.Empty),
                    new ConnectionReadContext()),
                ReadAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero),
                Observed = new ConnectionObserved(
                    "svc", "svc", "app", "10.0.0.5", 5432, false, "UTC", "UTF8", "public", "PostgresStudio",
                    0, 0, 0, null, "scram-sha-256", true, "TLSv1.3", "TLS_AES_256_GCM_SHA384", 256, null, null,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            });
        }

        public Task<ConnectionApplicationsRead> ReadApplicationsAsync(ResolvedSource resolved, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The comparison never reads the application's sessions.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly AsyncServiceScope circuit;

        private Harness(ServiceProvider provider, TestStoreAuthorizationService policies, FakeFacts facts, CapturingLogger log)
        {
            Provider = provider;
            Policies = policies;
            Facts = facts;
            Log = log;
            circuit = provider.CreateAsyncScope();
        }

        public ServiceProvider Provider { get; }

        public TestStoreAuthorizationService Policies { get; }

        public FakeFacts Facts { get; }

        public CapturingLogger Log { get; }

        public IConnectionCompareService Service => circuit.ServiceProvider.GetRequiredService<IConnectionCompareService>();

        public IReadOnlyList<StudioActionLogEntry> Ring => Provider.GetRequiredService<StudioActionLogService>().GetLatest();

        public static string Everything(params ConnectionCompareView[] views) =>
            string.Join('\n', views.Select(static x => JsonSerializer.Serialize(x, Dump) + x.Reason + string.Join('\n', x.Rows.Select(static r => r.Keyword + r.Pasted.Value + r.Pasted.Note))));

        public static Harness Create(bool policies = false)
        {
            TestAuthenticationStateProvider users = new();
            users.SignIn("operator");

            TestStoreAuthorizationService authorization = new();
            FakeFacts facts = new();
            CapturingLogger log = new();

            ServiceCollection services = new();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
            services.AddMarten(static options =>
            {
                options.Connection(HealthHarness.RefusedConnectionString);
                options.DatabaseSchemaName = "connection_compare_unit";
            });

            services.AddMartenStudio(options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();

                if (policies)
                {
                    options.StoreAuthorizationPolicy = "store-policy";
                    options.WriteAuthorizationPolicy = "write-policy";
                }
            });

            services.AddSingleton<IAuthorizationService>(authorization);
            services.AddScoped<AuthenticationStateProvider>(_ => users);
            services.AddSingleton<IConnectionFactsSource>(facts);

            return new Harness(services.BuildServiceProvider(), authorization, facts, log);
        }

        public async ValueTask DisposeAsync()
        {
            await circuit.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }
}
