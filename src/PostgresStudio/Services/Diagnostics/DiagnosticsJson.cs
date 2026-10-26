using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>
/// The snapshot as JSON, <c>schemaVersion</c> 1 (API design §4.1): the one writer behind
/// <see cref="PostgresDiagnosticsSnapshot.ToJson" /> and the studio's own download, so both are one shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-rolled, on purpose.</b> A <see cref="Utf8JsonWriter" /> and nothing else: no reflection, no serializer options of
/// the host's or a global's, so a host's own <c>JsonSerializerOptions</c> are never touched and never change the shape.
/// </para>
/// <para>
/// <b>The conventions.</b> Property names and enum values are camelCase; times are ISO 8601 in UTC (<c>"O"</c>); durations
/// are seconds, as numbers, under a name ending in <c>Seconds</c>; byte counts are numbers (exact in JavaScript up to 9 PB)
/// and <c>queryId</c> is a string, because a 64-bit hash is not; a number that is not finite is <c>null</c>. <b>Every key is
/// always present</b>, with <c>null</c> for "not asked, not allowed or not known", so a consumer reads one shape whatever was
/// found. The one regrouping of the public types: the snapshot's four findings properties are one <c>findings</c> object
/// (<c>items</c>, <c>checks</c>, <c>measuredAt</c>, <c>withheld</c>), and a finding's <c>key</c> is written too.
/// </para>
/// <para>
/// <b>Versioning.</b> Adding a key keeps the version; removing or renaming one, changing its type or what it means bumps
/// <see cref="SchemaVersion" />. The studio writes only its current version (<c>docs/diagnostics.md#snapshot-json</c>).
/// </para>
/// <para>
/// <b>Written to be read.</b> It is a file attached to a ticket, a page in the studio's JSON viewer, text on a clipboard - never
/// markup the studio embeds in a page - so characters are written as they are, a time's <c>+00:00</c> and an apostrophe
/// included, and only what JSON itself requires is escaped: quotes, backslashes and control characters. A host that puts the
/// text into HTML encodes it there, as it would any text.
/// </para>
/// </remarks>
internal static class DiagnosticsJson
{
    /// <summary>The <c>schema</c> value: what makes a file self-describing when it is attached to a ticket.</summary>
    public const string Schema = "postgresstudio-diagnostics";

    /// <summary>The version this release writes.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The snapshot, as indented JSON.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public static string Write(PostgresDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using MemoryStream stream = new();

        using (Utf8JsonWriter json = new(stream, Options))
        {
            WriteSnapshot(json, snapshot);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int) stream.Length);
    }

    /// <summary>An enum value as the JSON spells it: its name, camelCase.</summary>
    internal static string Name<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static void WriteSnapshot(Utf8JsonWriter json, PostgresDiagnosticsSnapshot snapshot)
    {
        json.WriteStartObject();
        json.WriteString("schema", Schema);
        json.WriteNumber("schemaVersion", snapshot.SchemaVersion);
        json.WriteString("producer", snapshot.Producer);
        Time(json, "generatedAt", snapshot.GeneratedAt);

        json.WritePropertyName("database");
        if (snapshot.Database is { } database)
        {
            json.WriteStartObject();
            json.WriteString("source", database.Source);
            json.WriteString("database", database.Database);
            json.WriteEndObject();
        }
        else
        {
            json.WriteNullValue();
        }

        json.WriteString("outcome", Name(snapshot.Outcome));
        Text(json, "reason", snapshot.Reason);
        Server(json, snapshot.Server);
        Compute(json, snapshot.Compute);

        json.WriteStartObject("findings");
        Time(json, "measuredAt", snapshot.FindingsMeasuredAt);
        json.WriteNumber("withheld", snapshot.FindingsWithheld);
        json.WriteStartArray("items");
        foreach (PostgresFinding finding in snapshot.Findings)
        {
            Finding(json, finding);
        }

        json.WriteEndArray();
        json.WriteStartArray("checks");
        foreach (PostgresCheck check in snapshot.Checks)
        {
            Check(json, check);
        }

        json.WriteEndArray();
        json.WriteEndObject();

        Storage(json, snapshot.Storage);
        Statements(json, snapshot.Statements);
        Settings(json, snapshot.Settings);
        Connections(json, snapshot.Connections);

        json.WriteStartObject("withheld");
        foreach (KeyValuePair<string, string> section in snapshot.Withheld.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            json.WriteString(section.Key, section.Value);
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void Server(Utf8JsonWriter json, PostgresServerInfo? server)
    {
        json.WritePropertyName("server");

        if (server is null)
        {
            json.WriteNullValue();
            return;
        }

        json.WriteStartObject();
        json.WriteNumber("versionNumber", server.VersionNumber);
        json.WriteString("version", server.Version);
        json.WriteString("platform", Name(server.Platform));
        Text(json, "instance", server.Instance);
        json.WriteBoolean("inRecovery", server.InRecovery);
        Time(json, "startedAt", server.StartedAt);
        json.WriteEndObject();
    }

    private static void Compute(Utf8JsonWriter json, PostgresComputeCapacity? compute)
    {
        json.WritePropertyName("compute");

        if (compute is null)
        {
            json.WriteNullValue();
            return;
        }

        json.WriteStartObject();
        Number(json, "current", compute.Current);
        Number(json, "minimum", compute.Minimum);
        Number(json, "maximum", compute.Maximum);
        json.WriteString("unit", compute.Unit);
        Time(json, "measuredAt", compute.MeasuredAt);
        Boolean(json, "ioOptimized", compute.IoOptimized);
        json.WriteEndObject();
    }

    private static void Finding(Utf8JsonWriter json, PostgresFinding finding)
    {
        json.WriteStartObject();
        json.WriteString("code", finding.Code);
        Text(json, "variant", finding.Variant);
        Text(json, "subject", finding.Subject);
        Text(json, "tenantId", finding.TenantId);
        json.WriteString("severity", Name(finding.Severity));
        json.WriteString("category", Name(finding.Category));
        json.WriteString("title", finding.Title);
        json.WriteString("detail", finding.Detail);
        Text(json, "caveat", finding.Caveat);
        Text(json, "action", finding.Action);
        Text(json, "actionExplanation", finding.ActionExplanation);
        Text(json, "actionKind", finding.ActionKind is { } kind ? Name(kind) : null);
        Text(json, "actionRequires", finding.ActionRequires);
        json.WriteStartArray("evidence");
        foreach (KeyValuePair<string, string> item in finding.Evidence)
        {
            json.WriteStartObject();
            json.WriteString("label", item.Key);
            json.WriteString("value", item.Value);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteString("docsUrl", finding.DocsUrl);
        Time(json, "measuredAt", finding.MeasuredAt);
        json.WriteString("key", finding.Key);
        json.WriteEndObject();
    }

    private static void Check(Utf8JsonWriter json, PostgresCheck check)
    {
        json.WriteStartObject();
        json.WriteString("code", check.Code);
        json.WriteString("category", Name(check.Category));
        json.WriteString("title", check.Title);
        json.WriteStartArray("variants");
        foreach (string variant in check.Variants)
        {
            json.WriteStringValue(variant);
        }

        json.WriteEndArray();
        json.WriteBoolean("requiresActivity", check.RequiresActivity);
        json.WriteString("docsUrl", check.DocsUrl);
        Text(json, "outcome", check.Outcome is { } outcome ? Name(outcome) : null);
        Text(json, "reason", check.Reason);
        Text(json, "sqlState", check.SqlState);
        json.WriteNumber("findings", check.Findings);
        json.WriteEndObject();
    }

    private static void Storage(Utf8JsonWriter json, PostgresStorageSection? storage)
    {
        json.WritePropertyName("storage");

        if (storage is null)
        {
            json.WriteNullValue();
            return;
        }

        json.WriteStartObject();
        json.WriteString("outcome", Name(storage.Outcome));
        Text(json, "reason", storage.Reason);
        Time(json, "measuredAt", storage.MeasuredAt);
        json.WriteNumber("databaseBytes", storage.DatabaseBytes);
        json.WriteBoolean("sizesAreEstimates", storage.SizesAreEstimates);
        Time(json, "statisticsSince", storage.StatisticsSince);
        Integer(json, "growthBytesPerDay", storage.GrowthBytesPerDay);
        json.WriteStartArray("largestTables");
        foreach (PostgresTableStorage table in storage.LargestTables)
        {
            json.WriteStartObject();
            json.WriteString("schema", table.Schema);
            json.WriteString("name", table.Name);
            json.WriteString("ownership", Name(table.Ownership));
            Text(json, "owner", table.Owner);
            json.WriteNumber("totalBytes", table.TotalBytes);
            json.WriteNumber("tableBytes", table.TableBytes);
            json.WriteNumber("indexBytes", table.IndexBytes);
            json.WriteNumber("toastBytes", table.ToastBytes);
            json.WriteNumber("estimatedRows", table.EstimatedRows);
            Number(json, "shareOfDatabase", table.ShareOfDatabase);
            Number(json, "hotUpdateRatio", table.HotUpdateRatio);
            Integer(json, "growthBytesPerDay", table.GrowthBytesPerDay);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void Statements(Utf8JsonWriter json, PostgresStatementsSection? statements)
    {
        json.WritePropertyName("statements");

        if (statements is null)
        {
            json.WriteNullValue();
            return;
        }

        json.WriteStartObject();
        json.WriteString("outcome", Name(statements.Outcome));
        Text(json, "reason", statements.Reason);
        Time(json, "measuredAt", statements.MeasuredAt);
        Time(json, "statisticsSince", statements.StatisticsSince);
        Number(json, "windowSeconds", statements.Window?.TotalSeconds);
        Number(json, "totalMilliseconds", statements.TotalMilliseconds);
        json.WriteStartArray("top");
        foreach (PostgresStatement statement in statements.Top)
        {
            json.WriteStartObject();
            json.WriteString("queryId", statement.QueryId.ToString(CultureInfo.InvariantCulture));
            Text(json, "text", statement.Text);
            json.WriteBoolean("textTruncated", statement.TextTruncated);
            Text(json, "textWithheld", statement.TextWithheld);
            json.WriteNumber("calls", statement.Calls);
            Number(json, "totalMilliseconds", statement.TotalMilliseconds);
            Number(json, "meanMilliseconds", statement.MeanMilliseconds);
            Number(json, "shareOfTime", statement.ShareOfTime);
            json.WriteNumber("rows", statement.Rows);
            json.WriteNumber("sharedBlocksHit", statement.SharedBlocksHit);
            json.WriteNumber("sharedBlocksRead", statement.SharedBlocksRead);
            json.WriteNumber("tempBlocksWritten", statement.TempBlocksWritten);
            Integer(json, "storageBlocksRead", statement.StorageBlocksRead);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void Settings(Utf8JsonWriter json, PostgresSettingsSection? settings)
    {
        json.WritePropertyName("settings");

        if (settings is null)
        {
            json.WriteNullValue();
            return;
        }

        json.WriteStartObject();
        json.WriteString("outcome", Name(settings.Outcome));
        Text(json, "reason", settings.Reason);
        Time(json, "measuredAt", settings.MeasuredAt);
        json.WriteStartArray("settings");
        foreach (PostgresSetting setting in settings.Settings)
        {
            json.WriteStartObject();
            json.WriteString("name", setting.Name);
            json.WriteString("group", setting.Group);
            Text(json, "value", setting.Value);
            Text(json, "unit", setting.Unit);
            Text(json, "source", setting.Source);
            json.WriteBoolean("pendingRestart", setting.PendingRestart);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("extensions");
        foreach (PostgresExtension extension in settings.Extensions)
        {
            json.WriteStartObject();
            json.WriteString("name", extension.Name);
            Text(json, "installedVersion", extension.InstalledVersion);
            Text(json, "availableVersion", extension.AvailableVersion);
            json.WriteBoolean("usable", extension.Usable);
            Text(json, "note", extension.Note);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void Connections(Utf8JsonWriter json, PostgresConnectionsSection? connections)
    {
        json.WritePropertyName("connections");

        if (connections is null)
        {
            json.WriteNullValue();
            return;
        }

        json.WriteStartObject();
        json.WriteString("outcome", Name(connections.Outcome));
        Text(json, "reason", connections.Reason);
        Time(json, "measuredAt", connections.MeasuredAt);
        json.WriteNumber("maxConnections", connections.MaxConnections);
        json.WriteNumber("reservedConnections", connections.ReservedConnections);
        json.WriteNumber("serverConnections", connections.ServerConnections);
        json.WritePropertyName("database");
        Counts(json, connections.Database ?? new PostgresConnectionCounts());
        json.WriteStartArray("byApplication");
        foreach (PostgresConnectionCounts application in connections.ByApplication)
        {
            Counts(json, application);
        }

        json.WriteEndArray();
        json.WriteBoolean("allSessionsVisible", connections.AllSessionsVisible);
        json.WriteEndObject();
    }

    private static void Counts(Utf8JsonWriter json, PostgresConnectionCounts counts)
    {
        json.WriteStartObject();
        Text(json, "applicationName", counts.ApplicationName);
        json.WriteNumber("total", counts.Total);
        json.WriteNumber("active", counts.Active);
        json.WriteNumber("idle", counts.Idle);
        json.WriteNumber("idleInTransaction", counts.IdleInTransaction);
        json.WriteNumber("waiting", counts.Waiting);
        json.WriteNumber("stateUnknown", counts.StateUnknown);
        Number(json, "oldestTransactionSeconds", counts.OldestTransaction?.TotalSeconds);
        json.WriteEndObject();
    }

    private static void Text(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }

    private static void Time(Utf8JsonWriter json, string name, DateTimeOffset? value) =>
        Text(json, name, value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static void Number(Utf8JsonWriter json, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number))
        {
            json.WriteNumber(name, number);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void Integer(Utf8JsonWriter json, string name, long? value)
    {
        if (value is { } number)
        {
            json.WriteNumber(name, number);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void Boolean(Utf8JsonWriter json, string name, bool? value)
    {
        if (value is { } flag)
        {
            json.WriteBoolean(name, flag);
        }
        else
        {
            json.WriteNull(name);
        }
    }
}
