using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Statements;

/// <summary>
/// Postgres Studio's statement groups (W5-Q1): the statement's verb, and the first table of the data source's judged set it
/// names - <c>select · public.orders</c>, <c>insert · public.audit_log</c>, <c>select · other</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The verb is read on <see cref="SqlLexer" />'s tokens</b>, so a comment or a literal is never a verb: the first word, or for
/// a <c>with</c> the word that starts the statement its common table expressions are for - the first <c>select</c>,
/// <c>insert</c>, <c>update</c>, <c>delete</c> or <c>merge</c> outside every parenthesis that follows a closing one.
/// </para>
/// <para>
/// <b>The table is matched by name, against the set alone</b> (<see cref="StatementReads.Named" />, T02's own rule): the
/// first table of the set the text names, in the order it names them - for a write, its target. A statement that names none
/// of them - a catalog read, <c>select 1</c>, a transaction's control - is grouped by its verb with <see cref="Other" />.
/// MartenStudio's classifier falls back to this one for every statement that is not one of Marten's own shapes.
/// </para>
/// </remarks>
internal sealed class PostgresStatementClassifier : IStatementClassifier
{
    /// <summary>What a group whose statements name no table of the set is called after its verb.</summary>
    public const string Other = "other";

    /// <summary>The one classifier: it holds nothing.</summary>
    public static PostgresStatementClassifier Instance { get; } = new();

    /// <summary>The statements that may follow a <c>with</c>'s common table expressions.</summary>
    private static readonly HashSet<string> MainVerbs = new(StringComparer.OrdinalIgnoreCase) { "select", "insert", "update", "delete", "merge" };

    /// <inheritdoc />
    public StatementGroup? Classify(string text, IStudioTableSet tables)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tables);

        if (Verb(text) is not { } verb)
        {
            return null;
        }

        // A comment is free text, never a table the statement names.
        IReadOnlyList<StudioTable> named = StatementReads.Named(StatementReads.MaskComments(text), tables);

        return named.Count == 0
            ? new StatementGroup(verb + "|", verb + " · " + Other) { Verb = verb }
            : Of(verb, named[0]);
    }

    /// <summary>The group of a statement that does <paramref name="verb" /> to <paramref name="table" />.</summary>
    public static StatementGroup Of(string verb, StudioTable table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(table);

        return new StatementGroup(verb + "|" + table.Key, verb + " · " + DatabaseLinks.QualifiedName(table.Schema, table.Table))
        {
            Table = table,
            Verb = verb,
        };
    }

    /// <summary>
    /// What <paramref name="text" /> does, in lower case: its first word, or for a <c>with</c> the statement its common table
    /// expressions are for - <see langword="null" /> for a text that does not start with a word or cannot be read.
    /// </summary>
    public static string? Verb(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        SqlLexer lexer = new(text);

        if (!lexer.TryRead(out SqlToken first) || first.Kind != SqlTokenKind.Word)
        {
            return null;
        }

        if (!first.IsWord("with"))
        {
            return first.Text.ToLowerInvariant();
        }

        SqlToken previous = first;

        while (lexer.TryRead(out SqlToken token))
        {
            if (token.Kind == SqlTokenKind.Unreadable)
            {
                return null;
            }

            // A ')' carries the depth before it closed, so the one ending a top-level common table expression is at one.
            if (token.Kind == SqlTokenKind.Word && token.Depth == 0 && MainVerbs.Contains(token.Text) && previous.Is(')') && previous.Depth == 1)
            {
                return token.Text.ToLowerInvariant();
            }

            previous = token;
        }

        return "with";
    }
}
