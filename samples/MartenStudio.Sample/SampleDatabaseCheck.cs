using MartenStudio.SampleDomain;

namespace MartenStudio.Sample;

/// <summary>
/// What the host does about a database another build of the sample seeded, decided before Marten touches it.
/// </summary>
/// <remarks>
/// <para>
/// A plain <c>dotnet run</c> reuses its container, so the database it meets can be weeks old: seeded by a build whose
/// tables had another shape, with rows this build's store cannot migrate over. Marten would fail on them halfway
/// through starting - <c>23503 mt_doc_invoice_customer_id_fkey</c> was the one that was hit - so the host judges the
/// database first (<see cref="SampleDatabase" />), and nothing has touched it yet.
/// </para>
/// <para>
/// <b>Reseeded, or refused.</b> On the throwaway container this process started, or when the host was started with
/// <c>--allow-data-generation</c> - which already says outright that this database is the demo's to break - the demo's
/// two Marten schemas are dropped, Marten builds them again in this build's shape, and the seeder fills them from
/// nothing. On a database somebody configured, which may be shared, nothing is dropped: the host stops with one line
/// that says what to do.
/// </para>
/// </remarks>
internal static class SampleDatabaseCheck
{
    /// <summary>
    /// Leaves a database this build can seed alone, reseeds one it cannot when <paramref name="mayReseed" /> says so,
    /// and otherwise answers the line to stop with.
    /// </summary>
    /// <param name="connectionString">The database the store is pointed at.</param>
    /// <param name="mayReseed">Whether the demo's schemas may be dropped: the throwaway container, or the flag.</param>
    /// <param name="output">Where a reseed is reported, as the throwaway container's banner is.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="null" /> when the host may start; otherwise the one line to print before it stops.</returns>
    public static async Task<string?> PrepareAsync(
        string connectionString,
        bool mayReseed,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        SampleDatabaseInspection found = await SampleDatabase.InspectAsync(connectionString, cancellationToken);

        if (!found.NeedsReseed)
        {
            return null;
        }

        if (!mayReseed)
        {
            return RefusalLine(found);
        }

        // Judged again under the lock: another host started on the same database may have reseeded it already.
        SampleDatabaseInspection dropped = await SampleDatabase.ReseedAsync(connectionString, cancellationToken);

        if (dropped.NeedsReseed)
        {
            await output.WriteLineAsync(ReseededLine(dropped));
        }

        return null;
    }

    /// <summary>The line the host stops with on a configured database it will not drop anything in.</summary>
    internal static string RefusalLine(SampleDatabaseInspection found) =>
        "The Marten Studio sample did not start: the database in ConnectionStrings:Marten " + What(found)
        + ", which this build cannot seed over. Start it once with --allow-data-generation to have it drop and seed "
        + Schemas + " again, or drop them yourself.";

    /// <summary>The line the host prints when it dropped the demo's schemas and is about to seed them again.</summary>
    internal static string ReseededLine(SampleDatabaseInspection found) =>
        "Marten Studio sample: the database " + What(found) + ", so " + Schemas + " were dropped and are being seeded again.";

    private static string Schemas => string.Join(" and ", SampleDatabase.Schemas);

    private static string What(SampleDatabaseInspection found) => found.State == SampleDatabaseState.Stale
        ? "was seeded by another build of the sample (seed version " + found.SeedVersion
            + "; this build seeds " + SampleDataSeeder.SeedVersion + ")"
        : "has rows in " + SampleDatabase.Schemas[0] + " that the sample did not seed (no seed marker)";
}
