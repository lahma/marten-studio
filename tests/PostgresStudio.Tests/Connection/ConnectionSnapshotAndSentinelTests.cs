using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

using Bunit;

using Npgsql;

using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Statements;
using PostgresStudio.Services.Storage;
using PostgresStudio.Tests.Components;

using static PostgresStudio.Tests.Connection.ConnectionTestFacts;
using static PostgresStudio.Tests.Diagnostics.DiagnosticsTestFacts;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// NP3's sentinel test (NP-DESIGN §2.6) for the surfaces the page adds, and the snapshot's <c>connection</c> section: a data source
/// whose every string keyword holds <c>S3NT1NEL-&lt;keyword&gt;</c>, both passwords, a custom option and the environment's
/// <c>PGPASSWORD</c> - and no withheld sentinel in the model, the rendered page, the snapshot JSON or the audit.
/// </summary>
public sealed class ConnectionSnapshotAndSentinelTests
{
    private static string Sentinel(string keyword) => "S3NT1NEL-" + keyword;

    private static readonly string[] Withheld =
    [
        Sentinel("Password"), Sentinel("SSL Password"), Sentinel("opt"), "S3NT1NEL-env-pgpassword",
    ];

    private static readonly HashSet<string> NotStringSentinelled = new(StringComparer.Ordinal) { "Options", "Host", "Target Session Attributes", "Require Auth" };

    private static string SentinelConnectionString()
    {
        NpgsqlConnectionStringBuilder builder = new() { PersistSecurityInfo = true };

        foreach (PropertyInfo property in typeof(NpgsqlConnectionStringBuilder).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (property.PropertyType == typeof(string)
                && property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName is { } keyword
                && property.GetCustomAttribute<ObsoleteAttribute>() is null
                && !NotStringSentinelled.Contains(keyword))
            {
                builder[keyword] = Sentinel(keyword);
            }
        }

        builder.Host = "db.example";
        builder.Options = "-c app.secret=" + Sentinel("opt");
        return builder.ConnectionString;
    }

    private static ConnectionPageView SentinelView(SessionDetailsAccess? activity = null)
    {
        ConnectionFacts facts = Facts(
            SentinelConnectionString(),
            Observed(),
            environment: new FakeEnvironment(("PGPASSWORD", "S3NT1NEL-env-pgpassword"), ("PGSSLKEY", "S3NT1NEL-env-sslkey")));

        return View(facts, Report(), activity ?? GateOpen);
    }

    private static readonly JsonSerializerOptions DumpOptions = new() { IncludeFields = true, MaxDepth = 64 };

    private static string Dump(ConnectionPageView view) =>
        JsonSerializer.Serialize(view, DumpOptions)
        + string.Join('\n', view.Settings.Select(static x => x.Row.Keyword + "=" + x.Row.Value + " " + x.Row.Note + " " + x.Row.DefaultValue))
        + string.Join('\n', view.Cards.Concat(view.Notes).Select(static x => x.Heading + x.Text + string.Join('|', x.Fixes.Select(static f => f.Code + f.Note))))
        + string.Join('\n', view.Observed.Lines.Select(static x => x.Label + x.Value + x.Note))
        + string.Join('\n', view.Header.Select(static x => x.Label + x.Value))
        + string.Join('\n', view.SettingsNotes);

    [Fact]
    public void No_withheld_sentinel_is_in_the_pages_model_however_it_is_dumped()
    {
        ConnectionPageView view = SentinelView();

        string dump = Dump(view);

        foreach (string sentinel in Withheld)
        {
            dump.Should().NotContain(sentinel);
        }

        dump.Should().Contain(Sentinel("Application Name"), "the dump is not vacuous: a shown keyword's value is in it");
    }

    [Fact]
    public async Task No_withheld_sentinel_is_in_the_rendered_page_with_every_setting_shown()
    {
        await using PostgresStudioComponentContext context = new();
        context.Connection.View = SentinelView();
        context.JSInterop.Setup<string?>("martenStudio.prefs.get", static x => x.Arguments.Count == 1 && (string?) x.Arguments[0] == "ms_conn_only_differs").SetResult("0");
        await context.ReadyAsync();

        IRenderedComponent<ConnectionPage> page = context.Render<ConnectionPage>();
        page.WaitForAssertion(() => page.FindAll("tr#conn-row-application-name").Should().ContainSingle());

        string text = page.Markup;
        foreach (string sentinel in Withheld)
        {
            text.Should().NotContain(sentinel);
        }

        text.Should().Contain(Sentinel("Application Name"));
        page.Find("tr#conn-row-password .ms-conn-value").TextContent.Should().Contain("set").And.NotContain("S3NT1NEL");
        page.Find("tr#conn-row-options .ms-conn-value").TextContent.Should().Contain("app.secret=(value withheld)");
    }

    // -------------------------------------------------------------------------------------------------
    // The snapshot's connection section
    // -------------------------------------------------------------------------------------------------

    private static readonly HealthCheckRegistry Registry = new(PostgresStudioDescriptor.Instance);

    internal static string Json(ConnectionPageView? view, ConnectionApplicationsView? applications = null) =>
        VisitorSnapshotBuilder.Compose(
            new PostgresDatabaseRef("default", "app"),
            new HealthReport { StoreKey = "default", DatabaseId = "app", MeasuredAt = At },
            new StorageView { MeasuredAt = At },
            StatementsView.Refused(DatabaseRefusal.CapabilityOff, "Statements need Capabilities.ViewDatabaseActivity."),
            new ServerPageView { MeasuredAt = At },
            new SessionActivitySummary { MeasuredAt = At },
            SessionActivityDetails.Refused(DatabaseRefusal.CapabilityOff, "Session details need Capabilities.ViewDatabaseActivity."),
            new ReplicationView { MeasuredAt = At },
            Registry,
            "PostgresStudio 1.1.0",
            At,
            view,
            applications).ToJson();

    [Fact]
    public void The_snapshot_carries_the_pages_settings_hints_and_what_the_server_saw()
    {
        ConnectionPageView view = View(Facts("Host=db.example;Database=app;Application Name=orders;SSL Mode=Prefer", Observed()), Report(), GateClosed);

        using JsonDocument document = JsonDocument.Parse(Json(view, applications: null));
        JsonElement connection = document.RootElement.GetProperty("connection");

        connection.GetProperty("tls").GetString().Should().Be("encryptedNotVerified");
        connection.GetProperty("npgsqlVersion").GetString().Should().Be(NpgsqlSettingsReader.LoadedVersion);
        connection.GetProperty("platform").ValueKind.Should().Be(JsonValueKind.Null, "no managed platform was detected, and a name is not a sentence");
        connection.GetProperty("pooler").ValueKind.Should().Be(JsonValueKind.Null);

        JsonElement host = connection.GetProperty("settings").EnumerateArray().Single(static x => x.GetProperty("keyword").GetString() == "Host");
        host.GetProperty("value").GetString().Should().Be("db.example");
        host.GetProperty("from").GetString().Should().Be("connectionString");
        host.GetProperty("default").GetString().Should().Be("not set");

        connection.GetProperty("settings").EnumerateArray().Single(static x => x.GetProperty("keyword").GetString() == "Password")
            .GetProperty("from").GetString().Should().Be("notVisible");

        connection.GetProperty("observed").EnumerateArray().Select(static x => x.GetProperty("name").GetString()).Should().Contain(["Logged in as", "statement_timeout"]);

        JsonElement hint = connection.GetProperty("hints").EnumerateArray().Single(static x => x.GetProperty("id").GetString() == "TLS");
        hint.GetProperty("connectionStringFix").GetString().Should().StartWith("SSL Mode=VerifyFull");
        hint.GetProperty("builderFix").GetString().Should().Contain("SslMode.VerifyFull");
    }

    [Fact]
    public void The_snapshot_names_the_platform_and_lists_the_poolers_in_front_of_the_server()
    {
        ConnectionPageView view = View(
            Facts("Host=app.proxy-abc123.eu-west-1.rds.amazonaws.com", Observed(), PostgresPlatformKind.AmazonRds),
            Report(),
            GateClosed);

        using JsonDocument document = JsonDocument.Parse(Json(view, applications: null));
        JsonElement connection = document.RootElement.GetProperty("connection");

        connection.GetProperty("platform").GetString().Should().Be("Amazon RDS for PostgreSQL");
        connection.GetProperty("pooler").EnumerateArray().Select(static x => x.GetString()).Should().Equal("Amazon RDS Proxy");
    }

    [Fact]
    public void A_visitor_short_of_the_activity_gate_gets_the_settings_and_a_withheld_entry_for_the_sessions()
    {
        ConnectionPageView view = View(Facts(Remote, Observed()), Report(), GateClosed);

        using JsonDocument document = JsonDocument.Parse(Json(view, applications: null));
        JsonElement root = document.RootElement;

        root.GetProperty("connection").GetProperty("settings").GetArrayLength().Should().BeGreaterThan(10);
        root.GetProperty("connection").GetProperty("applications").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("withheld").GetProperty("connection.applications").GetString().Should().Contain("Capabilities.ViewDatabaseActivity");
    }

    [Fact]
    public void Through_the_activity_gate_the_snapshot_names_the_applications_sessions()
    {
        ConnectionPageView view = View(Facts(Remote, Observed()), Report(), GateOpen);
        ConnectionApplicationsView applications = new()
        {
            Role = "svc",
            Rows = [new ConnectionApplicationRow("billing-api", 12, At, 12, 12)],
            ApplicationCount = 1,
        };

        using JsonDocument document = JsonDocument.Parse(Json(view, applications));
        JsonElement application = document.RootElement.GetProperty("connection").GetProperty("applications")[0];

        application.GetProperty("applicationName").GetString().Should().Be("billing-api");
        application.GetProperty("sessions").GetInt64().Should().Be(12);
        application.GetProperty("tlsOn").GetInt64().Should().Be(12);
        document.RootElement.GetProperty("withheld").TryGetProperty("connection.applications", out _).Should().BeFalse();
    }

    [Fact]
    public void A_visitor_the_database_gate_refuses_gets_a_withheld_entry_and_no_connection_section()
    {
        ConnectionPageView refused = ConnectionPageView.Refused(ConnectionRefusal.StorePolicy, "Your account may not see how this application connects.");

        using JsonDocument document = JsonDocument.Parse(Json(refused));

        document.RootElement.GetProperty("connection").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("withheld").GetProperty("connection").GetString().Should().Contain("may not see how this application connects");
    }

    [Fact]
    public void A_snapshot_a_host_asks_the_api_for_writes_connection_null_the_key_is_always_present_and_the_section_studio_only()
    {
        PostgresDiagnosticsSnapshot api = new() { SchemaVersion = 1, Producer = "PostgresStudio 1.1.0", GeneratedAt = At, Database = new PostgresDatabaseRef("default", "app") };

        using JsonDocument document = JsonDocument.Parse(api.ToJson());

        document.RootElement.TryGetProperty("connection", out JsonElement connection).Should().BeTrue("every key is always present");
        connection.ValueKind.Should().Be(JsonValueKind.Null);
        typeof(PostgresDiagnosticsSnapshot).GetProperty("Connection", BindingFlags.Public | BindingFlags.Instance).Should().BeNull("D31: the public read model does not carry it");
    }

    [Fact]
    public void No_withheld_sentinel_is_in_the_snapshot_json()
    {
        ConnectionPageView view = SentinelView();
        ConnectionApplicationsView applications = new() { Role = "svc", Rows = [new ConnectionApplicationRow("billing-api", 1, At, 1, 1)], ApplicationCount = 1 };

        string json = Json(view, applications);

        foreach (string sentinel in Withheld)
        {
            json.Should().NotContain(sentinel);
        }

        json.Should().Contain(Sentinel("Application Name"), "not vacuous: a shown keyword's value is in the file");
        json.Should().Contain("app.secret=(value withheld)");
    }
}
