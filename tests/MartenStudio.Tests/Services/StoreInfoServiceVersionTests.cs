using System.Reflection;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Tests.Projections;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Tests.Services;

/// <summary>
/// The CL1 review, item 4: <see cref="StoreInfoService.HasDefaultDatabase" /> runs outside the version read's own guard,
/// so a host's own tenancy whose <c>Default</c> throws something other than <see cref="NotSupportedException" /> must not
/// throw out of the Overview. It is read as "has one", the read is attempted and fails as it always did, and that is
/// event 9214 at the throttled Warning - the behaviour before CL1.
/// </summary>
public class StoreInfoServiceVersionTests
{
    private const string Unreachable = "Host=cl1-fix-version.invalid;Database=none;Username=u;Password=p;Timeout=2";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void A_tenancy_whose_default_throws_otherwise_is_read_as_having_one()
    {
        StoreOptions options = new();
        options.Connection(Unreachable);

        ITenancy throwing = (ITenancy) (object) ThrowingDefaultTenancy.Over(options.Tenancy, new InvalidOperationException("a host's own tenancy"));

        StoreInfoService.HasDefaultDatabase(throwing).Should().BeTrue("anything but NotSupportedException is not an answer");

        ITenancy unsupported = (ITenancy) (object) ThrowingDefaultTenancy.Over(options.Tenancy, new NotSupportedException("needs a tenant"));
        StoreInfoService.HasDefaultDatabase(unsupported).Should().BeFalse("NotSupportedException is master-table's and sharded's own answer");
    }

    [Fact]
    public async Task The_overview_draws_a_store_whose_tenancy_default_throws_and_logs_the_version_as_before()
    {
        var users = new TestAuthenticationStateProvider();
        users.SignIn("tester");

        var logs = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddAuthorization();
        services.AddMarten(options =>
        {
            options.MultiTenantedDatabases(tenancy =>
                tenancy.AddMultipleTenantDatabase(Unreachable, "own").ForTenants("acme"));
            options.DatabaseSchemaName = "cl1_version";
            options.Tenancy = (ITenancy) (object) ThrowingDefaultTenancy.Over(options.Tenancy, new InvalidOperationException("a host's own tenancy"));
        });
        services.AddMartenStudio(static options => options.QueryTimeout = TimeSpan.FromSeconds(5));
        services.AddScoped<AuthenticationStateProvider>(_ => users);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        StudioOverview overview = await circuit.ServiceProvider.GetRequiredService<IStoreInfoService>().GetOverviewAsync(Token);

        StoreOverview store = overview.Stores.Should().ContainSingle().Subject;
        store.IsAvailable.Should().BeTrue();
        store.Databases.Should().ContainSingle().Which.Error.Should().BeNull("the stores region did not fail");
        store.PostgresVersion.Should().BeNull("the read was attempted and could not reach a default");

        logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9214)
            .Which.Level.Should().Be(LogLevel.Warning, "as before CL1: the first failure in its window");
    }
}

/// <summary>
/// Any tenancy whose <c>Default</c> throws what a test says, forwarding everything else - a host's own
/// <see cref="ITenancy" /> in miniature. Public and unsealed because a proxy type must be.
/// </summary>
public class ThrowingDefaultTenancy : DispatchProxy
{
    private ITenancy? inner;
    private Exception? failure;

    internal static ThrowingDefaultTenancy Over(ITenancy tenancy, Exception thrown)
    {
        ITenancy proxy = Create<ITenancy, ThrowingDefaultTenancy>();
        var throwing = (ThrowingDefaultTenancy) (object) proxy;
        throwing.inner = tenancy;
        throwing.failure = thrown;
        return throwing;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (targetMethod.Name == "get_Default")
        {
            throw failure!;
        }

        return UnlistableStore.Forward(targetMethod, inner!, args);
    }
}
