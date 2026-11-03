using System.Globalization;

using Marten;

namespace MartenStudio.Services.Connection;

/// <summary>Which of the two configured values decides Marten's command timeout, as far as public members can prove it.</summary>
internal enum MartenCommandTimeoutBasis
{
    /// <summary>
    /// <c>StoreOptions.CommandTimeout</c> differs from Marten's default and from the connection string's, so it was set - by the
    /// application, or by <c>Connection(string)</c> / <c>UseNpgsqlDataSource()</c>, which copy the connection string's timeout into
    /// it - and it wins.
    /// </summary>
    StoreOption,

    /// <summary>The connection string names none and the store was left at Marten's default.</summary>
    Default,

    /// <summary>Both say the same number (whatever it is, Marten's default included), so which one wins does not matter.</summary>
    Agreed,

    /// <summary>
    /// <c>StoreOptions.CommandTimeout</c> is Marten's default and the database's connection string names another value:
    /// the connection string wins only if the option was left alone, and no public member says whether it was.
    /// </summary>
    Undetermined,
}

/// <summary>
/// The command timeout Marten gives its commands for one database, reproduced from public members only
/// (<c>SessionOptions.ResolveCommandTimeout</c>, Marten 9.57, is internal).
/// </summary>
/// <remarks>
/// <para>
/// <b>What Marten does</b> (<c>SessionOptions.ResolveCommandTimeout</c>, #5269): a session's own <c>Timeout</c>; else
/// <c>StoreOptions.CommandTimeout</c> when it was set on purpose; else the <c>Command Timeout</c> the database's own connection string
/// names; else <c>StoreOptions.CommandTimeout</c>, whose default is <see cref="StoreOptions.DefaultTimeout" />. "Set on purpose" is
/// <c>StoreOptions.CommandTimeoutIsExplicit</c>, which is internal, and which <c>StoreOptions.Connection(string)</c> also sets, to the
/// string's timeout or Npgsql's 30.
/// </para>
/// <para>
/// <b>What public members can prove.</b> Two equal numbers need no winner (<see cref="MartenCommandTimeoutBasis.Agreed" />). A value
/// that is not the default was necessarily set, so it wins (<see cref="MartenCommandTimeoutBasis.StoreOption" />) - most often by
/// <c>Connection(string)</c> or <c>UseNpgsqlDataSource()</c>, which copy the string's timeout into the option. A default value with no
/// timeout in the connection string is the default either way. The one case they cannot settle is <c>CommandTimeout</c> equal to the
/// default beside a connection string that names another number: if the option was set to the default on purpose it wins, and if it
/// was never touched the string does. That case is reported as <see cref="MartenCommandTimeoutBasis.Undetermined" /> with both
/// numbers, and no winner is claimed. A single database can reach it too: <c>Connection(string)</c> stores the string's timeout, so
/// an application that then sets <c>CommandTimeout</c> back to 5 leaves the option at the default beside a string naming another
/// number, as does a tenancy whose per-database strings never pass through <c>Connection(string)</c>. No reflection on Marten's
/// internals is used.
/// </para>
/// </remarks>
/// <param name="StoreSeconds"><c>StoreOptions.CommandTimeout</c>.</param>
/// <param name="ConnectionStringSeconds">
/// The <c>Command Timeout</c> the database's connection string names (<c>IMartenDatabase.ConfiguredCommandTimeout</c>), or
/// <see langword="null" /> when it names none - which is not Npgsql's default of 30.
/// </param>
internal sealed record MartenCommandTimeout(int StoreSeconds, int? ConnectionStringSeconds)
{
    /// <summary>Which value decides.</summary>
    public MartenCommandTimeoutBasis Basis => ConnectionStringSeconds == StoreSeconds
        ? MartenCommandTimeoutBasis.Agreed
        : StoreSeconds != StoreOptions.DefaultTimeout
            ? MartenCommandTimeoutBasis.StoreOption
            : ConnectionStringSeconds is null
                ? MartenCommandTimeoutBasis.Default
                : MartenCommandTimeoutBasis.Undetermined;

    /// <summary>The timeout in seconds, or <see langword="null" /> when the two configured values cannot be told apart.</summary>
    public int? EffectiveSeconds => Basis == MartenCommandTimeoutBasis.Undetermined ? null : StoreSeconds;

    /// <summary>The value for a grid: "30 s", or "5 s or 120 s" where the two cannot be told apart.</summary>
    public string Value => EffectiveSeconds is { } seconds
        ? Seconds(seconds)
        : Seconds(StoreSeconds) + " or " + Seconds(ConnectionStringSeconds!.Value);

    /// <summary>The connection string's timeout for a grid: "120 s", or "not named".</summary>
    public string ConnectionStringValue => ConnectionStringSeconds is { } seconds ? Seconds(seconds) : "not named";

    /// <summary>
    /// The sentence that says where the number comes from and which value wins - the same words on the Connection page and the
    /// Configuration page.
    /// </summary>
    public string Rule => Basis switch
    {
        MartenCommandTimeoutBasis.StoreOption =>
            "It is not Marten's default of " + Seconds(StoreOptions.DefaultTimeout) + ", so it was set, by the application or by Connection(string) / UseNpgsqlDataSource(), "
            + "which copy the connection string's Command Timeout into it (Npgsql's 30 s when the string names none), and it wins.",
        MartenCommandTimeoutBasis.Default =>
            Seconds(StoreOptions.DefaultTimeout) + ": Marten's default, or the same number set on purpose. The connection string names none, so either way "
            + Seconds(StoreOptions.DefaultTimeout) + ", not Npgsql's 30 s.",
        MartenCommandTimeoutBasis.Agreed =>
            "Both say the same, so it does not matter which one Marten reads.",
        _ =>
            "Marten uses the store option when it was set on purpose and the connection string's value when it was not. Marten does not publish which, "
            + "so this studio does not claim either: " + Seconds(ConnectionStringSeconds!.Value) + " if StoreOptions.CommandTimeout was left alone, "
            + Seconds(StoreSeconds) + " if it was set to " + Seconds(StoreSeconds) + " on purpose. Setting it explicitly to the number you want removes the doubt.",
    };

    /// <summary>
    /// The sentences that say where the number comes from and what overrides it - the same words on the Connection page and the
    /// Configuration page.
    /// </summary>
    public string Explanation
    {
        get
        {
            string options = "StoreOptions.CommandTimeout is " + Seconds(StoreSeconds) + ", and the database's connection string "
                + (ConnectionStringSeconds is { } named ? "names a Command Timeout of " + Seconds(named) : "names no Command Timeout") + ".";

            return options + " " + Rule + " A session that sets SessionOptions.Timeout overrides both. Marten sets the result on the commands its sessions run, "
                + "so Npgsql's own Command Timeout does not apply to them.";
        }
    }

    /// <summary>
    /// Reads the figures for a database.
    /// </summary>
    /// <param name="options">The store's options.</param>
    /// <param name="database">The database; its connection string is parsed inside Marten and only the number comes back.</param>
    public static MartenCommandTimeout Read(StoreOptions options, Marten.Storage.IMartenDatabase? database)
    {
        ArgumentNullException.ThrowIfNull(options);

        int? named;

        try
        {
            named = database?.ConfiguredCommandTimeout;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A database whose data source cannot be read has named nothing the studio can report.
            named = null;
        }

        return new MartenCommandTimeout(options.CommandTimeout, named);
    }

    private static string Seconds(int seconds) =>
        seconds == 0 ? "0 s (no limit)" : seconds.ToString(CultureInfo.InvariantCulture) + " s";
}
