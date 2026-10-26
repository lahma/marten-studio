using System.Reflection;

using MartenStudio.Services.Configuration;

namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// Which lock monitor the HotCold leader runs: DistributedLock's keepalive, which leaves the leader idle between statements,
/// or the older <c>pg_sleep</c> monitor, which keeps it active - read off the <c>DistributedLock.Postgres</c> this process
/// runs, never assumed from a Marten or Weasel version.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the boundary is.</b> Weasel took Patched.DistributedLock.Postgres 1.3.3 - its <c>UseTransaction()</c> and
/// <c>UseMultiplexing()</c>, and the keepalive in place of the transaction-scoped <c>pg_sleep</c> - in weasel#614
/// (<c>d74de32</c>), first in Weasel 9.34.0, which Marten 9.39.1 already depends on. Marten 9.39.0 runs Weasel 9.33.0 and the
/// upstream DistributedLock.Postgres 1.3.0. So the monitor changes between two patch releases of the studio's floor, and a
/// host can resolve either.
/// </para>
/// <para>
/// <b>How it is read, best first.</b> The <c>DistributedLock.Postgres</c> assembly loaded in this process, by its
/// informational version (upstream 1.3.0 is <c>1.3.0+306be8b</c>, Patched 1.3.3 <c>1.3.3+425b94a</c>); else the version
/// <c>Weasel.Postgresql</c> was compiled against, by assembly version (upstream 1.3.0 is <c>1.0.0.0</c>, Patched 1.3.3
/// <c>1.3.3.0</c>), which is what loads unless a host redirects it; else Weasel's own version, from 9.34. A monitor that
/// cannot be told at all is taken for the keepalive: the leader then sits idle longer, and a check that judges a timeout
/// against it errs towards saying so rather than towards silence.
/// </para>
/// </remarks>
/// <param name="Keepalive">Whether the monitor is the keepalive, the leader idle between its statements.</param>
/// <param name="Basis">What said so, for the evidence: "DistributedLock.Postgres 1.3.3 (loaded)".</param>
internal sealed record LeaderLockMonitor(bool Keepalive, string Basis)
{
    /// <summary>The assembly both the upstream and the Patched package ship.</summary>
    public const string AssemblyName = "DistributedLock.Postgres";

    /// <summary>The first DistributedLock.Postgres with the keepalive: Patched 1.3.3, informational and assembly version alike.</summary>
    public static readonly Version KeepaliveSince = new(1, 3, 3);

    /// <summary>The first Weasel that depends on it (weasel#614): 9.34.0, Marten 9.39.1.</summary>
    public static readonly Version WeaselSince = new(9, 34);

    /// <summary>The monitor this process runs.</summary>
    public static LeaderLockMonitor Current => Of(LoadedVersion(), ReferencedVersion(), MartenRuntime.Current.VersionOf(RuntimePackage.WeaselPostgresql));

    /// <summary>The monitor, from what could be read. Pure: the unit tests hold each source to it.</summary>
    /// <param name="loadedInformational">The loaded assembly's informational version, or <see langword="null" />.</param>
    /// <param name="referenced">The assembly version <c>Weasel.Postgresql</c> references, or <see langword="null" />.</param>
    /// <param name="weasel">Weasel's own version, or <see langword="null" />.</param>
    internal static LeaderLockMonitor Of(string? loadedInformational, Version? referenced, Version? weasel)
    {
        if (MartenRuntime.ParseVersion(loadedInformational) is { } loaded)
        {
            return new LeaderLockMonitor(loaded >= KeepaliveSince, AssemblyName + " " + MartenRuntime.Format(loaded) + " (loaded)");
        }

        if (referenced is { } compiled)
        {
            return new LeaderLockMonitor(
                compiled >= KeepaliveSince,
                AssemblyName + " assembly version " + compiled + " (the one Weasel.Postgresql references)");
        }

        if (weasel is { } version)
        {
            return new LeaderLockMonitor(version >= WeaselSince, "Weasel.Postgresql " + MartenRuntime.Format(version) + " (DistributedLock.Postgres not read)");
        }

        return new LeaderLockMonitor(true, "not known; taken for the keepalive");
    }

    /// <summary>What the monitor does, for a sentence: "DistributedLock's keepalive" or "the pg_sleep monitor".</summary>
    public string Name => Keepalive ? "DistributedLock's keepalive" : "the pg_sleep monitor";

    private static string? LoadedVersion()
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(static x => string.Equals(x.GetName().Name, AssemblyName, StringComparison.Ordinal))?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static Version? ReferencedVersion()
    {
        try
        {
            return MartenRuntime.AnchorType(RuntimePackage.WeaselPostgresql).Assembly.GetReferencedAssemblies()
                .FirstOrDefault(static x => string.Equals(x.Name, AssemblyName, StringComparison.Ordinal))?.Version;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }
}
