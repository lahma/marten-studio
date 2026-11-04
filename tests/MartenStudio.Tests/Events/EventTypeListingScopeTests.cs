using System.Security.Claims;

using Marten;

using MartenStudio.Services.Events;
using MartenStudio.Tests.Sql;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The shared counted listing under a store policy and the <b>real</b> scoped <c>ServerAuthenticationStateProvider</c>: its user is set in
/// each visitor's own scope only, and it throws everywhere else (a Blazor Server host's provider refuses a call "outside of the DI scope
/// for a Razor component"). Every rig that shares one provider across scopes passes whether or not the shared read resolves again in a
/// scope of its own - this one does not.
/// </summary>
public sealed class EventTypeListingScopeTests : IAsyncDisposable
{
    private const string Policy = "StoreOwners";

    private readonly ServiceProvider provider;
    private int reads;

    public EventTypeListingScopeTests()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMarten(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = "listing_scope";
        });

        // Before AddMartenStudio, whose registration only fills what is missing: the counted read is a fake, there is no database.
        services.AddSingleton(sp => new EventTypeListingCache(
            sp.GetRequiredService<ColumnCatalog>(),
            sp.GetRequiredService<IOptions<MartenStudioOptions>>(),
            TimeProvider.System,
            (_, _) =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(new EventTypeList([new EventTypeInfo("order_placed", "Shop.OrderPlaced", true, 5, 1, 5)], CountsLoaded: true));
            }));

        services.AddMartenStudio(options => options.StoreAuthorizationPolicy = Policy);

        // After it, so these win: the policy knows people by name, and the provider is the framework's own, scoped per visitor.
        services.AddSingleton<IAuthorizationService>(new NamedVisitors("alice", "bob"));
        services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();

        provider = services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => provider.DisposeAsync();

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private async Task<EventTypeList> ListAsync(string visitor, bool withCounts = true)
    {
        await using AsyncServiceScope own = provider.CreateAsyncScope();

        // The circuit sets the user in its own scope; no other scope has one.
        ((IHostEnvironmentAuthenticationStateProvider) own.ServiceProvider.GetRequiredService<AuthenticationStateProvider>())
            .SetAuthenticationState(Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, visitor)], "test")))));

        return await own.ServiceProvider.GetRequiredService<IEventDataService>()
            .ListEventTypesAsync(Scope, withCounts, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Load_counts_works_for_a_visitor_who_passes_the_policy_and_a_second_visitor_is_handed_the_same_listing()
    {
        EventTypeList first = await ListAsync("alice");
        EventTypeList second = await ListAsync("bob");

        first.Error.Should().BeNull("the shared read resolves nothing: it needs no user");
        first.CountsLoaded.Should().BeTrue();
        first.Types.Should().ContainSingle().Which.Count.Should().Be(5);
        second.Should().BeSameAs(first);
        reads.Should().Be(1, "one group-by, however many visitors");
    }

    [Fact]
    public async Task A_visitor_the_policy_refuses_is_refused_before_the_cache_and_never_handed_what_another_read()
    {
        (await ListAsync("alice")).Error.Should().BeNull();

        EventTypeList refused = await ListAsync("mallory");

        refused.Error.Should().NotBeNull();
        refused.Error!.Display.Should().Be("Not authorized for this store, database or tenant.");
        refused.Types.Should().BeEmpty();
    }

    [Fact]
    public async Task The_arrangement_is_real_a_scope_nobody_set_a_user_in_cannot_be_asked_who_is_there()
    {
        await using AsyncServiceScope circuitless = provider.CreateAsyncScope();

        await FluentActions.Awaiting(() => circuitless.ServiceProvider.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync())
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task The_uncounted_listing_reads_nothing()
    {
        EventTypeList list = await ListAsync("alice", withCounts: false);

        list.Error.Should().BeNull();
        list.CountsLoaded.Should().BeFalse();
        reads.Should().Be(0);
    }

    /// <summary>An authorization service that passes the named visitors and no one else.</summary>
    private sealed class NamedVisitors(params string[] names) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(Decide(user));

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(Decide(user));

        private AuthorizationResult Decide(ClaimsPrincipal user) =>
            user.Identity?.Name is { } name && names.Contains(name, StringComparer.Ordinal)
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed();
    }
}
