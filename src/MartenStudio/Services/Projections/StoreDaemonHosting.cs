using JasperFx.Events.Daemon;

namespace MartenStudio.Services.Projections;

/// <summary>Where a store's async projections run, as configuration says (1.2: the Stores page).</summary>
internal enum StoreDaemonHostingKind
{
    /// <summary>An external system runs them (<c>DaemonMode.ExternallyManaged</c>).</summary>
    ExternallyManaged,

    /// <summary>The store has no async projection and no subscription, so there is nothing to run.</summary>
    NoAsyncWork,

    /// <summary>A projection coordinator is registered in this process's container.</summary>
    RegisteredHere,

    /// <summary>No coordinator is registered here: the daemon runs in another process, or not at all.</summary>
    NotHostedHere,
}

/// <summary>
/// What the container and the store's options say about hosting the async daemon, without constructing a coordinator or
/// reading a database.
/// </summary>
/// <remarks>
/// Its words name configuration, never running state: a registered coordinator may be paused, stopped, waiting for a
/// leadership lock or failing, and only Projections - which asks the coordinator - can say.
/// </remarks>
/// <param name="Kind">Which answer.</param>
/// <param name="Mode">The configured <c>AsyncMode</c>, for <see cref="StoreDaemonHostingKind.RegisteredHere" />.</param>
internal sealed record StoreDaemonHosting(StoreDaemonHostingKind Kind, DaemonMode? Mode = null)
{
    /// <summary>The sentence the Stores page prints.</summary>
    public string Words => Kind switch
    {
        StoreDaemonHostingKind.ExternallyManaged => "Run by an external system",
        StoreDaemonHostingKind.NoAsyncWork => "No async projections",
        StoreDaemonHostingKind.RegisteredHere =>
            "A projection coordinator is registered in this process" + (Mode is { } mode ? $" ({mode})" : string.Empty)
            + "; Projections says whether it runs",
        _ => "Not hosted in this process",
    };
}
