using System.Globalization;

using PostgresStudio.Services.Connection;

namespace MartenStudio.Services.Connection;

/// <summary>What the async daemon hosted in this process needs from the database's pool.</summary>
/// <param name="Hosted">Whether a daemon is hosted here for this database.</param>
/// <param name="Explanation">Why not, when it is not.</param>
/// <param name="RunningShards">How many of its shards are running now.</param>
/// <param name="AsyncShards">How many async shards the store registers, or <see langword="null" /> when they cannot be listed.</param>
internal sealed record MartenDaemonNeeds(bool Hosted, string? Explanation, int RunningShards, int? AsyncShards)
{
    /// <summary>One for the daemon itself, then one per running shard (the same arithmetic as check C09).</summary>
    public int Connections => RunningShards + PostgresStudio.Services.Health.Checks.ConnectionThresholds.DaemonOwnConnections;
}

/// <summary>
/// "How Marten uses this connection": the rows, the card and the sentences Marten Studio adds to the Connection page - pure over
/// what <see cref="MartenConnectionInsightContributor" /> gathered, so every sentence is testable without a store.
/// </summary>
/// <remarks>
/// A row has its <c>StoreOptions</c> line and its default in the note, so an administrator can act on it without looking either up.
/// Nothing here is, or is made from, a connection string (NP-DESIGN §2): the origin is a reference comparison, the timeout is a
/// number, and the row-level facts are options.
/// </remarks>
internal static class MartenConnectionSection
{
    /// <summary>The section's element id.</summary>
    public const string SectionId = "conn-marten";

    /// <summary>The section's heading.</summary>
    public const string Title = "How Marten uses this connection";

    /// <summary>Builds the section.</summary>
    /// <param name="options">The store options that bear on connections.</param>
    /// <param name="origin">Where Marten's data source came from.</param>
    /// <param name="daemon">What the daemon needs, or <see langword="null" /> when the health sweep has not looked.</param>
    /// <param name="poolMax">The pool's <c>Maximum Pool Size</c>, or <see langword="null" /> when the data source does not pool or is unreadable.</param>
    public static ConnectionContributedSection Build(MartenConnectionOptions options, MartenDataSourceOrigin origin, MartenDaemonNeeds? daemon, int? poolMax)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(origin);

        List<ConnectionObservedLine> rows =
        [
            DataSource(origin),
            new("Command timeout", options.CommandTimeout.Value, options.CommandTimeout.Explanation),
            Sticky(options, poolMax),
            MultiHost(options, origin),
            Daemon(daemon, poolMax),
            new(
                "Hi-lo block size",
                options.HiloMaxLo.ToString("N0", CultureInfo.InvariantCulture),
                "StoreOptions.Advanced.HiloSequenceDefaults.MaxLo, default " + MartenConnectionOptions.Defaults.HiloMaxLo.ToString("N0", CultureInfo.InvariantCulture)
                + ". A document type with a numeric id reserves this many ids at a time with one round trip to the database, so a larger block means fewer round trips; "
                + "the ids a process has not used when it stops are skipped. A type can override it."),
            new(
                "Update batch size",
                options.UpdateBatchSize.ToString("N0", CultureInfo.InvariantCulture),
                "StoreOptions.UpdateBatchSize, default " + MartenConnectionOptions.Defaults.UpdateBatchSize.ToString("N0", CultureInfo.InvariantCulture)
                + ". SaveChanges sends at most this many document changes to the database in one batch, so a unit of work larger than this takes more than one round trip."),
            NpgsqlLogging(options, origin),
            new(
                "Connection tracing",
                options.TrackConnections,
                "StoreOptions.OpenTelemetry.TrackConnections, default None. With Normal or Verbose, and a listener on Marten's ActivitySource, Marten records an activity each time a session "
                + "opens a connection; Verbose also traces each event append. Leave it at None unless you are tracing connections."),
        ];

        List<ConnectionCard> cards = [];

        if (OwnPoolCard(origin) is { } own)
        {
            cards.Add(own);
        }

        return new ConnectionContributedSection(
            SectionId,
            Title,
            "What the application told Marten about connecting, and what it means for the pool above. Each row names the StoreOptions line that sets it and its default.",
            rows,
            cards);
    }

    /// <summary>
    /// The sentences that go to the end of the page's own hints, by hint id: what is different for a Marten store.
    /// </summary>
    /// <param name="options">The store options.</param>
    /// <param name="origin">Where Marten's data source came from.</param>
    /// <param name="observed">What the server saw of the studio's connection, or <see langword="null" />.</param>
    public static IReadOnlyDictionary<string, string> HintNotes(MartenConnectionOptions options, MartenDataSourceOrigin origin, ConnectionObserved? observed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(origin);

        Dictionary<string, string> notes = new(StringComparer.Ordinal);

        if (origin.MultiHost)
        {
            notes[ConnectionHintIds.MultiHost] = "For Marten's own sessions this does not decide: Marten asks the data source for a host by StoreOptions.Advanced.MultiHostSettings, "
                + "reads " + options.ReadPreference + " and writes " + options.WritePreference + " (default " + MartenConnectionOptions.Defaults.SessionPreference + " for both), "
                + (options.WritesCanLandOnAStandby
                    ? "and " + options.WritePreference + " " + options.WriteStandbyClause + ": set WriteSessionPreference to ReadWrite or Primary."
                    : "so Marten's writes go to a primary even though this connection string would accept any host.");
        }
        else
        {
            notes[ConnectionHintIds.MultiHost] = "StoreOptions.Advanced.MultiHostSettings, which picks the host of Marten's sessions, applies only to a multi-host data source, and Marten's data source for this database is not one.";
        }

        notes[ConnectionHintIds.CommandTimeout] = CommandTimeoutNote(options.CommandTimeout, observed);

        return notes;
    }

    private static string CommandTimeoutNote(MartenCommandTimeout timeout, ConnectionObserved? observed)
    {
        string note = "Marten sets the timeout of its own commands itself, so for the application's Marten sessions the figure that counts is Marten's, " + timeout.Value
            + " for this store (see \"" + Title + "\"), not Npgsql's Command Timeout above.";

        if (timeout.EffectiveSeconds is not { } seconds || observed is null)
        {
            return note;
        }

        long? cut = (observed.StatementTimeoutMs, observed.TransactionTimeoutMs) switch
        {
            (> 0 and var statement, > 0 and var transaction) => Math.Min(statement, transaction),
            (> 0 and var statement, _) => statement,
            (_, > 0 and var transaction) => transaction,
            _ => null,
        };

        if (cut is not { } serverMs)
        {
            return note;
        }

        return note + (seconds == 0 || serverMs < seconds * 1000L
            ? " Against the server's cut, the server cancels a long Marten command first."
            : " Against the server's cut, Marten's timeout cancels a long command first.");
    }

    private static ConnectionObservedLine DataSource(MartenDataSourceOrigin origin) => origin.Kind switch
    {
        MartenDataSourceOriginKind.Application => new(
            "Data source",
            "the application's NpgsqlDataSource" + (origin.ServiceKey is { } key ? " (service key " + key + ")" : string.Empty),
            "Marten uses an NpgsqlDataSource this application registered in its container (UseNpgsqlDataSource()), so Marten and the application draw on one pool. "
            + "That is what check C18, Several pools to one database in this process, recommends when several pools reach one database."),
        MartenDataSourceOriginKind.HandedIn => new(
            "Data source",
            "the application's data source, handed to Marten by the application",
            "Marten did not build this NpgsqlDataSource: the application gave it to Marten (UseNpgsqlDataSource(), or StoreOptions.Connection(dataSource)), so Marten draws on the application's pool, "
            + "unless a connection string was also given (AddMarten(connectionString) or options.Connection(...)), from which Marten may still build one of its own; C18 counts the pools. "
            + "Handing Marten one shared data source is what check C18, Several pools to one database in this process, recommends when several pools reach one database."),
        MartenDataSourceOriginKind.Marten => new(
            "Data source",
            "Marten's own",
            "Marten built its own NpgsqlDataSource from the connection string it was given (StoreOptions.Connection(string), or AddMarten(connectionString)), so it has a pool of its own. "
            + (origin.Registered > 0
                ? "The application also registers " + origin.Registered.ToString(CultureInfo.InvariantCulture) + " NpgsqlDataSource" + (origin.Registered == 1 ? string.Empty : "s")
                  + " in its container: if one of them connects to this database as well, the process holds two pools to it, which check C18 counts. UseNpgsqlDataSource() makes Marten use the registered one."
                : "The application registers no NpgsqlDataSource of its own, so there is no pool to share.")),
        _ => new(
            "Data source",
            "not known",
            "Marten does not say whether this database's data source is one it built or one it was handed, so the studio cannot say where it came from."
            + (origin.Registered > 0
                ? " The application registers " + origin.Registered.ToString(CultureInfo.InvariantCulture) + " NpgsqlDataSource" + (origin.Registered == 1 ? string.Empty : "s")
                  + "; whether Marten uses one of them could not be told without building it, and the studio never builds one."
                : string.Empty)),
    };

    /// <summary>
    /// <c>DisableNpgsqlLogging</c> is applied only inside Marten's own data-source factory, so it says nothing about a data source
    /// the application built or handed over: that one logs as its own builder was told (<c>AddNpgsqlDataSource</c> forwards Npgsql's log
    /// lines to the application's logger factory).
    /// </summary>
    private static ConnectionObservedLine NpgsqlLogging(MartenConnectionOptions options, MartenDataSourceOrigin origin)
    {
        string state = options.NpgsqlLoggingDisabled ? "disabled" : "enabled";
        string line = "StoreOptions.DisableNpgsqlLogging, default true (Marten 9), applies only to data sources Marten builds from a connection string. ";

        return origin.Kind switch
        {
            MartenDataSourceOriginKind.Application or MartenDataSourceOriginKind.HandedIn => new(
                "Npgsql logging",
                "no effect here",
                line + "Marten did not build this data source, so its logging is what the application's builder set (AddNpgsqlDataSource forwards Npgsql's log lines to the application's loggers); "
                + "the option is " + state + " and changes nothing for it."),
            MartenDataSourceOriginKind.Marten => new(
                "Npgsql logging",
                state,
                line + (options.NpgsqlLoggingDisabled
                    ? "Npgsql's own log lines are not forwarded to the application's loggers."
                    : "Npgsql's own log lines are forwarded to the application's loggers, which is noisy on a busy application; set it to true for quieter logs.")),
            _ => new(
                "Npgsql logging",
                state,
                line + "The studio could not tell whether Marten built this database's data source, so it cannot say whether the option reaches it."),
        };
    }

    private static ConnectionObservedLine Sticky(MartenConnectionOptions options, int? poolMax)
    {
        string pool = poolMax is { } max ? "the pool of " + max.ToString(CultureInfo.InvariantCulture) : "the pool";

        return new(
            "Sticky connections",
            options.StickyConnections ? "on" : "off",
            "StoreOptions.UseStickyConnectionLifetimes, default off. "
            + (options.StickyConnections
                ? "On: a session keeps the connection from its first command until it is disposed, so every open session holds one connection of " + pool
                  + ". A long-lived session, or a request that opens several, can use the pool up: check C01 watches its saturation. Off is the default and lets a few connections serve many sessions."
                : "Off: a session borrows a connection for each command and gives it back at once, so a few connections of " + pool + " can serve many sessions. Turning it on holds one per open session."));
    }

    private static ConnectionObservedLine MultiHost(MartenConnectionOptions options, MartenDataSourceOrigin origin) => new(
        "Session preference on several hosts",
        "reads: " + options.ReadPreference + ", writes: " + options.WritePreference,
        "StoreOptions.Advanced.MultiHostSettings.ReadSessionPreference and WriteSessionPreference, both default " + MartenConnectionOptions.Defaults.SessionPreference + " (a primary). "
        + (origin.MultiHost
            ? "Marten's data source names several hosts, so these choose the host of each session."
            : "Marten's data source names one host, so they have no effect here.")
        + (origin.MultiHost && options.WritesCanLandOnAStandby ? " WriteSessionPreference " + options.WritePreference + " " + options.WriteStandbyClause + "." : string.Empty));

    private static ConnectionObservedLine Daemon(MartenDaemonNeeds? daemon, int? poolMax)
    {
        const string Label = "Async daemon";

        if (daemon is null)
        {
            return new(Label, "not known yet", "No health sweep has looked at the daemon for this database yet; this row is filled in as of the last health sweep once there is one. Check C09, Async shards against the pool, weighs it against the pool.");
        }

        if (!daemon.Hosted)
        {
            return new(Label, "not hosted in this process", daemon.Explanation ?? "No daemon runs here, so none of its shards draw on this process's pool.");
        }

        string shards = daemon.RunningShards.ToString(CultureInfo.InvariantCulture) + (daemon.RunningShards == 1 ? " shard" : " shards");
        string registered = daemon.AsyncShards is { } all ? " The store registers " + all.ToString(CultureInfo.InvariantCulture) + " async shards in all." : string.Empty;

        return new(
            Label,
            shards + " running: about " + daemon.Connections.ToString(CultureInfo.InvariantCulture) + (daemon.Connections == 1 ? " connection" : " connections"),
            "One connection for the daemon itself (its high-water detection and, with HotCold, its leadership lock) plus one per running shard, borrowed from the same pool as the application's requests"
            + (poolMax is { } max ? ": about " + daemon.Connections.ToString(CultureInfo.InvariantCulture) + " of " + max.ToString(CultureInfo.InvariantCulture) + "." : ".")
            + registered
            + " Check C09, Async shards against the pool, raises a finding when they leave the application no room.");
    }

    private static ConnectionCard? OwnPoolCard(MartenDataSourceOrigin origin)
    {
        if (origin.Kind != MartenDataSourceOriginKind.Marten || origin.Registered == 0)
        {
            return null;
        }

        return new ConnectionCard(
            "conn-marten-own-pool",
            ConnectionCardKind.Note,
            "Marten has a pool of its own beside the application's data source",
            "If the NpgsqlDataSource the application registers connects to this database too, the process holds two pools to it: each may open up to its own Maximum Pool Size, "
            + "and each retires idle connections separately. Pointing Marten at the registered data source gives both one pool. "
            + "If a connection string stays beside " + origin.UseCall + " (options.Connection(...) or AddMarten(connectionString)), Marten can still build a pool of its own from it "
            + "and both pools stay open: remove the connection string.",
            [],
            [],
            [new ConnectionFix(
                "Marten registration",
                "services.AddMarten(options =>\n    {\n        // Remove options.Connection(...) and the connection-string argument of AddMarten(...):\n"
                + "        // " + origin.UseCall + " supplies it.\n        // ...\n    })\n    ." + origin.UseCall + ";")])
        {
            Code = "C18",
        };
    }
}
