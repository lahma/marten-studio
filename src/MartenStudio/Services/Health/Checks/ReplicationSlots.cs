using System.Globalization;

using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One replication slot, as <c>pg_replication_slots</c> describes it.</summary>
/// <param name="Name">Its name. A detail, behind the activity gate.</param>
/// <param name="SlotType"><c>physical</c> or <c>logical</c>.</param>
/// <param name="Plugin">A logical slot's output plugin (<c>pgoutput</c>, <c>wal2json</c>). A detail.</param>
/// <param name="Database">A logical slot's database. A detail.</param>
/// <param name="Temporary">Whether it goes away with the session that made it.</param>
/// <param name="Active">Whether a consumer is connected to it now.</param>
/// <param name="RetainedBytes">
/// WAL it holds back: the server's WAL position less its <c>restart_lsn</c>; <see langword="null" /> for a slot that
/// reserves nothing (a physical slot made without <c>immediately_reserve</c>, or an invalidated one).
/// </param>
/// <param name="WalStatus">
/// <c>reserved</c>, <c>extended</c>, <c>unreserved</c> or <c>lost</c> (PostgreSQL 13+), or <see langword="null" />.
/// </param>
/// <param name="SafeWalSize">
/// How much more WAL may be written before the slot is lost (PostgreSQL 13+), or <see langword="null" /> when
/// <c>max_slot_wal_keep_size</c> is unlimited or the slot is lost already.
/// </param>
/// <param name="XminAge">The age of the oldest transaction it keeps rows for (<c>age(xmin)</c>), a standby's feedback.</param>
/// <param name="CatalogXminAge">The age of the oldest transaction it keeps catalog rows for (<c>age(catalog_xmin)</c>).</param>
/// <param name="InactiveAtRead">
/// How long it had been without a consumer when read (<c>now() - inactive_since</c>, PostgreSQL 17+), measured by
/// Postgres's clock; <see langword="null" /> before 17 and for an active slot.
/// </param>
/// <param name="InvalidationReason">Why Postgres invalidated it (PostgreSQL 17+: <c>wal_removed</c>, <c>rows_removed</c>, …).</param>
/// <param name="Conflicting">Whether a logical slot was invalidated by a recovery conflict (PostgreSQL 16+).</param>
/// <param name="ThisDatabase">
/// Whether the slot is this database's to name: a physical slot (it belongs to no database, so it is the server's) or a
/// logical slot of the connected database. <see langword="false" /> for a logical slot of any other database, whose
/// <see cref="Name" />, <see cref="Plugin" /> and <see cref="Database" /> are that database's and are never shown to a
/// visitor of this one - not even through the activity gate (H10 review, B1). Its sizes and states are still counted:
/// the WAL it holds is the server's. No default, so a slot made anywhere says which it is.
/// </param>
internal sealed record ReplicationSlot(
    string Name,
    string SlotType,
    string? Plugin,
    string? Database,
    bool Temporary,
    bool Active,
    long? RetainedBytes,
    string? WalStatus,
    long? SafeWalSize,
    int? XminAge,
    int? CatalogXminAge,
    TimeSpan? InactiveAtRead,
    string? InvalidationReason,
    bool? Conflicting,
    bool ThisDatabase)
{
    /// <summary>
    /// Whether Postgres has given up on it: <c>wal_status = 'lost'</c>, an invalidation reason (17+), or a recovery
    /// conflict (16+). Such a slot holds no WAL and cannot be used again.
    /// </summary>
    public bool IsLost =>
        string.Equals(WalStatus, "lost", StringComparison.Ordinal)
        || InvalidationReason is { Length: > 0 }
        || Conflicting == true;

    /// <summary>Whether WAL it still needs goes at the next checkpoint: <c>wal_status = 'unreserved'</c>.</summary>
    public bool IsUnreserved => string.Equals(WalStatus, "unreserved", StringComparison.Ordinal);

    /// <summary>
    /// What it is, for a gated evidence line: its name, kind, plugin and database - or, for a logical slot of another
    /// database (<see cref="ThisDatabase" /> false), only that it is one, with nothing that names it or its database.
    /// </summary>
    public string Describe() =>
        (ThisDatabase
            ? "'" + Name + "', " + SlotType
              + (Plugin is { Length: > 0 } plugin ? " (" + plugin + ")" : string.Empty)
              + (Database is { Length: > 0 } database ? " for database '" + database + "'" : string.Empty)
            : AnotherDatabase)
        + (Temporary ? ", temporary" : string.Empty);

    /// <summary>How a logical slot of another database is described, in place of its name.</summary>
    public const string AnotherDatabase = "a logical slot of another database on this server";
}

/// <summary>
/// Every replication slot on the server and the settings that bound what one may hold, read once per sweep under
/// <see cref="HealthServerQueries.ReplicationSlotsKey" /> (<c>server.replication-slots</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The shared read of <c>pg_replication_slots</c></b>, and the canonical type under its key: R01 reads it for WAL
/// retention and T14 (H9) for the horizon a slot pins - which is why it carries <see cref="ReplicationSlot.XminAge" />
/// and <see cref="ReplicationSlot.CatalogXminAge" />, which R01 itself only shows. A key names one shape of answer, so a
/// second reader of the view reuses this type rather than declaring one of its own.
/// </para>
/// <para>
/// <b>Every reader honours <see cref="ReplicationSlot.ThisDatabase" />.</b> The slots are the whole server's, a
/// database-per-tenant store's other tenants' among them. A slot whose flag is false is a logical slot of another
/// database: count it, size it, age its horizon, and never print its name, plugin or database, behind any gate -
/// <see cref="ReplicationSlot.Describe" /> is the wording that does that.
/// </para>
/// </remarks>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="ServerVersion"><c>server_version_num</c>.</param>
/// <param name="InRecovery">Whether the server is a standby, so retention was measured against its replay position.</param>
/// <param name="MaxSlotWalKeepBytes">
/// <c>max_slot_wal_keep_size</c> in bytes, <c>-1</c> for unlimited (the default), or <see langword="null" /> before
/// PostgreSQL 13.
/// </param>
/// <param name="WalKeepBytes"><c>wal_keep_size</c> in bytes, or <see langword="null" /> before PostgreSQL 13.</param>
/// <param name="Total">How many slots the server has, beyond the ones read.</param>
/// <param name="Slots">The slots, most WAL retained first, at most <see cref="HealthServerQueries.MaxSlots" />.</param>
internal sealed record ReplicationSlotSnapshot(
    DateTimeOffset ReadAt,
    int ServerVersion,
    bool InRecovery,
    long? MaxSlotWalKeepBytes,
    long? WalKeepBytes,
    long Total,
    IReadOnlyList<ReplicationSlot> Slots)
{
    /// <summary>Whether <c>max_slot_wal_keep_size</c> caps what a slot may hold.</summary>
    public bool HasSlotCap => MaxSlotWalKeepBytes is > 0;

    /// <summary>The sweep's one read (<see cref="HealthCheckContext.Shared{T}" />).</summary>
    public static Task<ReplicationSlotSnapshot> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(HealthServerQueries.ReplicationSlotsKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    public static async Task<ReplicationSlotSnapshot> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<Row> rows = await context.Sql
            .QueryAsync(HealthServerQueries.ReplicationSlots(), Map, cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("The replication-slot read returned no row; it always returns the server's settings.");
        }

        Row first = rows[0];

        return new ReplicationSlotSnapshot(
            context.Now,
            first.ServerVersion,
            first.InRecovery,
            first.MaxSlotWalKeepBytes,
            first.WalKeepBytes,
            first.Total,
            [.. rows.Where(static x => x.Slot is not null).Select(static x => x.Slot!)]);
    }

    private static Row Map(NpgsqlDataReader reader)
    {
        ReplicationSlot? slot = reader.IsDBNull(4)
            ? null
            : new ReplicationSlot(
                reader.GetString(4),
                reader.GetString(5),
                NullableString(reader, 6),
                NullableString(reader, 7),
                reader.GetBoolean(8),
                reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : (long) Math.Max(0, reader.GetDouble(10)),
                NullableString(reader, 11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.IsDBNull(13) ? null : reader.GetInt32(13),
                reader.IsDBNull(14) ? null : reader.GetInt32(14),
                reader.IsDBNull(15) ? null : TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(15))),
                NullableString(reader, 16),
                reader.IsDBNull(17) ? null : reader.GetBoolean(17),

                // Fail closed: a row whose flag did not come back is somebody else's.
                !reader.IsDBNull(19) && reader.GetBoolean(19));

        return new Row(
            reader.GetInt32(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(18) ? 0 : reader.GetInt64(18),
            slot);
    }

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record Row(int ServerVersion, bool InRecovery, long? MaxSlotWalKeepBytes, long? WalKeepBytes, long Total, ReplicationSlot? Slot);
}

/// <summary>Sizes, worded once for the replication checks: "1.4 GiB".</summary>
internal static class WalText
{
    /// <summary>A byte count as a size a person reads, in the units <c>\dt+</c> uses.</summary>
    public static string Bytes(long bytes) => Schema.SchemaFormat.Bytes(bytes);

    /// <summary>A size setting: "64 MiB", "unlimited (-1)", or "not on this server" before PostgreSQL 13.</summary>
    public static string Setting(long? bytes) => bytes switch
    {
        null => "not on this server (PostgreSQL 13+)",
        < 0 => "unlimited (-1, the default)",
        0 => "0",
        { } value => Bytes(value),
    };

    /// <summary>A share, as "83%".</summary>
    public static string Percent(double share) =>
        Math.Round(share * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
}
