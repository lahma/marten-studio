using System.Globalization;
using System.Reflection;

using Marten.Events;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What E08 read.</summary>
/// <param name="TagTypes">How many DCB tag types the store registers.</param>
/// <param name="Table">What the statistics collector says about <c>mt_dcb_tag_version</c>, or <see langword="null" /> with no tag types.</param>
/// <param name="DeclaredFillFactor">
/// <c>Events.DcbTagVersionFillFactor</c> as this host set it, on a Marten that has it (9.47+); <see langword="null" />
/// for unset, or a Marten without it.
/// </param>
internal sealed record DcbTagVersionFacts(int TagTypes, DcbTagVersionRow? Table, int? DeclaredFillFactor)
{
    /// <summary>The table's rows: the collector's live count, or the planner's estimate when that is larger.</summary>
    public double Rows => Table is null ? 0 : Math.Max(Table.LiveRows, Table.EstimatedRows);

    /// <summary>The <c>fillfactor</c> storage parameter on the table itself, or <see langword="null" />.</summary>
    public int? TableFillFactor
    {
        get
        {
            foreach (string option in Table?.StorageOptions ?? [])
            {
                int equals = option.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0
                    && string.Equals(option[..equals].Trim(), "fillfactor", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(option[(equals + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    return value;
                }
            }

            return null;
        }
    }
}

/// <summary>
/// E08: the DCB tag-version table is large and its updates are no longer heap-only.
/// </summary>
/// <remarks>
/// <para>
/// <c>mt_dcb_tag_version</c> (#4591) takes an <c>UPDATE</c> on every <c>FetchForWritingByTags</c> boundary save, and is
/// built for PostgreSQL's HOT path: <c>version</c>, the only column that changes, is deliberately unindexed. HOT also
/// needs free space in the same heap page, which the default fillfactor of 100 does not leave once a large tag set has
/// packed the pages. Marten's own measurement (#5622, in <c>EventGraph.DcbTagVersionFillFactor</c>'s remarks at
/// <c>V9.50.2</c>): 100% HOT at 50 rows whatever the fillfactor; at 200k rows 92.9% HOT on the default and 100% at
/// fillfactor 70, for 45% more disk.
/// </para>
/// <para>
/// Fires with tag types registered, more than <see cref="MinimumRows" /> rows, and a HOT share under
/// <see cref="MinHotShare" /> over at least <see cref="MinimumUpdates" /> updates - measured over the advisor's own
/// window once two samples exist, since the statistics were reset until then. The remedy needs Marten 9.47+
/// (<see cref="RuntimeFeature.DcbTagVersionFillFactor" />), which the page says beside it on an older host.
/// </para>
/// </remarks>
[HealthCheck("E08", HealthArea.Events, HealthCostTier.Catalog, "DCB tag-version churn")]
internal sealed class DcbTagVersionChurnCheck : HealthCheck<DcbTagVersionFacts>
{
    /// <summary>The fewest rows before the fillfactor can matter: #5622 measured no difference at small tag sets.</summary>
    internal const double MinimumRows = 100_000;

    /// <summary>The HOT share below which the table is churning.</summary>
    internal const double MinHotShare = 0.90;

    /// <summary>The fewest updates a share is judged over.</summary>
    internal const long MinimumUpdates = 10_000;

    /// <summary>The fillfactor #5622 measured at 100% HOT.</summary>
    internal const int RecommendedFillFactor = 70;

    /// <summary>The history series of the table's update count.</summary>
    internal const string UpdatesSeries = "events.dcb-tag-version.updates";

    /// <summary>The history series of its HOT update count.</summary>
    internal const string HotUpdatesSeries = "events.dcb-tag-version.hot-updates";

    /// <summary><c>EventGraph.DcbTagVersionFillFactor</c>, on a Marten that has it.</summary>
    private static readonly PropertyInfo? DeclaredFillFactorProperty =
        typeof(EventGraph).GetProperty("DcbTagVersionFillFactor", BindingFlags.Public | BindingFlags.Instance);

    /// <inheritdoc />
    public override async Task<DcbTagVersionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.StoreOptions.Events is not EventGraph graph || graph.TagTypes.Count == 0)
        {
            return new DcbTagVersionFacts(0, null, null);
        }

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);
        if (shape.DcbTagVersion is null)
        {
            throw new HealthCheckUnavailableException(
                "The store registers DCB tag types, but mt_dcb_tag_version does not exist in this database yet; the next schema apply creates it.");
        }

        IReadOnlyList<DcbTagVersionRow> rows = await context.Sql
            .QueryAsync(HealthEventQueries.DcbTagVersion(shape.Schema), HealthEventQueries.ReadDcbTagVersion, cancellationToken)
            .ConfigureAwait(false);

        return new DcbTagVersionFacts(graph.TagTypes.Count, rows.Count == 0 ? null : rows[0], DeclaredFillFactor(graph));
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(DcbTagVersionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Table is { } table)
        {
            yield return new HealthSamplePart(UpdatesSeries, table.Updates);
            yield return new HealthSamplePart(HotUpdatesSeries, table.HotUpdates);
        }
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(DcbTagVersionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (facts.TagTypes == 0 || facts.Table is not { } table)
        {
            return HealthVerdict.Quiet("The store registers no DCB tag types, so mt_dcb_tag_version is not used.");
        }

        if (facts.Rows < MinimumRows)
        {
            return HealthVerdict.Quiet(
                "mt_dcb_tag_version holds about " + EventHealth.Count(facts.Rows) + " rows; below "
                + EventHealth.Count(MinimumRows) + " the fillfactor makes no measurable difference (#5622).");
        }

        (double updates, double hot, string over) = Measure(table, evaluation);

        if (updates < MinimumUpdates)
        {
            return HealthVerdict.Quiet(
                EventHealth.Count(updates) + " updates " + over + "; a HOT share is judged over at least " + EventHealth.Count(MinimumUpdates) + ".");
        }

        double share = Math.Clamp(hot / updates, 0, 1);
        if (share >= MinHotShare)
        {
            return HealthVerdict.Quiet(EventHealth.Percent(share) + " of mt_dcb_tag_version's updates " + over + " were heap-only.");
        }

        List<HealthEvidence> evidence =
        [
            new("Heap-only (HOT) updates", EventHealth.Percent(share) + " of " + EventHealth.Count(updates) + " " + over),
            new("Rows", "about " + EventHealth.Count(facts.Rows)),
            new("DCB tag types", EventHealth.Count(facts.TagTypes)),
            new("Table fillfactor", facts.TableFillFactor is { } fill ? fill.ToString(CultureInfo.InvariantCulture) : "100 (PostgreSQL's default)"),
        ];

        if (facts.DeclaredFillFactor is { } declared)
        {
            evidence.Add(new HealthEvidence("Events.DcbTagVersionFillFactor", declared.ToString(CultureInfo.InvariantCulture)));
        }

        string why =
            "mt_dcb_tag_version takes an UPDATE on every boundary save of FetchForWritingByTags, and Marten builds it for "
            + "PostgreSQL's heap-only (HOT) path: version, the only column that changes, has no index. HOT also needs free space "
            + "in the same heap page, and once a large tag set has packed the pages there is none - each update then writes a "
            + "new tuple elsewhere, adds an entry to the primary key, and leaves the old tuple for vacuum. Marten's own "
            + "measurement (#5622): at 200k tag rows, 92.9% HOT on the default fillfactor and 100% at fillfactor 70, for 45% "
            + "more disk. Here it is " + EventHealth.Percent(share) + ".";

        if (facts.DeclaredFillFactor is { } wanted && facts.TableFillFactor != wanted)
        {
            // Declared, and not on the table yet: the line is already there, so repeating it would be no help.
            return HealthVerdict.Of(new HealthFinding
            {
                Code = "E08",
                Severity = HealthSeverity.Warning,
                Title = "mt_dcb_tag_version is churning, and the fillfactor this store declares has not been applied to it",
                WhyItMatters = why + " Events.DcbTagVersionFillFactor is " + wanted.ToString(CultureInfo.InvariantCulture)
                    + ", and the table still has " + (facts.TableFillFactor is { } on ? "fillfactor " + on.ToString(CultureInfo.InvariantCulture) : "PostgreSQL's default of 100")
                    + ": the schema change that sets it is pending. Until it is applied the declaration does nothing. Applying "
                    + "it is not limited to this table: ApplyAllDatabaseChangesOnStartup() and db-apply apply every pending "
                    + "change in the database, under AutoCreate.CreateOrUpdate - which is not additive. It drops indexes and "
                    + "columns the configuration does not declare, alters a changed column's type, drops and recreates a "
                    + "changed primary key, and copies a table out and back to change its partitioning. Review the whole patch "
                    + "with db-patch first.",
                Evidence = evidence,
                Caveat = Caveat(table, over),
                Remedy = new HealthRemedy(
                    """
                    // The declaration is already there; the schema change is pending. Review EVERY pending change first -
                    // an apply runs them all, under CreateOrUpdate, which drops what the configuration does not declare:
                    //   dotnet run -- db-patch pending.sql     (with app.RunJasperFxCommands(args))
                    // then apply them at deploy, or at startup:
                    //   dotnet run -- db-apply
                    builder.Services.AddMarten(opts => { /* ... */ }).ApplyAllDatabaseChangesOnStartup();
                    """,
                    "Read the db-patch script before applying anything: it holds every pending change in this database, not only "
                    + "this table's. The fillfactor change itself is an ALTER TABLE ... SET (fillfactor), which does not rewrite "
                    + "the pages already written: the free space appears as pages are rewritten."),
            });
        }

        if (facts.DeclaredFillFactor is not null && facts.TableFillFactor is not null)
        {
            return HealthVerdict.Of(new HealthFinding
            {
                Code = "E08",
                Severity = HealthSeverity.Info,
                Title = "mt_dcb_tag_version's fillfactor is set, and its updates are still not heap-only",
                WhyItMatters = why + " The fillfactor is already declared and on the table, but ALTER TABLE ... SET (fillfactor) "
                    + "does not rewrite the pages already written: the free space appears as pages are rewritten, and fully only "
                    + "after the table is rewritten once (VACUUM FULL or CLUSTER, run by whoever owns the database, in a quiet "
                    + "moment - both lock the table).",
                Evidence = evidence,
                Caveat = Caveat(table, over),
            });
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "E08",
            Severity = HealthSeverity.Warning,
            Title = "mt_dcb_tag_version is churning: " + EventHealth.Percent(share) + " of its updates are heap-only",
            WhyItMatters = why,
            Evidence = evidence,
            Caveat = Caveat(table, over),
            Remedy = new HealthRemedy(
                "opts.Events.DcbTagVersionFillFactor = " + RecommendedFillFactor.ToString(CultureInfo.InvariantCulture) + ";",
                "Leaves 30% of every page free for in-place updates, which #5622 measured at 100% HOT and 45% more disk. The schema "
                + "delta then shows a pending ALTER TABLE ... SET (fillfactor = 70); it does not rewrite existing pages, so the "
                + "benefit arrives as pages are rewritten. The option arrived in Marten 9.47 (#5622); an older Marten has no way to "
                + "declare it, and a fillfactor set on the table by hand is schema the configuration does not know about.",
                RuntimeFeature.DcbTagVersionFillFactor),
        });
    }

    /// <summary>
    /// The updates and HOT updates to judge, and over what: the advisor's own window once two samples exist, the
    /// counters since the statistics were last reset until then.
    /// </summary>
    internal static (double Updates, double Hot, string Over) Measure(DcbTagVersionRow table, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(evaluation);

        HealthTrend updates = evaluation.History.Trend(UpdatesSeries);
        HealthTrend hot = evaluation.History.Trend(HotUpdatesSeries);

        if (updates.HasRate && hot.HasRate
            && updates.Points[^1].Value >= updates.Points[0].Value
            && hot.Points[^1].Value >= hot.Points[0].Value)
        {
            return (
                updates.Points[^1].Value - updates.Points[0].Value,
                hot.Points[^1].Value - hot.Points[0].Value,
                "in the last " + EventHealth.Duration(updates.Watched));
        }

        return (
            table.Updates,
            table.HotUpdates,
            table.StatsReset is { } reset ? "since the statistics were reset at " + EventHealth.Time(reset) : "since the statistics began");
    }

    private static string Caveat(DcbTagVersionRow table, string over) =>
        "The statistics collector's counters " + over + ", read at the health interval"
        + (table.StatsReset is { } reset ? "; the database's statistics were last reset at " + EventHealth.Time(reset) : string.Empty)
        + ". A share over a long window can hide a recent change, and a reset starts it again.";

    private static int? DeclaredFillFactor(EventGraph graph)
    {
        try
        {
            return DeclaredFillFactorProperty?.GetValue(graph) as int?;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }
}
