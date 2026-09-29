using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Database;

/// <summary>
/// Turns catalog rows into what a page is shown, through one visitor's <see cref="DatabaseGate" />.
/// </summary>
/// <remarks>
/// <para>
/// Pure, so the gate's consequences can be tested row by row without a database. The catalog reads are
/// already confined to <see cref="DatabaseGate.VisibleSchemas" />; every method here filters by the gate
/// again anyway, because a cached read made for a wider schema set must never leak into a narrower one,
/// and a second check costs nothing.
/// </para>
/// <para>
/// <b>What is redacted rather than dropped.</b> The far end of a foreign key, the table under a view, the
/// function a trigger calls and the table a sequence belongs to can sit in a schema this visitor may not
/// see; the object itself is still theirs to see, so it is listed with that one name blanked, and "a
/// relation outside the schemas shown here" is what the page says. A hidden type's table is different: it
/// is dropped wherever it would appear, and a view over it is refused its rows.
/// </para>
/// </remarks>
internal static class DatabaseObjectAssembler
{
    /// <summary>The overview: every visible schema with its per-kind counts.</summary>
    public static DatabaseBrowserOverview Overview(DatabaseGate gate, CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(snapshot);

        Dictionary<string, int[]> counts = new(StringComparer.Ordinal);

        foreach (string schema in gate.VisibleSchemas)
        {
            counts[schema] = new int[Enum.GetValues<DatabaseObjectCategory>().Length];
        }

        foreach (DatabaseObjectCategory category in Enum.GetValues<DatabaseObjectCategory>())
        {
            foreach (DatabaseObjectSummary summary in Summaries(gate, snapshot, category))
            {
                if (counts.TryGetValue(summary.Schema, out int[]? perKind))
                {
                    perKind[(int) category]++;
                }
            }
        }

        List<DatabaseSchemaSummary> schemas = [];

        foreach (string schema in gate.VisibleSchemas)
        {
            int[] perKind = counts[schema];

            schemas.Add(new DatabaseSchemaSummary(
                schema,
                gate.IsStoreSchema(schema),
                gate.DataAccess(schema).Allowed,
                perKind[(int) DatabaseObjectCategory.Tables],
                perKind[(int) DatabaseObjectCategory.Views],
                perKind[(int) DatabaseObjectCategory.Functions],
                perKind[(int) DatabaseObjectCategory.Triggers],
                perKind[(int) DatabaseObjectCategory.Sequences],
                perKind[(int) DatabaseObjectCategory.Types]));
        }

        bool truncated = snapshot.Relations.Truncated || snapshot.Routines.Truncated || snapshot.Triggers.Truncated
            || snapshot.Sequences.Truncated || snapshot.Types.Truncated;

        return new DatabaseBrowserOverview(schemas, gate.State, truncated, DatabaseRefusal.None, null);
    }

    /// <summary>
    /// One kind tab's list, filtered by schema and owner and trimmed to <paramref name="limit" />, with the
    /// owner filter's counts over everything else that matched.
    /// </summary>
    public static DatabaseObjectList List(DatabaseGate gate, CatalogSnapshot snapshot, DatabaseObjectQuery query, int limit)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);

        List<DatabaseObjectSummary> matched = [];
        int marten = 0;
        int other = 0;

        foreach (DatabaseObjectSummary summary in Summaries(gate, snapshot, query.Category))
        {
            if (query.Schema is { } schema && !string.Equals(summary.Schema, schema, StringComparison.Ordinal))
            {
                continue;
            }

            if (summary.Ownership.IsMarten)
            {
                marten++;
            }
            else
            {
                other++;
            }

            bool wanted = query.Owner switch
            {
                DatabaseOwnerFilter.Marten => summary.Ownership.IsMarten,
                DatabaseOwnerFilter.Other => !summary.Ownership.IsMarten,
                _ => true,
            };

            if (wanted)
            {
                matched.Add(summary);
            }
        }

        bool readTruncated = query.Category switch
        {
            DatabaseObjectCategory.Tables or DatabaseObjectCategory.Views => snapshot.Relations.Truncated,
            DatabaseObjectCategory.Functions => snapshot.Routines.Truncated,
            DatabaseObjectCategory.Triggers => snapshot.Triggers.Truncated,
            DatabaseObjectCategory.Sequences => snapshot.Sequences.Truncated,
            _ => snapshot.Types.Truncated,
        };

        return new DatabaseObjectList(
            query with { Limit = limit },
            matched.Count > limit ? matched[..limit] : matched,
            new DatabaseOwnerCounts(marten + other, marten, other),
            readTruncated || matched.Count > limit,
            limit,
            gate.State,
            DatabaseRefusal.None,
            null);
    }

    /// <summary>Every object of one tab this visitor may see, in catalog order.</summary>
    public static IEnumerable<DatabaseObjectSummary> Summaries(
        DatabaseGate gate,
        CatalogSnapshot snapshot,
        DatabaseObjectCategory category)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(snapshot);

        return category switch
        {
            DatabaseObjectCategory.Tables or DatabaseObjectCategory.Views => Relations(gate, snapshot, category),
            DatabaseObjectCategory.Functions => Routines(gate, snapshot.Routines.Items),
            DatabaseObjectCategory.Triggers => Triggers(gate, snapshot.Triggers.Items),
            DatabaseObjectCategory.Sequences => Sequences(gate, snapshot.Sequences.Items),
            _ => Types(gate, snapshot.Types.Items),
        };
    }

    /// <summary>The detail view of one relation.</summary>
    /// <param name="gate">The visitor's gate.</param>
    /// <param name="detail">The catalog's detail.</param>
    /// <returns>
    /// The detail, or a <see cref="DatabaseRefusal.NotFound" /> answer when the gate does not let this
    /// visitor see the relation at all - worded exactly as for a relation that does not exist.
    /// </returns>
    public static DatabaseObjectDetail Detail(DatabaseGate gate, CatalogRelationDetail detail)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(detail);

        CatalogRelation relation = detail.Relation;

        if (!gate.CanSeeStructure(relation.Schema)
            || gate.Classifier.ClassifyRelation(relation.Schema, relation.Name) is not { } ownership)
        {
            return DatabaseObjectDetail.Unavailable(
                DatabaseRefusal.NotFound, NotFound(relation.Schema, relation.Name), gate.State);
        }

        List<DatabaseForeignKeyInfo> outbound = [];
        List<DatabaseForeignKeyInfo> inbound = [];

        foreach (CatalogForeignKey key in detail.ForeignKeys.Items)
        {
            if (IsAt(key.Schema, key.Table, relation) && ForeignKeyOut(gate, key) is { } outgoing)
            {
                outbound.Add(outgoing);
            }

            if (IsAt(key.LinkedSchema, key.LinkedTable, relation) && ForeignKeyIn(gate, key) is { } incoming)
            {
                inbound.Add(incoming);
            }
        }

        List<DatabaseViewDependency> dependencies = [];
        bool dependsOnHidden = false;

        foreach (CatalogViewDependency dependency in detail.Dependencies.Items)
        {
            if (gate.Classifier.IsHiddenTable(dependency.Schema, dependency.Name))
            {
                dependsOnHidden = true;
                dependencies.Add(new DatabaseViewDependency(
                    null, null, DatabaseObjectKinds.FromRelkind(dependency.Kind), dependency.Depth, false, null));
                continue;
            }

            bool visible = gate.CanSeeStructure(dependency.Schema);

            dependencies.Add(new DatabaseViewDependency(
                visible ? dependency.Schema : null,
                visible ? dependency.Name : null,
                DatabaseObjectKinds.FromRelkind(dependency.Kind),
                dependency.Depth,
                visible,
                visible ? gate.Classifier.ClassifyRelation(dependency.Schema, dependency.Name) : null));
        }

        // A dependency read that hit its cap may have stopped before the hidden table: the answer then has
        // to be "not shown", because "nothing hidden underneath" was not established.
        DatabaseRelationSummary summary = Relation(
            gate,
            relation,
            ownership,
            outbound.Count,
            inbound.Count,
            dependsOnHidden,
            dependenciesUnknown: detail.Dependencies.Truncated);

        List<DatabaseColumnInfo> columns = [.. detail.Columns.Select(static column => new DatabaseColumnInfo(
            column.Name,
            column.Position,
            column.Type,
            !column.NotNull,
            column.Default,
            column.Identity switch
            {
                "a" => DatabaseIdentityKind.Always,
                "d" => DatabaseIdentityKind.ByDefault,
                _ => DatabaseIdentityKind.None,
            },
            !string.IsNullOrEmpty(column.Generated),
            column.Sortable,
            column.Comment,
            DatabaseCatalogQueries.IsQuotable(column.Name)))];

        return new DatabaseObjectDetail(
            summary,
            columns,
            RowKey(detail),
            [.. detail.Constraints.Select(static x => new DatabaseConstraintInfo(x.Name, ConstraintKind(x.Kind), x.Definition))],
            [.. detail.Indexes.Select(static x => new DatabaseIndexInfo(
                x.Name, x.Definition, x.IsPrimary, x.IsUnique, x.IsValid, x.HasPredicate, x.KeyColumns))],
            [.. Triggers(gate, detail.Triggers)],
            outbound,
            inbound,
            dependencies,
            gate.State,
            DatabaseRefusal.None,
            null);
    }

    /// <summary>
    /// The columns that identify one row: the primary key, or else the narrowest valid, unconditional
    /// unique index over plain <c>NOT NULL</c> columns - or <see langword="null" />.
    /// </summary>
    /// <remarks>
    /// A unique index over a nullable column does not identify a row (Postgres lets any number of rows
    /// share a null), a partial one identifies only some rows, an expression one cannot be compared
    /// against a column value, and an invalid one is a failed concurrent build. Only the key columns count -
    /// <c>INCLUDE</c> columns never make it into <see cref="CatalogIndex.KeyColumns" />.
    /// </remarks>
    public static DatabaseRowKey? RowKey(CatalogRelationDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        Dictionary<string, CatalogColumn> byName = detail.Columns.ToDictionary(static x => x.Name, StringComparer.Ordinal);

        foreach (CatalogIndex index in detail.Indexes)
        {
            if (index.IsPrimary && index.KeyColumns.Count > 0 && index.KeyColumns.All(static x => x is not null))
            {
                return new DatabaseRowKey(DatabaseRowKeySource.PrimaryKey, index.Name, [.. index.KeyColumns!]);
            }
        }

        CatalogIndex? unique = detail.Indexes
            .Where(index => index.IsUnique
                && index.IsValid
                && !index.HasPredicate
                && !index.HasExpressions
                && index.KeyColumns.Count > 0
                && index.KeyColumns.All(column => column is not null
                    && byName.TryGetValue(column, out CatalogColumn? found)
                    && found.NotNull))
            .OrderBy(static index => index.KeyColumns.Count)
            .ThenBy(static index => index.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        return unique is null
            ? null
            : new DatabaseRowKey(DatabaseRowKeySource.UniqueIndex, unique.Name, [.. unique.KeyColumns!]);
    }

    /// <summary>The sentence for an object this visitor may not see, or that is not there - one sentence for both.</summary>
    public static string NotFound(string schema, string name) =>
        "There is no object '" + schema + "." + name + "' among the schemas this studio shows you.";

    /// <summary>One relation, summarised through the gate.</summary>
    public static DatabaseRelationSummary Relation(
        DatabaseGate gate,
        CatalogRelation relation,
        DatabaseObjectOwnership ownership,
        int foreignKeysOut,
        int foreignKeysIn,
        bool dependsOnHidden,
        bool dependenciesUnknown)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(ownership);

        DatabaseObjectKind kind = DatabaseObjectKinds.FromRelkind(relation.Kind);
        bool isView = kind is DatabaseObjectKind.View or DatabaseObjectKind.MaterializedView;

        DatabaseRowAccess rows = gate.RowsFor(relation, ownership, dependsOnHidden);

        if (rows.Allowed && isView && dependenciesUnknown)
        {
            rows = DatabaseRowAccess.Refused(
                DatabaseRefusal.Unavailable,
                "There are too many view dependencies in scope to check this view's from a list; open it to " +
                "see whether its rows may be read.");
        }

        return new DatabaseRelationSummary(
            kind,
            relation.Schema,
            relation.Name,
            ownership,
            relation.Comment,
            DatabaseCatalogQueries.IsQuotable(relation.Schema) && DatabaseCatalogQueries.IsQuotable(relation.Name),
            relation.EstimatedRows,
            relation.SizeBytes,
            relation.HasPrimaryKey,
            foreignKeysOut,
            foreignKeysIn,
            relation.PartitionCount,
            relation.Unlogged,
            relation.Populated,
            relation.RowSecurity,
            relation.ForeignServer,
            relation.Readable,
            rows,
            isView && gate.DefinitionAccess(relation.Schema, ownership).Allowed);
    }

    private static IEnumerable<DatabaseObjectSummary> Relations(
        DatabaseGate gate,
        CatalogSnapshot snapshot,
        DatabaseObjectCategory category)
    {
        Dictionary<(string, string), int> outbound = new();
        Dictionary<(string, string), int> inbound = new();

        foreach (CatalogForeignKey key in snapshot.ForeignKeys.Items)
        {
            if (ForeignKeyOut(gate, key) is not null)
            {
                Increment(outbound, (key.Schema, key.Table));
            }

            if (ForeignKeyIn(gate, key) is not null)
            {
                Increment(inbound, (key.LinkedSchema, key.LinkedTable));
            }
        }

        HashSet<(string, string)> hiddenUnderneath = [];

        foreach (CatalogViewDependency dependency in snapshot.ViewDependencies.Items)
        {
            if (gate.Classifier.IsHiddenTable(dependency.Schema, dependency.Name))
            {
                hiddenUnderneath.Add((dependency.ViewSchema, dependency.ViewName));
            }
        }

        foreach (CatalogRelation relation in snapshot.Relations.Items)
        {
            DatabaseObjectKind kind = DatabaseObjectKinds.FromRelkind(relation.Kind);

            if (DatabaseObjectKinds.CategoryOf(kind) != category || !gate.CanSeeStructure(relation.Schema))
            {
                continue;
            }

            if (gate.Classifier.ClassifyRelation(relation.Schema, relation.Name) is not { } ownership)
            {
                continue;
            }

            yield return Relation(
                gate,
                relation,
                ownership,
                outbound.GetValueOrDefault((relation.Schema, relation.Name)),
                inbound.GetValueOrDefault((relation.Schema, relation.Name)),
                hiddenUnderneath.Contains((relation.Schema, relation.Name)),
                dependenciesUnknown: snapshot.ViewDependencies.Truncated);
        }
    }

    private static IEnumerable<DatabaseObjectSummary> Routines(DatabaseGate gate, IReadOnlyList<CatalogRoutine> routines)
    {
        foreach (CatalogRoutine routine in routines)
        {
            if (!gate.CanSeeStructure(routine.Schema))
            {
                continue;
            }

            DatabaseObjectKind kind = DatabaseObjectKinds.FromProkind(routine.Kind);
            DatabaseObjectOwnership ownership = gate.Classifier.ClassifyRoutine(routine.Schema, routine.Name);

            yield return new DatabaseRoutineSummary(
                kind,
                routine.Schema,
                routine.Name,
                ownership,
                routine.Comment,
                DatabaseCatalogQueries.IsQuotable(routine.Schema) && DatabaseCatalogQueries.IsQuotable(routine.Name),
                routine.IdentityArguments,
                routine.Result,
                routine.Language,
                routine.Volatility switch
                {
                    "i" => "immutable",
                    "s" => "stable",
                    _ => "volatile",
                },
                routine.SecurityDefiner,
                routine.Config.Any(static x => x.StartsWith("search_path=", StringComparison.OrdinalIgnoreCase)),
                routine.Config,
                kind != DatabaseObjectKind.Aggregate && gate.DefinitionAccess(routine.Schema, ownership).Allowed);
        }
    }

    private static IEnumerable<DatabaseTriggerSummary> Triggers(DatabaseGate gate, IReadOnlyList<CatalogTrigger> triggers)
    {
        foreach (CatalogTrigger trigger in triggers)
        {
            if (!gate.CanSeeStructure(trigger.Schema)
                || gate.Classifier.ClassifyTrigger(trigger.Schema, trigger.Table, trigger.Name) is not { } ownership
                || gate.Classifier.ClassifyRelation(trigger.Schema, trigger.Table) is not { } tableOwnership)
            {
                continue;
            }

            bool functionVisible = gate.CanSeeStructure(trigger.FunctionSchema)
                || BrowsableSchemaMatcher.IsSystemSchema(trigger.FunctionSchema);

            yield return new DatabaseTriggerSummary(
                DatabaseObjectKind.Trigger,
                trigger.Schema,
                trigger.Name,
                ownership,
                null,
                DatabaseCatalogQueries.IsQuotable(trigger.Schema)
                    && DatabaseCatalogQueries.IsQuotable(trigger.Table)
                    && DatabaseCatalogQueries.IsQuotable(trigger.Name),
                trigger.Table,
                tableOwnership,
                TriggerTiming(trigger.Type),
                TriggerEvents(trigger.Type),
                (trigger.Type & RowBit) != 0,
                trigger.Enabled is "O" or "A",
                trigger.Enabled,
                functionVisible ? trigger.FunctionSchema : null,
                functionVisible ? trigger.FunctionName : null,
                trigger.IsConstraintTrigger,
                gate.DefinitionAccess(trigger.Schema, ownership).Allowed);
        }
    }

    private static IEnumerable<DatabaseObjectSummary> Sequences(DatabaseGate gate, IReadOnlyList<CatalogSequence> sequences)
    {
        // mt_events_sequence_<tenant>: counted into the global sequence of the same schema, never listed.
        Dictionary<string, int> perTenant = new(StringComparer.Ordinal);

        foreach (CatalogSequence sequence in sequences)
        {
            if (DatabaseObjectClassifier.IsPerTenantEventSequence(sequence.Name))
            {
                perTenant[sequence.Schema] = perTenant.GetValueOrDefault(sequence.Schema) + 1;
            }
        }

        foreach (CatalogSequence sequence in sequences)
        {
            if (!gate.CanSeeStructure(sequence.Schema)
                || gate.Classifier.ClassifySequence(sequence.Schema, sequence.Name, sequence.OwnerSchema, sequence.OwnerTable)
                    is not { } ownership)
            {
                continue;
            }

            bool owned = sequence.OwnerSchema is not null && sequence.OwnerTable is not null;
            bool ownerVisible = owned && gate.CanSeeStructure(sequence.OwnerSchema!);
            bool valueAllowed = sequence.CanReadValue && gate.DefinitionAccess(sequence.Schema, ownership).Allowed;

            yield return new DatabaseSequenceSummary(
                DatabaseObjectKind.Sequence,
                sequence.Schema,
                sequence.Name,
                ownership,
                sequence.Comment,
                DatabaseCatalogQueries.IsQuotable(sequence.Schema) && DatabaseCatalogQueries.IsQuotable(sequence.Name),
                sequence.DataType,
                sequence.Start,
                sequence.Increment,
                sequence.Minimum,
                sequence.Maximum,
                sequence.Cycles,
                valueAllowed,
                valueAllowed ? sequence.LastValue : null,
                ownerVisible ? sequence.OwnerSchema : null,
                ownerVisible ? sequence.OwnerTable : null,
                ownerVisible ? sequence.OwnerColumn : null,
                owned && !ownerVisible,
                string.Equals(sequence.Name, DatabaseObjectClassifier.EventSequence, StringComparison.OrdinalIgnoreCase)
                    ? perTenant.GetValueOrDefault(sequence.Schema)
                    : 0);
        }
    }

    private static IEnumerable<DatabaseObjectSummary> Types(DatabaseGate gate, IReadOnlyList<CatalogType> types)
    {
        foreach (CatalogType type in types)
        {
            if (!gate.CanSeeStructure(type.Schema))
            {
                continue;
            }

            yield return Type(gate, type);
        }
    }

    /// <summary>One type, described only as far as the gate allows.</summary>
    public static DatabaseTypeSummary Type(DatabaseGate gate, CatalogType type)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(type);

        DatabaseObjectOwnership ownership = DatabaseObjectClassifier.ClassifyType(type.Schema, type.Name);
        bool described = gate.DefinitionAccess(type.Schema, ownership).Allowed;

        return new DatabaseTypeSummary(
            DatabaseObjectKinds.FromTyptype(type.Kind),
            type.Schema,
            type.Name,
            ownership,
            type.Comment,
            DatabaseCatalogQueries.IsQuotable(type.Schema) && DatabaseCatalogQueries.IsQuotable(type.Name),
            described ? type.BaseType : null,
            described && type.NotNull,
            described ? type.Default : null,
            described ? type.Labels : [],
            described ? type.Checks : [],
            described ? [.. type.Attributes.Select(static x => new DatabaseTypeAttribute(x.Name, x.Type))] : [],
            described ? type.RangeSubtype : null,
            type.UsedByColumns,
            described);
    }

    /// <summary>A key as its pointing table shows it, or <see langword="null" /> when it points into a hidden type.</summary>
    private static DatabaseForeignKeyInfo? ForeignKeyOut(DatabaseGate gate, CatalogForeignKey key)
    {
        if (!gate.CanSeeStructure(key.Schema)
            || gate.Classifier.IsHiddenTable(key.Schema, key.Table)
            || gate.Classifier.IsHiddenTable(key.LinkedSchema, key.LinkedTable))
        {
            return null;
        }

        bool visible = gate.CanSeeStructure(key.LinkedSchema);

        return new DatabaseForeignKeyInfo(
            key.Name,
            key.Schema,
            key.Table,
            key.Columns,
            visible ? key.LinkedSchema : null,
            visible ? key.LinkedTable : null,
            visible ? key.LinkedColumns : [],
            visible,
            visible ? gate.Classifier.ClassifyRelation(key.LinkedSchema, key.LinkedTable) : null,
            key.Validated,
            Action(key.OnDelete),
            Action(key.OnUpdate));
    }

    /// <summary>
    /// A key as its referenced table shows it, or <see langword="null" /> when the pointing table is one
    /// this visitor may not see - an inbound key from a withheld schema is not theirs to know about.
    /// </summary>
    private static DatabaseForeignKeyInfo? ForeignKeyIn(DatabaseGate gate, CatalogForeignKey key)
    {
        if (!gate.CanSeeStructure(key.Schema)
            || !gate.CanSeeStructure(key.LinkedSchema)
            || gate.Classifier.IsHiddenTable(key.Schema, key.Table)
            || gate.Classifier.IsHiddenTable(key.LinkedSchema, key.LinkedTable))
        {
            return null;
        }

        return new DatabaseForeignKeyInfo(
            key.Name,
            key.Schema,
            key.Table,
            key.Columns,
            key.LinkedSchema,
            key.LinkedTable,
            key.LinkedColumns,
            true,
            gate.Classifier.ClassifyRelation(key.Schema, key.Table),
            key.Validated,
            Action(key.OnDelete),
            Action(key.OnUpdate));
    }

    private static bool IsAt(string schema, string table, CatalogRelation relation) =>
        string.Equals(schema, relation.Schema, StringComparison.Ordinal)
        && string.Equals(table, relation.Name, StringComparison.Ordinal);

    private static void Increment(Dictionary<(string, string), int> counts, (string, string) key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;

    private const int RowBit = 1;
    private const int BeforeBit = 2;
    private const int InsertBit = 4;
    private const int DeleteBit = 8;
    private const int UpdateBit = 16;
    private const int TruncateBit = 32;
    private const int InsteadBit = 64;

    /// <summary><c>tgtype</c>'s timing bits, in SQL words.</summary>
    internal static string TriggerTiming(int type) =>
        (type & BeforeBit) != 0 ? "BEFORE" : (type & InsteadBit) != 0 ? "INSTEAD OF" : "AFTER";

    /// <summary><c>tgtype</c>'s event bits, in SQL words and SQL's order.</summary>
    internal static IReadOnlyList<string> TriggerEvents(int type)
    {
        List<string> events = [];

        if ((type & InsertBit) != 0)
        {
            events.Add("INSERT");
        }

        if ((type & UpdateBit) != 0)
        {
            events.Add("UPDATE");
        }

        if ((type & DeleteBit) != 0)
        {
            events.Add("DELETE");
        }

        if ((type & TruncateBit) != 0)
        {
            events.Add("TRUNCATE");
        }

        return events;
    }

    /// <summary>A foreign key action code, in SQL words.</summary>
    internal static string Action(string code) => code switch
    {
        "r" => "restrict",
        "c" => "cascade",
        "n" => "set null",
        "d" => "set default",
        _ => "no action",
    };

    private static DatabaseConstraintKind ConstraintKind(string code) => code switch
    {
        "p" => DatabaseConstraintKind.PrimaryKey,
        "u" => DatabaseConstraintKind.Unique,
        "c" => DatabaseConstraintKind.Check,
        "x" => DatabaseConstraintKind.Exclusion,
        "t" => DatabaseConstraintKind.ConstraintTrigger,
        _ => DatabaseConstraintKind.Other,
    };
}
