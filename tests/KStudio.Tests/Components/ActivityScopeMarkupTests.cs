using Bunit;

using DataStudio.Components.Pages;
using DataStudio.Services;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Services.Postgres;

namespace KStudio.Tests.Components;

/// <summary>
/// The Activity page draws an entry's scope from what the module that wrote it says, and a database entry and a Kafka entry come out as they
/// did when the page knew a store and a cluster: these literals are the rows of the page before the move (A2c), taken from its rendering.
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

    private static string Separator => "<span class=\"ms-activity-scope-separator\"> · </span>";

    [Fact]
    public async Task A_database_entry_and_a_Kafka_entry_render_the_rows_the_page_always_drew()
    {
        using KafkaStudioHarness harness = new();
        await harness.Context.ReadyAsync();
        harness.Context.AuthenticationState.SignIn("ops");
        harness.Context.Preferences.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;

        StudioActionLogService ring = harness.Context.Services.GetRequiredService<StudioActionLogService>();
        DateTimeOffset at = new(2026, 11, 14, 9, 30, 15, TimeSpan.Zero);
        string database = PostgresStudioDescriptor.Key;

        ring.Record(new StudioActionLogEntry(
            at, "ops", new DatabaseAuditScope("default", "main", "acme").ToAuditScope(database), "OpenRow", "public.orders", true, "done", null));
        ring.Record(new StudioActionLogEntry(
            at.AddMinutes(1), "viewer", new DatabaseAuditScope("default", "main", null).ToAuditScope(database), "OpenAll", "public.orders", false, "nope", null));
        ring.Record(new StudioActionLogEntry(
            at.AddMinutes(2), "ops", new StudioAuditScope("kafka", ["Primary · tenant-a"], "Primary · tenant-a", new KafkaAuditScope("primary", "tenant-a", ["orders.v1"])),
            "SearchRecords", "orders.v1", true, "done", null));

        IRenderedComponent<Activity> page = harness.Context.Render<Activity>();

        page.FindAll("tbody tr").Select(static x => x.OuterHtml).Should().Equal(
            Row("2026-11-14 09:32:15 +00:00", "ops", "Primary · tenant-a", "Primary · tenant-a", "SearchRecords", "orders.v1", true, "done"),
            Row("2026-11-14 09:31:15 +00:00", "viewer", "default" + Separator + "main" + Separator + "all", "Store default, database main, tenant all", "OpenAll", "public.orders", false, "nope"),
            Row("2026-11-14 09:30:15 +00:00", "ops", "default" + Separator + "main" + Separator + "acme", "Store default, database main, tenant acme", "OpenRow", "public.orders", true, "done"));
    }
}
