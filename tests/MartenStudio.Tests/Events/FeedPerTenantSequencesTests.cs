using Bunit;

using Marten;
using Marten.Storage;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

/// <summary>
/// SEC1-fix3: on a store whose events are numbered per tenant (<c>UseTenantPartitionedEvents</c>), the feed under "All
/// tenants" reads nothing and says why. Its paging and its follow mode run on <c>seq_id</c>, which there orders each
/// tenant's events and nothing across them.
/// </summary>
public class FeedPerTenantSequencesTests
{
    private const string Unreachable =
        "Host=sec1-feed-per-tenant.invalid;Database=none;Username=none;Password=none;Timeout=2";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_all_tenants_the_service_reads_no_feed_and_says_why()
    {
        var users = new TestAuthenticationStateProvider();
        users.SignIn("tester");

        var services = new ServiceCollection();
        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddMarten(static options =>
        {
            options.Connection(Unreachable);
            options.DatabaseSchemaName = "sec1_feed_per_tenant";
            options.Events.TenancyStyle = JasperFx.MultiTenancy.TenancyStyle.Conjoined;
            options.Events.UseTenantPartitionedEvents = true;
            options.Events.AppendMode = JasperFx.Events.EventAppendMode.QuickWithServerTimestamps;
            options.Events.AddEventType<DeadLetterVisibilityTests.NothingHappened>();
        });
        services.AddMartenStudio(static options =>
        {
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add("acme");
            options.QueryTimeout = TimeSpan.FromSeconds(5);
        });
        services.AddScoped<AuthenticationStateProvider>(_ => users);

        await using ServiceProvider provider = services.BuildServiceProvider();
        IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();
        var everyone = new StudioScope(MartenStoreRegistry.DefaultStoreKey, databases[0].Id.Identity, null);

        using IServiceScope circuit = provider.CreateScope();
        IEventDataService events = circuit.ServiceProvider.GetRequiredService<IEventDataService>();

        EventPage page = await events.GetFeedAsync(everyone, new EventFeedRequest(), Token);
        EventPage follow = await events.GetFeedAsync(everyone, new EventFeedRequest { SinceSequence = 10 }, Token);

        foreach (EventPage refused in (EventPage[]) [page, follow])
        {
            refused.Rows.Should().BeEmpty();
            refused.Error.Should().NotBeNull("an answer, before any connection - this host does not resolve");
            refused.Error!.Message.Should().Be(StreamCompactionRules.ReadNeedsTenant("the feed"));
            refused.Error.CanRetry.Should().BeFalse("retrying asks the same question; choosing a tenant is the answer");
            refused.Error.NeedsTenant.Should().BeTrue("an answer a page draws as a note, not a failure");
        }

        // The control: with a tenant selected the feed is read - and fails here only because the host does not resolve.
        EventPage scoped = await events.GetFeedAsync(everyone with { TenantId = "acme" }, new EventFeedRequest(), Token);
        scoped.Error!.Message.Should().NotBe(StreamCompactionRules.ReadNeedsTenant("the feed"));
    }

    [Fact]
    public async Task With_all_tenants_the_page_shows_why_and_follow_mode_does_not_start()
    {
        using var context = new StudioComponentContext();
        await context.ReadyAsync();
        context.EventData.Shape = context.EventData.Shape with { PerTenantSequences = true, EventsConjoined = true, HasTenantId = true };
        context.EventData.Feed = EventPage.Failed(new EventDataError(StreamCompactionRules.ReadNeedsTenant("the feed"), null, false));

        context.Navigate("marten/events/feed?follow=1");
        IRenderedComponent<Feed> page = context.Render<Feed>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("choose a tenant to read the feed");
        page.Find(".ms-follow-bar input[type=checkbox]").HasAttribute("disabled").Should().BeTrue("follow mode does not start");
        page.FindAll(".ms-event-card").Should().BeEmpty();
    }
}
