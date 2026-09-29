using System.Globalization;

using MartenStudio.Services.Database;

namespace MartenStudio.Services.Schema;

/// <summary>
/// A registered Marten store whose objects the Schema screen cannot tell from anybody else's.
/// </summary>
/// <param name="Name">
/// The store's registration key, or <see langword="null" /> when this visitor's store policy refuses them that
/// store - another store's key is shown only to a visitor that store's policy passes (AGENTS.md D27).
/// </param>
/// <param name="Problem">What went wrong, as a verb phrase: "could not be built", "could not be read".</param>
/// <param name="Detail">Why, in the store's own words - or <see langword="null" /> when the name is withheld.</param>
internal sealed record UnreadableStore(string? Name, string Problem, string? Detail);

/// <summary>
/// The classification the Schema screen's Tables, Indexes and Functions tabs draw with: the database
/// browser's, over every registered store - or, when a store cannot be read, the one the readable stores
/// give, with everything the unreadable one could own withheld.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not simply fail closed, as the browser does.</b> The browser's reason for failing closed is that it
/// reads rows and definitions: a table no store claims is "Other", and "Other" is readable. The Schema screen
/// reads neither - its tabs list the store's own structure, which was always shown - so blanking all three tabs
/// because an ancillary store in another database will not build (measured by the DB-7 review: one throwing
/// factory blanked Tables and Indexes for every store and withheld all twenty <c>mt_</c> function bodies)
/// protects nothing that the narrower rule below does not.
/// </para>
/// <para>
/// <b>The narrower rule.</b> What a readable store declares keeps its classification, a hidden type of a
/// readable store stays absent, and Marten's own bookkeeping is Marten's by its <c>mt_</c> name whoever owns
/// it. What is left - a table or routine no readable store declares, and an <c>mt_doc_</c> table nobody
/// declares, which could be a document type the unreadable store hides - could be that store's, so it is
/// withheld, counted, and the notice names the store (to a visitor that store's policy passes) and says it is
/// shown again once the store can be read.
/// </para>
/// </remarks>
internal sealed class SchemaClassification
{
    private SchemaClassification(DatabaseObjectClassifier classifier, IReadOnlyList<UnreadableStore> unreadable)
    {
        Classifier = classifier;
        Unreadable = unreadable;
    }

    /// <summary>Every registered store could be read: the database browser's own classification.</summary>
    public static SchemaClassification Complete(DatabaseObjectClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        return new SchemaClassification(classifier, []);
    }

    /// <summary>The readable stores' classification, and the stores that could not be read.</summary>
    public static SchemaClassification Degraded(DatabaseObjectClassifier classifier, IReadOnlyList<UnreadableStore> unreadable)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(unreadable);
        return new SchemaClassification(classifier, unreadable);
    }

    /// <summary>The classifier: over every store, or over the readable ones.</summary>
    public DatabaseObjectClassifier Classifier { get; }

    /// <summary>The stores that could not be read; empty when the classification is complete.</summary>
    public IReadOnlyList<UnreadableStore> Unreadable { get; }

    /// <summary>Whether a store could not be read, so what nobody readable declares is withheld.</summary>
    public bool IsDegraded => Unreadable.Count > 0;

    /// <summary>
    /// Whether a relation the classifier placed must still be withheld, because the unreadable store could own
    /// it: nothing readable declares it (<paramref name="ownership" /> names no store), and it is not Marten's
    /// by name - or it is named like a document table, which could be a type that store hides.
    /// </summary>
    /// <param name="name">The relation's name.</param>
    /// <param name="ownership">What <see cref="DatabaseObjectClassifier.ClassifyRelation" /> said.</param>
    public bool WithholdsRelation(string name, DatabaseObjectOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(ownership);

        return IsDegraded
            && ownership.StoreKey is null
            && (!ownership.IsMarten || name.StartsWith(DatabaseObjectClassifier.DocumentTablePrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a routine is one a hidden document type owns, and so absent from the list altogether - like
    /// the type's table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Marten 9 writes a document's upsert inline and installs no per-type routine, but a database a Marten of
    /// an earlier major version wrote keeps its <c>mt_upsert_&lt;alias&gt;</c>, <c>mt_insert_&lt;alias&gt;</c>,
    /// <c>mt_update_&lt;alias&gt;</c> and <c>mt_overwrite_&lt;alias&gt;</c> - named after the alias, beside the
    /// type's <c>mt_doc_&lt;alias&gt;</c> table, and with a body that names the table and every duplicated
    /// column. For a type the host hides, that is the hidden table by another name (DB-1-fix re-review).
    /// </para>
    /// <para>
    /// Hidden when the table beside it is a hidden type's, and - while the host hides any type at all - when no
    /// registered store declares that table, since a type Marten has not learned yet may be one it hides: the
    /// same rule the browser applies to a view over an undeclared document table.
    /// </para>
    /// <para>
    /// TODO(DB-3-fix, consolidate): DB-3-fix makes <c>DatabaseObjectClassifier.ClassifyRoutine</c> answer
    /// <see langword="null" /> for exactly these. Once it has landed, this becomes that null.
    /// </para>
    /// </remarks>
    /// <param name="schema">The routine's schema.</param>
    /// <param name="name">The routine's name.</param>
    public bool HidesRoutine(string schema, string name) => IsHiddenTypesRoutine(Classifier, schema, name);

    /// <inheritdoc cref="HidesRoutine" />
    /// <param name="classifier">The classification to ask.</param>
    /// <param name="schema">The routine's schema.</param>
    /// <param name="name">The routine's name.</param>
    internal static bool IsHiddenTypesRoutine(DatabaseObjectClassifier classifier, string schema, string name)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(name);

        foreach (string prefix in PerTypeRoutinePrefixes)
        {
            if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string table = DatabaseObjectClassifier.DocumentTablePrefix + name[prefix.Length..];

            return classifier.IsHiddenTable(schema, table)
                || (classifier.MayHideDocumentTypes && classifier.IsUndeclaredDocumentTable(schema, table));
        }

        return false;
    }

    /// <summary>The prefixes of the per-document-type routines earlier Marten versions installed.</summary>
    internal static readonly string[] PerTypeRoutinePrefixes = ["mt_upsert_", "mt_insert_", "mt_update_", "mt_overwrite_"];

    /// <summary>
    /// Whether a routine the classifier placed must still be withheld: nothing readable declares it and it is
    /// not Marten's by name.
    /// </summary>
    public bool WithholdsRoutine(DatabaseObjectOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        return IsDegraded && ownership.StoreKey is null && !ownership.IsMarten;
    }

    /// <summary>The Tables tab's notice, or <see langword="null" /> when nothing is degraded.</summary>
    /// <param name="withheld">How many tables were withheld.</param>
    public string? TablesNotice(int withheld) =>
        Notice(
            withheld switch
            {
                0 => "Every table here is declared by a store that could be read, so none is withheld.",
                1 => "1 table here that no readable store declares is withheld until then: it could be that " +
                     "store's - a table of a document type it hides from the studio, or a projection's table that " +
                     "would read as \"not Marten\".",
                _ => Count(withheld) + " tables here that no readable store declares are withheld until then: any " +
                     "of them could be that store's - a table of a document type it hides from the studio, or a " +
                     "projection's table that would read as \"not Marten\".",
            },
            "Marten's own bookkeeping tables are listed by their mt_ names, as always.");

    /// <summary>The Indexes tab's notice, or <see langword="null" /> when nothing is degraded.</summary>
    /// <param name="withheld">On how many tables the indexes were withheld.</param>
    public string? IndexesNotice(int withheld) =>
        Notice(
            withheld switch
            {
                0 => "Every index here is on a table a store that could be read declares, so none is withheld.",
                1 => "The indexes on 1 table that no readable store declares are withheld until then: it could be " +
                     "that store's - a table of a document type it hides from the studio, whose indexes are named " +
                     "after it.",
                _ => "The indexes on " + Count(withheld) + " tables that no readable store declares are withheld " +
                     "until then: any of them could be that store's - a table of a document type it hides from the " +
                     "studio, whose indexes are named after it.",
            },
            "Every index on a table a readable store declares is listed, with what an apply would do to it.");

    /// <summary>The Functions tab's notice, or <see langword="null" /> when nothing is degraded.</summary>
    /// <param name="withheld">How many routines were withheld.</param>
    public string? FunctionsNotice(int withheld) =>
        Notice(
            withheld switch
            {
                0 => "Every routine here is Marten's or declared by a store that could be read, so none is withheld.",
                1 => "1 routine here that no readable store declares is withheld until then: it could be one of " +
                     "that store's projection functions, which would read as \"not Marten\".",
                _ => Count(withheld) + " routines here that no readable store declares are withheld until then: " +
                     "any of them could be one of that store's projection functions, which would read as " +
                     "\"not Marten\".",
            },
            "Marten's own routines are listed with their bodies, read by the Schema screen itself: the database " +
            "browser, which reads the rest, reads nothing until every registered store can be read.");

    /// <summary>What the stores that could not be read are called, in one clause.</summary>
    internal string Who()
    {
        if (Unreadable.Count == 1)
        {
            return One(Unreadable[0]);
        }

        return Unreadable.Count.ToString(CultureInfo.InvariantCulture) + " registered Marten stores could not be read ("
            + string.Join("; ", Unreadable.Select(static x => x.Name is { } name
                ? "'" + name + "' " + x.Problem + (x.Detail is { } detail ? ": " + detail : string.Empty)
                : "one you may not see " + x.Problem))
            + ")";

        static string One(UnreadableStore store) => store.Name is { } name
            ? "Marten store '" + name + "' " + store.Problem + (store.Detail is { } detail ? " (" + detail + ")" : string.Empty)
            : "A registered Marten store you may not see " + store.Problem;
    }

    private string? Notice(string withheld, string kept) =>
        IsDegraded
            ? Who() + ", so the studio cannot tell its objects from anybody else's until it can be read. "
              + withheld + " " + kept
            : null;

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
