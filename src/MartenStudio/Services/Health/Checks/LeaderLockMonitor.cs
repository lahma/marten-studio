using System.Reflection;
using System.Runtime.Loader;

using MartenStudio.Services.Configuration;

namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// Which lock monitor the HotCold leader runs: DistributedLock's keepalive, which leaves the leader idle between statements,
/// or the older <c>pg_sleep</c> monitor, which keeps it active - read off the <c>DistributedLock.Postgres</c> that
/// <c>Weasel.Postgresql</c> runs in this process, never assumed from a Marten or Weasel version.
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
/// <b>How it is read, best first.</b> The <c>DistributedLock.Postgres</c> already loaded in <c>Weasel.Postgresql</c>'s own
/// load context, by its informational version (upstream 1.3.0 is <c>1.3.0+306be8b</c>, Patched 1.3.3 <c>1.3.3+425b94a</c>);
/// else the one that load context binds Weasel's reference to, loaded and read the same way - which is the assembly Weasel's
/// lock will run, whatever a host redirected; else Weasel's own version, from 9.34. Never the assembly version: the two
/// packages both ship <c>1.0.0.0</c>, and every Weasel from 9.33 to 9.46 references that, so it tells them apart not at all.
/// A monitor that cannot be told at all is taken for the keepalive: the leader then sits idle longer, and a check that
/// judges a timeout against it errs towards saying so rather than towards silence.
/// </para>
/// </remarks>
/// <param name="Keepalive">Whether the monitor is the keepalive, the leader idle between its statements.</param>
/// <param name="Basis">What said so, for the evidence: "DistributedLock.Postgres 1.3.3 (loaded)".</param>
internal sealed record LeaderLockMonitor(bool Keepalive, string Basis)
{
    /// <summary>The assembly both the upstream and the Patched package ship, at assembly version <c>1.0.0.0</c> both.</summary>
    public const string AssemblyName = "DistributedLock.Postgres";

    /// <summary>The first DistributedLock.Postgres with the keepalive, by package (informational) version: Patched 1.3.3.</summary>
    public static readonly Version KeepaliveSince = new(1, 3, 3);

    /// <summary>The first Weasel that depends on it (weasel#614): 9.34.0, Marten 9.39.1.</summary>
    public static readonly Version WeaselSince = new(9, 34);

    /// <summary>The monitor this process runs.</summary>
    public static LeaderLockMonitor Current => Of(LoadedVersion(), BoundVersion(), MartenRuntime.Current.VersionOf(RuntimePackage.WeaselPostgresql));

    /// <summary>The monitor, from what could be read. Pure: the unit tests hold each source to it.</summary>
    /// <param name="loaded">
    /// The informational version of the <c>DistributedLock.Postgres</c> already loaded beside Weasel, or <see langword="null" />.
    /// </param>
    /// <param name="bound">
    /// The informational version of the one Weasel's load context binds its reference to, or <see langword="null" />.
    /// </param>
    /// <param name="weasel">Weasel's own version, or <see langword="null" />.</param>
    internal static LeaderLockMonitor Of(string? loaded, string? bound, Version? weasel)
    {
        if (MartenRuntime.ParseVersion(loaded) is { } running)
        {
            return new LeaderLockMonitor(running >= KeepaliveSince, AssemblyName + " " + MartenRuntime.Format(running) + " (loaded)");
        }

        if (MartenRuntime.ParseVersion(bound) is { } resolved)
        {
            return new LeaderLockMonitor(
                resolved >= KeepaliveSince,
                AssemblyName + " " + MartenRuntime.Format(resolved) + " (the one Weasel.Postgresql binds to)");
        }

        if (weasel is { } version)
        {
            return new LeaderLockMonitor(version >= WeaselSince, "Weasel.Postgresql " + MartenRuntime.Format(version) + " (DistributedLock.Postgres not read)");
        }

        return new LeaderLockMonitor(true, "not known; taken for the keepalive");
    }

    /// <summary>What the monitor does, for a sentence: "DistributedLock's keepalive" or "the pg_sleep monitor".</summary>
    public string Name => Keepalive ? "DistributedLock's keepalive" : "the pg_sleep monitor";

    /// <summary>
    /// The informational version of the <c>DistributedLock.Postgres</c> already loaded in Weasel's own load context, or
    /// <see langword="null" /> when none is - nothing is loaded to answer.
    /// </summary>
    internal static string? LoadedVersion()
    {
        try
        {
            Assembly weasel = MartenRuntime.AnchorType(RuntimePackage.WeaselPostgresql).Assembly;
            return AssemblyLoadContext.GetLoadContext(weasel)?.Assemblies
                .FirstOrDefault(static x => string.Equals(x.GetName().Name, AssemblyName, StringComparison.Ordinal))?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The informational version of the <c>DistributedLock.Postgres</c> Weasel's load context binds its reference to - loading
    /// it if nothing has yet, as Weasel's first lock would - or <see langword="null" /> when Weasel references none or it
    /// cannot be loaded.
    /// </summary>
    internal static string? BoundVersion()
    {
        try
        {
            Assembly weasel = MartenRuntime.AnchorType(RuntimePackage.WeaselPostgresql).Assembly;
            AssemblyName? reference = weasel.GetReferencedAssemblies()
                .FirstOrDefault(static x => string.Equals(x.Name, AssemblyName, StringComparison.Ordinal));

            return reference is null
                ? null
                : AssemblyLoadContext.GetLoadContext(weasel)?.LoadFromAssemblyName(reference)
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }
}
