using System.Text;

using MartenStudio.Services.Query;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// How many documents a collection holds — or the honest admission that the studio could not find out.
/// </summary>
/// <remarks>
/// <para>
/// "Cannot report" is a value, not an error (plan §4.8): a table that has been dropped under the studio,
/// or a database that refused the read, must be drawn differently from a collection that genuinely holds
/// nothing. <see cref="IsEstimate"/> is what the UI prefixes with <c>~</c> (D8).
/// </para>
/// <para>
/// <see cref="Unknown"/> is the third answer, and it is why <see cref="IsUnknown"/> exists beside
/// <see cref="IsUnavailable"/>. Postgres reports <c>reltuples = -1</c> for a table it has never analysed,
/// which is every freshly seeded collection; rendering that as <c>~0</c> beside a collection that visibly
/// has rows in it is the studio telling a lie it could have avoided by asking. So a never-analysed table
/// reports "unknown" — a table that is certainly there and whose size nobody has measured — and the caller
/// decides whether an exact <c>count(*)</c> is worth paying for.
/// </para>
/// <para>
/// <see cref="IsExactRefused"/> is the fourth answer, and it is the one
/// <see cref="MartenStudioOptions.ExactCountThreshold"/> produces: the collection is too big for the
/// studio to pay for <c>count(*)</c>, so the estimate stands and says so. It is a <em>value</em> and not
/// an error — the number beside it is still true, it is simply the cheap one.
/// </para>
/// </remarks>
/// <param name="Value">The number of rows, or zero when <paramref name="IsUnavailable"/>.</param>
/// <param name="IsEstimate">Whether this came from <c>pg_class.reltuples</c> rather than <c>count(*)</c>.</param>
/// <param name="IsUnavailable">Whether the count could not be established at all.</param>
internal readonly record struct DocumentCount(long Value, bool IsEstimate, bool IsUnavailable)
{
    /// <summary>The count could not be established — the table is gone, or the read was refused.</summary>
    public static DocumentCount Unavailable { get; } = new(0, false, true);

    /// <summary>
    /// The table is there and Postgres has never analysed it, so there is no estimate to report.
    /// </summary>
    /// <remarks>
    /// A kind of <see cref="IsUnavailable"/> on purpose: every caller that already draws "no number" draws
    /// this correctly without being changed, and the ones that can do something about it —
    /// the collections rail, and the list header — branch on <see cref="IsUnknown"/>.
    /// </remarks>
    public static DocumentCount Unknown { get; } = new(0, false, true) { IsUnknown = true };

    /// <summary>Whether the table has simply never been analysed, rather than having refused the read.</summary>
    public bool IsUnknown { get; init; }

    /// <summary>
    /// Whether an exact <c>count(*)</c> was wanted here and was declined because the collection is bigger
    /// than <see cref="MartenStudioOptions.ExactCountThreshold"/>.
    /// </summary>
    /// <remarks>
    /// Not a failure and not a refusal to answer: the estimate beside it is the answer, and this says why
    /// it is the only one on offer. It is what makes the option's documented behaviour visible instead of
    /// the studio quietly showing a <c>~</c> the user pressed a button to get rid of.
    /// </remarks>
    public bool IsExactRefused { get; init; }

    /// <summary>The threshold that declined it, when <see cref="IsExactRefused"/>.</summary>
    public long ExactRefusedAbove { get; init; }

    /// <summary>
    /// A sentence to use in place of the threshold one, for a count declined on some other ground.
    /// </summary>
    /// <remarks>
    /// The rail declines a count for a second reason — a never-analysed table whose heap already occupies
    /// more pages than the rail will speculatively read — and "refused above 100,000 rows" would be a
    /// false account of that, because such a table may hold five hundred very large documents.
    /// </remarks>
    public string? ExactRefusedNote { get; init; }

    /// <summary>
    /// The sentence the UI puts beside the number when there is one to say, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The wording lives here rather than in a component because the rail and the list header both show
    /// it, and two hand-written versions of the same sentence drift. It names the option so that the
    /// person reading it knows which knob turns it.
    /// </remarks>
    public string? Reason => IsExactRefused
        ? ExactRefusedNote ??
          "Estimate only: an exact count is refused above " +
          ExactRefusedAbove.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) +
          " rows (MartenStudioOptions.ExactCountThreshold)."
        : null;

    /// <summary>A <c>reltuples</c> estimate.</summary>
    public static DocumentCount Estimate(long value) => new(value, true, false);

    /// <summary>An exact <c>count(*)</c>.</summary>
    public static DocumentCount Exact(long value) => new(value, false, false);

    /// <summary>
    /// The same count, marked as one the studio would not upgrade to an exact <c>count(*)</c>.
    /// </summary>
    /// <remarks>
    /// Applied to whatever the cheap answer was — an estimate, or <see cref="Unknown"/> for a table
    /// Postgres has never analysed — so that one rule covers both and neither loses the number it had.
    /// </remarks>
    /// <param name="cheap">The estimate, or <see cref="Unknown"/>.</param>
    /// <param name="threshold">The threshold that declined the upgrade.</param>
    /// <param name="note">A sentence to use instead of the threshold one, when the ground was different.</param>
    public static DocumentCount RefusedExact(DocumentCount cheap, long threshold, string? note = null) =>
        cheap with { IsExactRefused = true, ExactRefusedAbove = threshold, ExactRefusedNote = note };
}

/// <summary>
/// Counts documents the cheap way first (D8).
/// </summary>
/// <remarks>
/// <para>
/// An exact <c>count(*)</c> on a ten-million-row collection is a sequential scan, and a dashboard that
/// does one per collection per refresh is a denial of service against the database it is meant to help
/// you understand. So the first question is always <c>pg_class.reltuples</c>, which is free.
/// </para>
/// <para>
/// Two cases make the estimate useless and are the reason <see cref="ExactCountThreshold"/> exists.
/// Postgres reports <c>-1</c> for a table that has never been vacuumed or analysed — a freshly seeded
/// collection, which is exactly what a new user is looking at — and an estimate of "about 12" is worse
/// than the truth on a table where the truth costs nothing. Below the threshold the estimator therefore
/// pays for <c>count(*)</c>; above it the estimate stands and says so, and <em>asking</em> for the exact
/// number does not override it — a button that starts a ten-minute sequential scan is the denial of
/// service D8 exists to prevent, whoever pressed it.
/// </para>
/// <para>
/// A table with no estimate at all is settled by <see cref="AboveThresholdSql"/> rather than by counting
/// it: "does this hold more than N rows" is bounded by N, where "how many rows does this hold" is bounded
/// by the table. That probe is what keeps a rail full of freshly bulk-loaded collections from being
/// twenty-five sequential scans.
/// </para>
/// </remarks>
internal sealed class CountEstimator
{
    /// <summary>
    /// The estimate query. <c>to_regclass</c> returns null rather than throwing for a table that is not
    /// there, which is what turns "the table is gone" into <see cref="DocumentCount.Unavailable"/> instead
    /// of an exception on a dashboard tile.
    /// </summary>
    internal const string EstimateSql =
        """
        select c.reltuples::bigint
        from pg_catalog.pg_class c
        where c.oid = to_regclass(@qualified)
        """;

    /// <summary>
    /// The probe that answers "does this table hold more than <see cref="ExactCountThreshold"/> rows?"
    /// without counting them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>offset @threshold limit 1</c> stops as soon as it has thrown away that many rows, so the work is
    /// bounded by the <em>threshold</em> and not by the table: on a collection of ten million rows this
    /// reads a hundred thousand and stops, where the <c>count(*)</c> it is standing in front of would read
    /// all ten million. That is the whole point — the question the threshold asks is cheap to answer even
    /// when the answer it is protecting is not.
    /// </para>
    /// <para>
    /// Deliberately whole-table, with no tenant and no soft-delete predicate. The threshold is compared
    /// against <c>reltuples</c>, which describes the whole table and nothing narrower, so the probe has to
    /// ask the same question or the two disagree about which collection is "big". A predicated probe would
    /// also lose the bound: finding a hundred thousand rows of one rare tenant can mean scanning the whole
    /// table, which is the cost this exists to avoid paying.
    /// </para>
    /// </remarks>
    internal static string AboveThresholdSql(string schema, string table) =>
        "select 1 from " + SqlIdentifier.Qualify(schema, table) + " offset @threshold limit 1";

    /// <summary>
    /// Rows above which an exact count is declined. Wired from
    /// <see cref="MartenStudioOptions.ExactCountThreshold"/> by every caller in the studio.
    /// </summary>
    public long ExactCountThreshold { get; init; } = 10_000;

    /// <summary>How long either query may take. An exact count runs under this and nothing longer.</summary>
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Whether an exact <c>count(*)</c> of this table may run, given the cheap answer already in hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One rule, used by all three places that would otherwise each have their own: the rail's automatic
    /// upgrade for a never-analysed table, the list header, and the "=" button. An estimate above the
    /// threshold declines without touching the table at all; an estimate that does not exist — Postgres
    /// reports <c>reltuples = -1</c> until the first <c>ANALYZE</c>, which is every freshly bulk-loaded
    /// collection — is settled by <see cref="AboveThresholdSql"/> instead, because "no estimate" must not
    /// mean "scan it and find out".
    /// </para>
    /// <para>
    /// A threshold of zero therefore declines everything except an empty table, and that is the honest
    /// reading of the option rather than an edge case to special-case.
    /// </para>
    /// </remarks>
    /// <param name="connection">An open connection.</param>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="estimate">What <see cref="EstimateAsync"/> said.</param>
    /// <param name="cancellationToken">The usual.</param>
    public async Task<bool> MayCountExactlyAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        DocumentCount estimate,
        CancellationToken cancellationToken = default)
    {
        if (estimate.IsUnknown)
        {
            return !await IsAboveThresholdAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);
        }

        // A table that could not be reached at all is not counted either; there is nothing to count.
        return !estimate.IsUnavailable && estimate.Value <= ExactCountThreshold;
    }

    /// <summary>Whether the table holds more than <see cref="ExactCountThreshold"/> rows.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="cancellationToken">The usual.</param>
    public async Task<bool> IsAboveThresholdAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(AboveThresholdSql(schema, table), connection)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };

        command.Parameters.Add(new NpgsqlParameter("threshold", NpgsqlDbType.Bigint)
        {
            Value = Math.Max(ExactCountThreshold, 0),
        });

        // A row came back, so there is a row at position threshold + 1: more than the threshold allows.
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not (null or DBNull);
    }

    /// <summary>The <c>reltuples</c> estimate alone.</summary>
    public async Task<DocumentCount> EstimateAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(EstimateSql, connection) { CommandTimeout = CommandTimeoutSeconds };

        command.Parameters.Add(new NpgsqlParameter("qualified", NpgsqlDbType.Text)
        {
            Value = SqlIdentifier.Qualify(schema, table),
        });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (result is null or DBNull)
        {
            return DocumentCount.Unavailable;
        }

        var rows = Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);

        // -1 is Postgres for "never analysed", not for "minus one row" and not for "empty". Reporting it as
        // an estimate of zero is the studio saying "~0 documents" about a collection it has not looked at.
        return rows < 0 ? DocumentCount.Unknown : DocumentCount.Estimate(rows);
    }

    /// <summary>
    /// An exact <c>count(*)</c> that counts the same rows the list is showing: this tenant's, and the
    /// soft-delete tri-state the page is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three-argument overloads count the whole table, which is right for a rail whose estimate is a
    /// whole-table <c>reltuples</c> and wrong everywhere a scope narrows what is on screen. A number under
    /// a list of three invoices that says six is not a rounding error — it is the studio answering a
    /// different question from the one the page asked.
    /// </para>
    /// <para>
    /// It takes a <see cref="DocumentTableInfo"/> rather than a schema and table name because that is the
    /// only way to know whether <c>tenant_id</c> and <c>mt_deleted</c> exist and what they are called: on a
    /// single-tenant collection there is nothing to filter by, and a table with
    /// <c>DisableInformationalFields()</c> has no soft-delete column at all.
    /// </para>
    /// </remarks>
    /// <param name="connection">An open connection.</param>
    /// <param name="table">The collection, with its physical columns already settled.</param>
    /// <param name="tenantId">The tenant in scope, or <see langword="null"/> for all of them.</param>
    /// <param name="deleted">The soft-delete tri-state; ignored on a collection that is not soft-deleted.</param>
    /// <param name="commandTimeout">A bound on the scan, overriding <see cref="CommandTimeoutSeconds"/>.</param>
    /// <param name="cancellationToken">The usual.</param>
    public async Task<DocumentCount> CountExactAsync(
        NpgsqlConnection connection,
        DocumentTableInfo table,
        string? tenantId,
        DeletedFilter deleted,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(table);

        var tenantColumn = tenantId is not null && table.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
            ? table.MetadataColumnName(DocumentMetadataColumn.TenantId)
            : null;

        await using var command = new NpgsqlCommand(ExactSql(table, tenantColumn, deleted), connection)
        {
            CommandTimeout = commandTimeout is { } timeout
                ? Math.Max((int)Math.Ceiling(timeout.TotalSeconds), 1)
                : CommandTimeoutSeconds,
        };

        if (tenantColumn is not null)
        {
            command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Varchar) { Value = tenantId! });
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull
            ? DocumentCount.Unavailable
            : DocumentCount.Exact(Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The scoped exact-count query. Identifiers are quoted and come from the table's own column set; the
    /// tenant is the only value, and it is a parameter.
    /// </summary>
    internal static string ExactSql(DocumentTableInfo table, string? tenantColumn, DeletedFilter deleted)
    {
        ArgumentNullException.ThrowIfNull(table);

        var sql = new StringBuilder("select count(*) from ").Append(table.QualifiedName);
        var clauses = 0;

        if (tenantColumn is not null)
        {
            sql.Append(" where ").Append(SqlIdentifier.Quote(tenantColumn)).Append(" = @tenant");
            clauses++;
        }

        if (table.SoftDeleteEnabled &&
            deleted != DeletedFilter.Include &&
            table.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted) is { } deletedColumn)
        {
            sql.Append(clauses == 0 ? " where " : " and ")
                .Append(SqlIdentifier.Quote(deletedColumn))
                .Append(deleted == DeletedFilter.Only ? " = true" : " = false");
        }

        return sql.ToString();
    }
}
