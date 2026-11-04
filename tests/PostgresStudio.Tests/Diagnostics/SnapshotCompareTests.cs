using System.Reflection;
using System.Text;

using Bunit;

using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Components.Pages.Health;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Statements;
using PostgresStudio.Tests.Components;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>The fakes the compare's service and drawer tests build the visitor's view from, as the Snapshot menu's tests do.</summary>
internal static class SnapshotCompareFixtures
{
    /// <summary>The Statements page's service: a view the visitor may not have, and a count of the reads.</summary>
    internal sealed class RefusedStatements : IStatementsService
    {
        public Task<StatementsView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(StatementsView.Refused(DatabaseRefusal.CapabilityOff, "Statements need Capabilities.ViewDatabaseActivity."));

        public Task<StatementsView> GetLatestAsync(StudioScope scope, CancellationToken cancellationToken = default) => GetAsync(scope, cancellationToken);

        public Task<StatementPlanView> PlanAsync(StudioScope scope, long queryId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The compare never plans a statement.");

        public Task<PostgresStudio.Services.Query.WhatIfResult> WhatIfAsync(
            StudioScope scope, long queryId, PostgresStudio.Services.Query.HypotheticalIndex index, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The compare never imagines an index.");
    }

    internal sealed class OneServer : IServerPageService
    {
        public Task<ServerPageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerPageView { MeasuredAt = DateTimeOffset.UnixEpoch });
    }

    internal sealed class NoReplication : IReplicationService
    {
        public Task<ReplicationView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The compare asks for the numbers only.");

        public Task<ReplicationView> GetNumbersAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReplicationView { MeasuredAt = DateTimeOffset.UnixEpoch });

        public Task<TablespaceSize> MeasureTablespaceAsync(StudioScope scope, string tablespace, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The compare never measures a tablespace.");
    }

    /// <summary>A bUnit context with the visitor's view built from fakes, and the real compare service over it.</summary>
    public static async Task<PostgresStudioComponentContext> ContextAsync(Action<IServiceCollection>? more = null)
    {
        PostgresStudioComponentContext context = new(services =>
        {
            services.AddSingleton<IStatementsService>(new RefusedStatements());
            services.AddSingleton<IServerPageService>(new OneServer());
            services.AddSingleton<IReplicationService>(new NoReplication());
            services.AddSingleton(new HealthCheckRegistry(PostgresStudioDescriptor.Instance));
            services.AddScoped<VisitorSnapshotBuilder>();
            services.AddScoped<ISnapshotCompareService, SnapshotCompareService>();
            more?.Invoke(services);
        });

        context.Health.WithFindings(MartenStudio.Tests.Health.FakeHealthService.Finding("T01", HealthSeverity.Warning, "A table is read whole", HealthArea.Tables));

        return await context.ReadyAsync();
    }

    /// <summary>Everything logged - the message and the exception's whole <c>ToString()</c> - so a sentinel cannot hide in either.</summary>
    internal sealed class FullLogProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Text)> entries = [];
        private readonly Lock gate = new();

        public IReadOnlyList<(LogLevel Level, string Text)> Entries
        {
            get
            {
                lock (gate)
                {
                    return entries.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(FullLogProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.gate)
                {
                    owner.entries.Add((logLevel, category + ": " + formatter(state, exception) + (exception is null ? string.Empty : " | " + exception)));
                }
            }
        }
    }

    /// <summary>A snapshot file of the current view of the fake database, as Download JSON would save it.</summary>
    public static async Task<string> DownloadedJsonAsync(PostgresStudioComponentContext context)
    {
        VisitorSnapshotBuilder builder = context.Services.GetRequiredService<VisitorSnapshotBuilder>();

        return (await builder.BuildAsync(context.State.ActiveScope!, Xunit.TestContext.Current.CancellationToken)).Json;
    }
}

/// <summary>
/// The compare service (1.2-H-B, D-HEALTH C3): the visitor's gate first, then the file - and nothing of either kept, logged or audited.
/// </summary>
public class SnapshotCompareServiceTests
{
    [Fact]
    public async Task A_refused_current_view_never_opens_the_file()
    {
        await using PostgresStudioComponentContext context = await SnapshotCompareFixtures.ContextAsync();
        context.Health.Report = HealthReport.Refused(HealthRefusal.StorePolicy, "The store policy does not allow you this database.");
        int opened = 0;

        SnapshotComparison result = await context.Services.GetRequiredService<ISnapshotCompareService>()
            .CompareAsync(
                context.State.ActiveScope!,
                _ =>
                {
                    opened++;
                    throw new InvalidOperationException("The file must not be opened when the visitor's own view is refused.");
                },
                "earlier.json",
                Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SnapshotCompareOutcome.Refused);
        result.Reason.Should().Be("The store policy does not allow you this database.");
        opened.Should().Be(0, "the visitor's gate is asked first, and the file is opened - so starts crossing the circuit - only past it");
    }

    [Fact]
    public async Task A_file_is_opened_once_past_the_gate_and_disposed()
    {
        await using PostgresStudioComponentContext context = await SnapshotCompareFixtures.ContextAsync();
        string json = await SnapshotCompareFixtures.DownloadedJsonAsync(context);
        int opened = 0;
        MemoryStream stream = new(Encoding.UTF8.GetBytes(json));

        await context.Services.GetRequiredService<ISnapshotCompareService>()
            .CompareAsync(
                context.State.ActiveScope!,
                _ =>
                {
                    opened++;
                    return stream;
                },
                "x.json",
                Xunit.TestContext.Current.CancellationToken);

        opened.Should().Be(1);
        stream.CanRead.Should().BeFalse("the service disposes the file it opened");
    }

    [Fact]
    public async Task A_snapshot_downloaded_now_compares_with_now_and_changes_nothing()
    {
        await using PostgresStudioComponentContext context = await SnapshotCompareFixtures.ContextAsync();
        string json = await SnapshotCompareFixtures.DownloadedJsonAsync(context);
        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));

        SnapshotComparison result = await context.Services.GetRequiredService<ISnapshotCompareService>()
            .CompareAsync(context.State.ActiveScope!, _ => stream, "postgres-snapshot.json", Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SnapshotCompareOutcome.Compared);
        result.FileName.Should().Be("postgres-snapshot.json");
        result.Findings.Should().ContainSingle().Which.Kind.Should().Be(SnapshotChangeKind.Unchanged);
        result.Verdict.Should().Contain("nothing you may see has changed");
    }

    [Fact]
    public async Task A_file_that_is_not_a_snapshot_is_unreadable_and_says_why_without_echoing_it()
    {
        await using PostgresStudioComponentContext context = await SnapshotCompareFixtures.ContextAsync();
        await using MemoryStream stream = new(Encoding.UTF8.GetBytes("SENTINEL not json"));

        SnapshotComparison result = await context.Services.GetRequiredService<ISnapshotCompareService>()
            .CompareAsync(context.State.ActiveScope!, _ => stream, "x.json", Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SnapshotCompareOutcome.Unreadable);
        result.Reason.Should().StartWith("This file is not a snapshot's JSON").And.NotContain("SENTINEL");
    }

    // -------------------------------------------------------------------------------------------------
    // Structure: the service keeps nothing between calls, and no other service takes the compare
    // -------------------------------------------------------------------------------------------------

    private static bool IsCompareType(Type type)
    {
        Type element = type.HasElementType ? type.GetElementType()! : type;

        return element == typeof(ISnapshotCompareService)
            || element == typeof(SnapshotCompareService)
            || element == typeof(SnapshotComparison)
            || element == typeof(SnapshotFileRead)
            || element == typeof(SnapshotFindingChange)
            || element.Name.StartsWith("SnapshotCompar", StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_holds_services_only_in_readonly_fields_and_never_the_file_or_the_answer()
    {
        FieldInfo[] fields = typeof(SnapshotCompareService).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        fields.Should().NotBeEmpty("not vacuous");

        foreach (FieldInfo field in fields)
        {
            field.IsInitOnly.Should().BeTrue(field.Name + " is mutable, so it could hold the file between calls");

            (field.FieldType == typeof(string) || field.FieldType == typeof(Stream) || field.FieldType == typeof(StringBuilder)
                || field.FieldType == typeof(ReadOnlyMemory<byte>) || field.FieldType.IsArray || field.FieldType == typeof(PostgresDiagnosticsSnapshot)
                || field.FieldType == typeof(IMemoryCache) || typeof(ILogger).IsAssignableFrom(field.FieldType) || field.FieldType == typeof(StudioActionLog))
                .Should().BeFalse(field.Name + " (" + field.FieldType + ") could hold the file, log it, audit it or cache it");

            IsCompareType(field.FieldType).Should().BeFalse(field.Name + " holds the comparison's own types");

            (field.FieldType == typeof(VisitorSnapshotBuilder) || field.FieldType == typeof(IStudioSettings))
                .Should().BeTrue(field.Name + " (" + field.FieldType + ") is not one of the two services the compare asks");
        }

        typeof(SnapshotCompareService).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().BeEmpty("a property is a field with a name: the service keeps nothing between calls");

        typeof(SnapshotCompareService).GetConstructors().Single().GetParameters().Select(static x => x.ParameterType)
            .Should().BeEquivalentTo([typeof(VisitorSnapshotBuilder), typeof(IStudioSettings)], "no logger, no audit log, no cache");
    }

    [Fact]
    public void The_visitor_snapshot_builder_takes_holds_and_returns_nothing_of_the_compare()
    {
        Type builder = typeof(VisitorSnapshotBuilder);
        BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        IEnumerable<Type> constructed = builder.GetConstructors(all).SelectMany(static x => x.GetParameters()).Select(static x => x.ParameterType);
        IEnumerable<Type> held = builder.GetFields(all).Select(static x => x.FieldType);
        IEnumerable<Type> returned = builder.GetMethods(all).Select(static x => x.ReturnType);

        foreach (Type type in constructed.Concat(held).Concat(returned))
        {
            IsCompareType(type).Should().BeFalse(type + " is, or is the compare's: a download is built from the visitor's own gated views only");
            type.GenericTypeArguments.Should().NotContain(static x => IsCompareType(x), type + " wraps the compare");
        }

        constructed.Should().Contain(typeof(PostgresStudio.Services.Connection.IConnectionInsightService), "the guard is not vacuous");
    }
}

/// <summary>
/// The compare drawer (1.2-H-B, D-HEALTH C1, C2 and C7): a file input, not a form; a bounded file; the verdict and its tables in
/// labelled scroll regions; and nothing of the file left behind.
/// </summary>
public class SnapshotCompareTests
{
    private const string Sentinel = "SENTINEL-DRAWER-5e8d";

    private static async Task<(PostgresStudioComponentContext Context, IRenderedComponent<SnapshotMenu> Menu)> OpenAsync(Action<IServiceCollection>? more = null)
    {
        PostgresStudioComponentContext context = await SnapshotCompareFixtures.ContextAsync(more);
        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();

        await Select(menu, "Compare with an earlier snapshot");

        return (context, menu);
    }

    private static async Task Select(IRenderedComponent<SnapshotMenu> menu, string label)
    {
        AngleSharp.Dom.IElement item = menu.FindAll("button.ms-menu-item").Single(x => x.TextContent.Contains(label, StringComparison.Ordinal));
        await item.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
    }

    private static void Upload(IRenderedComponent<SnapshotMenu> menu, string name, string text) =>
        menu.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(text, name));

    [Fact]
    public async Task The_menu_has_the_three_new_items_and_the_three_it_had()
    {
        await using PostgresStudioComponentContext context = await SnapshotCompareFixtures.ContextAsync();
        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();

        menu.FindAll("button.ms-menu-item").Select(static x => x.QuerySelector(".ms-menu-item-label")?.TextContent ?? x.TextContent.Trim())
            .Should().Contain(["View JSON", "Copy JSON", "Download JSON", "Copy Markdown", "Download Markdown", "Compare with an earlier snapshot"]);
        menu.FindAll("button.ms-menu-item").Should().HaveCount(6);
    }

    [Fact]
    public async Task The_drawer_makes_its_promise_and_offers_a_file_input_that_asks_for_json()
    {
        (PostgresStudioComponentContext context, IRenderedComponent<SnapshotMenu> menu) = await OpenAsync();
        await using PostgresStudioComponentContext owned = context;

        menu.Find("section.ms-snapshot-compare").GetAttribute("aria-label").Should().Be("Compare with an earlier snapshot");
        string promise = System.Text.RegularExpressions.Regex.Replace(menu.Find(".ms-snapshot-compare-promise").TextContent, @"\s+", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        promise.Trim().Should().StartWith("Choose a snapshot file this menu saved.")
            .And.Contain("it is not stored, logged, audited or sent anywhere").And.Contain("counted, never described");

        AngleSharp.Dom.IElement input = menu.Find("input[type=file]");
        input.GetAttribute("accept").Should().Be(".json,application/json");
        input.Id.Should().Be("ms-snapshot-compare-file");
        menu.Find("label[for=ms-snapshot-compare-file]").Should().NotBeNull();
        menu.FindAll("form").Should().BeEmpty("the studio has no forms (D23)");
    }

    [Fact]
    public async Task Opening_the_compare_closes_the_json_view_and_the_other_way_round()
    {
        (PostgresStudioComponentContext context, IRenderedComponent<SnapshotMenu> menu) = await OpenAsync();
        await using PostgresStudioComponentContext owned = context;

        await Select(menu, "View JSON");

        menu.WaitForAssertion(() => menu.FindAll("section.ms-snapshot-view[aria-label='Snapshot JSON']").Should().ContainSingle());
        menu.FindAll("section.ms-snapshot-compare").Should().BeEmpty();

        await Select(menu, "Compare with an earlier snapshot");

        menu.FindAll("section.ms-snapshot-compare").Should().ContainSingle();
        menu.FindAll("section[aria-label='Snapshot JSON']").Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_over_four_megabytes_is_refused_before_a_byte_is_read()
    {
        (PostgresStudioComponentContext context, IRenderedComponent<SnapshotMenu> menu) = await OpenAsync();
        await using PostgresStudioComponentContext owned = context;

        menu.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[SnapshotFileReader.MaxBytes + 1], "big.json"));

        menu.WaitForAssertion(() => menu.Find(".ms-snapshot-compare-message").TextContent.Should().Contain("at most 4 MB is read"));
        menu.FindAll(".ms-snapshot-compare-result").Should().BeEmpty();
    }

    [Fact]
    public async Task A_snapshot_downloaded_now_is_compared_and_drawn_with_its_verdict()
    {
        (PostgresStudioComponentContext context, IRenderedComponent<SnapshotMenu> menu) = await OpenAsync();
        await using PostgresStudioComponentContext owned = context;

        // An earlier file: the current view plus a finding the current view has since lost, and a changed setting.
        PostgresDiagnosticsSnapshot current = SnapshotFileReader.Read(Encoding.UTF8.GetBytes(await SnapshotCompareFixtures.DownloadedJsonAsync(context))).Snapshot!;
        PostgresDiagnosticsSnapshot earlier = current with
        {
            GeneratedAt = current.GeneratedAt.AddDays(-1),
            Findings =
            [
                .. current.Findings.Select(static x => x with { Severity = PostgresSeverity.Critical }),
                new PostgresFinding
                {
                    Code = "T01",
                    Variant = "other",
                    Severity = PostgresSeverity.Critical,
                    Category = PostgresFindingCategory.Tables,
                    Title = "An earlier finding",
                    Detail = "d",
                    DocsUrl = "https://example.test",
                    MeasuredAt = current.GeneratedAt,
                },
            ],
            Checks = [],
        };

        Upload(menu, "earlier.json", DiagnosticsJson.Write(earlier));

        menu.WaitForAssertion(() => menu.Find(".ms-snapshot-compare-verdict").TextContent.Should().Contain("Compared with the snapshot from"));
        menu.Find(".ms-snapshot-compare-name").TextContent.Should().Be("earlier.json");

        // T01 has no check row in the (fake) report, so it is counted and not named.
        menu.Find(".ms-snapshot-compare-notcompared").TextContent.Should().Contain("1 Earlier findings no longer in your view");
        menu.FindAll(".ms-snapshot-compare-result .ms-table-scroll[role=region]").Should().NotBeEmpty();

        foreach (AngleSharp.Dom.IElement table in menu.FindAll(".ms-snapshot-compare-result table"))
        {
            table.ParentElement!.ClassList.Should().Contain("ms-table-scroll", "every data table sits in a labelled scroll region (D25)");
            table.ParentElement.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task A_file_that_is_not_a_snapshot_is_told_so()
    {
        (PostgresStudioComponentContext context, IRenderedComponent<SnapshotMenu> menu) = await OpenAsync();
        await using PostgresStudioComponentContext owned = context;

        Upload(menu, "x.json", "{ not json " + Sentinel);

        menu.WaitForAssertion(() => menu.Find(".ms-snapshot-compare-problem").TextContent.Should().Contain("This file is not a snapshot's JSON"));
        menu.Markup.Should().NotContain(Sentinel);
        menu.Find(".ms-snapshot-compare-problem").GetAttribute("role").Should().Be("alert");
    }

    [Fact]
    public async Task A_visitor_whose_own_view_is_refused_is_told_so()
    {
        (PostgresStudioComponentContext context, IRenderedComponent<SnapshotMenu> menu) = await OpenAsync();
        await using PostgresStudioComponentContext owned = context;
        context.Health.Report = HealthReport.Refused(HealthRefusal.StorePolicy, "The store policy does not allow you this database.");

        Upload(menu, "x.json", DiagnosticsJson.Write(SnapshotFileReaderTests.Everything()));

        menu.WaitForAssertion(() => menu.Find(".ms-snapshot-compare-problem").TextContent.Should().Contain("The store policy does not allow you this database."));
        menu.Find(".ms-snapshot-compare-problem").GetAttribute("role").Should().Be("status");
    }

    [Fact]
    public async Task Each_compare_and_Close_replace_the_input_so_the_browser_drops_its_file()
    {
        (PostgresStudioComponentContext context, IRenderedComponent<SnapshotMenu> menu) = await OpenAsync();
        await using PostgresStudioComponentContext owned = context;

        AngleSharp.Dom.IElement first = menu.Find("input[type=file]");

        Upload(menu, "x.json", "nope");
        menu.WaitForAssertion(() => menu.FindAll(".ms-snapshot-compare-problem").Should().ContainSingle());

        AngleSharp.Dom.IElement second = menu.Find("input[type=file]");
        second.Should().NotBeSameAs(first);

        await menu.Find(".ms-snapshot-compare-close").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        menu.FindAll("section.ms-snapshot-compare").Should().BeEmpty();

        await Select(menu, "Compare with an earlier snapshot");
        menu.FindAll(".ms-snapshot-compare-problem").Should().BeEmpty("a closed drawer keeps no answer");
    }

    [Fact]
    public async Task A_file_of_sentinel_strings_reaches_no_log_no_audit_entry_no_interop_call_and_no_field()
    {
        SnapshotCompareFixtures.FullLogProvider logs = new();
        (PostgresStudioComponentContext context, IRenderedComponent<SnapshotMenu> menu) = await OpenAsync(services =>
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)));
        await using PostgresStudioComponentContext owned = context;

        // A readable file whose every word is a sentinel: findings of checks the current view has no row for, a table, a statement,
        // a setting, an application and connection keywords.
        PostgresDiagnosticsSnapshot earlier = SnapshotFileReaderTests.Everything() with
        {
            GeneratedAt = DiagnosticsTestFacts.At.AddDays(-1),
            Findings =
            [
                new PostgresFinding
                {
                    Code = "Z99",
                    Variant = Sentinel + "-variant",
                    Subject = Sentinel + "-subject",
                    TenantId = Sentinel + "-tenant",
                    Severity = PostgresSeverity.Critical,
                    Category = PostgresFindingCategory.Tables,
                    Title = Sentinel + "-title",
                    Detail = Sentinel + "-detail",
                    DocsUrl = "https://example.test/" + Sentinel,
                    MeasuredAt = DiagnosticsTestFacts.At,
                    Evidence = [new KeyValuePair<string, string>(Sentinel + "-label", Sentinel + "-value")],
                },
            ],
            Statements = new PostgresStatementsSection { Outcome = PostgresReadOutcome.Read, Top = [new PostgresStatement { QueryId = 9, Text = "select " + Sentinel }] },
            Connection = SnapshotFileReaderTests.Everything().Connection! with
            {
                Settings = [new ConnectionSnapshotSetting("Options", "c", "-c " + Sentinel, "connectionString", null, null, null)],
            },
        };

        Upload(menu, Sentinel + ".json", DiagnosticsJson.Write(earlier));

        menu.WaitForAssertion(() => menu.FindAll(".ms-snapshot-compare-verdict").Should().ContainSingle());

        // The file's name is the visitor's own and is drawn (capped); nothing else of the file is.
        menu.Markup.Replace(Sentinel + ".json", string.Empty, StringComparison.Ordinal).Should().NotContain(Sentinel);

        logs.Entries.Should().NotContain(x => x.Text.Contains(Sentinel, StringComparison.Ordinal), "nothing of the file is logged, at any level, in a message or an exception");
        context.ActionLog.GetLatest().Should().BeEmpty("a compare is not audited: the file is the visitor's own");
        context.JSInterop.Invocations.SelectMany(static x => x.Arguments).Select(static x => x?.ToString() ?? string.Empty)
            .Should().NotContain(x => x.Contains(Sentinel, StringComparison.Ordinal), "no interop call carries the file");

        // The component keeps no stream, no bytes and no snapshot: its fields are the derived comparison and a few scalars.
        SnapshotCompare component = menu.FindComponent<SnapshotCompare>().Instance;

        foreach (FieldInfo field in typeof(SnapshotCompare).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            object? value = field.GetValue(component);

            (value is Stream or byte[] or PostgresDiagnosticsSnapshot or SnapshotFileRead or IBrowserFile or string { Length: > 4096 }).Should().BeFalse(field.Name + " holds the file");
        }

        context.ActionLog.GetLatest().Should().BeEmpty();
    }

    [Fact]
    public async Task A_compare_that_fails_unexpectedly_is_logged_once_and_nothing_of_the_file_is_in_the_log_or_the_page()
    {
        SnapshotCompareFixtures.FullLogProvider logs = new();
        (PostgresStudioComponentContext context, IRenderedComponent<SnapshotMenu> menu) = await OpenAsync(services =>
        {
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddScoped<ISnapshotCompareService>(static _ => new FailingCompare(new FormatException("boom")));
        });
        await using PostgresStudioComponentContext owned = context;

        Upload(menu, "x.json", "{\"words\": \"" + Sentinel + "\"}");

        menu.WaitForAssertion(() => menu.Find(".ms-snapshot-compare-message").TextContent.Should().Be("The file could not be compared; the server log has why."));
        logs.Entries.Should().ContainSingle(static x => x.Level == LogLevel.Warning && x.Text.Contains("snapshot compare", StringComparison.Ordinal));
        logs.Entries.Should().NotContain(x => x.Text.Contains(Sentinel, StringComparison.Ordinal), "the compare read the file; none of it is in a log line or an exception");
        menu.Markup.Should().NotContain(Sentinel);
    }

    [Fact]
    public async Task A_file_that_stops_arriving_from_the_browser_is_quiet_and_asks_to_choose_it_again()
    {
        foreach (Exception lost in (Exception[]) [new IOException("the circuit went away"), new TimeoutException("stalled"), new Microsoft.JSInterop.JSDisconnectedException("gone")])
        {
            SnapshotCompareFixtures.FullLogProvider logs = new();
            (PostgresStudioComponentContext context, IRenderedComponent<SnapshotMenu> menu) = await OpenAsync(services =>
            {
                services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
                services.AddScoped<ISnapshotCompareService>(_ => new FailingCompare(lost));
            });
            await using PostgresStudioComponentContext owned = context;

            Upload(menu, "x.json", "{}");

            menu.WaitForAssertion(() => menu.Find(".ms-snapshot-compare-message").TextContent.Should().Be("The file did not arrive from the browser; choose it again."));
            logs.Entries.Should().NotContain(static x => x.Level >= LogLevel.Warning, lost.GetType().Name + " is an expected state and logs quietly");
            logs.Entries.Should().Contain(static x => x.Level == LogLevel.Debug && x.Text.Contains("snapshot compare", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_change_of_scope_clears_the_comparison_and_the_json_view()
    {
        (PostgresStudioComponentContext context, IRenderedComponent<SnapshotMenu> menu) = await OpenAsync();
        await using PostgresStudioComponentContext owned = context;

        Upload(menu, "x.json", await SnapshotCompareFixtures.DownloadedJsonAsync(context));
        menu.WaitForAssertion(() => menu.FindAll(".ms-snapshot-compare-verdict").Should().ContainSingle());
        AngleSharp.Dom.IElement before = menu.Find("input[type=file]");

        await menu.InvokeAsync(() => context.State.NotifyAsync());

        menu.WaitForAssertion(() => menu.FindAll(".ms-snapshot-compare-result").Should().BeEmpty("a comparison is about the database it was made in"));
        menu.Find("input[type=file]").Should().NotBeSameAs(before);

        await Select(menu, "View JSON");
        menu.WaitForAssertion(() => menu.FindAll("section[aria-label='Snapshot JSON']").Should().ContainSingle());

        await menu.InvokeAsync(() => context.State.NotifyAsync());

        menu.WaitForAssertion(() => menu.FindAll("section[aria-label='Snapshot JSON']").Should().BeEmpty("the JSON shown is about the database it was built for"));
    }

    /// <summary>A service that reads the file it is handed - so it knows what is in it - and then fails with a message of its own.</summary>
    private sealed class FailingCompare(Exception failure) : ISnapshotCompareService
    {
        public async Task<SnapshotComparison> CompareAsync(StudioScope scope, Func<CancellationToken, Stream> openFile, string? fileName, CancellationToken cancellationToken = default)
        {
            await using Stream file = openFile(cancellationToken);
            using StreamReader reader = new(file);
            _ = await reader.ReadToEndAsync(cancellationToken);

            throw failure;
        }
    }
}
