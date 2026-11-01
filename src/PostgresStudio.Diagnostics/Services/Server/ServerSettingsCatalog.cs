namespace PostgresStudio.Services.Server;

/// <summary>One curated setting the Server page shows: its name, its group, and the release that first has it.</summary>
/// <param name="Name">The setting, as <c>pg_settings.name</c> spells it.</param>
/// <param name="Group">Its group: one of <see cref="ServerSettingsCatalog.Groups" />.</param>
/// <param name="Since">
/// The feature whose release first has the setting, or <see langword="null" /> for one every supported release has - so that
/// a server older than that says "needs PostgreSQL N" rather than "hidden from this role".
/// </param>
internal sealed record CuratedSetting(string Name, string Group, PostgresFeature? Since = null);

/// <summary>A family of settings the page shows whole: every <c>pg_settings</c> row whose name starts with the prefix.</summary>
/// <param name="Prefix">What each member's name starts with (<c>autovacuum</c>, <c>enable_</c>).</param>
/// <param name="Group">The family's group: one of <see cref="ServerSettingsCatalog.Groups" />.</param>
internal sealed record CuratedSettingFamily(string Prefix, string Group)
{
    /// <summary>The prefix as a <c>LIKE</c> pattern: <c>_</c> escaped, then <c>%</c>.</summary>
    public string Pattern => Prefix.Replace("_", "\\_", StringComparison.Ordinal) + "%";

    /// <summary>Whether <paramref name="name" /> is a member.</summary>
    public bool Contains(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>
/// The Server page's curated <c>pg_settings</c> (issue #8 G7): the one table of names and groups, pinned by
/// <c>ServerSettingsCatalogTests</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The groups are a frozen vocabulary</b> (API design §1.5, <c>PostgresSetting.Group</c>): <c>memory</c>,
/// <c>connections</c>, <c>wal</c>, <c>vacuum</c>, <c>planner</c>, <c>io</c> and <c>statistics</c>, like the codes - a host's
/// own page may key on one, so a group is never renamed and a setting moves group only as a change of meaning.
/// </para>
/// <para>
/// <b>Shown, never judged.</b> A value here is a fact for the person reading the page; what the advisor judges about a
/// setting is S02's, S09's, S10's and K08's, each with its own reasons, and sizing settings are judged by none (R1 §D).
/// </para>
/// <para>
/// <b>Names, then families.</b> A setting a family covers is never named as well, so no row is read twice.
/// </para>
/// </remarks>
internal static class ServerSettingsCatalog
{
    /// <summary>The groups, in the order the page draws them.</summary>
    public static IReadOnlyList<string> Groups { get; } = ["memory", "connections", "wal", "vacuum", "planner", "io", "statistics"];

    /// <summary>Every named setting, by group in <see cref="Groups" />' order.</summary>
    public static IReadOnlyList<CuratedSetting> Settings { get; } =
    [
        // memory
        new("shared_buffers", "memory"),
        new("work_mem", "memory"),
        new("maintenance_work_mem", "memory"),
        new("effective_cache_size", "memory"),
        new("max_locks_per_transaction", "memory"),
        new("max_prepared_transactions", "memory"),

        // connections
        new("max_connections", "connections"),
        new("superuser_reserved_connections", "connections"),
        new("reserved_connections", "connections", PostgresFeature.ReservedConnections),
        new("idle_in_transaction_session_timeout", "connections"),
        new("idle_session_timeout", "connections"),
        new("statement_timeout", "connections"),
        new("lock_timeout", "connections"),
        new("transaction_timeout", "connections", PostgresFeature.TransactionTimeout),
        new("default_transaction_read_only", "connections"),
        new("default_transaction_isolation", "connections"),
        new("password_encryption", "connections"),
        new("ssl", "connections"),

        // wal
        new("wal_level", "wal"),
        new("max_wal_size", "wal"),
        new("checkpoint_timeout", "wal"),
        new("synchronous_commit", "wal"),
        new("fsync", "wal"),
        new("full_page_writes", "wal"),
        new("archive_mode", "wal"),
        new("max_slot_wal_keep_size", "wal"),
        new("wal_keep_size", "wal"),
        new("max_replication_slots", "wal"),
        new("max_wal_senders", "wal"),
        new("hot_standby_feedback", "wal"),

        // vacuum (and the autovacuum family)
        new("vacuum_failsafe_age", "vacuum", PostgresFeature.VacuumFailsafeAge),
        new("vacuum_multixact_failsafe_age", "vacuum", PostgresFeature.VacuumFailsafeAge),

        // planner (and the enable_* family)
        new("random_page_cost", "planner"),
        new("seq_page_cost", "planner"),
        new("default_statistics_target", "planner"),
        new("jit", "planner"),

        // io
        new("effective_io_concurrency", "io"),
        new("maintenance_io_concurrency", "io"),

        // statistics
        new("track_counts", "statistics"),
        new("track_activities", "statistics"),
        new("track_io_timing", "statistics"),
        new("track_wal_io_timing", "statistics"),
        new("track_activity_query_size", "statistics"),
        new("compute_query_id", "statistics"),
        new("shared_preload_libraries", "statistics"),
    ];

    /// <summary>The families shown whole: every <c>autovacuum*</c> setting, and every <c>enable_*</c> planner switch.</summary>
    public static IReadOnlyList<CuratedSettingFamily> Families { get; } =
    [
        new("autovacuum", "vacuum"),
        new("enable_", "planner"),
    ];

    /// <summary>The named settings' names.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Settings.Select(static x => x.Name)];

    /// <summary>The families as <c>LIKE</c> patterns.</summary>
    public static IReadOnlyList<string> Patterns { get; } = [.. Families.Select(static x => x.Pattern)];

    /// <summary>The group of <paramref name="name" />: its own entry's, or its family's, or <see langword="null" />.</summary>
    public static string? GroupOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Settings.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal))?.Group
            ?? Families.FirstOrDefault(x => x.Contains(name))?.Group;
    }
}
