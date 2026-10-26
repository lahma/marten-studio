using System.Reflection;
using System.Text.RegularExpressions;

using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health;

/// <summary>One check, ready to run: its metadata, its single, stateless instance, and the sources it runs against.</summary>
/// <param name="Info">What its attribute says.</param>
/// <param name="Check">The instance every sweep uses.</param>
/// <param name="SourceKinds">
/// The kinds of source it runs against, or <see langword="null" /> for every kind - from the descriptor's
/// <see cref="StudioDescriptor.HealthCheckAssemblies" /> entry that found it.
/// </param>
internal sealed record HealthCheckDescriptor(HealthCheckInfo Info, IHealthCheck Check, IReadOnlySet<StudioSourceKind>? SourceKinds = null)
{
    /// <summary>
    /// Whether it runs against a source of <paramref name="kind" />. A check that does not is not run and has no row - it
    /// reads a kind of source this one is not, and a sweep of it would only ever be a "could not check".
    /// </summary>
    /// <param name="kind">The swept source's kind.</param>
    public bool RunsAgainst(StudioSourceKind kind) => SourceKinds is null || SourceKinds.Contains(kind);
}

/// <summary>
/// Every health check in the studio, found by reflection: adding a check is adding its class.
/// </summary>
/// <remarks>
/// <para>
/// A check is a concrete class implementing <see cref="IHealthCheck" /> - in practice deriving from
/// <see cref="HealthCheck{TFacts}" /> - with a <see cref="HealthCheckAttribute" /> and a parameterless
/// constructor. Nothing lists them, so there is nothing a later packet edits to add one; the conventions
/// tests and <see cref="Problems" /> hold every one to the same rules instead: a well-formed code whose
/// letter is its area's, unique, a title, no instance state, and a required feature that is in the probe
/// table.
/// </para>
/// <para>
/// Singleton, and built on first use. A registry that finds a problem refuses to build rather than running
/// some of the checks - a duplicate code would make one check's findings mute the other's - and the
/// conventions test is what makes sure that never reaches a host.
/// </para>
/// <para>
/// <b>Where the checks come from is the studio's.</b> The container builds the registry from the descriptor's
/// <see cref="StudioDescriptor.HealthCheckAssemblies" /> - the shared checks' assembly and the studio's own - and resolves
/// a check's <c>RequiresFeature</c> against the studio's runtime (<see cref="IStudioRuntime" />). A check found in a
/// studio's own assembly reads its sources through that studio's extension members, so the studio offers it only its own.
/// </para>
/// <para>
/// <b>Which sources a check runs against is the descriptor's too.</b> Each entry of
/// <see cref="StudioDescriptor.HealthCheckAssemblies" /> names the source kinds its checks are offered
/// (<see cref="HealthCheckDescriptor.SourceKinds" />), and the sweep runs a check only against a source of one of them: a
/// studio's own check, which reads the source through that studio's types, never meets another studio's source - no row,
/// rather than a "could not check" one. A check found by several entries - while the shared engine and a studio share an
/// assembly - runs only against a kind every one of them admits.
/// </para>
/// <para>
/// <b>One public constructor, which the container can satisfy only with a studio registered.</b> A test that wants
/// exactly its own checks uses <see cref="ForTypes(IEnumerable{Type}, IStudioRuntime?)" />. A public constructor taking
/// <c>IEnumerable&lt;Type&gt;</c> was a trap: the container treats any <c>IEnumerable&lt;T&gt;</c> as resolvable - to an
/// empty list - and picks the greediest constructor it can satisfy, so every real host got a registry with no checks in
/// it. The descriptor is resolvable only where a studio registered one, and then it is the list to use. (A parameterless
/// constructor over the engine's own assembly existed until PS-1e: it resolved features against no runtime, so it would
/// have thrown the day a check required one, and after the move it would have found the shared checks alone. Nothing but
/// tests used it, and they now build the studio's own registry.)
/// </para>
/// </remarks>
internal sealed partial class HealthCheckRegistry
{
    /// <summary>Every check in the assemblies <paramref name="descriptor" /> names, resolved against the studio's runtime.</summary>
    /// <param name="descriptor">The studio's descriptor.</param>
    /// <param name="runtime">The studio's runtime, or <see langword="null" /> for a studio that registers none.</param>
    public HealthCheckRegistry(StudioDescriptor descriptor, IStudioRuntime? runtime = null)
        : this(
            DiscoverAll((descriptor ?? throw new ArgumentNullException(nameof(descriptor))).HealthCheckAssemblies),
            runtime ?? NoStudioRuntime.Instance,
            descriptor.ProductName)
    {
    }

    private HealthCheckRegistry(
        IReadOnlyList<(Type Type, IReadOnlySet<StudioSourceKind>? SourceKinds)> checks,
        IStudioRuntime runtime,
        string productName = "Marten Studio")
    {
        IReadOnlyList<string> problems = Problems([.. checks.Select(static x => x.Type)], runtime);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                productName + "'s health checks are not well-formed: " + string.Join(" | ", problems));
        }

        All =
        [
            .. checks
                .Select(check => new HealthCheckDescriptor(
                    Describe(check.Type, runtime), (IHealthCheck) Activator.CreateInstance(check.Type)!, check.SourceKinds))
                .OrderBy(static x => x.Info.Area)
                .ThenBy(static x => x.Info.Code, StringComparer.Ordinal),
        ];
    }

    /// <summary>Every check, by area and then code.</summary>
    public IReadOnlyList<HealthCheckDescriptor> All { get; }

    /// <summary>Exactly these checks, each run against every kind of source, for a test that sweeps something it made up.</summary>
    /// <param name="checkTypes">The check classes.</param>
    /// <param name="runtime">
    /// The runtime their <c>RequiresFeature</c> is resolved against, or <see langword="null" /> for none - which refuses a
    /// check that requires a feature.
    /// </param>
    /// <exception cref="InvalidOperationException">One of them breaks a rule; the message lists every problem.</exception>
    internal static HealthCheckRegistry ForTypes(IEnumerable<Type> checkTypes, IStudioRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(checkTypes);
        return new HealthCheckRegistry([.. checkTypes.Select(static x => (x, (IReadOnlySet<StudioSourceKind>?) null))], runtime ?? NoStudioRuntime.Instance);
    }

    /// <summary>Exactly these checks, each run against the kinds of source it names, for a test of the source filter.</summary>
    /// <param name="checks">The check classes, each with its source kinds (<see langword="null" /> for every kind).</param>
    /// <param name="runtime">The runtime, as <see cref="ForTypes(IEnumerable{Type}, IStudioRuntime?)" />.</param>
    internal static HealthCheckRegistry ForTypes(
        IEnumerable<(Type CheckType, IReadOnlySet<StudioSourceKind>? SourceKinds)> checks,
        IStudioRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return new HealthCheckRegistry([.. checks], runtime ?? NoStudioRuntime.Instance);
    }

    /// <summary>
    /// Every concrete check class in <paramref name="entries" />' assemblies, each once, with the source kinds every entry
    /// that found it admits (<see langword="null" /> when every one admits every kind).
    /// </summary>
    /// <param name="entries">A descriptor's <see cref="StudioDescriptor.HealthCheckAssemblies" />.</param>
    internal static IReadOnlyList<(Type Type, IReadOnlySet<StudioSourceKind>? SourceKinds)> DiscoverAll(
        IEnumerable<StudioHealthCheckAssembly> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Dictionary<Type, IReadOnlySet<StudioSourceKind>?> found = [];
        List<Type> order = [];

        foreach (StudioHealthCheckAssembly entry in entries)
        {
            foreach (Type type in Discover(entry.Assembly))
            {
                if (!found.TryGetValue(type, out IReadOnlySet<StudioSourceKind>? kinds))
                {
                    order.Add(type);
                    found[type] = entry.SourceKinds;
                }
                else if (entry.SourceKinds is { } narrower)
                {
                    found[type] = kinds is null ? narrower : kinds.Where(narrower.Contains).ToHashSet();
                }
            }
        }

        return [.. order.Select(type => (type, found[type]))];
    }

    /// <summary>The check with <paramref name="code" />, or <see langword="null" />.</summary>
    public HealthCheckDescriptor? Find(string code) =>
        All.FirstOrDefault(x => string.Equals(x.Info.Code, code, StringComparison.Ordinal));

    /// <summary>
    /// Whether any check of <paramref name="tier" /> runs against a source of <paramref name="sourceKind" /> - what decides
    /// whether a button is offered.
    /// </summary>
    /// <param name="tier">The tier.</param>
    /// <param name="sourceKind">The kind of source the page is about.</param>
    public bool HasTier(HealthCostTier tier, StudioSourceKind sourceKind) =>
        All.Any(x => x.Info.Tier == tier && x.RunsAgainst(sourceKind));

    /// <summary>Every concrete check class in <paramref name="assembly" />.</summary>
    internal static IReadOnlyList<Type> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return
        [
            .. assembly.GetTypes()
                .Where(static x => x is { IsClass: true, IsAbstract: false } && typeof(IHealthCheck).IsAssignableFrom(x))
                .OrderBy(static x => x.FullName, StringComparer.Ordinal),
        ];
    }

    /// <summary>The metadata of one check class. It must carry the attribute.</summary>
    /// <param name="checkType">The check class.</param>
    /// <param name="runtime">The runtime its <c>RequiresFeature</c> is resolved against, or <see langword="null" /> for none.</param>
    internal static HealthCheckInfo Describe(Type checkType, IStudioRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(checkType);
        runtime ??= NoStudioRuntime.Instance;

        HealthCheckAttribute attribute = checkType.GetCustomAttribute<HealthCheckAttribute>()
            ?? throw new InvalidOperationException(checkType.FullName + " has no [HealthCheck] attribute.");

        return new HealthCheckInfo(
            attribute.Code,
            attribute.Area,
            attribute.Tier,
            attribute.Gate,
            attribute.Title,
            attribute.RequiresFeature is { } feature ? runtime.FindFeature(feature) : null,
            checkType)
        {
            Variants = [.. attribute.Variants ?? []],
        };
    }

    /// <summary>
    /// What is wrong with one finding of a check, one sentence each - empty when it is fine: a variant the check does not
    /// list (<see cref="HealthCheckAttribute.Variants" />), which a host keying its wording on the code and the variant
    /// would have no row for.
    /// </summary>
    /// <remarks>
    /// The sweep refuses a verdict that has one - that check's "could not check" row, like any broken verdict - and an
    /// evaluator test asserts it over every finding it makes, so a check cannot be born speaking a variant it never
    /// declared.
    /// </remarks>
    /// <param name="info">The check's metadata.</param>
    /// <param name="finding">One of its findings.</param>
    internal static IReadOnlyList<string> FindingProblems(HealthCheckInfo info, HealthFinding finding)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(finding);

        if (finding.Variant is not { } variant)
        {
            return [];
        }

        if (info.Variants.Count == 0)
        {
            return [info.Code + " made a finding of variant '" + variant + "', but lists no variants; a check with fixed kinds of finding lists them in [HealthCheck(Variants = ...)]."];
        }

        return info.Variants.Contains(variant, StringComparer.Ordinal)
            ? []
            : [info.Code + " made a finding of variant '" + variant + "', which is not one of its variants (" + string.Join(", ", info.Variants) + "); a variant is frozen per code."];
    }

    /// <summary>
    /// Everything wrong with a set of check classes, one sentence each - empty when they are fine.
    /// </summary>
    /// <remarks>
    /// The rules the registry refuses to build without, and the conventions test asserts over the real
    /// assembly with an anti-vacuity theory over made-up ones: each one names a mistake that would otherwise be
    /// silent - a duplicate code muting another check, a check whose area and code disagree landing in the
    /// wrong docs file, a field carrying one sweep's state into another's.
    /// </remarks>
    /// <param name="checkTypes">The check classes.</param>
    /// <param name="runtime">
    /// The runtime whose probe table a required feature must be in, or <see langword="null" /> for none - in which no
    /// feature is, so a check that requires one is a problem.
    /// </param>
    internal static IReadOnlyList<string> Problems(IReadOnlyList<Type> checkTypes, IStudioRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(checkTypes);
        runtime ??= NoStudioRuntime.Instance;

        List<string> problems = [];
        Dictionary<string, string> codes = new(StringComparer.Ordinal);

        foreach (Type type in checkTypes)
        {
            string name = type.FullName ?? type.Name;

            if (!typeof(IHealthCheck).IsAssignableFrom(type) || type.IsAbstract)
            {
                problems.Add(name + " is not a concrete IHealthCheck.");
                continue;
            }

            if (!IsHealthCheckOfFacts(type))
            {
                problems.Add(name + " implements IHealthCheck without deriving from HealthCheck<TFacts>, which is what keeps its gatherer and its evaluator apart.");
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                problems.Add(name + " has no parameterless constructor; a check takes what it needs from its context, never from a constructor.");
            }

            FieldInfo[] state = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (state.Length > 0)
            {
                problems.Add(name + " holds instance state (" + string.Join(", ", state.Select(static x => x.Name))
                    + "); one instance serves every sweep, so a check is stateless and remembers through its samples.");
            }

            HealthCheckAttribute? attribute = type.GetCustomAttribute<HealthCheckAttribute>();
            if (attribute is null)
            {
                problems.Add(name + " has no [HealthCheck] attribute.");
                continue;
            }

            string code = attribute.Code;

            if (!CodeShape().IsMatch(code))
            {
                problems.Add(name + "'s code '" + code + "' is not a letter and two digits.");
            }
            else if (AreaOf(code[0]) != attribute.Area)
            {
                problems.Add(name + "'s code " + code + " is in area " + AreaOf(code[0])
                    + " but the attribute says " + attribute.Area + ".");
            }

            if (codes.TryGetValue(code, out string? first))
            {
                problems.Add("The code " + code + " is used by both " + first + " and " + name + ".");
            }
            else
            {
                codes[code] = name;
            }

            if (string.IsNullOrWhiteSpace(attribute.Title))
            {
                problems.Add(name + " has no title, which is also its docs heading.");
            }

            if (attribute.RequiresFeature is { } feature && runtime.FindFeature(feature) is null)
            {
                problems.Add(name + " requires feature '" + feature + "', which is not in the probe table of the studio's runtime.");
            }

            if (attribute.Tier == HealthCostTier.Statements && attribute.Gate != HealthGate.Activity)
            {
                problems.Add(name + " reads statements (tier 3) and is not behind the activity gate.");
            }

            string[] variants = attribute.Variants ?? [];

            foreach (string variant in variants.Where(static x => x is null || !VariantShape().IsMatch(x)))
            {
                problems.Add(name + "'s variant '" + variant + "' is not lower-case words joined by hyphens.");
            }

            foreach (string twice in variants.Where(static x => x is not null).GroupBy(static x => x, StringComparer.Ordinal).Where(static x => x.Count() > 1).Select(static x => x.Key))
            {
                problems.Add(name + " lists the variant '" + twice + "' twice; each kind of finding is one variant.");
            }
        }

        return problems;
    }

    /// <summary>The area a code's letter names, or <see langword="null" /> for a letter that names none.</summary>
    internal static HealthArea? AreaOf(char letter) => letter switch
    {
        'D' => HealthArea.Daemon,
        'E' => HealthArea.Events,
        'T' => HealthArea.Tables,
        'C' => HealthArea.Connections,
        'R' => HealthArea.Replication,
        'S' => HealthArea.Server,
        'K' => HealthArea.Configuration,
        _ => null,
    };

    private static bool IsHealthCheckOfFacts(Type type)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(HealthCheck<>))
            {
                return true;
            }
        }

        return false;
    }

    // \A and \z, because .NET's $ also matches before a final line feed.
    [GeneratedRegex(@"\A[DETCRSK][0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodeShape();

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex VariantShape();
}
