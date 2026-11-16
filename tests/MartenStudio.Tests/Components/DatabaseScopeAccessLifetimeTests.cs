using Bunit;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Components;

/// <summary>
/// Shell A2b (the review of A2a, F1): the scope gate's answer is the circuit's, and a layout is not. <c>StudioRoutes</c> renders a matched page and
/// "Not found" through two different branches, so the whole layout is re-created when the circuit goes from one to the other - a new circuit host and
/// a new gate in front of the same <see cref="DatabaseScopeAccess" />. The new gate must show the spinner until its own host has answered, and the
/// old host, disposed with a policy question in flight, must not overwrite that answer when its own arrives.
/// </summary>
public class DatabaseScopeAccessLifetimeTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_re_created_layouts_gate_waits_for_its_own_host_and_the_old_hosts_late_answer_changes_nothing()
    {
        ClickableNavigationManager navigation = new();

        await using StudioComponentContext context = new(services =>
        {
            services.AddSingleton<NavigationManager>(navigation);
            services.AddSingleton<INavigationInterception, QuietNavigationInterception>();
            services.AddSingleton<IScrollToLocationHash, QuietScrollToLocationHash>();
        });

        context.Options.RefreshInterval = TimeSpan.FromMinutes(5);
        context.StoreInfo.WithStore("default", "Default", databaseIdentity: "db-a").WithStore("other", "Other", databaseIdentity: "db-x");
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "db-a")
            .WithStore("other", "Other", databaseIdentities: "db-x");
        context.WithPolicy("default", "other");

        DatabaseScopeAccess access = context.Services.GetRequiredService<DatabaseScopeAccess>();

        // The first layout, on the Overview: its host asked the policy and was admitted.
        IRenderedComponent<StudioRoutes> routes = context.Render<StudioRoutes>();
        routes.WaitForAssertion(() => access.Value.Should().Be(ScopeAccess.Granted));

        // The first layout's host has a policy question in flight: the visitor moved the picker to another store, and the policy has not
        // answered. The change itself waits for it, so it is not awaited here.
        TaskCompletionSource oldAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.AuthorizationService.Gate = oldAnswer;
        int asked = context.AuthorizationService.Calls.Count;
        Task change = routes.InvokeAsync(() => context.State.SetScopeAsync("other", null, null, Token));
        routes.WaitForAssertion(() => context.AuthorizationService.Calls.Count.Should().BeGreaterThan(asked, "the old host asked"));

        // The layout is re-created: a page's own navigation to an address no page matches is "Not found", in a layout of its own.
        TaskCompletionSource newAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.AuthorizationService.Gate = newAnswer;
        asked = context.AuthorizationService.Calls.Count;
        await routes.InvokeAsync(() => navigation.NavigateTo("no-such-page"));
        routes.WaitForAssertion(() => context.AuthorizationService.Calls.Count.Should().BeGreaterThan(asked, "the new host asked"));

        // Until its own host has answered the new gate draws the spinner: not the previous layout's Granted, and so not the page.
        access.Value.Should().Be(ScopeAccess.Unknown);
        routes.FindAll(".ms-loading-spinner").Should().ContainSingle();
        routes.FindAll("h1").Should().BeEmpty("the body is drawn only once the new host was admitted");

        newAnswer.SetResult();
        routes.WaitForAssertion(() => routes.Find("h1").TextContent.Trim().Should().Be("Not found"));
        access.Value.Should().Be(ScopeAccess.Granted);

        // The old host's answer arrives late - and would refuse. It belongs to a host that is gone, and changes nothing.
        context.AuthorizationService.DenyEverything();
        oldAnswer.SetResult();
        await change;
        await routes.InvokeAsync(static () => { });

        access.Value.Should().Be(ScopeAccess.Granted, "the old host's late answer is ignored");
        routes.Find("h1").TextContent.Trim().Should().Be("Not found");
        routes.FindAll(".ms-empty-title").Should().BeEmpty("the refusal was never drawn");
    }

    /// <summary>
    /// The way back (review of A2a, F1, the leg the first test does not take): from "Not found", whose layout the circuit's gate had admitted, to a page
    /// whose scope the policy now refuses. The re-created layout's gate shows the spinner until its own host has answered, then the refusal - never the
    /// page, and never the previous layout's Granted.
    /// </summary>
    [Fact]
    public async Task The_way_back_from_Not_found_to_a_page_whose_scope_is_refused_shows_the_spinner_then_the_refusal_and_never_the_body()
    {
        ClickableNavigationManager navigation = new();

        await using StudioComponentContext context = new(services =>
        {
            services.AddSingleton<NavigationManager>(navigation);
            services.AddSingleton<INavigationInterception, QuietNavigationInterception>();
            services.AddSingleton<IScrollToLocationHash, QuietScrollToLocationHash>();
        });

        context.Options.RefreshInterval = TimeSpan.FromMinutes(5);
        context.StoreInfo.WithStore("default", "Default", databaseIdentity: "db-a");
        context.Catalog.WithStore("default", "Default", databaseIdentities: "db-a");
        context.WithPolicy("default");

        DatabaseScopeAccess access = context.Services.GetRequiredService<DatabaseScopeAccess>();

        // The first layout, on the Overview: admitted. Then "Not found", in a layout of its own: admitted as well.
        IRenderedComponent<StudioRoutes> routes = context.Render<StudioRoutes>();
        routes.WaitForAssertion(() => access.Value.Should().Be(ScopeAccess.Granted));
        await routes.InvokeAsync(() => navigation.NavigateTo("no-such-page"));
        routes.WaitForAssertion(() => routes.Find("h1").TextContent.Trim().Should().Be("Not found"));
        access.Value.Should().Be(ScopeAccess.Granted);

        // The policy now refuses the scope, and answers slowly. The way back to a page re-creates the layout again.
        TaskCompletionSource answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.AuthorizationService.DenyEverything();
        context.AuthorizationService.Gate = answer;
        int asked = context.AuthorizationService.Calls.Count;
        await routes.InvokeAsync(() => navigation.NavigateTo(string.Empty));
        routes.WaitForAssertion(() => context.AuthorizationService.Calls.Count.Should().BeGreaterThan(asked, "the new host asked"));

        access.Value.Should().Be(ScopeAccess.Unknown, "the previous layout's Granted is not this layout's answer");
        routes.FindAll(".ms-loading-spinner").Should().ContainSingle();
        routes.FindAll(".ms-empty-title").Should().BeEmpty("nothing is refused or shown before the policy has answered");
        routes.FindAll("h1").Should().BeEmpty("neither the page nor 'Not found' is drawn");

        answer.SetResult();
        routes.WaitForAssertion(() => access.Value.Should().Be(ScopeAccess.Denied));
        routes.WaitForAssertion(() => routes.FindAll(".ms-empty-title").Should().ContainSingle("the refusal is drawn"));
        routes.FindAll(".ms-loading-spinner").Should().BeEmpty();
        routes.FindAll("h1").Should().BeEmpty("the page is never drawn for a refused scope");
    }
}
