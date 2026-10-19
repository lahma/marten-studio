using System.Reflection;

using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Schema;

using IJasperFxEventStore = JasperFx.Events.IEventStore;

namespace MartenStudio.Services.Configuration;

/// <summary>
/// One thing a Marten (or JasperFx) newer than the studio's floor can do, how to tell whether this host
/// can do it, and the first release that could: the F1 probe table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The table is the static fields of this class, and nothing else.</b> The constructor is private, so the
/// only code that can make a feature is code inside <c>partial class RuntimeFeature</c> - which is what lets
/// a health remedy say "Marten 9.47+" only about a feature this table probes, with no convention test
/// needed to hold the two together: a remedy takes a <see cref="RuntimeFeature" />, and there is no other
/// way to get one. A later packet adds a feature with a <c>partial class RuntimeFeature</c> file of its own
/// and touches nothing here; <see cref="All" /> finds it by reflection.
/// </para>
/// <para>
/// <b>Every probe is pinned at both ends.</b> <c>MartenRuntimeProbeTests</c> asserts, for each feature,
/// "present iff the resolved package is at or above <see cref="IntroducedIn" />" - so the same test is right
/// at the floor, where each probe must find nothing, and at the latest Marten, where a probe that silently
/// finds nothing (a renamed member, a moved type) is exactly the failure light-up would otherwise hide.
/// Each version here was read from the release's own source, not from release notes.
/// </para>
/// <para>
/// <b>Cached, and never thrown out of.</b> A probe is reflection over assemblies that do not change while the
/// process runs, so it runs once; a probe that throws answers "absent", because the only thing on the other
/// side of a probe is a feature the studio then does without.
/// </para>
/// </remarks>
internal sealed partial class RuntimeFeature
{
    private static readonly Lazy<IReadOnlyList<RuntimeFeature>> Table = new(Discover, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Lazy<bool>? probe;

    private RuntimeFeature(
        string name,
        RuntimePackage package,
        string introducedIn,
        string? firstMarten,
        string summary,
        Func<bool>? probe)
    {
        Name = name;
        Package = package;
        IntroducedIn = Version.Parse(introducedIn);
        FirstMarten = firstMarten is null ? null : Version.Parse(firstMarten);
        Summary = summary;
        this.probe = probe is null ? null : new Lazy<bool>(() => Run(probe), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // ---------------------------------------------------------------------------------------------------
    // The table. Package and version are where the member first appears; FirstMarten is the first Marten
    // release whose Directory.Packages.props brings that package version (null: none yet).
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>opts.Schema.For&lt;T&gt;().FillFactor(n)</c> - Marten 9.47.0, <c>src/Marten/MartenRegistry.cs</c>
    /// (absent at V9.46.0).
    /// </summary>
    public static readonly RuntimeFeature DocumentFillFactor = new(
        nameof(DocumentFillFactor),
        RuntimePackage.Marten,
        "9.47.0",
        "9.47.0",
        "A document table's fill factor, declared with Schema.For<T>().FillFactor(n) and managed by the schema delta.",
        static () => typeof(MartenRegistry.DocumentMappingExpression<>).GetMethod(
            "FillFactor", BindingFlags.Public | BindingFlags.Instance, [typeof(int)]) is not null);

    /// <summary>
    /// <c>opts.Schema.For&lt;T&gt;().StorageParameter(name, value)</c> - Marten 9.47.0,
    /// <c>src/Marten/MartenRegistry.cs</c> (absent at V9.46.0).
    /// </summary>
    public static readonly RuntimeFeature DocumentStorageParameter = new(
        nameof(DocumentStorageParameter),
        RuntimePackage.Marten,
        "9.47.0",
        "9.47.0",
        "Any table storage parameter on a document table (autovacuum_vacuum_scale_factor, ...), declared with Schema.For<T>().StorageParameter(name, value).",
        static () => typeof(MartenRegistry.DocumentMappingExpression<>).GetMethod(
            "StorageParameter", BindingFlags.Public | BindingFlags.Instance, [typeof(string), typeof(object)]) is not null);

    /// <summary>
    /// <c>DocumentMapping.StorageParameters</c>, the declared parameters a table's <c>reloptions</c> can be
    /// compared against - Marten 9.47.0, <c>src/Marten/Schema/DocumentMapping.cs</c> (absent at V9.31.0 and
    /// V9.46.0).
    /// </summary>
    public static readonly RuntimeFeature DeclaredStorageParameters = new(
        nameof(DeclaredStorageParameters),
        RuntimePackage.Marten,
        "9.47.0",
        "9.47.0",
        "The storage parameters a document mapping declares, readable from DocumentMapping.StorageParameters.",
        static () => typeof(DocumentMapping).GetProperty(
            "StorageParameters", BindingFlags.Public | BindingFlags.Instance) is not null);

    /// <summary>
    /// The store-level tenant overloads of <c>IEventStore.CompactStreamAsync</c>, <em>implemented by
    /// Marten</em> - Marten 9.41.0, <c>src/Marten/DocumentStore.EventStore.cs</c>. JasperFx.Events 2.76.0
    /// declares them as default interface methods that refuse any tenant, so the probe asks whether
    /// <c>DocumentStore</c> maps them to methods of its own rather than whether the interface has them.
    /// </summary>
    /// <remarks>
    /// The studio does not need them. It compacts through <c>session.Events.CompactStreamAsync&lt;T&gt;</c> on a
    /// session pinned by <c>SessionOptions.ForDatabase</c>, whose statements Marten already filters by tenant at
    /// the floor; these overloads open an unpinned <c>LightweightSession(tenantId)</c>, which hard rule 15
    /// forbids. The probe is kept so the Configuration page can say which Marten the host runs against, and so a
    /// later packet that wants them finds the version pinned.
    /// </remarks>
    public static readonly RuntimeFeature TenantStreamCompaction = new(
        nameof(TenantStreamCompaction),
        RuntimePackage.Marten,
        "9.41.0",
        "9.41.0",
        "The store-level tenant overloads of IEventStore.CompactStreamAsync. The studio compacts on a tenant- and database-pinned session and does not need them.",
        static () => ImplementedByDocumentStore([typeof(Guid), typeof(string), typeof(CancellationToken)])
            && ImplementedByDocumentStore([typeof(string), typeof(string), typeof(CancellationToken)]));

    /// <summary>
    /// <c>ShardStartException.Reason</c>, a start refusal classified as a value - JasperFx.Events 2.76.0,
    /// <c>src/JasperFx.Events/Daemon/ShardStartException.cs</c> (absent at V2.75.2), first in Marten 9.41.0.
    /// </summary>
    public static readonly RuntimeFeature ShardStartReason = new(
        nameof(ShardStartReason),
        RuntimePackage.JasperFxEvents,
        "2.76.0",
        "9.41.0",
        "Why a shard refused to start (AgentPaused, HighWaterNotRunning, StartRace, Rebuilding, ...), as a value on ShardStartException.Reason.",
        static () => typeof(ShardStartException).GetProperty("Reason", BindingFlags.Public | BindingFlags.Instance) is not null);

    /// <summary>
    /// <c>IProjectionDaemon.IsRebuilding(ShardName)</c> - JasperFx.Events 2.86.0,
    /// <c>src/JasperFx.Events/Daemon/IProjectionDaemon.cs</c> (absent at V2.85.1), first in Marten 9.50.0
    /// (9.49.0 brings 2.84.0).
    /// </summary>
    public static readonly RuntimeFeature DaemonIsRebuilding = new(
        nameof(DaemonIsRebuilding),
        RuntimePackage.JasperFxEvents,
        "2.86.0",
        "9.50.0",
        "Whether the in-process daemon is rebuilding a shard right now (IProjectionDaemon.IsRebuilding).",
        static () => typeof(IProjectionDaemon).GetMethod(
            "IsRebuilding", BindingFlags.Public | BindingFlags.Instance, [typeof(ShardName)]) is not null);

    /// <summary>
    /// <c>ProjectionAlreadyRebuildingException</c> - JasperFx.Events 2.86.0,
    /// <c>src/JasperFx.Events/Daemon/ProjectionAlreadyRebuildingException.cs</c> (absent at V2.85.1), first in
    /// Marten 9.50.0.
    /// </summary>
    public static readonly RuntimeFeature ProjectionAlreadyRebuilding = new(
        nameof(ProjectionAlreadyRebuilding),
        RuntimePackage.JasperFxEvents,
        "2.86.0",
        "9.50.0",
        "A rebuild refused because the shard is already being rebuilt, as its own exception type rather than a message.",
        static () => typeof(IProjectionDaemon).Assembly.GetType(
            "JasperFx.Events.Daemon.ProjectionAlreadyRebuildingException", throwOnError: false) is not null);

    /// <summary>
    /// <c>JasperFx.Linq.DynamicQuery</c>, Dynamic-LINQ text applied under an allow-list - JasperFx 2.84.0,
    /// <c>src/JasperFx/Linq/DynamicQuery.cs</c> (absent at V2.82.0), first in Marten 9.48.0 (9.47.0 brings
    /// 2.81.0).
    /// </summary>
    public static readonly RuntimeFeature DynamicLinqQuery = new(
        nameof(DynamicLinqQuery),
        RuntimePackage.JasperFx,
        "2.84.0",
        "9.48.0",
        "A typed LINQ predicate parsed under JasperFx's allow-list and applied to Marten's own Query<T>().",
        static () => typeof(JasperFx.AutoCreate).Assembly.GetType("JasperFx.Linq.DynamicQuery", throwOnError: false) is not null);

    /// <summary>
    /// <c>TransientFailures.IsTransient(Exception)</c> - JasperFx.Events 2.89.0,
    /// <c>src/JasperFx.Events/Daemon/TransientFailures.cs</c> (absent at V2.88.0), in no Marten release as of
    /// 9.50.2 (which brings 2.87.1).
    /// </summary>
    public static readonly RuntimeFeature TransientFailureClassifier = new(
        nameof(TransientFailureClassifier),
        RuntimePackage.JasperFxEvents,
        "2.89.0",
        null,
        "Whether a shard's failure is transient or permanent, as JasperFx classifies it (TransientFailures.IsTransient).",
        static () => typeof(IProjectionDaemon).Assembly
            .GetType("JasperFx.Events.Daemon.TransientFailures", throwOnError: false)?
            .GetMethod("IsTransient", BindingFlags.Public | BindingFlags.Static, [typeof(Exception)]) is not null);

    // ---------------------------------------------------------------------------------------------------

    /// <summary>The feature's name, as the table spells its field.</summary>
    public string Name { get; }

    /// <summary>The package the member first appeared in.</summary>
    public RuntimePackage Package { get; }

    /// <summary>The version of <see cref="Package" /> the member first appeared in.</summary>
    public Version IntroducedIn { get; }

    /// <summary>
    /// The first Marten release that brings <see cref="IntroducedIn" />, or <see langword="null" /> when no
    /// Marten release does yet.
    /// </summary>
    public Version? FirstMarten { get; }

    /// <summary>What it lets the studio, or a remedy, do.</summary>
    public string Summary { get; }

    /// <summary>Whether a member is probed for, rather than the version alone compared.</summary>
    public bool HasProbe => probe is not null;

    /// <summary>
    /// What a remedy prints beside a line that needs this: "Marten 9.47+", or for a feature no Marten release
    /// brings yet, the package that does.
    /// </summary>
    public string Requirement => FirstMarten is { } marten
        ? "Marten " + marten.ToString(2) + "+"
        : MartenRuntime.PackageName(Package) + " " + IntroducedIn.ToString(2) + "+ (in no Marten release yet)";

    /// <summary>Every feature in the table, in package and version order.</summary>
    public static IReadOnlyList<RuntimeFeature> All => Table.Value;

    /// <summary>The feature named <paramref name="name" />, or <see langword="null" />.</summary>
    public static RuntimeFeature? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Whether <paramref name="runtime" /> has this: the probe's answer for this process's own runtime, the
    /// version comparison for a runtime a test made up (or for a feature with no member to probe).
    /// </summary>
    public bool IsAvailable(MartenRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (runtime.IsProcessRuntime && probe is not null)
        {
            return probe.Value;
        }

        return runtime.VersionOf(Package) is { } version && version >= IntroducedIn;
    }

    /// <summary>What the probe finds in this process, or <see langword="null" /> for a version-only feature.</summary>
    internal bool? Probe() => probe?.Value;

    /// <inheritdoc />
    public override string ToString() => Name + " (" + Requirement + ")";

    private static bool Run(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <c>DocumentStore</c> implements the <c>IEventStore.CompactStreamAsync</c> overload with these
    /// parameters itself, rather than inheriting JasperFx's refusing default.
    /// </summary>
    private static bool ImplementedByDocumentStore(Type[] parameters)
    {
        MethodInfo? declared = typeof(IJasperFxEventStore).GetMethod(
            "CompactStreamAsync", BindingFlags.Public | BindingFlags.Instance, parameters);

        if (declared is null)
        {
            return false;
        }

        InterfaceMapping map = typeof(DocumentStore).GetInterfaceMap(typeof(IJasperFxEventStore));
        int index = Array.IndexOf(map.InterfaceMethods, declared);

        return index >= 0 && map.TargetMethods[index].DeclaringType == typeof(DocumentStore);
    }

    private static IReadOnlyList<RuntimeFeature> Discover() =>
    [
        .. typeof(RuntimeFeature)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static x => x.IsInitOnly && x.FieldType == typeof(RuntimeFeature))
            .Select(static x => (RuntimeFeature) x.GetValue(null)!)
            .OrderBy(static x => x.Package)
            .ThenBy(static x => x.IntroducedIn)
            .ThenBy(static x => x.Name, StringComparer.Ordinal),
    ];
}
