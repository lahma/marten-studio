using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>What reading a snapshot file came to: the snapshot, or the one sentence that says why not.</summary>
/// <param name="Snapshot">The snapshot, or <see langword="null" /> when the file was refused.</param>
/// <param name="Problem">The refusal, in the studio's own words: it names a key path the studio writes, never a value from the file.</param>
/// <param name="SkippedItems">How many items named a value this studio does not know, and were left out.</param>
internal sealed record SnapshotFileRead(PostgresDiagnosticsSnapshot? Snapshot, string? Problem, int SkippedItems);

/// <summary>
/// Reads a <c>schemaVersion</c> 1 snapshot file back into the model (1.2-H-B, D-HEALTH C5): the mirror of
/// <see cref="DiagnosticsJson.Write" />, and as strict as a reader of somebody's file must be.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-closed.</b> The depth, the size, every list and every string are bounded; a wrong type is a refusal that names the
/// <i>studio's own</i> key path ("<c>findings.items[12].severity is not a severity</c>") and never a value from the file, and
/// a parse error says where (line and byte) and never what the parser read. Whatever the file holds is therefore not echoed by
/// any message this class writes.
/// </para>
/// <para>
/// <b>Tolerant where the contract is.</b> A key the studio does not know is ignored, a section the file lacks reads as absent,
/// and an enumeration member this studio does not know - allowed within version 1 - leaves that item out and counts it. A
/// finding's <c>key</c> is ignored: its identity is recomputed from code, variant and subject.
/// </para>
/// </remarks>
internal static class SnapshotFileReader
{
    /// <summary>The largest file read: far over anything the studio writes (at most 100 statements of 4,096 characters, findings, settings), so it is headroom, not a limit anyone meets.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>The deepest nesting read.</summary>
    public const int MaxDepth = 16;

    /// <summary>The longest string read, in characters.</summary>
    public const int MaxString = 65_536;

    private const int MaxFindings = 5_000;
    private const int MaxEvidence = 100;
    private const int MaxChecks = 1_000;
    private const int MaxTables = 1_000;
    private const int MaxStatements = 1_000;
    private const int MaxSettings = 2_000;
    private const int MaxExtensions = 1_000;
    private const int MaxConnectionRows = 1_000;
    private const int MaxHints = 500;
    private const int MaxWithheld = 100;
    private const int MaxVariants = 100;

    /// <summary>Reads the snapshot from <paramref name="stream" />, which is read once, to its end or to <see cref="MaxBytes" />.</summary>
    /// <param name="stream">The file's bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<SnapshotFileRead> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] rented = ArrayPool<byte>.Shared.Rent(MaxBytes + 1);

        try
        {
            int length = 0;

            while (length <= MaxBytes)
            {
                int read = await stream.ReadAsync(rented.AsMemory(length, MaxBytes + 1 - length), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (length > MaxBytes)
            {
                return Refused("This file is over 4 MB; a snapshot this studio writes is far smaller, and at most 4 MB is read.");
            }

            // Parsed and dropped before the buffer goes back to the pool: the document refers to it.
            return Read(rented.AsMemory(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>Reads the snapshot from its bytes.</summary>
    /// <param name="bytes">The file's bytes, at most <see cref="MaxBytes" />.</param>
    internal static SnapshotFileRead Read(ReadOnlyMemory<byte> bytes)
    {
        // A byte order mark is not JSON, and some editors add one.
        if (bytes.Span.StartsWith("﻿"u8))
        {
            bytes = bytes[3..];
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        }
        catch (JsonException e)
        {
            string where = e.LineNumber is { } line && e.BytePositionInLine is { } position
                ? " (line " + (line + 1).ToString(CultureInfo.InvariantCulture) + ", byte " + (position + 1).ToString(CultureInfo.InvariantCulture) + ")"
                : string.Empty;

            return Refused("This file is not a snapshot's JSON" + where + ".");
        }

        using (document)
        {
            Parser parser = new();

            try
            {
                PostgresDiagnosticsSnapshot snapshot = parser.Snapshot(document.RootElement);

                return new SnapshotFileRead(snapshot, null, parser.Skipped);
            }
            catch (SnapshotFormatException e)
            {
                return Refused(e.Message);
            }
            catch (InvalidOperationException)
            {
                // Text the parser let through that cannot be read as text (an unpaired surrogate escape): not a snapshot this studio wrote.
                return Refused("This file holds text that is not valid.");
            }
        }
    }

    private static SnapshotFileRead Refused(string problem) => new(null, problem, 0);

    /// <summary>A refusal; its message is the sentence the visitor reads, made of the studio's own words and key paths.</summary>
    private sealed class SnapshotFormatException(string message) : Exception(message);

    private sealed class Parser
    {
        private bool unknownMember;

        public int Skipped { get; private set; }

        public PostgresDiagnosticsSnapshot Snapshot(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new SnapshotFormatException("This file is not a snapshot this studio writes: its top level is not an object.");
            }

            if (!root.TryGetProperty("schema", out JsonElement schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != DiagnosticsJson.Schema)
            {
                throw new SnapshotFormatException("This file is not a snapshot this studio writes: its \"schema\" is not \"" + DiagnosticsJson.Schema + "\".");
            }

            if (!root.TryGetProperty("schemaVersion", out JsonElement version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number))
            {
                throw new SnapshotFormatException("schemaVersion is not a whole number.");
            }

            if (number > DiagnosticsJson.SchemaVersion)
            {
                throw new SnapshotFormatException(
                    "This file was written by a newer studio (schemaVersion " + number.ToString(CultureInfo.InvariantCulture) + "); this one reads " + DiagnosticsJson.SchemaVersion.ToString(CultureInfo.InvariantCulture) + ".");
            }

            if (number < DiagnosticsJson.SchemaVersion)
            {
                throw new SnapshotFormatException("schemaVersion " + number.ToString(CultureInfo.InvariantCulture) + " is not one this studio reads.");
            }

            JsonElement database = Object(root, "database", string.Empty) ?? throw new SnapshotFormatException("database is missing.");
            string source = Text(database, "source", "database.") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(source))
            {
                throw new SnapshotFormatException("database.source is missing.");
            }

            PostgresDatabaseRef reference = new(source, Text(database, "database", "database.") ?? string.Empty);
            JsonElement? findings = Object(root, "findings", string.Empty);

            return new PostgresDiagnosticsSnapshot
            {
                SchemaVersion = number,
                Producer = Text(root, "producer", string.Empty) ?? string.Empty,
                GeneratedAt = Time(root, "generatedAt", string.Empty) ?? throw new SnapshotFormatException("generatedAt is not a time."),
                Database = reference,
                Outcome = Outcome(root, "outcome", string.Empty),
                Reason = Text(root, "reason", string.Empty),
                Server = Server(Object(root, "server", string.Empty)),
                Compute = Compute(Object(root, "compute", string.Empty)),
                Findings = findings is { } f ? FindingItems(f) : [],
                Checks = findings is { } c ? CheckItems(c) : [],
                FindingsMeasuredAt = findings is { } m ? Time(m, "measuredAt", "findings.") : null,
                FindingsWithheld = findings is { } w ? Int(w, "withheld", "findings.") ?? 0 : 0,
                Storage = Storage(Object(root, "storage", string.Empty)),
                Statements = Statements(Object(root, "statements", string.Empty)),
                Settings = Settings(Object(root, "settings", string.Empty)),
                Connections = Connections(Object(root, "connections", string.Empty)),
                ConnectionHistory = ConnectionHistory(Object(root, "connectionHistory", string.Empty)),
                Wal = Wal(Object(root, "wal", string.Empty)),
                Connection = ConnectionSection(Object(root, "connection", string.Empty)),
                Withheld = WithheldMap(Object(root, "withheld", string.Empty)),
            };
        }

        // ------------------------------------------------------------------------------------------------
        // Sections
        // ------------------------------------------------------------------------------------------------

        private PostgresServerInfo? Server(JsonElement? element)
        {
            if (element is not { } e)
            {
                return null;
            }

            unknownMember = false;
            PostgresPlatformKind platform = EnumOf<PostgresPlatformKind>(e, "platform", "server.");

            if (unknownMember)
            {
                Skipped++;
                return null;
            }

            return new PostgresServerInfo
            {
                VersionNumber = Int(e, "versionNumber", "server.") ?? 0,
                Version = Text(e, "version", "server.") ?? string.Empty,
                Platform = platform,
                Instance = Text(e, "instance", "server."),
                InRecovery = Bool(e, "inRecovery", "server.") ?? false,
                StartedAt = Time(e, "startedAt", "server."),
            };
        }

        private static PostgresComputeCapacity? Compute(JsonElement? element)
        {
            if (element is not { } e)
            {
                return null;
            }

            double? current = Double(e, "current", "compute.");
            double? minimum = Double(e, "minimum", "compute.");
            double? maximum = Double(e, "maximum", "compute.");
            string? unit = Text(e, "unit", "compute.");
            DateTimeOffset? measuredAt = Time(e, "measuredAt", "compute.");

            if (current is null || minimum is null || maximum is null || string.IsNullOrWhiteSpace(unit) || measuredAt is null)
            {
                throw new SnapshotFormatException("compute is not a capacity.");
            }

            try
            {
                return PostgresComputeCapacity.Of(current.Value, minimum.Value, maximum.Value, unit, measuredAt.Value) with { IoOptimized = Bool(e, "ioOptimized", "compute.") };
            }
            catch (ArgumentException)
            {
                throw new SnapshotFormatException("compute is not a capacity.");
            }
        }

        private List<PostgresFinding> FindingItems(JsonElement findings)
        {
            List<PostgresFinding> result = [];
            int index = 0;

            foreach (JsonElement item in Items(findings, "items", "findings.", MaxFindings))
            {
                string path = "findings.items[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                Entry(item, path);
                unknownMember = false;

                PostgresSeverity severity = EnumOf<PostgresSeverity>(item, "severity", path, required: true);
                PostgresFindingCategory category = EnumOf<PostgresFindingCategory>(item, "category", path, required: true);
                PostgresActionKind? kind = OptionalEnum<PostgresActionKind>(item, "actionKind", path);

                if (unknownMember)
                {
                    Skipped++;
                    continue;
                }

                result.Add(new PostgresFinding
                {
                    Code = Text(item, "code", path, required: true)!,
                    Variant = Text(item, "variant", path),
                    Subject = Text(item, "subject", path),
                    TenantId = Text(item, "tenantId", path),
                    Severity = severity,
                    Category = category,
                    Title = Text(item, "title", path) ?? string.Empty,
                    Detail = Text(item, "detail", path) ?? string.Empty,
                    Caveat = Text(item, "caveat", path),
                    Action = Text(item, "action", path),
                    ActionExplanation = Text(item, "actionExplanation", path),
                    ActionKind = kind,
                    ActionRequires = Text(item, "actionRequires", path),
                    Evidence = EvidenceItems(item, path),
                    DocsUrl = Text(item, "docsUrl", path) ?? string.Empty,
                    MeasuredAt = Time(item, "measuredAt", path) ?? default,
                    Accepted = Bool(item, "accepted", path) ?? false,
                    AcceptedReason = Text(item, "acceptedReason", path),
                });
            }

            return result;
        }

        private static List<KeyValuePair<string, string>> EvidenceItems(JsonElement finding, string path)
        {
            List<KeyValuePair<string, string>> result = [];
            int index = 0;

            foreach (JsonElement item in Items(finding, "evidence", path, MaxEvidence))
            {
                string itemPath = path + "evidence[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                Entry(item, itemPath);
                result.Add(new KeyValuePair<string, string>(Text(item, "label", itemPath) ?? string.Empty, Text(item, "value", itemPath) ?? string.Empty));
            }

            return result;
        }

        private List<PostgresCheck> CheckItems(JsonElement findings)
        {
            List<PostgresCheck> result = [];
            int index = 0;

            foreach (JsonElement item in Items(findings, "checks", "findings.", MaxChecks))
            {
                string path = "findings.checks[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                Entry(item, path);
                unknownMember = false;

                PostgresFindingCategory category = EnumOf<PostgresFindingCategory>(item, "category", path, required: true);

                if (unknownMember)
                {
                    Skipped++;
                    continue;
                }

                List<string> variants = [];

                foreach (JsonElement variant in Items(item, "variants", path, MaxVariants))
                {
                    variants.Add(Value(variant, path + "variants"));
                }

                result.Add(new PostgresCheck
                {
                    Code = Text(item, "code", path, required: true)!,
                    Category = category,
                    Title = Text(item, "title", path) ?? string.Empty,
                    Variants = variants,
                    RequiresActivity = Bool(item, "requiresActivity", path) ?? false,
                    DocsUrl = Text(item, "docsUrl", path) ?? string.Empty,
                    Outcome = Present(item, "outcome") ? Outcome(item, "outcome", path) : null,
                    Reason = Text(item, "reason", path),
                    SqlState = Text(item, "sqlState", path),
                    Findings = Int(item, "findings", path) ?? 0,
                });
            }

            return result;
        }

        private PostgresStorageSection? Storage(JsonElement? element)
        {
            if (element is not { } e)
            {
                return null;
            }

            List<PostgresTableStorage> tables = [];
            int index = 0;

            foreach (JsonElement item in Items(e, "largestTables", "storage.", MaxTables))
            {
                string path = "storage.largestTables[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                Entry(item, path);
                unknownMember = false;

                PostgresTableOwnership ownership = EnumOf<PostgresTableOwnership>(item, "ownership", path, required: true);

                if (unknownMember)
                {
                    Skipped++;
                    continue;
                }

                tables.Add(new PostgresTableStorage
                {
                    Schema = Text(item, "schema", path, required: true)!,
                    Name = Text(item, "name", path, required: true)!,
                    Ownership = ownership,
                    Owner = Text(item, "owner", path),
                    TotalBytes = Size(item, "totalBytes", path) ?? 0,
                    TableBytes = Size(item, "tableBytes", path) ?? 0,
                    IndexBytes = Size(item, "indexBytes", path) ?? 0,
                    ToastBytes = Size(item, "toastBytes", path) ?? 0,
                    EstimatedRows = Size(item, "estimatedRows", path) ?? 0,
                    ShareOfDatabase = Double(item, "shareOfDatabase", path) ?? 0,
                    HotUpdateRatio = Double(item, "hotUpdateRatio", path),
                    GrowthBytesPerDay = Long(item, "growthBytesPerDay", path),
                    AllVisibleRatio = Double(item, "allVisibleRatio", path),
                });
            }

            return new PostgresStorageSection
            {
                Outcome = Outcome(e, "outcome", "storage."),
                Reason = Text(e, "reason", "storage."),
                MeasuredAt = Time(e, "measuredAt", "storage.") ?? default,
                DatabaseBytes = Size(e, "databaseBytes", "storage.") ?? 0,
                SizesAreEstimates = Bool(e, "sizesAreEstimates", "storage.") ?? false,
                StatisticsSince = Time(e, "statisticsSince", "storage."),
                GrowthBytesPerDay = Long(e, "growthBytesPerDay", "storage."),
                LargestTables = tables,
            };
        }

        private PostgresStatementsSection? Statements(JsonElement? element)
        {
            if (element is not { } e)
            {
                return null;
            }

            List<PostgresStatement> top = [];
            int index = 0;

            foreach (JsonElement item in Items(e, "top", "statements.", MaxStatements))
            {
                string path = "statements.top[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                Entry(item, path);

                string? queryId = Text(item, "queryId", path, required: true);

                if (!long.TryParse(queryId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id))
                {
                    throw new SnapshotFormatException(path + "queryId is not a number.");
                }

                top.Add(new PostgresStatement
                {
                    QueryId = id,
                    Text = Text(item, "text", path),
                    TextTruncated = Bool(item, "textTruncated", path) ?? false,
                    TextWithheld = Text(item, "textWithheld", path),
                    Calls = Size(item, "calls", path) ?? 0,
                    TotalMilliseconds = Double(item, "totalMilliseconds", path) ?? 0,
                    MeanMilliseconds = Double(item, "meanMilliseconds", path) ?? 0,
                    ShareOfTime = Double(item, "shareOfTime", path) ?? 0,
                    Rows = Size(item, "rows", path) ?? 0,
                    SharedBlocksHit = Size(item, "sharedBlocksHit", path) ?? 0,
                    SharedBlocksRead = Size(item, "sharedBlocksRead", path) ?? 0,
                    TempBlocksWritten = Size(item, "tempBlocksWritten", path) ?? 0,
                    StorageBlocksRead = Size(item, "storageBlocksRead", path),
                });
            }

            double? window = Double(e, "windowSeconds", "statements.");

            return new PostgresStatementsSection
            {
                Outcome = Outcome(e, "outcome", "statements."),
                Reason = Text(e, "reason", "statements."),
                MeasuredAt = Time(e, "measuredAt", "statements.") ?? default,
                StatisticsSince = Time(e, "statisticsSince", "statements."),
                Window = Seconds(window, "statements.windowSeconds"),
                TotalMilliseconds = Double(e, "totalMilliseconds", "statements.") ?? 0,
                Top = top,
            };
        }

        private PostgresSettingsSection? Settings(JsonElement? element)
        {
            if (element is not { } e)
            {
                return null;
            }

            List<PostgresSetting> settings = [];
            int index = 0;

            foreach (JsonElement item in Items(e, "settings", "settings.", MaxSettings))
            {
                string path = "settings.settings[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                Entry(item, path);

                settings.Add(new PostgresSetting
                {
                    Name = Text(item, "name", path, required: true)!,
                    Group = Text(item, "group", path) ?? string.Empty,
                    Value = Text(item, "value", path),
                    Unit = Text(item, "unit", path),
                    Source = Text(item, "source", path),
                    PendingRestart = Bool(item, "pendingRestart", path) ?? false,
                });
            }

            List<PostgresExtension> extensions = [];
            index = 0;

            foreach (JsonElement item in Items(e, "extensions", "settings.", MaxExtensions))
            {
                string path = "settings.extensions[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                Entry(item, path);

                extensions.Add(new PostgresExtension
                {
                    Name = Text(item, "name", path, required: true)!,
                    InstalledVersion = Text(item, "installedVersion", path),
                    AvailableVersion = Text(item, "availableVersion", path),
                    Usable = Bool(item, "usable", path) ?? false,
                    Note = Text(item, "note", path),
                });
            }

            return new PostgresSettingsSection
            {
                Outcome = Outcome(e, "outcome", "settings."),
                Reason = Text(e, "reason", "settings."),
                MeasuredAt = Time(e, "measuredAt", "settings.") ?? default,
                Settings = settings,
                Extensions = extensions,
                DataChecksums = Bool(e, "dataChecksums", "settings."),
                ChecksumFailures = Size(e, "checksumFailures", "settings."),
                LastChecksumFailure = Time(e, "lastChecksumFailure", "settings."),
            };
        }

        private PostgresConnectionsSection? Connections(JsonElement? element)
        {
            if (element is not { } e)
            {
                return null;
            }

            List<PostgresConnectionCounts> byApplication = [];
            int index = 0;

            foreach (JsonElement item in Items(e, "byApplication", "connections.", MaxConnectionRows))
            {
                string path = "connections.byApplication[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                byApplication.Add(Counts(item, path));
            }

            JsonElement? database = Object(e, "database", "connections.");

            return new PostgresConnectionsSection
            {
                Outcome = Outcome(e, "outcome", "connections."),
                Reason = Text(e, "reason", "connections."),
                MeasuredAt = Time(e, "measuredAt", "connections.") ?? default,
                MaxConnections = Int(e, "maxConnections", "connections.") ?? 0,
                ReservedConnections = Int(e, "reservedConnections", "connections.") ?? 0,
                ServerConnections = Int(e, "serverConnections", "connections.") ?? 0,
                Database = database is { } d ? Counts(d, "connections.database.") : new PostgresConnectionCounts(),
                ByApplication = byApplication,
                AllSessionsVisible = Bool(e, "allSessionsVisible", "connections.") ?? false,
            };
        }

        private static PostgresConnectionCounts Counts(JsonElement item, string path)
        {
            Entry(item, path);

            return new PostgresConnectionCounts
            {
                ApplicationName = Text(item, "applicationName", path),
                Total = Int(item, "total", path) ?? 0,
                Active = Int(item, "active", path) ?? 0,
                Idle = Int(item, "idle", path) ?? 0,
                IdleInTransaction = Int(item, "idleInTransaction", path) ?? 0,
                Waiting = Int(item, "waiting", path) ?? 0,
                StateUnknown = Int(item, "stateUnknown", path) ?? 0,
                OldestTransaction = Seconds(Double(item, "oldestTransactionSeconds", path), path + "oldestTransactionSeconds"),
            };
        }

        private PostgresWalSection? Wal(JsonElement? element)
        {
            if (element is not { } e)
            {
                return null;
            }

            return new PostgresWalSection
            {
                Outcome = Outcome(e, "outcome", "wal."),
                Reason = Text(e, "reason", "wal."),
                MeasuredAt = Time(e, "measuredAt", "wal.") ?? default,
                Records = Size(e, "records", "wal."),
                FullPageImages = Size(e, "fullPageImages", "wal."),
                Bytes = Size(e, "bytes", "wal."),
                Writes = Size(e, "writes", "wal."),
                Syncs = Size(e, "syncs", "wal."),
                WriteTimeMs = Double(e, "writeTimeMs", "wal."),
                SyncTimeMs = Double(e, "syncTimeMs", "wal."),
                StatsReset = Time(e, "statsReset", "wal."),
            };
        }

        private PostgresConnectionHistorySection? ConnectionHistory(JsonElement? element)
        {
            if (element is not { } e)
            {
                return null;
            }

            List<PostgresConnectionPeak> byApplication = [];
            int index = 0;

            foreach (JsonElement item in Items(e, "byApplication", "connectionHistory.", MaxConnectionRows))
            {
                string path = "connectionHistory.byApplication[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                Entry(item, path);

                byApplication.Add(new PostgresConnectionPeak
                {
                    ApplicationName = Text(item, "applicationName", path),
                    Peak = Int(item, "peak", path) ?? 0,
                    At = Time(item, "at", path) ?? default,
                });
            }

            return new PostgresConnectionHistorySection
            {
                Outcome = Outcome(e, "outcome", "connectionHistory."),
                Reason = Text(e, "reason", "connectionHistory."),
                Server = Text(e, "server", "connectionHistory.") ?? string.Empty,
                Window = Seconds(Double(e, "windowSeconds", "connectionHistory."), "connectionHistory.windowSeconds") ?? TimeSpan.Zero,
                Since = Time(e, "since", "connectionHistory."),
                Until = Time(e, "until", "connectionHistory."),
                Samples = Int(e, "samples", "connectionHistory.") ?? 0,
                PendingSamples = Int(e, "pendingSamples", "connectionHistory.") ?? 0,
                InstanceChanges = Int(e, "instanceChanges", "connectionHistory.") ?? 0,
                Peak = Long(e, "peak", "connectionHistory.") ?? 0,
                PeakAt = Time(e, "peakAt", "connectionHistory."),
                ClientLimit = Int(e, "clientLimit", "connectionHistory.") ?? 0,
                ClientLimitNow = Int(e, "clientLimitNow", "connectionHistory.") ?? 0,
                ByApplication = byApplication,
            };
        }

        private static ConnectionSnapshotSection? ConnectionSection(JsonElement? element)
        {
            if (element is not { } e)
            {
                return null;
            }

            IReadOnlyList<string>? pooler = null;

            if (Present(e, "pooler"))
            {
                List<string> poolers = [];

                foreach (JsonElement item in Items(e, "pooler", "connection.", MaxVariants))
                {
                    poolers.Add(Value(item, "connection.pooler"));
                }

                pooler = poolers;
            }

            List<ConnectionSnapshotSetting> settings = [];
            int index = 0;

            foreach (JsonElement item in Items(e, "settings", "connection.", MaxSettings))
            {
                string path = "connection.settings[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                Entry(item, path);

                settings.Add(new ConnectionSnapshotSetting(
                    Text(item, "keyword", path, required: true)!,
                    Text(item, "category", path) ?? string.Empty,
                    Text(item, "value", path) ?? string.Empty,
                    Text(item, "from", path) ?? string.Empty,
                    Text(item, "environmentVariable", path),
                    Text(item, "default", path),
                    Text(item, "obsolete", path)));
            }

            List<ConnectionSnapshotValue> observed = [];
            index = 0;

            foreach (JsonElement item in Items(e, "observed", "connection.", MaxConnectionRows))
            {
                string path = "connection.observed[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                Entry(item, path);
                observed.Add(new ConnectionSnapshotValue(Text(item, "name", path) ?? string.Empty, Text(item, "value", path) ?? string.Empty));
            }

            List<ConnectionSnapshotHint> hints = [];
            index = 0;

            foreach (JsonElement item in Items(e, "hints", "connection.", MaxHints))
            {
                string path = "connection.hints[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                index++;
                Entry(item, path);

                hints.Add(new ConnectionSnapshotHint(
                    Text(item, "id", path, required: true)!,
                    Text(item, "keyword", path),
                    Text(item, "text", path) ?? string.Empty,
                    Text(item, "connectionStringFix", path),
                    Text(item, "builderFix", path)));
            }

            List<ConnectionSnapshotApplication>? applications = null;

            if (Present(e, "applications"))
            {
                applications = [];
                index = 0;

                foreach (JsonElement item in Items(e, "applications", "connection.", MaxConnectionRows))
                {
                    string path = "connection.applications[" + index.ToString(CultureInfo.InvariantCulture) + "].";
                    index++;
                    Entry(item, path);

                    applications.Add(new ConnectionSnapshotApplication(
                        Text(item, "applicationName", path, required: true)!,
                        Size(item, "sessions", path) ?? 0,
                        Time(item, "oldestConnection", path),
                        Size(item, "tlsKnown", path) ?? 0,
                        Size(item, "tlsOn", path) ?? 0));
                }
            }

            List<string> notes = [];

            foreach (JsonElement item in Items(e, "notes", "connection.", MaxHints))
            {
                notes.Add(Value(item, "connection.notes"));
            }

            return new ConnectionSnapshotSection
            {
                MeasuredAt = Time(e, "measuredAt", "connection.") ?? default,
                NpgsqlVersion = Text(e, "npgsqlVersion", "connection.") ?? string.Empty,
                Platform = Text(e, "platform", "connection."),
                Pooler = pooler,
                Tls = Text(e, "tls", "connection.") ?? "notRead",
                Settings = settings,
                Observed = observed,
                Hints = hints,
                Applications = applications,
                Notes = notes,
            };
        }

        private static Dictionary<string, string> WithheldMap(JsonElement? element)
        {
            Dictionary<string, string> result = new(StringComparer.Ordinal);

            if (element is not { } e)
            {
                return result;
            }

            foreach (JsonProperty property in e.EnumerateObject())
            {
                if (result.Count >= MaxWithheld)
                {
                    throw new SnapshotFormatException("withheld has more entries than a snapshot this studio writes.");
                }

                result[property.Name.Length > 256 ? property.Name[..256] : property.Name] = Value(property.Value, "withheld");
            }

            return result;
        }

        // ------------------------------------------------------------------------------------------------
        // Primitives: every failure names the studio's key path
        // ------------------------------------------------------------------------------------------------

        private static bool Present(JsonElement parent, string key) =>
            parent.TryGetProperty(key, out JsonElement value) && value.ValueKind != JsonValueKind.Null;

        private static void Entry(JsonElement item, string path)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new SnapshotFormatException(path.TrimEnd('.') + " is not an object.");
            }
        }

        private static JsonElement? Object(JsonElement parent, string key, string path)
        {
            if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new SnapshotFormatException(path + key + " is not an object.");
            }

            return value;
        }

        private static JsonElement[] Items(JsonElement parent, string key, string path, int maximum)
        {
            if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return [];
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new SnapshotFormatException(path + key + " is not a list.");
            }

            if (value.GetArrayLength() > maximum)
            {
                throw new SnapshotFormatException(path + key + " has more items than a snapshot this studio writes.");
            }

            return [.. value.EnumerateArray()];
        }

        private static string Value(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new SnapshotFormatException(path + " holds something that is not text.");
            }

            string text = element.GetString()!;

            return text.Length > MaxString ? throw new SnapshotFormatException(path + " holds text longer than a snapshot this studio writes.") : text;
        }

        private static string? Text(JsonElement parent, string key, string path, bool required = false)
        {
            if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return required ? throw new SnapshotFormatException(path + key + " is missing.") : null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                throw new SnapshotFormatException(path + key + " is not text.");
            }

            string text = value.GetString()!;

            return text.Length > MaxString ? throw new SnapshotFormatException(path + key + " is longer than a snapshot this studio writes.") : text;
        }

        private static bool? Bool(JsonElement parent, string key, string path)
        {
            if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new SnapshotFormatException(path + key + " is not true or false."),
            };
        }

        private static long? Long(JsonElement parent, string key, string path)
        {
            if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
                ? number
                : throw new SnapshotFormatException(path + key + " is not a whole number.");
        }

        /// <summary>A size or a count: a whole number that is never negative.</summary>
        private static long? Size(JsonElement parent, string key, string path)
        {
            long? value = Long(parent, key, path);

            return value is < 0 ? throw new SnapshotFormatException(path + key + " is not a size.") : value;
        }

        private static int? Int(JsonElement parent, string key, string path)
        {
            if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
                ? number
                : throw new SnapshotFormatException(path + key + " is not a whole number.");
        }

        private static double? Double(JsonElement parent, string key, string path)
        {
            if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number)
                ? number
                : throw new SnapshotFormatException(path + key + " is not a number.");
        }

        private static DateTimeOffset? Time(JsonElement parent, string key, string path)
        {
            if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: <= 64 } text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at))
            {
                return at;
            }

            throw new SnapshotFormatException(path + key + " is not a time.");
        }

        private static TimeSpan? Seconds(double? seconds, string path)
        {
            if (seconds is not { } value)
            {
                return null;
            }

            if (value < 0 || value > TimeSpan.MaxValue.TotalSeconds)
            {
                throw new SnapshotFormatException(path + " is not a duration.");
            }

            return TimeSpan.FromSeconds(value);
        }

        private PostgresReadOutcome Outcome(JsonElement parent, string key, string path)
        {
            // A section that does not say how its read went is one this studio cannot tell was read.
            if (!Present(parent, key))
            {
                return PostgresReadOutcome.Unavailable;
            }

            unknownMember = false;
            PostgresReadOutcome outcome = EnumOf<PostgresReadOutcome>(parent, key, path, required: false);

            // An outcome this studio does not know is not an item to leave out: the section is there, and what it says of itself is not understood.
            if (unknownMember)
            {
                unknownMember = false;
                return PostgresReadOutcome.Unavailable;
            }

            return outcome;
        }

        private T EnumOf<T>(JsonElement parent, string key, string path, bool required = false)
            where T : struct, Enum
        {
            T? value = OptionalEnum<T>(parent, key, path);

            if (value is null && !unknownMember)
            {
                return required ? throw new SnapshotFormatException(path + key + " is missing.") : default;
            }

            return value ?? default;
        }

        private T? OptionalEnum<T>(JsonElement parent, string key, string path)
            where T : struct, Enum
        {
            if (!parent.TryGetProperty(key, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                throw new SnapshotFormatException(path + key + " is not " + Article(typeof(T).Name) + ".");
            }

            string? name = element.GetString();

            foreach (T each in System.Enum.GetValues<T>())
            {
                if (string.Equals(DiagnosticsJson.Name(each), name, StringComparison.Ordinal))
                {
                    return each;
                }
            }

            // Allowed within version 1: a member added later. The item is left out and counted.
            unknownMember = true;
            return null;
        }

        private static string Article(string typeName) => typeName switch
        {
            "PostgresSeverity" => "a severity",
            "PostgresFindingCategory" => "a category",
            "PostgresActionKind" => "an action kind",
            "PostgresTableOwnership" => "an ownership",
            "PostgresPlatformKind" => "a platform",
            _ => "a known value",
        };
    }
}
