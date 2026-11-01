using System.Globalization;

namespace PostgresStudio.Services.Statements;

/// <summary>The Statements page's addresses: the list, and one statement's drill-in by its <c>queryid</c> (D9).</summary>
internal static class StatementLinks
{
    /// <summary>The page's route under the studio's path.</summary>
    public const string Route = "statements";

    /// <summary>The query-string name of the drill-in's statement.</summary>
    public const string QueryIdParameter = "queryid";

    /// <summary>A <c>queryid</c> from the query string, or <see langword="null" /> for anything that is not one.</summary>
    public static long? ParseQueryId(string? value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long queryId) ? queryId : null;
}
