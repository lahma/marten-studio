using System.Reflection;
using System.Runtime.ExceptionServices;

using Marten;
using Marten.Linq;

using Npgsql;

using Weasel.Postgresql;

namespace MartenStudio.Services.Query;

/// <summary>The statement Marten built for a LINQ query, and the document types its parser says it reads.</summary>
/// <param name="Command">The command: Marten's SQL text and its parameters, never executed by Marten.</param>
/// <param name="DocumentTypes">
/// <c>LinqQueryParser.DocumentTypes()</c>: the queried type first, then every type an <c>Include</c> or a join
/// would read. LINQ mode can express neither, and refuses a statement that names anything but its one type.
/// </param>
internal sealed record MartenLinqStatement(NpgsqlCommand Command, IReadOnlyList<Type> DocumentTypes);

/// <summary>
/// Marten's SQL for a LINQ query, built the way <c>QueryableExtensions.ToCommand</c> builds it - minus the one
/// thing <c>ToCommand</c> also does, which is to create tables.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>ToCommand</c>.</b> At Marten <c>V9.39.0</c> and <c>V9.50.2</c> alike, <c>ToCommand</c> is
/// <c>MartenLinqQueryable&lt;T&gt;.ToPreviewCommand</c>, which builds the SQL and then runs
/// <c>Session.Database.EnsureStorageExists(documentType)</c> for every type the query reads. For a type this process
/// has not used yet, under the database's own <c>AutoCreate</c> - <c>CreateOrUpdate</c> by default - that is Weasel's
/// <c>ensureStorageExistsAsync</c>: <c>initializeSchemaWithNewConnection</c> (it creates the schema), then
/// <c>generateOrUpdateFeature</c>, which calls <c>AllObjects()</c> (hard rule 14: on a store with a HiLo id that
/// provisions <c>mt_hilo</c>) and applies <c>Migrator.ApplyAllAsync</c> - read at Weasel <c>9.33.0</c>. Every LINQ
/// execution path does the same (<c>MartenLinqQueryProvider.EnsureStorageExistsAsync</c>). A studio that wrote DDL
/// because somebody pressed Run on a predicate is the failure hard rule 14 exists to stop, and
/// <c>CreateOrUpdate</c> is not additive.
/// </para>
/// <para>
/// <b>So this calls what <c>ToPreviewCommand</c> calls, and stops.</b> <c>IMartenLinqQueryable.BuildLinqParser()</c>
/// then <c>LinqQueryParser.BuildDiagnosticCommand(FetchType, CommandBuilder)</c>, with the builder's
/// <c>TenantId</c> set to the session's exactly as <c>ToPreviewCommand</c> sets it - which is what binds Marten's
/// own tenant filter. Both are internal to Marten, present and identical in shape at <c>V9.39.0</c>,
/// <c>V9.48.0</c> and <c>V9.50.2</c>, and reached by reflection, once; if either is missing the mode is absent
/// (<see cref="DynamicLinqAdapter.Availability" />) rather than falling back to <c>ToCommand</c>. Marten's own
/// diagnostics read (9.48+, <c>DocumentStore.queryWithCriteriaOfTAsync</c>) builds its statement the same way and
/// says why: "a diagnostic read must not issue DDL".
/// </para>
/// <para>
/// This is the point at which Marten first sees the expression: constructing the parser walks it, and building
/// the command translates it, partial evaluation included. Everything that reaches here has been through
/// <see cref="LinqTextScreen" />, JasperFx's allow-list and <see cref="LinqExpressionGuard" />.
/// </para>
/// </remarks>
internal static class MartenLinqCommand
{
    private static readonly Lazy<Members?> Resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process's Marten builds LINQ statements the way the studio knows how to read.</summary>
    public static bool IsAvailable => Resolved.Value is not null;

    /// <summary>
    /// Marten's statement for <paramref name="queryable" />, with <paramref name="tenantId" /> as the tenant its
    /// tenant filter binds. Nothing is executed and no storage is ensured.
    /// </summary>
    /// <exception cref="InvalidOperationException">The members are missing, or this is not a Marten queryable.</exception>
    /// <remarks>Whatever Marten's translation throws - an expression it cannot turn into SQL - is rethrown as itself.</remarks>
    public static MartenLinqStatement Build<T>(IQueryable<T> queryable, string tenantId)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(queryable);
        ArgumentNullException.ThrowIfNull(tenantId);

        Members members = Resolved.Value
            ?? throw new InvalidOperationException("This Marten does not build LINQ statements the way the studio knows.");

        if (!members.Queryable.IsInstanceOfType(queryable))
        {
            throw new InvalidOperationException("Only a query Marten's own session created can be built into Marten's SQL.");
        }

        object parser = Invoke(members.BuildParser, queryable, [])
            ?? throw new InvalidOperationException("Marten built no LINQ parser.");

        var command = new NpgsqlCommand();
        var builder = new CommandBuilder(command) { TenantId = tenantId };

        Invoke(members.BuildDiagnosticCommand, parser, [FetchType.FetchMany, builder]);

        command.CommandText = builder.ToString();

        var types = Invoke(members.DocumentTypes, parser, []) as IEnumerable<Type>
            ?? throw new InvalidOperationException("Marten's LINQ parser listed no document types.");

        return new MartenLinqStatement(command, [.. types]);
    }

    private static object? Invoke(MethodInfo method, object target, object?[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }

    private static Members? Resolve()
    {
        try
        {
            Assembly marten = typeof(DocumentStore).Assembly;

            Type? queryable = marten.GetType("Marten.Linq.IMartenLinqQueryable", throwOnError: false);
            MethodInfo? buildParser = queryable?.GetMethod("BuildLinqParser", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            Type? parser = buildParser?.ReturnType;

            MethodInfo? diagnostic = parser?.GetMethod(
                "BuildDiagnosticCommand", BindingFlags.Public | BindingFlags.Instance, [typeof(FetchType), typeof(CommandBuilder)]);
            MethodInfo? documentTypes = parser?.GetMethod("DocumentTypes", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);

            if (queryable is null
                || buildParser is null
                || parser is null
                || !string.Equals(parser.FullName, "Marten.Linq.Parsing.LinqQueryParser", StringComparison.Ordinal)
                || diagnostic is null
                || documentTypes is null
                || !typeof(IEnumerable<Type>).IsAssignableFrom(documentTypes.ReturnType))
            {
                return null;
            }

            return new Members(queryable, buildParser, diagnostic, documentTypes);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private sealed record Members(Type Queryable, MethodInfo BuildParser, MethodInfo BuildDiagnosticCommand, MethodInfo DocumentTypes);
}
