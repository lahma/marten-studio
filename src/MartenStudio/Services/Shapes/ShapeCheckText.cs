using System.Globalization;

namespace MartenStudio.Services.Shapes;

/// <summary>
/// Every sentence of the shape check, one method each, so each is unit-tested and none is written twice (D-SHAPE S6 and S10).
/// </summary>
/// <remarks>
/// <para>
/// A sentence carries counts, paths, kinds and type names, never a value and never an exception message: its inputs have no
/// way to hold one. A name or path is wrapped in backticks, and <see cref="Split" /> turns a sentence into text and code runs
/// for a component to render - always as text, never as markup.
/// </para>
/// <para>
/// Document sentences are here; the event sentences are appended beside them.
/// </para>
/// </remarks>
internal static class ShapeCheckText
{
    // ---- Members (documents) --------------------------------------------------------------------------------------------

    /// <summary>A member the type no longer declares.</summary>
    public static string DocumentDropped(int count, int examined, string path, string typeName) =>
        $"{Of(count, examined)} sampled documents have `{path}`, which `{typeName}` no longer declares: the next save drops it.";

    /// <summary>A stored value the type reads as empty.</summary>
    public static string DocumentNulled(int count, int examined, string path, string typeName) =>
        $"{Of(count, examined)} sampled documents have a value at `{path}` that `{typeName}` reads as empty: the next save writes null over it.";

    /// <summary>A stored value of one JSON kind that the type writes as another.</summary>
    public static string DocumentRetyped(int count, int examined, string path, string typeName, System.Text.Json.JsonValueKind? stored, System.Text.Json.JsonValueKind? read) =>
        $"{Of(count, examined)} sampled documents store `{path}` as {ShapeAggregator.KindPhrase(stored)}, and `{typeName}` writes it back as {ShapeAggregator.KindPhrase(read)}: the next save rewrites it.";

    /// <summary>A stored value that comes back different.</summary>
    public static string DocumentRewritten(int count, int examined, string path) =>
        $"{Of(count, examined)} sampled documents have a value at `{path}` that comes back different: the next save rewrites it (precision, format or an enum's spelling).";

    /// <summary>A value kept under a different spelling of its name.</summary>
    public static string DocumentRespelled(int count, int examined, string path, string pairedPath, string typeName) =>
        $"{Of(count, examined)} sampled documents store `{path}`, which `{typeName}` writes as `{pairedPath}`: the value is kept, but a duplicated field, an index or raw SQL on the old spelling stops matching it after the next save.";

    /// <summary>A member the stored documents lack.</summary>
    public static string DocumentDefaulted(int count, int examined, string path, string typeName) =>
        count >= examined
            ? $"`{path}` is declared by `{typeName}` and set in none of the {examined.ToString("N0", CultureInfo.InvariantCulture)} sampled documents."
            : $"`{path}` is absent from {Of(count, examined)} sampled documents: they load with its default, and the next save writes it.";

    /// <summary>The question under a dropped and a defaulted member that are about equally common.</summary>
    public static string RenameQuestion(string droppedPath, string defaultedPath) =>
        $"`{droppedPath}` is dropped and `{defaultedPath}` is never set in about the same documents: was it renamed?";

    // ---- Failures (documents) -------------------------------------------------------------------------------------------

    /// <summary>A failure group of a document check.</summary>
    public static string DocumentFailure(ShapeFailureGroup failure, int examined, string typeName)
    {
        ArgumentNullException.ThrowIfNull(failure);

        string head = $"{Of(failure.Count, examined)} sampled documents";
        string how = failure.Path is null
            ? $" (`{failure.ExceptionType}`)"
            : failure.StoredKind is { } kind
                ? $": `{failure.Path}` is stored as {ShapeAggregator.KindPhrase(kind)} (`{failure.ExceptionType}`)"
                : $": `{failure.Path}` cannot be read (`{failure.ExceptionType}`)";

        return failure.Kind switch
        {
            ShapeFailureKind.UnknownSubclass =>
                $"{head} name the subclass `{failure.Detail}`, which `{typeName}` no longer registers: Marten cannot load them.",
            ShapeFailureKind.ReadAsNothing =>
                $"{head} load as nothing at all as `{typeName}`. Loading one returns null, and the studio's editor refuses to save it.",
            ShapeFailureKind.NotWritable =>
                $"{head} load as `{typeName}`, but writing them back could not be checked (`{failure.ExceptionType}`): the next save of one may throw.",
            _ => $"{head} cannot be loaded as `{typeName}`{how}. Loading one throws, and the studio's editor refuses to save it.",
        };
    }

    /// <summary>The line under a type with nothing found.</summary>
    public static string EveryDocumentFits(string typeName) => $"Every sampled document fits `{typeName}`.";

    /// <summary>The "and N more" line of a capped table.</summary>
    public static string AndMore(int more, string what) =>
        $"and {more.ToString("N0", CultureInfo.InvariantCulture)} more {what} not shown.";

    // ---- The result (documents) -----------------------------------------------------------------------------------------

    /// <summary>The alias is not a visible registered document type; one sentence for hidden, unknown and discovered alike.</summary>
    public static string NoSuchDocumentType(string alias) => $"There is no registered document type called '{alias}' to check.";

    /// <summary>The collection's table is not in this database.</summary>
    public const string TableMissing = "Marten has not created this table in this database yet, so there is nothing to check.";

    /// <summary>The spread branch had no statistics.</summary>
    public const string NotAnalysed =
        "PostgreSQL has not analysed this table yet, so only the lowest and highest ids were read.";

    /// <summary>A conjoined collection read for every tenant: no spread, because the primary key leads with the tenant.</summary>
    public const string AllTenantsEdgesOnly =
        "With all tenants in scope only the lowest and highest keys were read; choose a tenant for a spread sample.";

    /// <summary>The spread branch cannot be made for this id column.</summary>
    public const string IdTypeCannotBeSpread =
        "The id column's type cannot be spread, so only the lowest and highest ids were read.";

    /// <summary>What the scope leaves out of a document check.</summary>
    public const string SoftDeletedLeftOut = "Soft-deleted documents are not checked.";

    /// <summary>What a projection-owned collection adds.</summary>
    public const string ProjectionOwned =
        "A projection writes this collection, so a rebuild rewrites it from the events: check the event types too.";

    /// <summary>The summary line of a finished check.</summary>
    /// <param name="result">The result.</param>
    /// <param name="checkedAt">When it finished, in the visitor's time zone, as text.</param>
    public static string Summary(ShapeCheckResult result, string checkedAt)
    {
        ArgumentNullException.ThrowIfNull(result);

        string count = result.Examined.ToString("N0", CultureInfo.InvariantCulture);

        return result.Coverage switch
        {
            ShapeCoverage.Complete when result.Examined == 0 => "There are no documents in scope to check.",
            ShapeCoverage.Complete => $"Checked all {count} {Noun(result.Examined)} at {checkedAt}.",
            ShapeCoverage.Sampled =>
                $"Checked {count} {Noun(result.Examined)} at {checkedAt}: the {result.LowestPart.ToString("N0", CultureInfo.InvariantCulture)} lowest "
                + $"and {result.HighestPart.ToString("N0", CultureInfo.InvariantCulture)} highest ids"
                + (result.SpreadPart > 0
                    ? $" and {result.SpreadPart.ToString("N0", CultureInfo.InvariantCulture)} spread across the collection."
                    : "."),
            ShapeCoverage.Partial => $"Stopped after {count} {Noun(result.Examined)}: {result.CoverageReason}",
            _ => result.CoverageReason ?? result.Error ?? "Nothing was checked.",
        };
    }

    /// <summary>Why a check stopped early because the wall budget ran out.</summary>
    public static string BudgetRanOut(TimeSpan budget) =>
        $"the {budget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}-second budget ran out.";

    /// <summary>Why a check stopped early because PostgreSQL cancelled a statement.</summary>
    public static string StatementTimedOut(TimeSpan timeout) =>
        $"a statement ran past the {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}-second statement timeout.";

    /// <summary>Items too large to read.</summary>
    public static string TooLarge(int count) =>
        $"Too large to check: {count.ToString("N0", CultureInfo.InvariantCulture)} (over {ShapeCheckLimits.MaxItemBytes / 1024} KiB each, counted and not read).";

    /// <summary>The check could not be made.</summary>
    public static string Failed(string reason) => $"The shape check could not be completed: {reason}";

    /// <summary>The disabled "Check again" button's title.</summary>
    public static string AvailableAt(string time) => $"Available at {time}";

    /// <summary>Splits a sentence at its backticks into runs of text and code.</summary>
    /// <param name="sentence">A sentence with names in backticks.</param>
    public static IEnumerable<(string Text, bool IsCode)> Split(string sentence)
    {
        ArgumentNullException.ThrowIfNull(sentence);

        string[] parts = sentence.Split('`');

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                yield return (parts[i], i % 2 == 1);
            }
        }
    }

    /// <summary>"12 of 200".</summary>
    public static string Of(int count, int examined) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} of {examined.ToString("N0", CultureInfo.InvariantCulture)}";

    private static string Noun(int count) => count == 1 ? "document" : "documents";
}
