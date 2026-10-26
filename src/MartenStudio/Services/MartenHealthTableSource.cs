using System.Globalization;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Sources;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="IHealthTableSource" /> (issue #8, G1): every ordinary and partitioned table of the store's own
/// schemas and of the schemas the database browser admits, with the store's declarations laid over the catalog.
/// </summary>
/// <remarks>
/// <para>
/// <b>A declared table is judged exactly as before.</b> The tables the store declares (<see cref="MartenTables" />) come
/// from its configuration alone, keep their kind, their origin-aware index rules and their <c>StoreOptions</c> remedies, and
/// stay <see cref="StudioTableOwnership.Owned" /> - Marten's event tables and bookkeeping included - and count toward the
/// cap first: they are always in the set.
/// </para>
/// <para>
/// <b>Every other table is the catalog's</b>, read by one shared, memoised statement through the sweep's own runner
/// (<see cref="HealthTableQueries.UndeclaredTables" />): <c>pg_class</c> and its kin, estimates only, no size function, no
/// per-table query, nothing locked that it lists (D27). It reads the store's own schemas and - only while
/// <c>Capabilities.BrowseDatabase</c> is on and <c>ReadOnly</c> is off - the ones <c>BrowsableSchemas</c> admits, and
/// never a schema the database browser does not. Each table is classified by <see cref="MartenDatabaseTables.Classify" />:
/// the host's own (<see cref="StudioTableKind.Relational" />, judged by every rule and given migration SQL), a recognised
/// library's (exempt from the index-design rules only), or somebody else's in a schema only <c>BrowsableSchemas</c> admits.
/// </para>
/// <para>
/// <b>What it leaves out.</b> A hidden document type's table, which is not even read; another registered store's tables,
/// whose policy a visitor-less sweep cannot ask, so the coverage sentence says they are not judged here; and, past
/// <see cref="TableCap" />, the smallest by estimated size, counted in the same sentence. Fail closed: when another
/// store's declarations cannot be read, nothing here can be told from that store's, and the set is the declared tables
/// alone.
/// </para>
/// </remarks>
internal sealed class MartenHealthTableSource : IHealthTableSource
{
    /// <summary>How many tables a sweep judges at most - Postgres Studio's cap (design R5), declared tables first.</summary>
    internal const int TableCap = PostgresHealthTableSource.TableCap;

    /// <summary>The memo key of the one statement that lists the other tables.</summary>
    internal const string UndeclaredTablesKey = "tables.marten-undeclared";

    /// <inheritdoc />
    public async Task<IStudioTableSet> TablesAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MartenTableSet declared = await MartenTables.SetAsync(context).ConfigureAwait(false);

        if (EveryStoresDeclarations(context) is not { } classifier)
        {
            return MartenDatabaseTables.DeclaredOnly(declared, MartenDatabaseTables.UnclassifiedCoverage);
        }

        MartenTableScope scope = MartenTableScope.For(declared, classifier, context.Settings, context.StoreKey, TableCap);

        IReadOnlyList<HealthRelationalTableRow> rows = scope.ReadsCatalog
            ? await context
                .Shared(
                    UndeclaredTablesKey,
                    token => context.Sql.QueryAsync(
                        HealthTableQueries.UndeclaredTables(scope.Names, scope.Everything, scope.Declared, scope.SkipDocumentTables, scope.Cap),
                        HealthTableQueries.ReadRelationalTable,
                        token))
                .ConfigureAwait(false)
            : [];

        return MartenDatabaseTables.Build(declared, rows, scope, classifier, TableCap);
    }

    /// <summary>
    /// Every registered store's declarations, the swept one's among them - read afresh from their options, touching no
    /// connection (<see cref="IDatabaseDeclarationsSource.ReadUncached" />, as C06 reads them) - or <see langword="null" />
    /// when they cannot be: a store that will not build, a configuration that will not read, or a source that will not
    /// resolve in a scope with no circuit.
    /// </summary>
    internal static DatabaseObjectClassifier? EveryStoresDeclarations(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return context.Services.GetService(typeof(IDatabaseDeclarationsSource)) is IDatabaseDeclarationsSource source
                && source.ReadUncached(context.Source) is { Succeeded: true } declared
                    ? declared.Classifier
                    : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>
/// MartenStudio's remedy wording (<see cref="IHealthTableRemedies" />): a <c>StoreOptions</c> line for a table the store's
/// configuration declares, worded exactly as the checks have always worded it, and nothing for a table no Marten line
/// changes - never DDL for a Marten table (D29), because the next <c>CreateOrUpdate</c> reconciles whatever the
/// configuration does not declare.
/// </summary>
/// <remarks>
/// <para>
/// <b>A table no Marten store declares</b> (issue #8, G1) - one <see cref="MartenHealthTableSource" /> found beside the
/// store - is nothing an apply reconciles, so it is given Postgres Studio's migration SQL
/// (<see cref="PostgresHealthTableRemedies" />, composed rather than copied) where the design is the host's: the host's own
/// relational table in the store's schemas gets every statement. A library's table or somebody else's gets none that
/// changes its design - an index added or dropped, its fillfactor - because that design is not the host's; the
/// maintenance G1 still applies to it, an autovacuum setting or the rebuild of a broken index, is the statement with a
/// sentence that the library's design is left as it is. An undeclared <c>mt_</c> table gets nothing: no store declares it,
/// and a migration on a table named like Marten's is one a store may yet reconcile.
/// </para>
/// </remarks>
internal sealed class MartenHealthRemedyProvider : IHealthTableRemedies
{
    /// <summary>Postgres Studio's migration wording, which a table no Marten store declares is given.</summary>
    private static PostgresHealthTableRemedies Migration => PostgresHealthTableRemedies.Instance;

    /// <summary>
    /// A remedy that changes an undeclared table's design: the migration statement for the host's own table, and nothing
    /// for a library's or somebody else's, whose design is theirs.
    /// </summary>
    private static HealthRemedy? Design(StudioTable table, Func<HealthRemedy?> migration) =>
        table.Ownership == StudioTableOwnership.Owned ? migration() : null;

    /// <summary>
    /// A remedy that keeps an undeclared table's design as it is - an autovacuum setting, an invalid index rebuilt - which
    /// G1 applies to a library's table too: the migration statement, and for a library's table a sentence that says the
    /// library's design is left alone.
    /// </summary>
    private static HealthRemedy? Upkeep(StudioTable table, HealthRemedy? migration)
    {
        if (migration is null || table.Ownership != StudioTableOwnership.Library || migration.Kind != HealthRemedyKind.Migration)
        {
            return migration;
        }

        string library = table.Library ?? "a library";
        string explanation = migration.Explanation ?? string.Empty;
        string suffix = " " + HealthRemedy.HandOverNote;

        if (explanation.EndsWith(suffix, StringComparison.Ordinal))
        {
            explanation = explanation[..^suffix.Length];
        }

        return HealthRemedy.ForMigration(
            migration.Code,
            "The table is " + library + "'s: its columns and indexes are that library's design, and this changes neither - only "
            + "how Postgres maintains them. " + explanation);
    }

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

    /// <summary>
    /// A <c>StoreOptions</c> line that sets one storage parameter of a document table (<c>fillfactor</c>,
    /// <c>autovacuum_*</c>), or <see langword="null" /> for any other table - the shape <see cref="AutovacuumSetting" /> is
    /// worded in.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="parameter">The storage parameter.</param>
    /// <param name="value">Its suggested value, as C# spells it in the line.</param>
    /// <param name="explanation">What it changes and what it costs.</param>
    internal static HealthRemedy? StorageParameter(StudioTable table, string parameter, string value, string explanation)
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
    public HealthRemedy? IndexTheFilteredMember(StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (!table.IsDeclaredByStore)
        {
            return Design(table, () => Migration.IndexTheFilteredMember(table));
        }

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

        if (!table.IsDeclaredByStore)
        {
            return Design(table, () => Migration.IndexTheUnnamedMember(table));
        }

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
    /// A re-apply, for every origin a Marten store gives an index on a table it declares: Weasel reads an invalid index as
    /// drift whether the configuration declares it or not, and drops one nobody declares. On a table no store declares no
    /// apply touches the index, so it is rebuilt by a migration statement.
    /// </remarks>
    public HealthRemedy? RebuildInvalidIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);

        if (!table.IsDeclaredByStore)
        {
            return Upkeep(table, Migration.RebuildInvalidIndex(table, index, origin));
        }

        return origin == IndexOrigin.Host ? null : new HealthRemedy(ReapplyCode, ReapplyExplanation);
    }

    /// <inheritdoc />
    public HealthRemedy? RemoveRedundantIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin, HealthIndexRow coveredBy)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(coveredBy);

        if (!table.IsDeclaredByStore)
        {
            return Design(table, () => Migration.RemoveRedundantIndex(table, index, origin, coveredBy));
        }

        string name = index.Name;

        // Both names are printed into the lines a person pastes: one that could end a comment line or a C# string -
        // a line break, a quote, a control or format character - gets no line at all (MigrationSqlText.IsPrintableName).
        if (!MigrationSqlText.IsPrintableName(name) || !MigrationSqlText.IsPrintableName(coveredBy.Name))
        {
            return null;
        }

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

        if (!table.IsDeclaredByStore)
        {
            return Design(table, () => Migration.RemoveUnusedIndex(table, index, origin));
        }

        if (!MigrationSqlText.IsPrintableName(index.Name))
        {
            return null;
        }

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

        if (!table.IsDeclaredByStore)
        {
            return Design(table, () => Migration.LeaveRoomForHotUpdates(table, fillFactor));
        }

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

        if (!table.IsDeclaredByStore)
        {
            return Design(table, () => Migration.IndexAColumnInPlaceOfAnExpression(table));
        }

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

        if (!table.IsDeclaredByStore)
        {
            return Upkeep(table, Migration.ReenableAutovacuum(table));
        }

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

        if (!table.IsDeclaredByStore)
        {
            return Upkeep(table, Migration.AutovacuumSetting(table, parameter, value, explanation));
        }

        return StorageParameter(
            table,
            parameter,
            value,
            explanation + " Below Marten 9.47 the same setting is a DBA's ALTER TABLE ... SET (" + parameter + " = " + value
            + ") on the table, which no Marten apply resets unless the apply rebuilds the table (a changed partition scheme "
            + "copies it out and back): Marten manages only the storage parameters it declares.");
    }

    // ---- W5-V1 ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Words for a table a store declares: Marten's <c>StorageParameter</c> does not manage <c>toast.*</c> parameters (its own
    /// documentation, <c>MartenRegistry.StorageParameter</c> at V9.54.1), so no line sets one - T24 says what a DBA's
    /// <c>ALTER TABLE ... RESET (toast.autovacuum_enabled)</c> does. A table no store declares gets the migration statement.
    /// </remarks>
    public HealthRemedy? ReenableToastAutovacuum(StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return !table.IsDeclaredByStore ? Upkeep(table, Migration.ReenableToastAutovacuum(table)) : null;
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
