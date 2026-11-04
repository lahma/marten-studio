using System.Globalization;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Services.Find;

/// <summary>
/// Find's statements (1.2): a typed number shaped like a <c>queryid</c> is looked for among the statements the last statement sweep
/// read, through <see cref="IStatementsService.GetLatestAsync" /> - the Statements page's own gate and its own read, never a sweep.
/// </summary>
/// <remarks>
/// <para>
/// A refusal becomes a note that says what the page says (it names the option that would change it); it is logged by the service at
/// Debug and never audited here. The note appears only when the value had a <c>queryid</c>'s shape. A hit is a statement the
/// reading role's list holds; the text beside it is exactly as much of the text as the page lets this visitor read.
/// </para>
/// </remarks>
internal sealed class StatementFindContributor : IFindContributor
{
    /// <summary>How much of a statement's text a hit shows.</summary>
    internal const int TextLength = 120;

    private readonly IStatementsService statements;
    private readonly IStudioSettings settings;
    private readonly StudioDescriptor descriptor;

    public StatementFindContributor(IStatementsService statements, IStudioSettings settings, StudioDescriptor descriptor)
    {
        this.statements = statements;
        this.settings = settings;
        this.descriptor = descriptor;
    }

    /// <inheritdoc />
    public int Order => 60;

    /// <inheritdoc />
    public string Noun => "Statements";

    /// <summary>The Statements page does not apply its address's scope, so a statement of another store is not offered from here.</summary>
    public bool SearchesOtherScopes => false;

    /// <inheritdoc />
    public async Task<FindContribution> FindAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        if (!query.CouldBeAnId || query.QueryId is not { } queryId)
        {
            return FindContribution.NotApplicable;
        }

        StatementsView view = await statements.GetLatestAsync(scope, cancellationToken).ConfigureAwait(false);

        if (view.Refusal != DatabaseRefusal.None)
        {
            return new FindContribution([], null, [$"Statements: not searched. {view.Reason}"]);
        }

        if (view.State != StatementsState.Read)
        {
            return new FindContribution([], null, [$"Statements: not searched. {view.Status}"]);
        }

        List<FindHit> hits = [];
        foreach (StatementRowView row in view.Rows)
        {
            if (row.QueryId != queryId)
            {
                continue;
            }

            string key = queryId.ToString(CultureInfo.InvariantCulture);
            hits.Add(new FindHit(
                Kind: "Statement",
                Title: "Statement " + key,
                Url: StatementLinks.ToStatement(settings, descriptor, scope, queryId),
                Scope: scope,
                Id: key,
                Detail: Excerpt(row.Text)));
        }

        return new FindContribution(hits, "the statements the last check read", []);
    }

    private static string? Excerpt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string flat = string.Join(' ', text.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= TextLength ? flat : flat[..TextLength] + "…";
    }
}
