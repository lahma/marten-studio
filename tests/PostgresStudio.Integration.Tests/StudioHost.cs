using System.Security.Claims;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Services;

namespace PostgresStudio.Integration.Tests;

/// <summary>
/// Postgres Studio's container exactly as <c>AddPostgresStudio()</c> builds it for a host with no Marten - the application's
/// own <see cref="Npgsql.NpgsqlDataSource" />s and database sources, the host's authorization - and circuit-shaped scopes
/// of it, each with a visitor of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the studio's own services.</b> Every live class here resolves what a page resolves - the database browser,
/// Sessions, Health, the console - from a DI scope the way a circuit does, so the scope resolution, the gates, the audit
/// and the caches are exercised rather than bypassed.
/// </para>
/// <para>
/// <b>The two resource policies are predicates</b> over <see cref="PostgresStudioResource" /> and the visitor, registered
/// as <see cref="SourcePolicy" /> and <see cref="WritePolicy" /> only when a test passes one, and every question either is
/// asked is recorded in <see cref="Calls" />. The visitor is per circuit (<see cref="CircuitAsync" />), so two visitors in
/// one process share every singleton cache the studio has - which is what an isolation test needs.
/// </para>
/// </remarks>
internal sealed class StudioHost : IAsyncDisposable
{
    /// <summary>The source policy's name.</summary>
    public const string SourcePolicy = "test-source";

    /// <summary>The write policy's name.</summary>
    public const string WritePolicy = "test-write";

    private readonly ServiceProvider provider;

    private StudioHost(ServiceProvider provider, LogCapture logs, PolicyCalls calls)
    {
        this.provider = provider;
        Logs = logs;
        Calls = calls;
    }

    /// <summary>What the studio logged, its own categories only.</summary>
    public LogCapture Logs { get; }

    /// <summary>Every question either policy was asked, in order.</summary>
    public PolicyCalls Calls { get; }

    /// <summary>The root container, for a singleton a test reads.</summary>
    public IServiceProvider Services => provider;

    /// <summary>The audit ring.</summary>
    public StudioActionLog Ring => provider.GetRequiredService<StudioActionLog>();

    /// <summary>Builds the container.</summary>
    /// <param name="services">The application's own registrations: its data sources, a clock, an observer.</param>
    /// <param name="studio">Postgres Studio's options.</param>
    /// <param name="sourcePolicy">The source policy, or <see langword="null" /> for none (every source passes).</param>
    /// <param name="writePolicy">The write policy, or <see langword="null" /> for none (every capability passes).</param>
    public static StudioHost Create(
        Action<IServiceCollection> services,
        Action<PostgresStudioOptions>? studio = null,
        Func<PostgresStudioResource, ClaimsPrincipal, bool>? sourcePolicy = null,
        Func<PostgresStudioResource, ClaimsPrincipal, bool>? writePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        LogCapture logs = new();
        PolicyCalls calls = new();
        ServiceCollection collection = new();

        collection.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(logs);
        });

        collection.AddAuthorization(options =>
        {
            if (sourcePolicy is not null)
            {
                options.AddPolicy(SourcePolicy, policy => policy.AddRequirements(new PredicateRequirement(SourcePolicy, sourcePolicy, calls)));
            }

            if (writePolicy is not null)
            {
                options.AddPolicy(WritePolicy, policy => policy.AddRequirements(new PredicateRequirement(WritePolicy, writePolicy, calls)));
            }
        });

        collection.AddSingleton<IAuthorizationHandler, PredicateHandler>();
        services(collection);

        collection.AddPostgresStudio(options =>
        {
            if (sourcePolicy is not null)
            {
                options.SourceAuthorizationPolicy = SourcePolicy;
            }

            if (writePolicy is not null)
            {
                options.WriteAuthorizationPolicy = WritePolicy;
            }

            studio?.Invoke(options);
        });

        // After AddPostgresStudio, so the visitor is this one whatever the studio's own registrations add: one per
        // circuit, as Blazor's is.
        collection.AddScoped<TestAuthenticationStateProvider>();
        collection.AddScoped<AuthenticationStateProvider>(static x => x.GetRequiredService<TestAuthenticationStateProvider>());

        return new StudioHost(collection.BuildServiceProvider(), logs, calls);
    }

    /// <summary>A circuit-shaped scope, signed in as <paramref name="user" /> with <paramref name="claims" />.</summary>
    public StudioCircuit Circuit(string user = "admin", params (string Type, string Value)[] claims)
    {
        AsyncServiceScope scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestAuthenticationStateProvider>().SignIn(user, claims);
        return new StudioCircuit(scope);
    }

    /// <summary>Runs <paramref name="action" /> in a circuit of its own and disposes it.</summary>
    public async Task<T> CircuitAsync<T>(string user, Func<IServiceProvider, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using StudioCircuit circuit = Circuit(user);
        return await action(circuit.Services);
    }

    public ValueTask DisposeAsync() => provider.DisposeAsync();

    /// <summary>One policy's requirement: the predicate the test gave it.</summary>
    private sealed class PredicateRequirement(
        string policy,
        Func<PostgresStudioResource, ClaimsPrincipal, bool> allow,
        PolicyCalls calls) : IAuthorizationRequirement
    {
        public string Policy { get; } = policy;

        public Func<PostgresStudioResource, ClaimsPrincipal, bool> Allow { get; } = allow;

        public PolicyCalls Calls { get; } = calls;
    }

    /// <summary>Answers either requirement from the resource the studio passes, and records the question.</summary>
    private sealed class PredicateHandler : AuthorizationHandler<PredicateRequirement, PostgresStudioResource>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PredicateRequirement requirement,
            PostgresStudioResource resource)
        {
            bool allowed = requirement.Allow(resource, context.User);
            requirement.Calls.Add(new PolicyCall(requirement.Policy, context.User.Identity?.Name, resource, allowed));

            if (allowed)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary>One circuit: a DI scope with a visitor of its own.</summary>
internal sealed class StudioCircuit(AsyncServiceScope scope) : IAsyncDisposable
{
    /// <summary>The circuit's services.</summary>
    public IServiceProvider Services => scope.ServiceProvider;

    /// <summary>One of the circuit's services.</summary>
    public T Get<T>()
        where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}

/// <summary>One question a policy was asked, and its answer.</summary>
internal sealed record PolicyCall(string Policy, string? User, PostgresStudioResource Resource, bool Allowed);

/// <summary>Every question the host's policies were asked.</summary>
internal sealed class PolicyCalls
{
    private readonly List<PolicyCall> calls = [];
    private readonly Lock gate = new();

    /// <summary>A copy, in order.</summary>
    public IReadOnlyList<PolicyCall> All
    {
        get
        {
            lock (gate)
            {
                return [.. calls];
            }
        }
    }

    public void Add(PolicyCall call)
    {
        lock (gate)
        {
            calls.Add(call);
        }
    }
}

/// <summary>One line the studio logged.</summary>
internal sealed record StudioLogLine(string Category, LogLevel Level, EventId EventId, string Message);

/// <summary>Captures what Postgres Studio logs - its own categories only, at every level.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly List<StudioLogLine> lines = [];
    private readonly Lock gate = new();

    /// <summary>Everything logged so far, in order.</summary>
    public IReadOnlyList<StudioLogLine> Lines
    {
        get
        {
            lock (gate)
            {
                return [.. lines];
            }
        }
    }

    /// <summary>Every line at Warning or above.</summary>
    public IReadOnlyList<StudioLogLine> WarningsOrWorse => [.. Lines.Where(static x => x.Level >= LogLevel.Warning)];

    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("PostgresStudio", StringComparison.Ordinal)
            ? new Capturing(this, categoryName)
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
    }

    private void Add(StudioLogLine line)
    {
        lock (gate)
        {
            lines.Add(line);
        }
    }

    private sealed class Capturing(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Add(new StudioLogLine(category, logLevel, eventId, formatter(state, exception)));
    }
}
