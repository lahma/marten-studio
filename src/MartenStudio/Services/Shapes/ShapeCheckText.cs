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

    // ---- Members (events) -----------------------------------------------------------------------------------------------------

    /// <summary>A member the event type no longer declares: projections never see it, and nothing is lost from the log.</summary>
    public static string EventDropped(int count, int examined, string path, string typeName) =>
        $"{Of(count, examined)} sampled `{typeName}` events carry `{path}`, which `{typeName}` no longer declares: projections never see it. "
        + "Events are never rewritten, so nothing is lost from the log.";

    /// <summary>A stored value the event type reads as empty.</summary>
    public static string EventNulled(int count, int examined, string path, string typeName) =>
        $"{Of(count, examined)} sampled `{typeName}` events have a value at `{path}` that `{typeName}` reads as empty: projections see it as empty.";

    /// <summary>A stored value of one JSON kind that the event type reads as another.</summary>
    public static string EventRetyped(int count, int examined, string path, string typeName, System.Text.Json.JsonValueKind? stored, System.Text.Json.JsonValueKind? read) =>
        $"{Of(count, examined)} sampled `{typeName}` events store `{path}` as {ShapeAggregator.KindPhrase(stored)}, and `{typeName}` reads it as {ShapeAggregator.KindPhrase(read)}: "
        + $"projections see it as {ShapeAggregator.KindPhrase(read)}.";

    /// <summary>A stored scalar that comes back different.</summary>
    public static string EventRewritten(int count, int examined, string path, string typeName) =>
        $"{Of(count, examined)} sampled `{typeName}` events have a value at `{path}` that comes back different: projections see a different value from the one stored.";

    /// <summary>A value read under another spelling of its name.</summary>
    public static string EventRespelled(int count, int examined, string path, string pairedPath, string typeName) =>
        $"{Of(count, examined)} sampled `{typeName}` events store `{path}`, which `{typeName}` reads as `{pairedPath}`: the value reaches projections under the new spelling.";

    /// <summary>A member the stored events lack.</summary>
    public static string EventDefaulted(int count, int examined, string path, string typeName) =>
        count >= examined
            ? $"`{path}` is declared by `{typeName}` and set in none of the {examined.ToString("N0", CultureInfo.InvariantCulture)} sampled events."
            : $"`{path}` is absent from {Of(count, examined)} sampled `{typeName}` events: projections read its default.";

    // ---- Failures (events) ----------------------------------------------------------------------------------------------------

    /// <summary>A failure group of an event check.</summary>
    public static string EventFailure(ShapeFailureGroup failure, int examined, string typeName)
    {
        ArgumentNullException.ThrowIfNull(failure);

        string head = $"{Of(failure.Count, examined)} sampled `{typeName}` events";

        switch (failure.Kind)
        {
            case ShapeFailureKind.UnknownEventType:
                string[] names = (failure.Detail ?? string.Empty).Split(" / ", 2);
                string dotNet = names.Length > 1 && names[1].Length > 0 && names[1] != "(none)"
                    ? $", and `{names[1]}` cannot be loaded"
                    : ", and the row names no .NET type to load";

                return $"{Of(failure.Count, examined)} `{typeName}` events cannot be read: no type is registered for `{names[0]}`{dotNet}.";
            case ShapeFailureKind.UnregisteredEventType:
                string[] stored = (failure.Detail ?? string.Empty).Split(" / ", 2);
                string clr = stored.Length > 1 ? ClrName(stored[1]) : "a loaded type";

                return $"{Of(failure.Count, examined)} `{typeName}` events have no registered type, but `{stored.ElementAtOrDefault(1)}` is loaded: Marten would read these as `{clr}` "
                    + $"and register it on first read; register it with `opts.Events.AddEventType<{clr}>()`. Their members are not compared.";
            case ShapeFailureKind.ReadAsNothing:
                return $"{head} are read as nothing at all: projections never see them.";
            case ShapeFailureKind.NotWritable:
                return $"{head} are read, but writing them back could not be checked (`{failure.ExceptionType}`).";
            default:
                string how = failure.Path is null
                    ? $" (`{failure.ExceptionType}`)"
                    : failure.StoredKind is { } kind
                        ? $": `{failure.Path}` is stored as {ShapeAggregator.KindPhrase(kind)} (`{failure.ExceptionType}`)"
                        : $": `{failure.Path}` cannot be read (`{failure.ExceptionType}`)";

                return $"{head} cannot be read{how}.";
        }
    }

    /// <summary>The short name of the type a stored <c>mt_dotnet_type</c> ("Shop.Orders.OrderPlaced, Shop") names.</summary>
    private static string ClrName(string stored)
    {
        int comma = stored.LastIndexOf(", ", StringComparison.Ordinal);
        string full = comma > 0 ? stored[..comma] : stored;
        int dot = full.LastIndexOf('.');

        return dot >= 0 ? full[(dot + 1)..] : full;
    }

    private static string At(long? firstSequence, string? firstTenant) =>
        firstSequence is { } sequence
            ? $"stops at sequence {sequence.ToString("N0", CultureInfo.InvariantCulture)}{(firstTenant is null ? string.Empty : $" (tenant {firstTenant})")}, the first of them, "
            : "stops at the first of them, ";

    /// <summary>What a rebuild and the running daemon do about the events that cannot be deserialized.</summary>
    /// <param name="typeName">The event type.</param>
    /// <param name="firstSequence">The lowest sequence of a failing event.</param>
    /// <param name="rebuildSkips"><c>Projections.RebuildErrors.SkipSerializationErrors</c>.</param>
    /// <param name="daemonSkips"><c>Projections.Errors.SkipSerializationErrors</c>.</param>
    /// <param name="firstTenant">The tenant of that event, named when each tenant numbers its own events and every tenant is in scope.</param>
    public static string RebuildSerialization(string typeName, long? firstSequence, bool rebuildSkips, bool daemonSkips, string? firstTenant = null)
    {
        string at = At(firstSequence, firstTenant);

        string rebuild = rebuildSkips
            ? "A rebuild skips them and records each as a dead letter (`RebuildErrors.SkipSerializationErrors` is on): its projections leave them out."
            : $"A rebuild of a projection that reads `{typeName}` {at}because `RebuildErrors.SkipSerializationErrors` is off.";

        string daemon = daemonSkips
            ? "The running daemon skips them and records each as a dead letter (`Errors.SkipSerializationErrors` is on)."
            : "The running daemon pauses the shard at the first of them (`Errors.SkipSerializationErrors` is off).";

        return rebuild + " " + daemon;
    }

    /// <summary>What a rebuild and the running daemon do about the events no type answers to.</summary>
    /// <param name="firstSequence">The lowest sequence of a failing event.</param>
    /// <param name="rebuildSkips"><c>Projections.RebuildErrors.SkipUnknownEvents</c>.</param>
    /// <param name="daemonSkips"><c>Projections.Errors.SkipUnknownEvents</c>.</param>
    /// <param name="firstTenant">The tenant of that event, named when each tenant numbers its own events and every tenant is in scope.</param>
    public static string RebuildUnknown(long? firstSequence, bool rebuildSkips, bool daemonSkips, string? firstTenant = null)
    {
        string at = At(firstSequence, firstTenant);

        string rebuild = rebuildSkips
            ? "A rebuild skips them (`RebuildErrors.SkipUnknownEvents` is on): its projections leave them out."
            : $"A rebuild of a projection that reads every event type {at}because `RebuildErrors.SkipUnknownEvents` is off.";

        string daemon = daemonSkips
            ? "The running daemon skips them (`Errors.SkipUnknownEvents` is on)."
            : "The running daemon pauses the shard at the first of them (`Errors.SkipUnknownEvents` is off).";

        return rebuild + " " + daemon;
    }

    /// <summary>The remedy for events the reader cannot deserialize.</summary>
    public const string UpcasterRemedy =
        "Register an upcaster for the old shape (`opts.Events.Upcast(...)`), or make the member accept both shapes.";

    /// <summary>The line under an event type with nothing found.</summary>
    public static string EveryEventFits(string typeName) => $"Every sampled event fits `{typeName}`.";

    /// <summary>Events written as one type and read as another.</summary>
    public static string EventsReadAsOtherType(int count, int examined, string typeName) =>
        $"{Of(count, examined)} sampled `{typeName}` events were written as another type and are read as `{typeName}`: an upcaster or a renamed type maps them, so their members are not compared.";

    /// <summary>Events with a binary body.</summary>
    public static string EventsBinary(int count, int examined, string typeName) =>
        $"{Of(count, examined)} sampled `{typeName}` events have a binary body: they are read, and not compared.";

    // ---- The result (events) --------------------------------------------------------------------------------------------------

    /// <summary>The event type is not one the store registers or its events table holds; one sentence for every such name.</summary>
    public static string NoSuchEventType(string name) => $"There is no event type called '{name}' to check.";

    /// <summary>No event of the type is in scope; the page's own count said so, or the table has none.</summary>
    public const string NoEventsInScope = "There are no events of this type in scope.";

    /// <summary>What the scope leaves out of an event check: archived events.</summary>
    public const string ArchivedLeftOut = "Archived events are not checked: a rebuild skips them.";

    /// <summary>What the scope leaves out of an event check: events marked skipped.</summary>
    public const string SkippedLeftOut = "Events marked as skipped are not checked: a rebuild skips them.";

    /// <summary>The type has no index, so only the edges were read.</summary>
    public const string NoTypeIndex =
        "Without an index on type, only the oldest and newest events of each type were read.";

    /// <summary>The line that adds the index.</summary>
    public const string TypeIndexRemedy = "opts.Events.EnableEventTypeIndex = true;";

    /// <summary>Per-tenant sequences with every tenant in scope: no spread.</summary>
    public const string EventsAllTenantsEdgesOnly =
        "With all tenants in scope and a sequence of each tenant's own, only the oldest and newest events of each type were read; choose a tenant for a spread sample.";

    /// <summary>The events table is missing columns the store's configuration reads.</summary>
    public static string EventColumnsMissing(string schema, IReadOnlyList<string> missing) =>
        $"{schema}.mt_events has no {(missing.Count == 1 ? "column" : "columns")} {string.Join(", ", missing)}, which this store's configuration reads. "
        + "Apply the pending change from the Schema screen, then check again.";

    /// <summary>The summary of a finished events check.</summary>
    /// <param name="result">The result.</param>
    /// <param name="checkedAt">When it finished, as text.</param>
    /// <param name="age">How long before this press it finished, when the cache handed it back, else <see langword="null" />.</param>
    public static string EventSummary(ShapeCheckResult result, string checkedAt, TimeSpan? age = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        checkedAt = WithAge(checkedAt, age);

        string count = result.Examined.ToString("N0", CultureInfo.InvariantCulture);
        string noun = result.Examined == 1 ? "event" : "events";

        return result.Coverage switch
        {
            ShapeCoverage.Complete when result.Examined == 0 => NoEventsInScope,
            ShapeCoverage.Complete => $"Checked all {count} {noun} at {checkedAt}.",
            ShapeCoverage.Sampled =>
                $"Checked {count} {noun} at {checkedAt}: the {result.LowestPart.ToString("N0", CultureInfo.InvariantCulture)} oldest "
                + $"and {result.HighestPart.ToString("N0", CultureInfo.InvariantCulture)} newest"
                + (result.SpreadPart > 0
                    ? $" and {result.SpreadPart.ToString("N0", CultureInfo.InvariantCulture)} spread between them."
                    : "."),
            ShapeCoverage.Partial => $"Stopped after {count} {noun}: {result.CoverageReason}",
            _ => result.CoverageReason ?? result.Error ?? "Nothing was checked.",
        };
    }

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
    /// <param name="age">How long before this press it finished, when the cache handed it back, else <see langword="null" />.</param>
    public static string Summary(ShapeCheckResult result, string checkedAt, TimeSpan? age = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        checkedAt = WithAge(checkedAt, age);

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

    /// <summary>
    /// "14:05 (3 min ago, from the last check)" for a result the cache handed back, else the time alone: a result that is not this
    /// press's says so, rather than read as if it had just been made.
    /// </summary>
    /// <param name="checkedAt">When it finished, as text.</param>
    /// <param name="age">How long before this press it finished, or <see langword="null" /> for a check this press made.</param>
    public static string WithAge(string checkedAt, TimeSpan? age) =>
        age is { } elapsed ? $"{checkedAt} ({Ago(elapsed)}, from the last check)" : checkedAt;

    /// <summary>"40 s ago", "3 min ago", "2 h ago": whole units, never "0 min ago".</summary>
    /// <param name="age">How long ago.</param>
    public static string Ago(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalMinutes < 1
            ? $"{Math.Max(1, (int) age.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s ago"
            : age.TotalHours < 1
                ? $"{((int) age.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min ago"
                : $"{((int) age.TotalHours).ToString(CultureInfo.InvariantCulture)} h ago";
    }

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
