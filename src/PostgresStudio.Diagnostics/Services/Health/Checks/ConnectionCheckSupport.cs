using System.Globalization;

using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// What the connection-settings checks (C16, C17, C18, S11, S12, S13; 1.1) share: the sweep's one read of the data source's
/// settings, and how a remedy shows a setting in every form a host can change it in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Redaction binds every sentence here</b> (NP-DESIGN §2, D33). A check judges <see cref="ConnectionFacts" />, which hold
/// the redacted settings and counts, never the connection string; so no finding names a pool, a connection string, a password,
/// an environment value of a secret keyword or an <c>Options</c> value. The evidence says whether a path or a secret is
/// <i>set</i>, never what it is, and names a host only as a kind of endpoint ("an Amazon RDS Proxy"), never the host.
/// </para>
/// </remarks>
internal static class ConnectionCheckSupport
{
    private const string StatementsPlaceholder = "{statements}";

    /// <summary>
    /// The sweep's memoized read of the data source's settings and what the server saw
    /// (<see cref="ConnectionFacts.SharedAsync" />), refused when the connection string cannot be read.
    /// </summary>
    /// <param name="context">The sweep's context.</param>
    /// <param name="standDown">
    /// Why the check does not apply, judged from the data source's connection string alone (a pooler sign, pooling off), or
    /// <see langword="null" /> when it does. It is asked before the connection read's failure is thrown again: a check that does
    /// not apply to this data source does not apply to it whether or not the server answers.
    /// </param>
    /// <exception cref="HealthCheckUnavailableException">The data source's connection string could not be parsed, or there is none.</exception>
    /// <exception cref="HealthCheckNotApplicableException"><paramref name="standDown" /> answered.</exception>
    /// <remarks>
    /// <b>A database the server did not answer is not read as in good order.</b> The facts keep the settings of a database that
    /// could not be reached (the Connection page shows them), but a check judging them would report on a database the sweep never
    /// reached, and <c>PostgresDiagnosticsService.Reached</c> counts a catalog check that did not fail as proof that it was. So the
    /// failure the connection read met is thrown again here (captured once, so its trace does not grow check after check), and the
    /// sweep classifies it as it does every other check's.
    /// </remarks>
    public static async Task<ConnectionFacts> ReadAsync(HealthCheckContext context, Func<ConnectionFacts, string?>? standDown = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        ConnectionFacts facts = await ConnectionFacts.SharedAsync(context).ConfigureAwait(false);

        if (facts.Settings.Readable && standDown?.Invoke(facts) is { } reason)
        {
            throw new HealthCheckNotApplicableException(reason);
        }

        facts.ObservedFailure?.Throw();

        if (!facts.Settings.Readable)
        {
            // The exception's type only, as the reader keeps it: Npgsql's message can quote a value.
            throw new HealthCheckUnavailableException(
                "The data source's connection string could not be read (" + (facts.Settings.UnreadableReason ?? "unknown") + "), so its settings are not judged.");
        }

        return facts;
    }

    /// <summary>A number of seconds as evidence: <c>300 s</c>, or <c>off</c> for zero where zero means off.</summary>
    /// <param name="seconds">The value.</param>
    /// <param name="zeroMeans">What zero is called; <see langword="null" /> for a number.</param>
    public static string Seconds(int seconds, string? zeroMeans = "off") =>
        seconds == 0 && zeroMeans is not null ? zeroMeans : seconds.ToString("N0", CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// A remedy's code for one change, in the forms a host can make it: the connection-string fragment, and the same change on
    /// the data source builder worded for the studio (<see cref="StudioText.DataSourceBuilderRemedy" />).
    /// </summary>
    /// <param name="text">The studio's words.</param>
    /// <param name="heading">A comment line saying what this change does (without the leading slashes).</param>
    /// <param name="connectionString">The connection-string fragment: <c>Keepalive=30</c>.</param>
    /// <param name="statements">The builder statements, one per entry: <c>b.ConnectionStringBuilder.KeepAlive = 30;</c>.</param>
    public static string Block(StudioText text, string heading, string connectionString, params string[] statements)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return "// " + heading + "\n" + connectionString + "\n\n" + Builder(text, statements);
    }

    /// <summary>A change that has no connection-string form (a callback), as the studio words the builder it goes on.</summary>
    /// <param name="text">The studio's words.</param>
    /// <param name="heading">A comment line saying what this change does (without the leading slashes).</param>
    /// <param name="lines">The lines of code.</param>
    public static string CodeBlock(StudioText text, string heading, params string[] lines)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);

        return "// " + heading + "\n" + Builder(text, lines);
    }

    /// <summary>
    /// <paramref name="lines" /> inside the studio's builder template: each line on the placeholder's own indentation, so a
    /// template that wraps them in a lambda keeps them aligned.
    /// </summary>
    /// <param name="text">The studio's words.</param>
    /// <param name="lines">The lines.</param>
    public static string Builder(StudioText text, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lines);

        string template = text.DataSourceBuilderRemedy;
        int at = template.IndexOf(StatementsPlaceholder, StringComparison.Ordinal);

        if (at < 0)
        {
            throw new InvalidOperationException("The studio's DataSourceBuilderRemedy has no " + StatementsPlaceholder + " line.");
        }

        int lineStart = template.LastIndexOf('\n', at) + 1;
        string indent = template[lineStart..at];

        if (!string.IsNullOrWhiteSpace(indent))
        {
            indent = string.Empty;
        }

        return template.Remove(at, StatementsPlaceholder.Length).Insert(at, string.Join("\n" + indent, lines));
    }
}
