using System.Reflection;
using System.Reflection.Emit;

using MartenStudio.Internal;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Services;

/// <summary>
/// <c>AddMartenStudio</c> refuses to start beside any PostgresStudio but its own build, and says why - including when
/// NuGet left PostgresStudio out of the deployment altogether, which a central <c>PackageVersion</c> at another version
/// does silently under transitive pinning (PS-0, 2026-10-20).
/// </summary>
/// <remarks>
/// In this test host PostgresStudio is always present and always the same build, so the two failures are driven through
/// the guard's seam - the loader is a parameter - and the property that lets the guard run at all when the assembly is
/// missing, that neither it nor the public method calling it names a PostgresStudio member, is checked on the IL: a
/// reference that crept into either would break nothing here and everything in the deployment the guard exists for.
/// The end-to-end half, a real host whose central pin drops PostgresStudio, was run by PS-6 against packed nupkgs.
/// </remarks>
public class PostgresStudioVersionGuardTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly Marten = typeof(MartenStudioOptions).Assembly;

    private static readonly Assembly Shared = typeof(StudioCoreRegistration).Assembly;

    private static readonly Dictionary<short, OpCode> ByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(static field => (OpCode) field.GetValue(null)!)
        .GroupBy(static code => code.Value)
        .ToDictionary(static group => group.Key, static group => group.First());

    [Fact]
    public void Beside_the_PostgresStudio_it_was_built_with_the_guard_passes()
    {
        string marten = PostgresStudioVersionGuard.ReleaseVersion(Marten);

        marten.Should().NotBe("?", "both assemblies carry the version the build stamped");
        PostgresStudioVersionGuard.ReleaseVersion(Shared).Should().Be(marten);
        PostgresStudioVersionGuard.InformationalVersion(Shared).Should().Be(PostgresStudioVersionGuard.InformationalVersion(Marten),
            "one build stamps both, commit included");

        FluentActions.Invoking(() => PostgresStudioVersionGuard.Ensure()).Should().NotThrow();
        FluentActions.Invoking(() => new ServiceCollection().AddMartenStudio()).Should().NotThrow();
        FluentActions.Invoking(() => new ServiceCollection().AddHealthChecks().AddMartenStudio()).Should().NotThrow();
    }

    [Fact]
    public void A_missing_PostgresStudio_is_refused_and_the_central_pin_is_named()
    {
        var missing = new FileNotFoundException("Could not load file or assembly 'PostgresStudio'.", "PostgresStudio");
        string required = PostgresStudioVersionGuard.ReleaseVersion(Marten);

        InvalidOperationException refusal = FluentActions
            .Invoking(() => PostgresStudioVersionGuard.Ensure(Marten, () => throw missing))
            .Should().Throw<InvalidOperationException>().Which;

        refusal.InnerException.Should().BeSameAs(missing);
        refusal.Message.Should()
            .Contain($"MartenStudio {required} needs PostgresStudio {required} exactly")
            .And.Contain("deployed without PostgresStudio")
            .And.Contain("central PackageVersion for PostgresStudio")
            .And.Contain("CentralPackageTransitivePinningEnabled")
            .And.Contain($"pin PostgresStudio to {required}");
    }

    [Fact]
    public void Another_PostgresStudio_version_is_refused_and_named()
    {
        FluentActions.Invoking(() => PostgresStudioVersionGuard.Ensure(WithVersion("0.5.0"), () => WithVersion("0.5.1")))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("MartenStudio 0.5.0 needs PostgresStudio 0.5.0 exactly, and found PostgresStudio 0.5.1.")
            .And.Contain("upgraded together")
            .And.Contain("central PackageVersion or a PackageReference for PostgresStudio must say 0.5.0");
    }

    /// <summary>
    /// The commit after the <c>+</c> is compared when, and only when, both assemblies are Release builds - as every pack
    /// is - and both carry one: the same version packed from two commits is refused. A Debug build compares the release
    /// versions only, because an IDE's incremental build after a commit recompiles MartenStudio with the new commit and
    /// leaves an unchanged PostgresStudio on the old one; so does a mixed pair, a missing configuration, and a side with
    /// no commit.
    /// </summary>
    [Theory]
    // Release and Release: the commit counts.
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.0+5f1c2a9d", "Release", true)]
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.0+0b7e3c41", "Release", false)]
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.1+5f1c2a9d", "Release", false)]
    [InlineData("0.5.0+5f1c2a9d", "release", "0.5.0+0b7e3c41", "Release", false)]
    // Release and Release, one side or neither with a commit: the version only.
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.0", "Release", true)]
    [InlineData("0.5.0", "Release", "0.5.0+0b7e3c41", "Release", true)]
    [InlineData("0.5.0", "Release", "0.5.0", "Release", true)]
    [InlineData("0.5.0", "Release", "0.5.1", "Release", false)]
    // Debug and Debug: two commits are an incremental build, not a skew; another version still is.
    [InlineData("0.5.0+5f1c2a9d", "Debug", "0.5.0+0b7e3c41", "Debug", true)]
    [InlineData("0.5.0+5f1c2a9d", "Debug", "0.5.1+0b7e3c41", "Debug", false)]
    // Mixed, or no configuration at all: the version only.
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.0+0b7e3c41", "Debug", true)]
    [InlineData("0.5.0+5f1c2a9d", "Debug", "0.5.0+0b7e3c41", "Release", true)]
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.1+5f1c2a9d", "Debug", false)]
    [InlineData("0.5.0+5f1c2a9d", null, "0.5.0+0b7e3c41", "Release", true)]
    [InlineData("0.5.0+5f1c2a9d", null, "0.5.0+0b7e3c41", null, true)]
    public void The_commit_is_compared_only_between_two_release_builds_that_both_carry_one(
        string martenStudio, string? martenConfiguration, string postgresStudio, string? postgresConfiguration, bool passes)
    {
        AssemblyBuilder marten = WithVersion(martenStudio, martenConfiguration);
        AssemblyBuilder postgres = WithVersion(postgresStudio, postgresConfiguration);
        bool bothRelease = PostgresStudioVersionGuard.IsReleaseBuild(marten) && PostgresStudioVersionGuard.IsReleaseBuild(postgres);

        PostgresStudioVersionGuard.SameBuild(martenStudio, postgresStudio, bothRelease).Should().Be(passes);

        Action ensure = () => PostgresStudioVersionGuard.Ensure(marten, () => postgres);

        if (passes)
        {
            ensure.Should().NotThrow();
        }
        else
        {
            ensure.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("needs PostgresStudio");
        }
    }

    [Theory]
    [InlineData("Release", true)]
    [InlineData("release", true)]
    [InlineData("Debug", false)]
    [InlineData("ReleaseWithSymbols", false)]
    [InlineData(null, false)]
    public void A_release_build_is_read_from_its_assembly_configuration(string? configuration, bool release)
    {
        PostgresStudioVersionGuard.IsReleaseBuild(WithVersion("0.5.0", configuration)).Should().Be(release);
    }

    [Fact]
    public void Two_commits_of_one_version_are_named_in_full()
    {
        FluentActions.Invoking(() => PostgresStudioVersionGuard.Ensure(
                WithVersion("0.5.0+5f1c2a9d", "Release"), () => WithVersion("0.5.0+0b7e3c41", "Release")))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("MartenStudio 0.5.0+5f1c2a9d needs PostgresStudio 0.5.0+5f1c2a9d exactly, and found PostgresStudio 0.5.0+0b7e3c41.")
            .And.Contain("must say 0.5.0,");
    }

    [Fact]
    public void Any_other_load_failure_is_not_dressed_up_as_a_version_problem()
    {
        var corrupt = new BadImageFormatException("not an assembly");

        FluentActions.Invoking(() => PostgresStudioVersionGuard.Ensure(Marten, () => throw corrupt))
            .Should().Throw<BadImageFormatException>().Which.Should().BeSameAs(corrupt);
    }

    [Theory]
    [InlineData("0.5.0+5f1c2a9d", "0.5.0")]
    [InlineData("0.5.0-rc.1+5f1c2a9d", "0.5.0-rc.1")]
    [InlineData("0.5.0", "0.5.0")]
    [InlineData("0.5.0+", "0.5.0")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Only_the_version_before_the_build_metadata_is_compared(string? informational, string expected)
    {
        PostgresStudioVersionGuard.ReleaseVersion(informational).Should().Be(expected);
    }

    /// <summary>
    /// Both public <c>AddMartenStudio</c>s - on the service collection and on the health checks builder - and the guard
    /// name no PostgresStudio type or member - in a call, a field, a token or a local - so the JIT can compile them, and
    /// the guard can explain, when PostgresStudio.dll is absent.
    /// </summary>
    [Fact]
    public void Neither_AddMartenStudio_nor_the_guard_names_a_PostgresStudio_member()
    {
        MethodBase[] methods =
        [
            AddMartenStudio,
            AddMartenStudioHealthCheck,
            .. GuardMethods(),
        ];

        methods.Should().HaveCountGreaterThan(4, "both public methods, the guard's checks and its loader are all scanned");
        methods.SelectMany(PostgresStudioMembersIn).Should().BeEmpty(
            "a PostgresStudio member here is resolved when the method is compiled - before the guard can run - and a " +
            "deployment without PostgresStudio then fails with a bare FileNotFoundException instead of the guard's reason");
    }

    /// <summary>Anti-vacuity: the scan above does see PostgresStudio members, in the method that is meant to hold them.</summary>
    [Fact]
    public void The_scan_finds_PostgresStudio_members_where_they_are_meant_to_be()
    {
        MethodInfo services = typeof(MartenStudioServiceCollectionExtensions).GetMethod("AddMartenStudioServices", Declared)!;
        MethodInfo healthCheck = typeof(MartenStudioHealthChecksBuilderExtensions).GetMethod("AddMartenStudioHealthCheck", Declared)!;

        services.Should().NotBeNull();
        healthCheck.Should().NotBeNull();
        PostgresStudioMembersIn(services).Should().Contain(static x => x.Contains(nameof(StudioCoreRegistration), StringComparison.Ordinal));
        PostgresStudioMembersIn(healthCheck).Should().Contain(static x => x.Contains("HealthExportRunner", StringComparison.Ordinal));
    }

    [Fact]
    public void What_names_PostgresStudio_is_never_inlined_into_what_runs_before_the_guard()
    {
        MethodInfo services = typeof(MartenStudioServiceCollectionExtensions).GetMethod("AddMartenStudioServices", Declared)!;
        MethodInfo healthCheck = typeof(MartenStudioHealthChecksBuilderExtensions).GetMethod("AddMartenStudioHealthCheck", Declared)!;
        MethodInfo ensure = typeof(PostgresStudioVersionGuard).GetMethod(nameof(PostgresStudioVersionGuard.Ensure), Type.EmptyTypes)!;
        MethodInfo load = typeof(PostgresStudioVersionGuard).GetMethod("LoadPostgresStudio", Declared)!;

        foreach (MethodInfo method in new[] { services, healthCheck, ensure, load })
        {
            method.Should().NotBeNull();
            method.MethodImplementationFlags.HasFlag(MethodImplAttributes.NoInlining).Should().BeTrue(
                $"{method.Name} must be compiled on its own: inlined, its tokens would be resolved with its caller's");
        }
    }

    private static MethodInfo AddMartenStudio =>
        typeof(MartenStudioServiceCollectionExtensions).GetMethod(
            nameof(MartenStudioServiceCollectionExtensions.AddMartenStudio),
            [typeof(IServiceCollection), typeof(Action<MartenStudioOptions>)])!;

    private static MethodInfo AddMartenStudioHealthCheck =>
        typeof(MartenStudioHealthChecksBuilderExtensions).GetMethod(
            nameof(MartenStudioHealthChecksBuilderExtensions.AddMartenStudio),
            [typeof(IHealthChecksBuilder), typeof(string), typeof(HealthStatus?), typeof(IEnumerable<string>), typeof(TimeSpan?)])!;

    /// <summary>
    /// A throwaway in-memory assembly carrying <paramref name="informationalVersion" />, so every pair of versions the
    /// guard can meet goes through the real <c>Ensure</c> rather than a stand-in for it.
    /// </summary>
    private static AssemblyBuilder WithVersion(string informationalVersion, string? configuration = "Release")
    {
        List<CustomAttributeBuilder> attributes =
        [
            new(typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informationalVersion]),
        ];

        if (configuration is not null)
        {
            attributes.Add(new(typeof(AssemblyConfigurationAttribute).GetConstructor([typeof(string)])!, [configuration]));
        }

        return AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("GuardProbe" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run,
            attributes);
    }

    /// <summary>Every method of the guard, the compiler's nested helpers (a cached delegate's holder) included.</summary>
    private static IEnumerable<MethodBase> GuardMethods()
    {
        Type guard = typeof(PostgresStudioVersionGuard);

        return new[] { guard }
            .Concat(guard.GetNestedTypes(Declared))
            .SelectMany(static type => type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            .Where(static method => method.GetMethodBody() is not null);
    }

    /// <summary>Each PostgresStudio type or member a method's body names, as <c>opcode member</c> or <c>local type</c>.</summary>
    private static IEnumerable<string> PostgresStudioMembersIn(MethodBase method)
    {
        MethodBody body = method.GetMethodBody()!;
        byte[] il = body.GetILAsByteArray()!;
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int position = 0;

        while (position < il.Length)
        {
            short value = il[position] == 0xFE && position + 1 < il.Length
                ? unchecked((short) (0xFE00 | il[++position]))
                : il[position];
            position++;

            OpCode code = ByValue[value];

            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                MemberInfo member = method.Module.ResolveMember(BitConverter.ToInt32(il, position), typeArguments, methodArguments)!;

                if (TypesOf(member).Any(IsPostgresStudio))
                {
                    // The member's own ToString carries generic arguments (TryAddSingleton[HealthExportRunner]).
                    yield return $"{method.Name}: {code.Name} {member.DeclaringType?.Name}::{member}";
                }
            }

            position += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
                _ => 4,
            };
        }

        foreach (LocalVariableInfo local in body.LocalVariables.Where(static x => Expand(x.LocalType).Any(IsPostgresStudio)))
        {
            yield return $"{method.Name}: local {local.LocalType.Name}";
        }
    }

    private static IEnumerable<Type> TypesOf(MemberInfo member) => member switch
    {
        Type type => Expand(type),
        MethodInfo method => Expand(method.DeclaringType)
            .Concat(Expand(method.ReturnType))
            .Concat(method.GetParameters().SelectMany(static x => Expand(x.ParameterType)))
            .Concat(method.IsGenericMethod ? method.GetGenericArguments().SelectMany(Expand) : []),
        ConstructorInfo constructor => Expand(constructor.DeclaringType)
            .Concat(constructor.GetParameters().SelectMany(static x => Expand(x.ParameterType))),
        FieldInfo field => Expand(field.DeclaringType).Concat(Expand(field.FieldType)),
        _ => Expand(member.DeclaringType),
    };

    /// <summary>A type, its element type and its generic arguments, all the way down.</summary>
    private static IEnumerable<Type> Expand(Type? type)
    {
        if (type is null)
        {
            yield break;
        }

        yield return type;

        foreach (Type inner in (type.HasElementType ? [type.GetElementType()!] : Array.Empty<Type>())
            .Concat(type.IsGenericType ? type.GetGenericArguments() : [])
            .SelectMany(Expand))
        {
            yield return inner;
        }
    }

    private static bool IsPostgresStudio(Type type) =>
        !type.IsGenericParameter
        && string.Equals(type.Assembly.GetName().Name, PostgresStudioVersionGuard.PostgresStudioAssemblyName, StringComparison.Ordinal);
}
