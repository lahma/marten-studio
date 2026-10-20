using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Health;

using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Query;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The Event types screen's 0.4 half: the E05 banner, read from the health advisor's cache and absent without
/// it, and the DCB tag types with their estimates and filter verdicts.
/// </summary>
public class EventTypesHealthAndTagsTests
{
    /// <summary>
    /// A health service whose cache-only read answers what a test says and counts every call - so a test can
    /// prove the page read the cache and never asked for a report, which is what sweeps.
    /// </summary>
    private sealed class CachedHealth : IHealthService
    {
        public IReadOnlyList<HealthReportFinding> Cached { get; set; } = [];

        public List<IReadOnlyCollection<string>> CachedReads { get; } = [];

        public int ReportRequests { get; private set; }

        public Task<HealthReport> GetReportAsync(StudioScope scope, HealthReportRequest request, CancellationToken cancellationToken = default)
        {
            ReportRequests++;
            return Task.FromResult(new HealthReport());
        }

        public Task<HealthGlance?> GlanceAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<HealthGlance?>(null);

        public Task<IReadOnlyList<HealthReportFinding>> CachedFindingsAsync(
            StudioScope scope,
            IReadOnlyCollection<string> codes,
            CancellationToken cancellationToken = default)
        {
            CachedReads.Add(codes);
            return Task.FromResult<IReadOnlyList<HealthReportFinding>>([.. Cached.Where(x => codes.Contains(x.Code))]);
        }
    }

    private static HealthReportFinding E05(HealthSeverity severity = HealthSeverity.Warning) =>
        new(
            FakeHealthService.Finding("E05", severity, "Unregistered event types", HealthArea.Events, subject: "legacy_event"),
            [],
            "default|localhost.marten|E05|legacy_event",
            "https://example.invalid/docs/health/events.md#e05-unregistered-event-types",
            RemedyAvailable: true,
            HostRuns: null);

    // ------------------------------------------------------------------------------------------------
    // E05
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task E05_from_the_advisors_cache_is_a_banner_that_links_to_its_finding()
    {
        CachedHealth health = new() { Cached = [E05()] };
        using StudioComponentContext context = await ContextAsync(health);

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.WaitForAssertion(() => page.FindAll(".ms-event-types-health").Should().ContainSingle());

        var banner = page.Find(".ms-event-types-health");
        banner.GetAttribute("role").Should().Be("status");
        banner.TextContent.Should().Contain("E05").And.Contain("Unregistered event types").And.Contain("Because the test says so.");
        banner.QuerySelector(".ms-health-banner-link")!.GetAttribute("href").Should().Contain("health").And.Contain("#finding-");

        health.CachedReads.Should().NotBeEmpty().And.OnlyContain(static codes => codes.SequenceEqual(EventTypesHealth.BannerCodes));
        health.ReportRequests.Should().Be(0, "the screen reads the cache, and never asks for the report that sweeps");
    }

    /// <summary>No recent sweep is "not checked" - which draws nothing, and in particular no "healthy" claim.</summary>
    [Fact]
    public async Task Without_a_cached_sweep_there_is_no_banner_and_no_claim_of_health()
    {
        CachedHealth health = new();
        using StudioComponentContext context = await ContextAsync(health);

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.WaitForAssertion(() => health.CachedReads.Should().NotBeEmpty());
        page.FindAll(".ms-event-types-health").Should().BeEmpty();
        page.Markup.Should().NotContain("healthy").And.NotContain("E05");
        health.ReportRequests.Should().Be(0);
    }

    [Fact]
    public async Task A_critical_E05_is_an_alert_rather_than_a_status()
    {
        CachedHealth health = new() { Cached = [E05(HealthSeverity.Critical)] };
        using StudioComponentContext context = await ContextAsync(health);

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.WaitForAssertion(() => page.Find(".ms-event-types-health").GetAttribute("role").Should().Be("alert"));
        page.Find(".ms-event-types-health").ClassList.Should().Contain("ms-alert-error");
    }

    /// <summary>The screen's own marking is this page's read, with or without the advisor.</summary>
    [Fact]
    public async Task The_screens_own_not_registered_marking_stays_whatever_the_advisor_says()
    {
        CachedHealth health = new();
        using StudioComponentContext context = await ContextAsync(health, static data =>
            data.TypesWithoutCounts = new EventTypeList([new EventTypeInfo("legacy_event", null, IsRegistered: false)], false));

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.WaitForAssertion(() => page.Markup.Should().Contain("not registered"));
        page.FindAll(".ms-event-types-health").Should().BeEmpty();
    }

    /// <summary>A banner is a decoration: one that cannot be read is no banner, never a broken page.</summary>
    [Fact]
    public async Task A_cache_read_that_throws_leaves_the_page_whole()
    {
        ThrowingHealth health = new();
        using StudioComponentContext context = await ContextAsync(health, static data =>
            data.TypesWithoutCounts = new EventTypeList([new EventTypeInfo("order_placed", "Sample.OrderPlaced", IsRegistered: true)], false));

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.WaitForAssertion(() => page.FindAll(".ms-table-row").Should().ContainSingle());
        page.FindAll(".ms-event-types-health").Should().BeEmpty();
        page.FindAll(".ms-error-alert").Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // DCB tag types
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_store_without_tag_types_draws_no_tag_section()
    {
        using StudioComponentContext context = await ContextAsync(new CachedHealth());

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.WaitForAssertion(() => page.FindAll(".ms-loading-spinner").Should().BeEmpty());
        page.FindAll(".ms-event-tag-types").Should().BeEmpty();
    }

    [Fact]
    public async Task Tag_types_are_listed_with_their_table_estimate_and_filter_verdict()
    {
        using StudioComponentContext context = await ContextAsync(new CachedHealth(), static data =>
            data.TagTypes = new EventTagTypeList(
                EventTagStorage.TagTables,
                [
                    new EventTagTypeInfo(
                        "CustomerId", "customer", "String", "Order", "mt_event_tag_customer", true,
                        EventStoreCount.Estimate(12_345), null, IndexVerdictLevel.Green, "Index-backed."),
                    new EventTagTypeInfo(
                        "RegionId", "region", "Guid", null, "mt_event_tag_region", false,
                        EventStoreCount.Unknown, "The table is not in this database yet.", IndexVerdictLevel.Red, "No table."),
                ],
                VersionTableExists: true,
                EventStoreCount.Estimate(40)));

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.WaitForAssertion(() => page.FindAll(".ms-event-tag-types .ms-table-row").Should().HaveCount(2));

        var rows = page.FindAll(".ms-event-tag-types .ms-table-row");
        rows[0].TextContent.Should().Contain("CustomerId").And.Contain("customer").And.Contain("~12,345").And.Contain("indexed");
        rows[1].TextContent.Should().Contain("not created").And.Contain("full scan");
        page.Find(".ms-event-tag-version").TextContent.Should().Contain("~40");
        page.FindAll(".ms-event-tag-types table.ms-table").Should().ContainSingle()
            .Which.ParentElement!.ClassList.Should().Contain("ms-table-scroll", "every table sits in a scroll region (D25)");
    }

    /// <summary>The lead says how the tag tables are keyed on this store, and names the tenant only where it is in the key.</summary>
    [Theory]
    [InlineData(false, "keyed (value, seq_id)")]
    [InlineData(true, "keyed (value, tenant_id, seq_id)")]
    public async Task The_tag_tables_key_is_described_as_this_store_has_it(bool conjoined, string expected)
    {
        using StudioComponentContext context = await ContextAsync(new CachedHealth(), data =>
            data.TagTypes = new EventTagTypeList(
                EventTagStorage.TagTables,
                [
                    new EventTagTypeInfo(
                        "CustomerId", "customer", "String", null, "mt_event_tag_customer", true,
                        EventStoreCount.Estimate(10), null, IndexVerdictLevel.Green, "Index-backed."),
                ],
                VersionTableExists: false,
                EventStoreCount.Unknown,
                IsConjoined: conjoined));

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.WaitForAssertion(() => page.Find(".ms-event-tag-types-lead").TextContent.Should().Contain(expected));

        if (!conjoined)
        {
            page.Find(".ms-event-tag-types-lead").TextContent.Should().NotContain("tenant");
        }
    }

    // ------------------------------------------------------------------------------------------------

    private static async Task<StudioComponentContext> ContextAsync(IHealthService health, Action<FakeEventDataService>? data = null)
    {
        StudioComponentContext context = new(services => services.AddSingleton(health));
        data?.Invoke(context.EventData);
        await context.ReadyAsync();
        context.Navigate("marten/events/types");
        return context;
    }

    private sealed class ThrowingHealth : IHealthService
    {
        public Task<HealthReport> GetReportAsync(StudioScope scope, HealthReportRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthReport());

        public Task<HealthGlance?> GlanceAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<HealthGlance?>(null);

        public Task<IReadOnlyList<HealthReportFinding>> CachedFindingsAsync(
            StudioScope scope,
            IReadOnlyCollection<string> codes,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<HealthReportFinding>>(new InvalidOperationException("the cache is gone"));
    }
}
