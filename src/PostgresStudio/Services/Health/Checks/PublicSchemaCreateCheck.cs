using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>One of the source's own schemas, and whether every role may create in it.</summary>
/// <param name="Schema">The schema.</param>
/// <param name="PublicCreate">Whether <c>PUBLIC</c> holds <c>CREATE</c> on it.</param>
internal sealed record SchemaCreateRow(string Schema, bool PublicCreate);

/// <summary>What S06 reads.</summary>
/// <param name="OwnSchemas">The source's own schemas, as its declarations name them.</param>
/// <param name="Schemas">Each of them that exists, with its <c>PUBLIC</c> grant.</param>
/// <param name="VersionNumber">The server's <c>server_version_num</c>.</param>
internal sealed record PublicSchemaCreateFacts(IReadOnlyList<string> OwnSchemas, IReadOnlyList<SchemaCreateRow> Schemas, int VersionNumber);

/// <summary>
/// S06: <c>PUBLIC</c> - every role on the server, now and later - may create objects in one of the source's own schemas.
/// </summary>
/// <remarks>
/// <para>
/// <b>The source's own schemas only</b>: a Marten store's schemas, Postgres Studio's admitted <c>BrowsableSchemas</c>
/// (<see cref="IDatabaseDeclarationsSource" />, read from configuration). Their names are structure every visitor through the
/// check's gate is shown already, so the finding names the schema.
/// </para>
/// <para>
/// <b>Read from the ACL as Postgres applies it</b>: <c>aclexplode</c> over the schema's ACL, or its owner's default when it has
/// none, which grants <c>PUBLIC</c> nothing. A cluster created on 14 gives <c>public</c> that grant, and one created on 15 or
/// later does not - its <c>public</c> is owned by <c>pg_database_owner</c> (B0 §6: <c>t</c> on 14, <c>f</c> on 17 and 18) -
/// but a cluster upgraded from 14 keeps it.
/// </para>
/// <para>
/// <b>The remedy is words.</b> It is a privilege change, which no remedy may be (<see cref="HealthRemedy" /> refuses
/// <c>REVOKE</c>): whoever owns the schema makes it.
/// </para>
/// </remarks>
[HealthCheck("S06", HealthArea.Server, HealthCostTier.Catalog, "Anyone may create in an application schema")]
internal sealed class PublicSchemaCreateCheck : HealthCheck<PublicSchemaCreateFacts>
{
    /// <summary>What the status row says when the source's declarations - its own schemas - could not be read.</summary>
    internal const string DeclarationsUnread =
        "The source's own schemas could not be read from its declarations, so whether PUBLIC may create in them is not checked.";

    /// <summary>
    /// The source's own schemas from its declarations - failing closed, as the database browser's classification does: a
    /// studio with no declarations, or declarations that could not all be read, has no schemas the studio may call its own,
    /// and "nothing to check" would be a quiet row for a check that never looked. Pure, for the unit tests.
    /// </summary>
    /// <exception cref="HealthCheckUnavailableException">There are no declarations, or they did not succeed.</exception>
    internal static IReadOnlyList<string> OwnSchemas(DatabaseDeclarations? declared) =>
        declared is { Succeeded: true } ? declared.StoreSchemas : throw new HealthCheckUnavailableException(DeclarationsUnread);

    /// <inheritdoc />
    public override async Task<PublicSchemaCreateFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IDatabaseDeclarationsSource? declarations;

        try
        {
            declarations = context.Services.GetService<IDatabaseDeclarationsSource>();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A source the sweep's scope, which has no circuit, cannot build fails closed, as C06's classifier does.
            throw new HealthCheckUnavailableException(DeclarationsUnread, exception);
        }

        IReadOnlyList<string> own = OwnSchemas(declarations?.ReadUncached(context.Source));
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        if (own.Count == 0)
        {
            return new PublicSchemaCreateFacts(own, [], server.VersionNumber);
        }

        var query = ServerPostureQueries.SchemaCreate(own);
        IReadOnlyList<SchemaCreateRow> rows = await context.Sql
            .QueryAsync(query, static reader => new SchemaCreateRow(reader.GetString(0), reader.GetBoolean(1)), cancellationToken)
            .ConfigureAwait(false);

        return new PublicSchemaCreateFacts(own, rows, server.VersionNumber);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(PublicSchemaCreateFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (facts.OwnSchemas.Count == 0)
        {
            return HealthVerdict.Quiet("There is no schema of " + evaluation.Text.SourcePossessive + " own to check.");
        }

        List<SchemaCreateRow> open = [.. facts.Schemas.Where(static x => x.PublicCreate)];

        if (facts.Schemas.Count == 0)
        {
            return HealthVerdict.Quiet("None of " + evaluation.Text.SourcePossessive + " own schemas exists yet.");
        }

        if (open.Count == 0)
        {
            return HealthVerdict.Quiet(
                "No role but the ones granted it may create in " + Count(facts.Schemas.Count) + " of " + evaluation.Text.SourcePossessive
                + " own schemas" + (facts.Schemas.Count < facts.OwnSchemas.Count ? " (the others do not exist yet)." : "."));
        }

        bool fifteenDefault = PostgresFeatures.Has(PostgresFeature.PublicSchemaOwnedByDatabaseOwner, facts.VersionNumber);

        return new HealthVerdict(
        [
            .. open.Select(schema => new HealthFinding
            {
                Code = "S06",
                Severity = HealthSeverity.Warning,
                Subject = schema.Schema,
                Title = "Any role may create objects in schema " + schema.Schema,
                WhyItMatters =
                    "PUBLIC - every role on this server, present and future - holds CREATE on " + schema.Schema + ". Any of them can put a "
                    + "table, a view, a function or an operator there; one named like something the application calls unqualified, and "
                    + "earlier on its search_path, is called in its place, with the application's rights (the pattern of CVE-2018-1058). "
                    + "Whoever owns the schema revokes CREATE on it from PUBLIC and grants it to the roles that create objects there - a "
                    + "privilege change, so the studio says it here and never as a statement to paste.",
                Evidence =
                [
                    new("Schema", schema.Schema),
                    new("May create", "PUBLIC (every role)"),
                    new("PostgreSQL", (facts.VersionNumber / 10_000).ToString(CultureInfo.InvariantCulture)),
                ],
                Caveat = fifteenDefault
                    ? "PostgreSQL 15 and later create the public schema without this grant (owned by pg_database_owner), so on this server "
                      + "somebody granted it, or the cluster was upgraded from an older release that had it."
                    : "Below PostgreSQL 15 every database's public schema is created with this grant; from 15 it is not.",
            }),
        ]);
    }

    private static string Count(int value) => value == 1 ? "the one" : "all " + value.ToString(CultureInfo.InvariantCulture);
}
