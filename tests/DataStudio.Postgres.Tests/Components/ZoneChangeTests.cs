using System.Globalization;

using Bunit;

using DataStudio.Components.Layout;
using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Replication;
using DataStudio.Postgres.Tests.Replication;
using DataStudio.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using ReplicationPage = DataStudio.Postgres.Components.Pages.Replication.Replication;

namespace DataStudio.Postgres.Tests.Components;

/// <summary>
/// A change of the visitor's time zone is a change of display and nothing else (F6): the Postgres module's pages spell the time they hold in the new zone
/// and read nothing again. Operations › Replication, over a fake service that counts its reads, through the real layout: the browser answers UTC+14 at
/// circuit start, and the visitor then picks another zone in Preferences.
/// </summary>
public class ZoneChangeTests
{
    private const string Browser = "Pacific/Kiritimati";

    private static string Spelt(DateTimeOffset instant, string zoneId) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zoneId)).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_browsers_zone_and_a_zone_picked_afterwards_draw_the_page_again_and_it_reads_its_data_once()
    {
        ReplicationView view = ReplicationService.Build(
            ReplicationViewTests.Facts(),
            names: true,
            null,
            HealthHistoryView.Empty,
            PostgresText.Instance);
        FakeReplicationService service = new(view);
        await using PostgresComponentContext context = new(services => services.AddSingleton<IReplicationService>(service));
        context.WithAllCapabilities();
        await context.ReadyAsync();

        context.JSInterop.Setup<string?>("martenStudio.prefs.get", StudioPreferences.ThemePreferenceKey).SetResult(null);
        context.JSInterop.Setup<string?>("martenStudio.prefs.get", StudioPreferences.TimeZonePreferenceKey).SetResult(null);
        context.JSInterop.Setup<string?>("martenStudio.timeZone.get").SetResult(Browser);

        RenderFragment body = static builder =>
        {
            builder.OpenComponent<ReplicationPage>(0);
            builder.CloseComponent();
        };

        IRenderedComponent<StudioLayout> layout = context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, body));
        layout.WaitForState(() => layout.Instance.PreferencesLoaded);

        string Measured() => layout.Find(".ms-repl-measured").TextContent;

        // The browser's zone (UTC+14) is the circuit's, and the page that read for the circuit's start has read once.
        layout.WaitForAssertion(() => Measured().Should().Contain("Read " + Spelt(view.MeasuredAt, Browser)));
        service.Reads.Should().ContainSingle("circuit start is one load of the page, not one for the zone as well");

        string picked = layout.FindAll("#ms-timezone-select option").Select(static x => x.GetAttribute("value")!).First(id =>
            TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone) && zone is not null && zone.GetUtcOffset(view.MeasuredAt) != TimeSpan.FromHours(14));
        layout.Find("#ms-timezone-select").Change(picked);

        layout.WaitForAssertion(() => Measured().Should().Contain("Read " + Spelt(view.MeasuredAt, picked)));
        service.Reads.Should().ContainSingle("a zone is not a scope: the page spells the time it holds again and reads nothing");
    }

    private sealed class FakeReplicationService(ReplicationView view) : IReplicationService
    {
        public List<StudioScope> Reads { get; } = [];

        public Task<ReplicationView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
        {
            Reads.Add(scope);
            return Task.FromResult(view);
        }

        public Task<ReplicationView> GetNumbersAsync(StudioScope scope, CancellationToken cancellationToken = default) => Task.FromResult(view);

        public Task<TablespaceSize> MeasureTablespaceAsync(StudioScope scope, string tablespace, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TablespaceSize(tablespace, 1 << 20, null));
    }
}
