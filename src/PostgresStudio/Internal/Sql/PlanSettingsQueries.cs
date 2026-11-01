using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;

using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>One planner setting the server has, as <c>pg_settings</c> describes it: what the Plan page offers a what-if of.</summary>
/// <param name="Name">The setting's name, one of <see cref="PlanSettingsQueries.Allowed" />.</param>
/// <param name="Setting">Its value for this session now.</param>
/// <param name="Unit">Its unit (<c>kB</c>, <c>ms</c>), or <see langword="null" />.</param>
/// <param name="VarType">Its type: <c>bool</c>, <c>integer</c>, <c>real</c>, <c>enum</c> or <c>string</c>.</param>
/// <param name="MinValue">The least it takes, for a number.</param>
/// <param name="MaxValue">The most it takes, for a number.</param>
internal sealed record PlanSettingRow(string Name, string Setting, string? Unit, string VarType, string? MinValue, string? MaxValue);

/// <summary>A memory setting's value the Plan page refuses: why, and whether the studio could not read it at all.</summary>
/// <param name="Message">What the visitor is told, naming the ceiling.</param>
/// <param name="Unreadable">Whether the value is not one the studio can read (as opposed to one above the ceiling).</param>
internal sealed record PlanSettingCeiling(string Message, bool Unreadable);

/// <summary>
/// 0.10-B (issue #28): the Plan page's settings what-if - the allow-list of planner settings a visitor may change for one plan,
/// the one read that says which of them the server has, and the call that sets one. The second place besides
/// <see cref="ReadOnlySqlSession" /> where a setting's name and value meet a statement, and they meet it as parameters.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name is never typed.</b> <see cref="Allowed" /> is fixed in code, and a run that names anything else is refused before a
/// connection is opened (<c>PlanRequests.Refusal</c>); the page offers only the names <see cref="ReadAsync" /> found on the
/// server. Every one of them is a plain user-settable parameter - no <c>SET</c> privilege, no superuser - and none is a
/// resource the visitor could not already spend with a statement of their own: the console's read-only transaction and its
/// <c>statement_timeout</c> bound whatever they plan.
/// </para>
/// <para>
/// <b>The value is a parameter.</b> <c>select set_config(@name, @value, true)</c> - the third argument makes it local to the
/// transaction, which is always rolled back - so a value is never SQL text and one Postgres refuses is an ordinary error.
/// </para>
/// </remarks>
internal static partial class PlanSettingsQueries
{
    /// <summary>The most memory <c>work_mem</c> may be set to for one plan, in bytes: 1 GB.</summary>
    public const decimal MaxWorkMemBytes = 1024m * 1024m * 1024m;

    /// <summary>The most <c>hash_mem_multiplier</c> may be set to for one plan.</summary>
    public const double MaxHashMemMultiplier = 8;

    /// <summary>The most settings one run changes.</summary>
    public const int MaxSettings = 8;

    /// <summary>The longest value one setting takes, in characters; a memory size or a number is never near it.</summary>
    public const int MaxValueCharacters = 64;

    /// <summary>The planner settings a what-if may change, in the order the page lists them: costs and memory first, then the <c>enable_*</c> family.</summary>
    public static IReadOnlyList<string> Allowed { get; } =
    [
        "work_mem",
        "hash_mem_multiplier",
        "random_page_cost",
        "effective_io_concurrency",
        "jit",
        "max_parallel_workers_per_gather",
        "enable_async_append",
        "enable_bitmapscan",
        "enable_gathermerge",
        "enable_hashagg",
        "enable_hashjoin",
        "enable_incremental_sort",
        "enable_indexonlyscan",
        "enable_indexscan",
        "enable_material",
        "enable_memoize",
        "enable_mergejoin",
        "enable_nestloop",
        "enable_parallel_append",
        "enable_parallel_hash",
        "enable_partition_pruning",
        "enable_partitionwise_aggregate",
        "enable_partitionwise_join",
        "enable_presorted_aggregate",
        "enable_seqscan",
        "enable_sort",
        "enable_tidscan",
        "enable_group_by_reordering",
        "enable_distinct_reordering",
    ];

    private static readonly FrozenSet<string> AllowedSet = Allowed.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The offered settings, by name, as <c>pg_settings</c> has them. The names are a bound array.</summary>
    internal const string OffersSql =
        "select name, setting, unit, vartype, min_val, max_val from pg_catalog.pg_settings where name = any(@names) order by name";

    /// <summary>
    /// The ceiling a memory setting is held to, or <see langword="null" /> when <paramref name="name" /> has none or the value is within
    /// it. <c>work_mem</c> is at most 1 GB and <c>hash_mem_multiplier</c> at most 8, in every mode: under <c>EXPLAIN ANALYZE</c> a huge
    /// one lets a hash or a sort that would have spilled stay in memory, and the server can run out of memory before
    /// <c>statement_timeout</c> fires. The value is read as Postgres reads it - a whole number and an optional unit, <c>B</c>, <c>kB</c>,
    /// <c>MB</c>, <c>GB</c> or <c>TB</c>, a bare number being kilobytes - and one the studio cannot read is refused rather than passed on.
    /// Never clamped: the visitor is told the ceiling and what was refused.
    /// </summary>
    /// <param name="name">The setting.</param>
    /// <param name="value">The value as typed.</param>
    public static PlanSettingCeiling? Ceiling(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        switch (name)
        {
            case "work_mem":
                if (TryParseMemoryBytes(value, 1024m, out decimal bytes))
                {
                    return bytes > MaxWorkMemBytes
                        ? new PlanSettingCeiling(
                            "work_mem is held to 1GB for a plan on this page, and the value is more. Under EXPLAIN ANALYZE a larger one lets a hash or a sort stay in memory that "
                            + "would have spilled, and the server can run out of memory before the statement timeout stops it. The value is refused, not reduced.",
                            Unreadable: false)
                        : null;
                }

                return new PlanSettingCeiling(
                    "work_mem takes a whole number with a unit - kB, MB, GB or TB - and at most 1GB on this page; a bare number is kilobytes.",
                    Unreadable: true);

            case "hash_mem_multiplier":
                if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double multiplier) && double.IsFinite(multiplier))
                {
                    return multiplier > MaxHashMemMultiplier
                        ? new PlanSettingCeiling(
                            "hash_mem_multiplier is held to 8 for a plan on this page, and the value is more. A larger one lets a hash stay in memory that would have "
                            + "spilled, and the server can run out of memory before the statement timeout stops it. The value is refused, not reduced.",
                            Unreadable: false)
                        : null;
                }

                return new PlanSettingCeiling("hash_mem_multiplier takes a number, at most 8 on this page.", Unreadable: true);

            default:
                return null;
        }
    }

    /// <summary>
    /// Reads a memory value as Postgres does: a whole number, optional spaces, and an optional unit that is case-sensitive; with none
    /// the number is in <paramref name="defaultUnitBytes" />. A fraction or a word is not read.
    /// </summary>
    internal static bool TryParseMemoryBytes(string value, decimal defaultUnitBytes, out decimal bytes)
    {
        bytes = 0;
        Match match = MemoryValue().Match(value.Trim());

        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long number))
        {
            return false;
        }

        decimal unit = match.Groups[2].Value switch
        {
            "B" => 1m,
            "kB" => 1024m,
            "MB" => 1024m * 1024m,
            "GB" => 1024m * 1024m * 1024m,
            "TB" => 1024m * 1024m * 1024m * 1024m,
            _ => defaultUnitBytes,
        };

        bytes = number * unit;
        return true;
    }

    [GeneratedRegex("^([0-9]{1,15})\\s*(B|kB|MB|GB|TB)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MemoryValue();

    /// <summary>Whether <paramref name="name" /> is one the what-if may change. Exact, and case-sensitive: the name is a key, not a word.</summary>
    public static bool IsAllowed(string? name) => name is not null && AllowedSet.Contains(name);

    /// <summary>Reads which of <see cref="Allowed" /> the server has, in <paramref name="transaction" />, as the role it runs as.</summary>
    public static async Task<IReadOnlyList<PlanSettingRow>> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(OffersSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = Allowed.ToArray() });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<PlanSettingRow> rows = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PlanSettingRow(
                reader.GetString(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
                reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5)));
        }

        // The page lists them in the allow-list's order, not the alphabet's.
        return [.. rows.OrderBy(static x => IndexOf(x.Name))];
    }

    /// <summary>Sets <paramref name="name" /> to <paramref name="value" /> for the rest of <paramref name="transaction" />. Both are parameters.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="name" /> is not in <see cref="Allowed" />: nothing is sent.</exception>
    public static async Task SetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string name,
        string value,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(value);

        if (!IsAllowed(name))
        {
            throw new ArgumentOutOfRangeException(nameof(name), name, "Only a planner setting on the allow-list is changed.");
        }

        await using NpgsqlCommand command = new(ReadOnlySqlSession.SetConfigSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
        command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = value });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int IndexOf(string name)
    {
        for (int i = 0; i < Allowed.Count; i++)
        {
            if (string.Equals(Allowed[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
