using PostgresStudio.Services.Database;

namespace PostgresStudio.Internal.Sql;

/// <summary>One result column of a statement, as the describe round trip reports it - before any value is read.</summary>
/// <param name="TypeOid">
/// The OID of the column's type (<c>pg_type.oid</c>), or zero when the reader did not report one. A domain is reported by
/// Postgres as its base type, so a column of a domain over <c>vector(3)</c> arrives as <c>vector</c>.
/// </param>
/// <param name="TypeName">
/// The type's name as the driver knows it, unqualified: what <c>pg_type.typname</c> holds for a type the driver has loaded,
/// and a placeholder for one it has not (an extension created after the connection was opened).
/// </param>
/// <param name="Readable">
/// Whether the driver can read the column as a value. A type with no handler - <c>vector</c>, <c>halfvec</c> and
/// <c>sparsevec</c> unless the host called <c>UseVector()</c>, or any other extension's type - is not.
/// </param>
internal readonly record struct DescribedColumn(uint TypeOid, string TypeName, bool Readable);

/// <summary>Which result columns of a statement arrive as the text Postgres prints, and which of those are pgvector's.</summary>
/// <param name="UnknownResultTypes">
/// The list <c>NpgsqlCommand.UnknownResultTypeList</c> takes - one flag per result column, <see langword="true" /> for a
/// column requested in text form - or <see langword="null" /> when every column is read as the driver reads it.
/// </param>
/// <param name="Vectors">
/// One entry per result column: the pgvector type the column holds, or <see langword="null" /> for a column that is not
/// one. Always the length of the result.
/// </param>
internal sealed record ResultTextPlan(bool[]? UnknownResultTypes, IReadOnlyList<PgVectorColumn?> Vectors)
{
    /// <summary>A statement that returns no columns, or none that need text.</summary>
    public static ResultTextPlan None(int columns) => new(null, new PgVectorColumn?[columns]);
}

/// <summary>
/// Chooses, from a statement's described columns, the ones to read as text (D13: the console's guard is the read-only
/// transaction, and this changes none of it).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Npgsql cannot read a column whose type it has no handler for. pgvector's three types are the common case
/// - the driver reads them only when the host's data source called <c>UseVector()</c>, and the <c>Pgvector</c> package that
/// provides it is outside the package budget (hard rule 1) - so the grid said "(cannot read vector)". Asking Postgres for
/// those columns in text form (<c>NpgsqlCommand.UnknownResultTypeList</c>) needs no handler: the value arrives as the
/// database prints it, which is also what the database browser shows and what a copy gives back.
/// </para>
/// <para>
/// <b>Which.</b> A column is text when the driver cannot read it, or when it is a pgvector type - even for a host whose
/// data source can read one, so the text is Postgres's own in every host and the grid has one shape to collapse. A type is
/// pgvector's when the <c>vector</c> extension owns it (<c>pg_depend.deptype 'e'</c>), exactly as the database browser
/// decides, so a type of a host's own that happens to be called <c>vector</c> is not. Every other column is read as before.
/// </para>
/// </remarks>
internal static class ResultColumnTypes
{
    /// <summary>
    /// Which of the given type OIDs the <c>vector</c> extension owns, with each one's name. Read from the catalog, inside the
    /// console's read-only transaction; the OIDs are a parameter and nothing a visitor typed is in the text.
    /// </summary>
    internal const string PgVectorTypesSql =
        """
        select t.oid::bigint,
               t.typname::text
        from pg_catalog.pg_type t
        join pg_catalog.pg_depend d
          on d.classid = 'pg_catalog.pg_type'::pg_catalog.regclass
         and d.objid = t.oid
         and d.refclassid = 'pg_catalog.pg_extension'::pg_catalog.regclass
         and d.deptype = 'e'
        join pg_catalog.pg_extension e on e.oid = d.refobjid
        where e.extname = 'vector'
          and t.oid::bigint = any(@oids)
        """;

    /// <summary>
    /// The type OIDs worth asking the catalog about: those of columns the driver cannot read, and those whose name is one of
    /// pgvector's three - the only columns that could turn out to be pgvector's. Distinct, and never zero.
    /// </summary>
    public static long[] Candidates(IReadOnlyList<DescribedColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        SortedSet<long> oids = [];

        foreach (DescribedColumn column in columns)
        {
            if (column.TypeOid != 0 && (!column.Readable || IsPgVectorName(column.TypeName)))
            {
                oids.Add(column.TypeOid);
            }
        }

        return [.. oids];
    }

    /// <summary>
    /// The plan for a statement's result.
    /// </summary>
    /// <param name="columns">The described columns, in order.</param>
    /// <param name="pgVectorTypes">
    /// The OID and name of each type among <see cref="Candidates" /> that the <c>vector</c> extension owns (the rows of
    /// <see cref="PgVectorTypesSql" />).
    /// </param>
    public static ResultTextPlan Plan(IReadOnlyList<DescribedColumn> columns, IReadOnlyDictionary<long, string> pgVectorTypes)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(pgVectorTypes);

        bool[] text = new bool[columns.Count];
        PgVectorColumn?[] vectors = new PgVectorColumn?[columns.Count];
        bool any = false;

        for (int i = 0; i < columns.Count; i++)
        {
            DescribedColumn column = columns[i];

            if (column.TypeOid != 0
                && pgVectorTypes.TryGetValue(column.TypeOid, out string? typeName)
                && PgVector.Column(PgVector.Extension, typeName, -1) is { } vector)
            {
                // Read as Postgres prints it, readable or not.
                vectors[i] = vector;
                text[i] = true;
            }
            else
            {
                text[i] = !column.Readable;
            }

            any |= text[i];
        }

        return new ResultTextPlan(any ? text : null, vectors);
    }

    private static bool IsPgVectorName(string typeName)
    {
        foreach (string name in PgVector.TypeNames)
        {
            if (string.Equals(typeName, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
