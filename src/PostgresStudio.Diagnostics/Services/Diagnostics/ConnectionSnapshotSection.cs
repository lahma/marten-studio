namespace PostgresStudio.Services.Diagnostics;

/// <summary>
/// The Connection page's settings, as the studio's own snapshot download carries them (<c>connection</c>, 1.1): the visitor's
/// view of <see cref="PostgresStudio.Services.Connection.ConnectionSettings" />, already redacted, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not part of the diagnostics API.</b> It rides on <see cref="PostgresDiagnosticsSnapshot" /> through an <c>internal</c> property,
/// so the public read model (D31) and its baseline do not move: a host's <c>IPostgresDiagnostics</c> snapshot writes
/// <c>"connection": null</c>, and only <c>VisitorSnapshotBuilder</c> - which builds the file from the visitor's own gated view -
/// fills it. A value the visitor may not see is a <c>withheld</c> entry, never a half-filled section.
/// </para>
/// <para>
/// <b>Redaction is upstream.</b> Every string here was produced by <c>NpgsqlSettingsReader</c> or a hint, which hold no secret and
/// no part of the connection string; this type only carries them to the writer.
/// </para>
/// </remarks>
internal sealed record ConnectionSnapshotSection
{
    /// <summary>When the settings were read.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>The loaded Npgsql's version, <c>9.0.4</c>.</summary>
    public string NpgsqlVersion { get; init; } = string.Empty;

    /// <summary>The managed platform's name ("Amazon RDS"), or <see langword="null" /> when none was detected. A name, never a sentence.</summary>
    public string? Platform { get; init; }

    /// <summary>
    /// The poolers detected in front of the server, by name ("Amazon RDS Proxy"), or <see langword="null" /> when none was. In the
    /// JSON an array of strings.
    /// </summary>
    public IReadOnlyList<string>? Pooler { get; init; }

    /// <summary>
    /// How the connection is protected: <c>verified</c> (TLS, the server's certificate is checked), <c>encryptedNotVerified</c> (TLS,
    /// the certificate is not checked), <c>encryptedVerificationUnknown</c> (TLS, but the settings could not be read, so whether the
    /// certificate is checked is not known), <c>encryptedByProxy</c> (the Cloud SQL Auth Proxy wraps the way onward in TLS),
    /// <c>encryptedGss</c> (GSSAPI encryption, not TLS), <c>notEncrypted</c>, <c>notEncryptedLocal</c> (a socket or loopback host),
    /// <c>notVisible</c> (the reading role cannot see its own connection's state) or <c>notRead</c> (the server was not asked).
    /// </summary>
    public string Tls { get; init; } = "notRead";

    /// <summary>Every keyword, in the page's order.</summary>
    public IReadOnlyList<ConnectionSnapshotSetting> Settings { get; init; } = [];

    /// <summary>What the server saw of the studio's connection: a label and the value.</summary>
    public IReadOnlyList<ConnectionSnapshotValue> Observed { get; init; } = [];

    /// <summary>The page's advice.</summary>
    public IReadOnlyList<ConnectionSnapshotHint> Hints { get; init; } = [];

    /// <summary>This application's sessions, or <see langword="null" /> when the visitor may not see them.</summary>
    public IReadOnlyList<ConnectionSnapshotApplication>? Applications { get; init; }

    /// <summary>Sentences about the settings as a whole.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>One keyword of the connection string, redacted.</summary>
/// <param name="Keyword">The keyword.</param>
/// <param name="Category">Npgsql's category for it.</param>
/// <param name="Value">What is in effect, a <c>set</c> / <c>not set</c> for a secret, or the reason none is shown.</param>
/// <param name="From">Where the value came from: <c>connectionString</c>, <c>environment</c>, <c>default</c>, <c>notVisible</c>, <c>withheld</c> or <c>unreadable</c>.</param>
/// <param name="EnvironmentVariable">The variable it came from or would fall back to, or <see langword="null" />.</param>
/// <param name="Default">Npgsql's default, or <see langword="null" />.</param>
/// <param name="Obsolete">Npgsql's obsolete message, or <see langword="null" />.</param>
internal sealed record ConnectionSnapshotSetting(
    string Keyword,
    string Category,
    string Value,
    string From,
    string? EnvironmentVariable,
    string? Default,
    string? Obsolete);

/// <summary>One value the server reported about the studio's connection.</summary>
/// <param name="Name">What it is.</param>
/// <param name="Value">The value.</param>
internal sealed record ConnectionSnapshotValue(string Name, string Value);

/// <summary>One sentence of advice.</summary>
/// <param name="Id">The hint's id.</param>
/// <param name="Keyword">The keyword it is about, or <see langword="null" />.</param>
/// <param name="Text">The advice.</param>
/// <param name="ConnectionStringFix">The change as a connection-string fragment, or <see langword="null" />.</param>
/// <param name="BuilderFix">The change in code, or <see langword="null" />.</param>
internal sealed record ConnectionSnapshotHint(string Id, string? Keyword, string Text, string? ConnectionStringFix, string? BuilderFix);

/// <summary>One application's sessions.</summary>
/// <param name="Application">Its <c>application_name</c>, empty when it sets none.</param>
/// <param name="Sessions">How many sessions.</param>
/// <param name="OldestConnection">The oldest <c>backend_start</c> the role can see, or <see langword="null" />.</param>
/// <param name="TlsKnown">How many of the sessions the role could tell the TLS state of.</param>
/// <param name="TlsOn">How many of those are on TLS.</param>
internal sealed record ConnectionSnapshotApplication(string Application, long Sessions, DateTimeOffset? OldestConnection, long TlsKnown, long TlsOn);
