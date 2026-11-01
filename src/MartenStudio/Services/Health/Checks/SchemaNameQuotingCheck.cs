using System.Globalization;

using Marten;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Schema;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What K10 reads: the store's schema names that Postgres writes quoted, and the Weasel this host runs.</summary>
/// <param name="Schemas">The declared schema names that need quoting (<see cref="SchemaNameQuoting.QuotedSchemas" />), each once.</param>
/// <param name="Weasel">The loaded <c>Weasel.Postgresql</c>'s version, or <see langword="null" /> when it cannot be read.</param>
/// <param name="Marten">The loaded <c>Marten</c>'s version, or <see langword="null" /> when it cannot be read.</param>
internal sealed record SchemaNameQuotingFacts(IReadOnlyList<string> Schemas, Version? Weasel, Version? Marten);

/// <summary>
/// The rule behind K10 and the Schema screen's notice: which schema names break index readback below Weasel 9.47.1
/// (Marten 9.55), and the sentence that says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mechanism</b> (weasel#750): <c>Table.FetchExisting</c> keeps an index row when its <c>table_name</c> -
/// <c>regclassout</c> of the indexed table - equals one of four spellings of the table it was asked about, none of which
/// quotes the schema. <c>regclassout</c> writes the schema quoted whenever <c>quote_ident</c> would, so no index of such a
/// schema is ever read back, every declared index is "missing", and each apply after the first re-issues
/// <c>create index</c> and fails with <c>42P07</c>.
/// </para>
/// <para>
/// <b>Which names</b> are the ones <c>quote_ident</c> quotes, not <c>SqlIdentifier.Quote</c>'s rule, which quotes every
/// name there is: a name that is not made of lower-case ASCII letters, digits and underscores, that starts with a digit, or
/// that is a keyword Postgres does not list as unreserved (reserved, "type or function name" and "column name" keywords all
/// get quoted). Marten lowercases the schema names it is given, so in a store an upper-case letter is never the cause; a
/// schema of an <c>ExtendedSchemaObjects</c> table, which Marten does not lower, can still carry one.
/// </para>
/// <para>
/// <b>Which schemas</b> are every one the store declares: the document schema, the events schema, a document type's own
/// (<c>[DatabaseSchemaName]</c>), a flat table's and an <c>ExtendedSchemaObjects</c> object's. A schema only a hidden document
/// type declares is never named.
/// </para>
/// </remarks>
internal static class SchemaNameQuoting
{
    /// <summary>The first <c>Weasel.Postgresql</c> that reads such a schema's indexes back (in Marten 9.55.0).</summary>
    internal static readonly Version FixedInWeasel = new(9, 47, 1);

    /// <summary>
    /// The keywords <c>quote_ident</c> quotes: PostgreSQL 17's reserved, "type or function name" and "column name"
    /// categories (<c>pg_get_keywords()</c> codes <c>R</c>, <c>T</c> and <c>C</c>). Every release since 9.x differs by at
    /// most a word or two at the edges, and a name at an edge is a name nobody gives a schema.
    /// </summary>
    private static readonly HashSet<string> QuotedKeywords = new(StringComparer.Ordinal)
    {
        // Reserved.
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "both", "case", "cast", "check",
        "collate", "column", "constraint", "create", "current_catalog", "current_date", "current_role", "current_time",
        "current_timestamp", "current_user", "default", "deferrable", "desc", "distinct", "do", "else", "end", "except",
        "false", "fetch", "for", "foreign", "from", "grant", "group", "having", "in", "initially", "intersect", "into",
        "lateral", "leading", "limit", "localtime", "localtimestamp", "not", "null", "offset", "on", "only", "or", "order",
        "placing", "primary", "references", "returning", "select", "session_user", "some", "symmetric", "system_user",
        "table", "then", "to", "trailing", "true", "union", "unique", "user", "using", "variadic", "when", "where",
        "window", "with",

        // Type or function name.
        "authorization", "binary", "collation", "concurrently", "cross", "current_schema", "freeze", "full", "ilike",
        "inner", "is", "isnull", "join", "left", "like", "natural", "notnull", "outer", "overlaps", "right", "similar",
        "tablesample", "verbose",

        // Column name.
        "between", "bigint", "bit", "boolean", "char", "character", "coalesce", "dec", "decimal", "exists", "extract",
        "float", "greatest", "grouping", "inout", "int", "integer", "interval", "json", "json_array", "json_arrayagg",
        "json_exists", "json_object", "json_objectagg", "json_query", "json_scalar", "json_serialize", "json_table",
        "json_value", "least", "merge_action", "national", "nchar", "none", "normalize", "nullif", "numeric", "out",
        "overlay", "position", "precision", "real", "row", "setof", "smallint", "substring", "time", "timestamp", "treat",
        "trim", "values", "varchar", "xmlattributes", "xmlconcat", "xmlelement", "xmlexists", "xmlforest",
        "xmlnamespaces", "xmlparse", "xmlpi", "xmlroot", "xmlserialize", "xmltable",
    };

    /// <summary>Whether Postgres writes <paramref name="name" /> quoted, as <c>quote_ident</c> would.</summary>
    public static bool WouldBeQuoted(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name[0] is not ((>= 'a' and <= 'z') or '_'))
        {
            return true;
        }

        foreach (char c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return true;
            }
        }

        return QuotedKeywords.Contains(name);
    }

    /// <summary>
    /// Every schema the store declares that needs quoting, each once: the document and events schemas first, then a document
    /// type's own, a flat table's and an extended object's.
    /// </summary>
    /// <remarks>
    /// Read with <see cref="SchemaDeclarationReader" />, which touches no connection. A schema only a hidden document type
    /// declares is left out; when the declarations cannot be read only the two store-level names are judged.
    /// </remarks>
    /// <param name="options">The store's options.</param>
    /// <param name="isDocumentTypeVisible">The host's document-type filter, or <see langword="null" /> when it hides none.</param>
    public static IReadOnlyList<string> QuotedSchemas(IReadOnlyStoreOptions options, Func<Type, bool>? isDocumentTypeVisible = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> names = [];

        foreach (string schema in DeclaredSchemas(options, isDocumentTypeVisible))
        {
            if (WouldBeQuoted(schema) && !names.Contains(schema, StringComparer.Ordinal))
            {
                names.Add(schema);
            }
        }

        return names;
    }

    private static List<string> DeclaredSchemas(IReadOnlyStoreOptions options, Func<Type, bool>? isDocumentTypeVisible)
    {
        List<string> names = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        void Add(string? schema)
        {
            if (!string.IsNullOrWhiteSpace(schema) && seen.Add(schema))
            {
                names.Add(schema);
            }
        }

        Add(options.DatabaseSchemaName);
        Add(options.Events.DatabaseSchemaName);

        try
        {
            SchemaDeclarations declarations = SchemaDeclarationReader.Read(options, isDocumentTypeVisible);

            // The reader lists every schema, a hidden type's included (the Schema screen must not lie about disk), and declares
            // a hidden type's table with Visible = false. So a schema is named only when a store-level name or something
            // visible sits in it.
            HashSet<string> visible = new(StringComparer.OrdinalIgnoreCase)
            {
                options.DatabaseSchemaName,
                options.Events.DatabaseSchemaName,
            };

            MartenDeclaredObject[] shown = [.. declarations.Objects.Values.Where(static x => x.Visible)];

            foreach (MartenDeclaredObject declared in shown)
            {
                visible.Add(declared.Schema);
            }

            foreach (string schema in declarations.Schemas)
            {
                if (visible.Contains(schema))
                {
                    Add(schema);
                }
            }

            // An extended object's schema is deliberately not in Schemas (D27); it is a schema an apply creates in all the same.
            foreach (MartenDeclaredObject declared in shown)
            {
                Add(declared.Schema);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The two store-level names stand; a misconfigured mapping is the Drift tab's to report.
        }

        return names;
    }

    /// <summary>
    /// The sentence that says the hazard applies, or <see langword="null" /> when it does not: a Weasel that reads such
    /// indexes back, a Weasel the studio cannot read the version of, or schema names that need no quoting.
    /// </summary>
    /// <param name="schemas">The names that need quoting (<see cref="QuotedSchemas" />).</param>
    /// <param name="weasel">The loaded <c>Weasel.Postgresql</c>'s version.</param>
    public static string? Notice(IReadOnlyList<string> schemas, Version? weasel)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        if (schemas.Count == 0 || weasel is null || weasel >= FixedInWeasel)
        {
            return null;
        }

        return (schemas.Count == 1 ? "The schema " : "The schemas ") + Names(schemas) + (schemas.Count == 1 ? " needs" : " need")
            + " quoting in PostgreSQL (a reserved word, or a name with a character outside a to z, 0 to 9 and "
            + "underscore), and this host's Weasel.Postgresql " + Format(weasel) + " does not read the indexes of such a schema "
            + "back: every schema apply after the first fails with 42P07, \"relation already exists\". Upgrade to Marten 9.55 "
            + "or later, or rename the schema.";
    }

    /// <summary>The names, quoted for a sentence.</summary>
    internal static string Names(IReadOnlyList<string> schemas) => string.Join(", ", schemas.Select(static x => "\"" + x + "\""));

    /// <summary>A version as three numbers.</summary>
    internal static string Format(Version version) =>
        version.Major.ToString(CultureInfo.InvariantCulture) + "." + version.Minor.ToString(CultureInfo.InvariantCulture) + "."
        + Math.Max(version.Build, 0).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// K10: a schema name that breaks every apply after the first, below Weasel 9.47.1 (Marten 9.55).
/// </summary>
/// <remarks>
/// <para>
/// Fires only when the loaded <c>Weasel.Postgresql</c> is older than 9.47.1 <em>and</em> a schema the store declares is
/// written quoted by Postgres (<see cref="SchemaNameQuoting" />). Tier 0, no database read: both facts are in the
/// process, and the schema names are the store's configuration, so the gate is <see cref="HealthGate.None" />; a schema only a
/// hidden document type declares is never named.
/// </para>
/// <para>
/// Runs at every Marten version, because the hazard is a property of the one in use: at the floor (Weasel 9.33) it fires for
/// a schema such as <c>"order"</c> or <c>"my-schema"</c>, and from Marten 9.55 never.
/// </para>
/// </remarks>
[HealthCheck("K10", HealthArea.Configuration, HealthCostTier.InProcess, "Schema name breaks every apply after the first", Gate = HealthGate.None)]
internal sealed class SchemaNameQuotingCheck : HealthCheck<SchemaNameQuotingFacts>
{
    /// <inheritdoc />
    public override Task<SchemaNameQuotingFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        MartenRuntime runtime = context.Runtime;

        return Task.FromResult(new SchemaNameQuotingFacts(
            SchemaNameQuoting.QuotedSchemas(context.StoreOptions, context.IsDocumentTypeVisible),
            runtime.VersionOf(RuntimePackage.WeaselPostgresql),
            runtime.VersionOf(RuntimePackage.Marten)));
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(SchemaNameQuotingFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (facts.Schemas.Count == 0)
        {
            return HealthVerdict.Quiet("None of the store's declared schema names needs quoting.");
        }

        if (facts.Weasel is null)
        {
            return HealthVerdict.Quiet("The version of Weasel.Postgresql this host runs could not be read, so the studio cannot tell whether it reads such a schema's indexes back.");
        }

        if (SchemaNameQuoting.Notice(facts.Schemas, facts.Weasel) is not { } notice)
        {
            return HealthVerdict.Quiet(
                "Weasel.Postgresql " + SchemaNameQuoting.Format(facts.Weasel) + " reads the indexes of a schema that needs quoting back (from 9.47.1, in Marten 9.55).");
        }

        string names = SchemaNameQuoting.Names(facts.Schemas);

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "K10",
            Severity = HealthSeverity.Warning,
            Title = (facts.Schemas.Count == 1 ? "The schema " : "The schemas ") + names + (facts.Schemas.Count == 1 ? " breaks" : " break")
                + " every schema apply after the first",
            WhyItMatters =
                notice + " Marten reads the existing indexes back to decide what an apply must create; for such a schema it reads none, "
                + "so the next apply sees every declared index as missing and issues a plain create index, which Postgres refuses "
                + "because the index exists - and the failed statement stops everything ordered after it, so the schema never converges "
                + "and a deploy-time db-assert can never pass. The first apply, onto an empty schema, works, which is why it goes "
                + "unnoticed until the second.",
            Evidence =
            [
                new HealthEvidence(facts.Schemas.Count == 1 ? "Schema" : "Schemas", names),
                new HealthEvidence("Weasel.Postgresql", SchemaNameQuoting.Format(facts.Weasel) + " (reads such a schema's indexes back from 9.47.1)"),
                new HealthEvidence("Marten", facts.Marten is { } marten ? SchemaNameQuoting.Format(marten) : "unknown"),
                new HealthEvidence("Read from", "The schemas the store declares: StoreOptions.DatabaseSchemaName, Events.DatabaseSchemaName, a document type's own and an extended object's"),
            ],
            Caveat =
                "Read from this process's configuration alone; the studio does not look for the failure in the database. A host that "
                + "never applies a second time is not affected, and neither is a schema on the connection's search_path, whose name "
                + "Postgres writes bare. On PostgreSQL 14 and 15 json*, merge_action and system_user are not keywords.",
            Remedy = new HealthRemedy(
                """
                // Either upgrade Marten (Weasel 9.47.1 is in Marten 9.55), or name the schemas
                // in lower case, with no reserved word, wherever they are set:
                opts.DatabaseSchemaName = "orders";
                opts.Events.DatabaseSchemaName = "orders_events";
                // and a document type's own: [DatabaseSchemaName("orders")]
                """,
                "A rename moves the store to schemas that do not exist yet: it is a migration of the data, not a setting. Upgrading changes no name."),
        });
    }
}
