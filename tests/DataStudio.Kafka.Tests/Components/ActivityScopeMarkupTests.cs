using Bunit;

using DataStudio.Components.Pages;
using DataStudio.Services;
using DataStudio.Kafka.Services;
using DataStudio.Kafka.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Tests.Components;

/// <summary>
/// The Activity page draws an entry's scope from what the module that wrote it says, and a Kafka entry comes out as it did when the page knew a
/// store and a cluster: this literal is the row of the page before the move (A2c), taken from its rendering. A database entry's row is the Marten
/// suite's (<c>ActivityTests</c>), which has the database module.
/// </summary>
public class ActivityScopeMarkupTests
{
    private static readonly string Gap = "\n" + new string(' ', 28);

    private static string Row(string when, string user, string scope, string title, string action, string target, bool succeeded, string message) =>
        "<tr class=\"ms-table-row\"><td class=\"ms-table-cell ms-table-cell-mono ms-activity-when\">" + when + "</td>"
        + Gap + "<td class=\"ms-table-cell\">" + user + "</td>"
        + Gap + "<td class=\"ms-table-cell ms-table-cell-mono ms-activity-scope\" title=\"" + title + "\">" + scope + "</td>"
        + Gap + "<td class=\"ms-table-cell\">" + action + "</td>"
        + Gap + "<td class=\"ms-table-cell ms-table-cell-mono ms-activity-target\">" + target + "</td>"
        + Gap + "<td class=\"ms-table-cell\"><span class=\"ms-state-indicator\"><span class=\"ms-state-dot " + (succeeded ? "ms-state-success" : "ms-state-error") + "\" aria-hidden=\"true\"></span>"
        + "\n" + new string(' ', 36) + "<span class=\"ms-state-label\">" + (succeeded ? "succeeded" : "failed") + "</span></span></td>"
        + Gap + "<td class=\"ms-table-cell\">-</td>"
        + Gap + "<td class=\"ms-table-cell ms-activity-message\">" + message + "</td></tr>";

    [Fact]
    public async Task A_Kafka_entry_renders_the_row_the_page_always_drew()
    {
        using KafkaHarness harness = new();
        await harness.Context.ReadyAsync();
        harness.Context.AuthenticationState.SignIn("ops");
        harness.Context.Preferences.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;

        StudioActionLogService ring = harness.Context.Services.GetRequiredService<StudioActionLogService>();
        DateTimeOffset at = new(2026, 11, 14, 9, 30, 15, TimeSpan.Zero);

        ring.Record(new StudioActionLogEntry(
            at.AddMinutes(2), "ops", new StudioAuditScope("kafka", ["Primary · tenant-a"], "Primary · tenant-a", new KafkaAuditScope("primary", "tenant-a", ["orders.v1"])),
            "SearchRecords", "orders.v1", true, "done", null));

        IRenderedComponent<Activity> page = harness.Context.Render<Activity>();

        page.FindAll("tbody tr").Select(static x => x.OuterHtml).Should().Equal(
            Row("2026-11-14 09:32:15 +00:00", "ops", "Primary · tenant-a", "Primary · tenant-a", "SearchRecords", "orders.v1", true, "done"));
    }
}
