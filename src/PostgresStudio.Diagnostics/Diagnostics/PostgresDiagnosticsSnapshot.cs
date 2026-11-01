using PostgresStudio.Services.Diagnostics;

namespace PostgresStudio;

/// <summary>One database as the studio saw it. Each section is null unless it was asked for and allowed.</summary>
/// <remarks>
/// A result, not a seam: a host may build one in its own tests. Every property has a safe default and none is required, so
/// a property added in a later release breaks nothing.
/// </remarks>
public sealed record PostgresDiagnosticsSnapshot
{
    /// <summary>The JSON shape's version: 1 in this release. Only a removal, a rename or a change of meaning bumps it.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Which studio produced it, and its version: <c>PostgresStudio 0.5.0</c>, <c>MartenStudio 0.5.0</c>.</summary>
    public string Producer { get; init; } = "";

    /// <summary>When the snapshot was put together. Each part has its own <c>MeasuredAt</c>.</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// The database, as the studio resolved it: the source's registered name and the database's identity - or, when it
    /// could not be resolved, as it was asked for.
    /// </summary>
    public PostgresDatabaseRef Database { get; init; } = null!;

    /// <summary>
    /// Whether the database was reached: <see cref="PostgresReadOutcome.Read" />, <see cref="PostgresReadOutcome.NotFound" />
    /// or <see cref="PostgresReadOutcome.Failed" />.
    /// </summary>
    public PostgresReadOutcome Outcome { get; init; }

    /// <summary>Why not, in the studio's own words, when <see cref="Outcome" /> is not <c>Read</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>The server: version, platform, which instance answered.</summary>
    public PostgresServerInfo? Server { get; init; }

    /// <summary>Compute capacity from the host's <see cref="IPostgresCapacitySource" />, when one is registered and answered.</summary>
    public PostgresComputeCapacity? Compute { get; init; }

    /// <summary>The findings, worst first, then by code and key.</summary>
    public IReadOnlyList<PostgresFinding> Findings { get; init; } = [];

    /// <summary>Every check considered, with its outcome, by code.</summary>
    public IReadOnlyList<PostgresCheck> Checks { get; init; } = [];

    /// <summary>When the regular sweep behind the findings read its facts.</summary>
    public DateTimeOffset? FindingsMeasuredAt { get; init; }

    /// <summary>How many findings exist that this request did not include (Activity-grade ones).</summary>
    public int FindingsWithheld { get; init; }

    /// <summary>Database size, growth and the largest tables.</summary>
    public PostgresStorageSection? Storage { get; init; }

    /// <summary>The top statements.</summary>
    public PostgresStatementsSection? Statements { get; init; }

    /// <summary>The curated settings and the extensions.</summary>
    public PostgresSettingsSection? Settings { get; init; }

    /// <summary>Connections and headroom.</summary>
    public PostgresConnectionsSection? Connections { get; init; }

    /// <summary>The write-ahead log's counters: records, full-page images and bytes since the statistics last began.</summary>
    public PostgresWalSection? Wal { get; init; }

    /// <summary>
    /// Sections asked for and not included, by section name (<c>statements</c>), with the reason - for the API, the
    /// <see cref="PostgresDiagnosticsRequest.IncludeActivity" /> it would take; in the studio's own download, the gate the
    /// visitor is short of.
    /// </summary>
    public IReadOnlyDictionary<string, string> Withheld { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The snapshot as the studio's versioned JSON (<c>schemaVersion</c> 1, <c>docs/diagnostics.md</c>): the same shape as the
    /// studio's own download, written with no reflection and no global serializer options, every key always present.
    /// </summary>
    /// <returns>The JSON text, indented.</returns>
    public string ToJson() => DiagnosticsJson.Write(this);
}
