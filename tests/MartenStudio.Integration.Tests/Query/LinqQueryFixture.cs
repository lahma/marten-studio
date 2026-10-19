using System.Runtime.CompilerServices;
using System.Security.Claims;

using JasperFx;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Query;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Weasel.Core;

namespace MartenStudio.Integration.Tests.Query;

public enum LinqLiveStatus
{
    Open,
    Shipped,
    Cancelled,
}

public sealed class LinqLiveCustomer
{
    public string Name { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;
}

/// <summary>Conjoined, soft-deleted, with one duplicated field: the shape a cross-tenant leak would show on.</summary>
public sealed class LinqLiveOrder
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Total { get; set; }

    public LinqLiveStatus Status { get; set; }

    public LinqLiveCustomer Customer { get; set; } = new();

    public DateTime PlacedAt { get; set; }

    public string[] Tags { get; set; } = [];
}

/// <summary>The root of a hierarchy: one table, told apart by <c>mt_doc_type</c>.</summary>
public abstract class LinqLiveVehicle
{
    public Guid Id { get; set; }

    public string Make { get; set; } = string.Empty;

    public int Year { get; set; }
}

public sealed class LinqLiveCar : LinqLiveVehicle
{
    public int Doors { get; set; }
}

public sealed class LinqLiveTruck : LinqLiveVehicle
{
    public decimal Payload { get; set; }
}

/// <summary>Single-tenanted: nothing narrows it by tenant, so a tenant scope reaches all of it.</summary>
public sealed class LinqLiveNote
{
    public Guid Id { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>Registered and never stored: a type whose table nothing in this process has created.</summary>
public sealed class LinqLiveUntouched
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Hidden with <c>IsDocumentTypeVisible</c>.</summary>
public sealed class LinqLiveHidden
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;
}

/// <summary>Whether this run can drive LINQ mode live: Docker, and a Marten with DynamicQuery (9.48+).</summary>
public static class LinqLiveAvailability
{
    public const string SkipReason =
        "Needs Docker and a Marten with JasperFx's DynamicQuery (9.48+). At the floor run the suite with " +
        "-p:MartenStudioMartenVersion=9.50.2; without Docker, start it and run `docker info`.";

    public static bool CanRun => DockerAvailability.IsAvailable && DynamicLinqAdapter.Current is not null && MartenLinqCommand.IsAvailable;
}

/// <summary>A live LINQ-mode fact: skipped at the floor and without Docker.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LinqLiveFactAttribute : FactAttribute
{
    public LinqLiveFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = LinqLiveAvailability.SkipReason;
        SkipType = typeof(LinqLiveAvailability);
        SkipUnless = nameof(LinqLiveAvailability.CanRun);
    }
}

/// <summary>
/// One store, one schema, the shapes LINQ mode has to get right - driven through the registered
/// <see cref="ILinqQueryService" />, so the gating is exercised rather than bypassed.
/// </summary>
internal sealed class LinqQueryHarness : IAsyncDisposable
{
    public const string Acme = "acme";
    public const string Globex = "globex";

    /// <summary>Allows <see cref="Acme" /> and nothing wider - not the database as a whole, not another tenant.</summary>
    public const string AcmeOnlyPolicy = "linq-acme-only";

    public static readonly Guid AcmeAda = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid AcmeBob = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    public static readonly Guid AcmeDeleted = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    public static readonly Guid GlobexAda = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    public static readonly Guid GlobexDeleted = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    public static readonly Guid Car = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    public static readonly Guid Truck = Guid.Parse("c0000000-0000-0000-0000-000000000002");

    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;

    private LinqQueryHarness(ServiceProvider provider, IServiceScope scope, string schema, HarnessLogCollector logs)
    {
        this.provider = provider;
        this.scope = scope;
        Schema = schema;
        Logs = logs;
    }

    public string Schema { get; }

    public HarnessLogCollector Logs { get; }

    public ILinqQueryService Linq => scope.ServiceProvider.GetRequiredService<ILinqQueryService>();

    public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

    public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

    public static StudioScope AllTenants { get; } = new(MartenStoreRegistry.DefaultStoreKey, string.Empty, null);

    public static StudioScope For(string tenantId) => new(MartenStoreRegistry.DefaultStoreKey, string.Empty, tenantId);

    public static async Task<LinqQueryHarness> CreateAsync(PostgresFixture fixture, string schema, Action<MartenStudioOptions>? configure = null)
    {
        await fixture.CreateSchemaAsync(schema);

        var services = new ServiceCollection();
        var logs = new HarnessLogCollector();

        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddAuthorization(options => options.AddPolicy(AcmeOnlyPolicy, policy => policy.RequireAssertion(
            static context => context.Resource is MartenStoreResource { TenantId: Acme })));
        services.AddSingleton<AuthenticationStateProvider, SignedIn>();

        services.AddMarten(options =>
        {
            options.Connection(fixture.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema + "_events";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.UseSystemTextJsonForSerialization(EnumStorage.AsString);
            options.Schema.For<LinqLiveOrder>().MultiTenanted().SoftDeleted().Duplicate(x => x.Name);
            options.Schema.For<LinqLiveVehicle>().AddSubClassHierarchy(typeof(LinqLiveCar), typeof(LinqLiveTruck));
            options.Schema.For<LinqLiveNote>();
            options.Schema.For<LinqLiveUntouched>();
            options.Schema.For<LinqLiveHidden>();
        });

        services.AddMartenStudio(options =>
        {
            options.Capabilities.RunLinqQueries = true;
            options.QueryTimeout = TimeSpan.FromSeconds(30);
            options.IsDocumentTypeVisible = static type => type != typeof(LinqLiveHidden);
            configure?.Invoke(options);
        });

        ServiceProvider provider = services.BuildServiceProvider();
        var harness = new LinqQueryHarness(provider, provider.CreateScope(), schema, logs);

        await harness.SeedAsync();

        return harness;
    }

    private async Task SeedAsync()
    {
        await SeedOrdersAsync(Acme,
            new LinqLiveOrder { Id = AcmeAda, Name = "Ada", Total = 150m, Status = LinqLiveStatus.Open, PlacedAt = new DateTime(2024, 6, 1), Customer = new() { Name = "Ada", City = "London" }, Tags = ["vip"] },
            new LinqLiveOrder { Id = AcmeBob, Name = "Bob", Total = 50m, Status = LinqLiveStatus.Shipped, PlacedAt = new DateTime(2023, 6, 1), Customer = new() { Name = "Bob", City = "Leeds" } });
        await SeedOrdersAsync(Globex,
            new LinqLiveOrder { Id = GlobexAda, Name = "Ada", Total = 900m, Status = LinqLiveStatus.Open, PlacedAt = new DateTime(2024, 6, 1), Customer = new() { Name = "Ada", City = "Paris" }, Tags = ["vip"] });

        await DeletedOrderAsync(Acme, AcmeDeleted);
        await DeletedOrderAsync(Globex, GlobexDeleted);

        await using IDocumentSession session = Store.LightweightSession();
        session.Store<LinqLiveVehicle>(new LinqLiveCar { Id = Car, Make = "Volvo", Year = 2020, Doors = 4 });
        session.Store<LinqLiveVehicle>(new LinqLiveTruck { Id = Truck, Make = "Volvo", Year = 2018, Payload = 12m });
        session.Store(new LinqLiveNote { Id = Guid.NewGuid(), Text = "shared" });
        session.Store(new LinqLiveHidden { Id = Guid.NewGuid(), Code = "secret" });
        await session.SaveChangesAsync();
    }

    private async Task SeedOrdersAsync(string tenant, params LinqLiveOrder[] orders)
    {
        await using IDocumentSession session = Store.LightweightSession(tenant);
        session.Store(orders);
        await session.SaveChangesAsync();
    }

    private async Task DeletedOrderAsync(string tenant, Guid id)
    {
        await using (IDocumentSession session = Store.LightweightSession(tenant))
        {
            session.Store(new LinqLiveOrder { Id = id, Name = "Ada", Total = 500m, Status = LinqLiveStatus.Cancelled, Customer = new() { Name = "Ada" } });
            await session.SaveChangesAsync();
        }

        await using IDocumentSession remover = Store.LightweightSession(tenant);
        remover.Delete<LinqLiveOrder>(id);
        await remover.SaveChangesAsync();
    }

    /// <summary>The alias Marten gave a type, read the way the page reads it.</summary>
    public async Task<string> AliasOfAsync<T>(StudioScope? on = null)
    {
        LinqModeStatus status = await Linq.DescribeAsync(on ?? AllTenants, TestContext.Current.CancellationToken);

        return status.Types.Single(static x => x.TypeName == typeof(T).Name).Alias;
    }

    public async ValueTask DisposeAsync()
    {
        scope.Dispose();
        await provider.DisposeAsync();
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ops")], "test"))));
    }
}
