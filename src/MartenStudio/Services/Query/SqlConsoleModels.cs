using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Query;

/// <summary>Which of the Query page's modes a request, a saved query or an example belongs to.</summary>
/// <remarks>
/// Shared because the saved-query drawer keys its storage by mode and the SQL console is one of them. A studio
/// with no document types offers <see cref="Sql" /> alone; MartenStudio's Query page offers all three.
/// </remarks>
internal enum QueryMode
{
    /// <summary>
    /// A Marten <c>where</c> clause against one registered document type. A read, needing no capability.
    /// </summary>
    Marten,

    /// <summary>
    /// The read-only SQL console. Gated on <see cref="StudioCapability.RunSql" />.
    /// </summary>
    Sql,

    /// <summary>
    /// A typed LINQ predicate over one document type, parsed by JasperFx's Dynamic LINQ under the studio's
    /// allow-list and translated by Marten's own <c>Query&lt;T&gt;()</c>. Gated on
    /// <see cref="StudioCapability.RunLinqQueries" />, and present only on Marten 9.48+ (F7).
    /// </summary>
    Linq,
}

/// <summary>What the SQL console was asked to run.</summary>
/// <param name="Statement">The statement as typed. Never rewritten before it is sent.</param>
internal sealed record SqlConsoleRequest(string Statement);

/// <summary>
/// What the SQL console came back with, including the two ways it can refuse before Postgres sees
/// anything.
/// </summary>
/// <param name="Columns">The columns, with the Postgres type of each for the grid header.</param>
/// <param name="Rows">The rows, already formatted and capped by <see cref="SqlValueFormatter" />.</param>
/// <param name="Truncated">Whether the reader was stopped at the row cap.</param>
/// <param name="RowLimit">The row cap that was in force.</param>
/// <param name="Duration">How long the statement took.</param>
/// <param name="Notices">Anything Postgres raised as a notice, for the Messages tab.</param>
/// <param name="Error">The Postgres error, when there was one.</param>
/// <param name="Rejection">The guard's refusal, when the statement was never sent.</param>
internal sealed record SqlConsoleResult(
    IReadOnlyList<SqlResultColumn> Columns,
    IReadOnlyList<IReadOnlyList<SqlCell>> Rows,
    bool Truncated,
    int RowLimit,
    TimeSpan Duration,
    IReadOnlyList<string> Notices,
    SqlError? Error,
    SqlRejection? Rejection)
{
    /// <summary>Whether the statement ran and Postgres was happy with it.</summary>
    public bool Succeeded => Error is null && Rejection is null;

    /// <summary>The result of a statement the guard refused.</summary>
    public static SqlConsoleResult Refused(SqlRejection rejection, int rowLimit) =>
        new([], [], false, rowLimit, TimeSpan.Zero, [], null, rejection);
}

/// <summary>
/// What one SQL console shows - the statement in its editor, the last result, the error under it and the status
/// line - kept by whoever hosts the console, so that leaving its tab and coming back finds them where they were.
/// </summary>
/// <remarks>
/// Plain state, one per page and never shared: it holds nothing a visitor may not see, because everything in it is
/// what the console already showed this circuit. A scope change forgets the result (<see cref="ForgetResult" />),
/// since a grid read from one database must not sit under another's name.
/// </remarks>
internal sealed class SqlConsoleSession
{
    /// <summary>The statement in the editor.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The last result, or <see langword="null" /> before anything ran in this scope.</summary>
    public SqlConsoleResult? Result { get; set; }

    /// <summary>The error the editor shows, or <see langword="null" />.</summary>
    public string? Error { get; set; }

    /// <summary>Where in <see cref="Text" /> the error is, zero-based, or <c>-1</c> for no caret.</summary>
    public int ErrorPosition { get; set; } = -1;

    /// <summary>The status line under the editor: the row count and time, or "Cancelled.".</summary>
    public string? Status { get; set; }

    /// <summary>Forgets the last result, as a scope change does.</summary>
    public void ForgetResult() => Result = null;
}

/// <summary>
/// The statement guard's refusal, as a value the editor can point at.
/// </summary>
/// <remarks>
/// <see cref="Position" /> is a zero-based character offset into the statement - the same convention
/// <see cref="SqlGuardResult" /> uses - so the editor can show the offending character with a caret under
/// it rather than only saying that something was wrong.
/// </remarks>
/// <param name="Reason">Which rule refused it, for the tests and the log.</param>
/// <param name="Message">What to tell the person who typed it.</param>
/// <param name="Token">The offending token, when there is one.</param>
/// <param name="Position">Where in the statement the problem is, zero-based.</param>
internal sealed record SqlRejection(string Reason, string Message, string? Token, int Position)
{
    /// <summary>Adapts the guard's own answer. Unknown reasons keep working: the name is carried as text.</summary>
    public static SqlRejection FromGuard(SqlGuardResult result) =>
        new(result.Reason.ToString(), result.Message, result.Token, result.Position);
}
