using System.Globalization;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Sources;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="IHealthTableSource" />: the tables the store declares (<see cref="MartenTables" />), from its
/// configuration alone, each of them the application's own.
/// </summary>
/// <remarks>
/// The set is the declared tables, as it has always been; widening it to every table of the database - with a library's
/// tables classified as such (issue #8, G1) - is a later packet's. Every declared table is
/// <see cref="StudioTableOwnership.Owned" />, Marten's event tables and bookkeeping included, so the index-design rules judge
/// them exactly as before: their indexes are told apart by where they come from (<see cref="IndexOrigin" />).
/// </remarks>
internal sealed class MartenHealthTableSource : IHealthTableSource
{
    /// <inheritdoc />
    public async Task<IStudioTableSet> TablesAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await MartenTables.SetAsync(context).ConfigureAwait(false);
    }
}

/// <summary>
/// MartenStudio's remedy wording (<see cref="IHealthRemedyProvider" />, <see cref="IHealthTableRemedies" />): a
/// <c>StoreOptions</c> line for a table the store's configuration declares, worded exactly as the checks have always
/// worded it, and nothing for a table no Marten line changes - never DDL (D29), because the next <c>CreateOrUpdate</c>
/// reconciles whatever the configuration does not declare.
/// </summary>
internal sealed class MartenHealthRemedyProvider : IHealthRemedyProvider, IHealthTableRemedies
{
    /// <summary>What T03's re-apply remedy says, for an invalid index on an ordinary table.</summary>
    internal const string ReapplyCode =
        "// Re-apply the configuration: Weasel reads an invalid index as drift, drops it and builds it again\n"
        + "// (and drops one nobody declares).\n"
        + "await store.Storage.Database.ApplyAllConfiguredChangesToDatabaseAsync();\n"
        + "// or, with app.RunJasperFxCommands(args): dotnet run -- db-patch (review it), then dotnet run -- db-apply";

    /// <summary>What the apply also does, said beside it - hard rule 14's wording half.</summary>
    internal const string ReapplyExplanation =
        "The apply is the whole configuration's, under AutoCreate.CreateOrUpdate, which is not additive: it also drops every "
        + "index on a Marten table that nothing declares, drops extra columns and alters changed ones - read db-patch's script "
        + "(or the Schema screen's preview) first. A unique index whose build failed on duplicate values fails again until the "
        + "duplicates are resolved. Building an index takes a lock that blocks writes to the table for as long as the build runs.";

    /// <summary>The one instance the sweep and the tests use: it holds nothing.</summary>
    public static MartenHealthRemedyProvider Instance { get; } = new();

    // ---- IHealthRemedyProvider ------------------------------------------------------------------------------------

    /// <inheritdoc />
    public HealthRemedy? StorageParameter(StudioTable table, string parameter, string value, string explanation)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return table.TypeName is { } type
            ? new HealthRemedy(
                "opts.Schema.For<" + type + ">().StorageParameter(\"" + parameter + "\", " + value + ");",
                explanation,
                RuntimeFeature.DocumentStorageParameter)
            : null;
    }

    /// <inheritdoc />
    /// <remarks>None: the checks that suggest an index say what they need through <see cref="IHealthTableRemedies" />.</remarks>
    public HealthRemedy? AddIndex(StudioTable table, IReadOnlyList<string> columns, string explanation) => null;

    /// <inheritdoc />
    /// <remarks>None: the checks that remove an index say why through <see cref="IHealthTableRemedies" />.</remarks>
    public HealthRemedy? RemoveIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin, string explanation) => null;

    // ---- IHealthTableRemedies -------------------------------------------------------------------------------------

    /// <inheritdoc />
    public HealthRemedy? IndexTheFilteredMember(StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table is { Kind: StudioTableKind.DocumentTable, TypeName: { } type }
            ? new HealthRemedy(
                "// The member the slow query filters, sorts or joins on:\n"
                + "opts.Schema.For<" + type + ">().Index(x => x.FilteredMember);\n"
                + "// or, where it is also sorted on or compared as a column:\n"
                + "opts.Schema.For<" + type + ">().Duplicate(x => x.FilteredMember);",
                "Query in Marten mode, on the " + table.Alias + " collection, judges a where clause: its index advice says "
                + "whether an index serves it and names the member. Every write maintains every index, so add the one the slow "
                + "query needs and no other.")
            : null;
    }

    /// <inheritdoc />
    public HealthRemedy? IndexTheUnnamedMember(StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table is { TypeName: { } type, Alias: { } alias }
            ? new HealthRemedy(
                "// The member this statement filters on, which pg_stat_statements does not name -\n"
                + "// if no index on that member exists already:\n"
                + "opts.Schema.For<" + type + ">().Index(x => x.FilteredMember);\n"
                + "// or, where it is also sorted on or compared as a column:\n"
                + "opts.Schema.For<" + type + ">().Duplicate(x => x.FilteredMember);",
                "pg_stat_statements records this statement's constants as placeholders - the JSON member names Marten writes into "
                + "its SQL included - so neither the plan nor the advisor can say which member this filters on, nor whether an index "
                + "on it exists already: a generic plan cannot use one for a placeholder key. Query in Marten mode, on the " + alias
                + " collection, judges a where clause and names it; add the line only if it finds no index on that member.")
            : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A re-apply, for every origin a Marten store gives an index: Weasel reads an invalid index as drift whether the
    /// configuration declares it or not, and drops one nobody declares.
    /// </remarks>
    public HealthRemedy? RebuildInvalidIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);

        return origin == IndexOrigin.Host ? null : new HealthRemedy(ReapplyCode, ReapplyExplanation);
    }

    /// <inheritdoc />
    public HealthRemedy? RemoveRedundantIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin, HealthIndexRow coveredBy)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(coveredBy);

        string name = index.Name;

        return origin switch
        {
            IndexOrigin.Declared when table.TypeName is { } type => new HealthRemedy(
                "// opts.Schema.For<" + type + ">() declares " + name + ", which " + coveredBy.Name + " already covers:\n"
                + "// remove the .Index(...), .Duplicate(...) or .UniqueIndex(...) that declares it from the registration.",
                "The next apply then drops it. Removing a Duplicate(...) removes its column along with its index, so where a "
                + "query or a sort reads that column, keep it and remove the declaration of the other index instead."),
            IndexOrigin.Managed => new HealthRemedy(
                "// Remove " + name + " from the Weasel table's Indexes where the Marten-managed table is defined.",
                "The next apply then drops it, as it drops any index such a table does not declare."),
            IndexOrigin.Ignored when table.TypeName is { } type => new HealthRemedy(
                "// Stop ignoring it, and the next apply drops it:\n"
                + "// remove opts.Schema.For<" + type + ">().IgnoreIndex(\"" + name + "\") from the registration.",
                "AutoCreate.CreateOrUpdate drops every index on a Marten table that the configuration does not declare, once the "
                + "configuration no longer says to leave it alone."),
            _ => null,
        };
    }

    /// <inheritdoc />
    public HealthRemedy? RemoveUnusedIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);

        return origin switch
        {
            IndexOrigin.Declared when table.TypeName is { } type => new HealthRemedy(
                "// opts.Schema.For<" + type + ">() declares " + index.Name + ", and nothing has scanned it:\n"
                + "// remove the .Index(...), .Duplicate(...) or GIN/full-text index that declares it from the registration.",
                "The next apply drops it. Removing a Duplicate(...) removes its column along with its index."),
            IndexOrigin.Managed => new HealthRemedy(
                "// Remove " + index.Name + " from the Weasel table's Indexes where the Marten-managed table is defined.",
                "The next apply then drops it."),
            IndexOrigin.Ignored when table.TypeName is { } type => new HealthRemedy(
                "// Stop ignoring it, and the next apply drops it:\n"
                + "// remove opts.Schema.For<" + type + ">().IgnoreIndex(\"" + index.Name + "\") from the registration.",
                "AutoCreate.CreateOrUpdate drops every index on a Marten table that the configuration does not declare."),
            _ => null,
        };
    }

    /// <inheritdoc />
    public HealthRemedy? LeaveRoomForHotUpdates(StudioTable table, int fillFactor)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.TypeName is { } type
            ? new HealthRemedy(
                "opts.Schema.For<" + type + ">().FillFactor(" + fillFactor.ToString(CultureInfo.InvariantCulture) + ");",
                "Leaves a fifth of each page free for an update to stay on. It costs about a quarter more heap, and - in Marten's own "
                + "words - affects only pages written after the change: existing rows are not repacked until the table is rewritten.",
                RuntimeFeature.DocumentFillFactor)
            : null;
    }

    /// <inheritdoc />
    public HealthRemedy? IndexAColumnInPlaceOfAnExpression(StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.TypeName is { } type
            ? new HealthRemedy(
                "// A computed index reads data, which every upsert rewrites. A duplicated field is a column of its own,\n"
                + "// changed only when the member is, so its index lets the other updates stay HOT:\n"
                + "opts.Schema.For<" + type + ">().Duplicate(x => x.IndexedMember);  // in place of .Index(x => x.IndexedMember)",
                "Worth it only for a member that changes less often than the document does. A GIN index on the whole document "
                + "and IndexLastModified() block HOT by design; keep them only where a query needs them.")
            : null;
    }

    /// <inheritdoc />
    public HealthRemedy? ReenableAutovacuum(StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.TypeName is { } type
            ? new HealthRemedy(
                "opts.Schema.For<" + type + ">().StorageParameter(\"autovacuum_enabled\", true);",
                "Autovacuum is switched off for this table (its autovacuum_enabled storage parameter is false). Declaring it true "
                + "lets Marten's next apply switch it back on; Marten manages only the storage parameters it declares.",
                RuntimeFeature.DocumentStorageParameter)
            : null;
    }

    /// <inheritdoc />
    public HealthRemedy? AutovacuumSetting(StudioTable table, string parameter, string value, string explanation)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return StorageParameter(
            table,
            parameter,
            value,
            explanation + " Below Marten 9.47 the same setting is a DBA's ALTER TABLE ... SET (" + parameter + " = " + value
            + ") on the table, which no Marten apply resets unless the apply rebuilds the table (a changed partition scheme "
            + "copies it out and back): Marten manages only the storage parameters it declares.");
    }
}

/// <summary>
/// MartenStudio's <see cref="IHorizonContributor" />: whether the store runs async projections, whose high-water mark an open
/// writer pins (D01), and whether its HotCold leader holds its lock in a transaction (<c>Events.UseAdvisoryLockTransaction</c>)
/// - both from the store's configuration, read with no I/O.
/// </summary>
internal sealed class MartenHorizonContributor : IHorizonContributor
{
    /// <inheritdoc />
    public HorizonSourceFacts Describe(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new HorizonSourceFacts(
            DaemonCheckConfiguration.AsyncShardsOf(context.Store).Count > 0,
            context.StoreOptions.Events.UseAdvisoryLockTransaction);
    }
}
