using System.Reflection;
using System.Text.RegularExpressions;

using MartenStudio.Services.Configuration;

namespace MartenStudio.Services.Health;

/// <summary>One check, ready to run: its metadata and its single, stateless instance.</summary>
/// <param name="Info">What its attribute says.</param>
/// <param name="Check">The instance every sweep uses.</param>
internal sealed record HealthCheckDescriptor(HealthCheckInfo Info, IHealthCheck Check);

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
/// <b>One public constructor, and it is the parameterless one.</b> A test that wants exactly its own checks
/// uses <see cref="ForTypes" />. A second public constructor taking <c>IEnumerable&lt;Type&gt;</c> was a
/// trap: the container treats any <c>IEnumerable&lt;T&gt;</c> as resolvable - to an empty list - and picks
/// the greediest constructor it can satisfy, so every real host got a registry with no checks in it.
/// </para>
/// </remarks>
internal sealed partial class HealthCheckRegistry
{
    /// <summary>Every check in the studio's own assembly.</summary>
    public HealthCheckRegistry() : this([.. Discover(typeof(HealthCheckRegistry).Assembly)])
    {
    }

    private HealthCheckRegistry(Type[] types)
    {
        IReadOnlyList<string> problems = Problems(types);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Marten Studio's health checks are not well-formed: " + string.Join(" | ", problems));
        }

        All =
        [
            .. types
                .Select(static type => new HealthCheckDescriptor(Describe(type), (IHealthCheck) Activator.CreateInstance(type)!))
                .OrderBy(static x => x.Info.Area)
                .ThenBy(static x => x.Info.Code, StringComparer.Ordinal),
        ];
    }

    /// <summary>Every check, by area and then code.</summary>
    public IReadOnlyList<HealthCheckDescriptor> All { get; }

    /// <summary>Exactly these checks, for a test that sweeps something it made up.</summary>
    /// <exception cref="InvalidOperationException">One of them breaks a rule; the message lists every problem.</exception>
    internal static HealthCheckRegistry ForTypes(IEnumerable<Type> checkTypes)
    {
        ArgumentNullException.ThrowIfNull(checkTypes);
        return new HealthCheckRegistry([.. checkTypes]);
    }

    /// <summary>The check with <paramref name="code" />, or <see langword="null" />.</summary>
    public HealthCheckDescriptor? Find(string code) =>
        All.FirstOrDefault(x => string.Equals(x.Info.Code, code, StringComparison.Ordinal));

    /// <summary>Whether any check of <paramref name="tier" /> exists - what decides whether a button is offered.</summary>
    public bool HasTier(HealthCostTier tier) => All.Any(x => x.Info.Tier == tier);

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
    internal static HealthCheckInfo Describe(Type checkType)
    {
        ArgumentNullException.ThrowIfNull(checkType);

        HealthCheckAttribute attribute = checkType.GetCustomAttribute<HealthCheckAttribute>()
            ?? throw new InvalidOperationException(checkType.FullName + " has no [HealthCheck] attribute.");

        return new HealthCheckInfo(
            attribute.Code,
            attribute.Area,
            attribute.Tier,
            attribute.Gate,
            attribute.Title,
            RuntimeFeature.Find(attribute.RequiresFeature),
            checkType);
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
    internal static IReadOnlyList<string> Problems(IReadOnlyList<Type> checkTypes)
    {
        ArgumentNullException.ThrowIfNull(checkTypes);

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

            if (attribute.RequiresFeature is { } feature && RuntimeFeature.Find(feature) is null)
            {
                problems.Add(name + " requires feature '" + feature + "', which is not in the probe table (RuntimeFeature).");
            }

            if (attribute.Tier == HealthCostTier.Statements && attribute.Gate != HealthGate.Activity)
            {
                problems.Add(name + " reads statements (tier 3) and is not behind the activity gate.");
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

    [GeneratedRegex("^[DETCK][0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeShape();
}
