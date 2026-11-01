using System.Reflection;
using System.Reflection.Emit;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Internal;

namespace PostgresStudio.Tests.Conventions;

/// <summary>
/// <c>AddPostgresStudio</c> refuses to start beside a newer PostgresStudio.Diagnostics than its own, or another build of
/// its version, and says why - which is what a central <c>PackageVersion</c> at another version produces under transitive
/// pinning (PS-0, 2026-10-20, for the same pin one package up). A missing or older engine never reaches it: the runtime
/// loads the engine to compile <c>AddPostgresStudio</c> itself and fails in the loader, naming it.
/// </summary>
/// <remarks>
/// In this test host the engine is always present and always the same build, so the two failures are driven through the
/// guard's seam - the loader is a parameter - and the property that lets the guard run at all when the assembly is
/// missing, that neither it nor the public method calling it names a PostgresStudio.Diagnostics member, is checked on
/// the IL: a reference that crept into either would break nothing here and everything in the deployment the guard exists
/// for. Modelled on <c>PostgresStudioVersionGuardTests</c> in MartenStudio.Tests, one package down.
/// </remarks>
public class PostgresStudioDiagnosticsVersionGuardTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly Studio = typeof(PostgresStudioOptions).Assembly;

    private static readonly Assembly Engine = typeof(PostgresDiagnosticsOptions).Assembly;

    private static readonly Dictionary<short, OpCode> ByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(static field => (OpCode) field.GetValue(null)!)
        .GroupBy(static code => code.Value)
        .ToDictionary(static group => group.Key, static group => group.First());

    [Fact]
    public void Beside_the_engine_it_was_built_with_the_guard_passes()
    {
        string studio = PostgresStudioDiagnosticsVersionGuard.ReleaseVersion(Studio);

        studio.Should().NotBe("?", "both assemblies carry the version the build stamped");
        PostgresStudioDiagnosticsVersionGuard.ReleaseVersion(Engine).Should().Be(studio);
        PostgresStudioDiagnosticsVersionGuard.InformationalVersion(Engine)
            .Should().Be(PostgresStudioDiagnosticsVersionGuard.InformationalVersion(Studio), "one build stamps both, commit included");

        FluentActions.Invoking(() => PostgresStudioDiagnosticsVersionGuard.Ensure()).Should().NotThrow();
        FluentActions.Invoking(() => new ServiceCollection().AddPostgresStudio()).Should().NotThrow();
    }

    [Fact]
    public void A_missing_engine_is_refused_and_the_central_pin_is_named()
    {
        var missing = new FileNotFoundException("Could not load file or assembly 'PostgresStudio.Diagnostics'.", "PostgresStudio.Diagnostics");
        string required = PostgresStudioDiagnosticsVersionGuard.ReleaseVersion(Studio);

        InvalidOperationException refusal = FluentActions
            .Invoking(() => PostgresStudioDiagnosticsVersionGuard.Ensure(Studio, () => throw missing))
            .Should().Throw<InvalidOperationException>().Which;

        refusal.InnerException.Should().BeSameAs(missing);
        refusal.Message.Should()
            .Contain($"PostgresStudio {required} needs PostgresStudio.Diagnostics {required} exactly")
            .And.Contain("deployed without PostgresStudio.Diagnostics")
            .And.Contain("central PackageVersion for PostgresStudio.Diagnostics")
            .And.Contain("CentralPackageTransitivePinningEnabled")
            .And.Contain($"pin PostgresStudio.Diagnostics to {required}")
            .And.Contain("let PostgresStudio bring it");
    }

    [Fact]
    public void Another_engine_version_is_refused_and_named()
    {
        FluentActions.Invoking(() => PostgresStudioDiagnosticsVersionGuard.Ensure(WithVersion("0.11.0"), () => WithVersion("0.11.1")))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("PostgresStudio 0.11.0 needs PostgresStudio.Diagnostics 0.11.0 exactly, and found PostgresStudio.Diagnostics 0.11.1.")
            .And.Contain("upgraded together")
            .And.Contain("central PackageVersion or a PackageReference for PostgresStudio.Diagnostics must say 0.11.0");
    }

    /// <summary>
    /// The commit after the <c>+</c> is compared when, and only when, both assemblies are Release builds - as every pack
    /// is - and both carry one: the same version packed from two commits is refused. A Debug build compares the release
    /// versions only, because an IDE's incremental build after a commit recompiles PostgresStudio with the new commit and
    /// leaves an unchanged engine on the old one; so does a mixed pair, a missing configuration, and a side with no commit.
    /// </summary>
    [Theory]
    // Release and Release: the commit counts.
    [InlineData("0.11.0+5f1c2a9d", "Release", "0.11.0+5f1c2a9d", "Release", true)]
    [InlineData("0.11.0+5f1c2a9d", "Release", "0.11.0+0b7e3c41", "Release", false)]
    [InlineData("0.11.0+5f1c2a9d", "Release", "0.11.1+5f1c2a9d", "Release", false)]
    [InlineData("0.11.0+5f1c2a9d", "release", "0.11.0+0b7e3c41", "Release", false)]
    // Release and Release, one side or neither with a commit: the version only.
    [InlineData("0.11.0+5f1c2a9d", "Release", "0.11.0", "Release", true)]
    [InlineData("0.11.0", "Release", "0.11.0+0b7e3c41", "Release", true)]
    [InlineData("0.11.0", "Release", "0.11.0", "Release", true)]
    [InlineData("0.11.0", "Release", "0.11.1", "Release", false)]
    // Debug and Debug: two commits are an incremental build, not a skew; another version still is.
    [InlineData("0.11.0+5f1c2a9d", "Debug", "0.11.0+0b7e3c41", "Debug", true)]
    [InlineData("0.11.0+5f1c2a9d", "Debug", "0.11.1+0b7e3c41", "Debug", false)]
    // Mixed, or no configuration at all: the version only.
    [InlineData("0.11.0+5f1c2a9d", "Release", "0.11.0+0b7e3c41", "Debug", true)]
    [InlineData("0.11.0+5f1c2a9d", "Debug", "0.11.0+0b7e3c41", "Release", true)]
    [InlineData("0.11.0+5f1c2a9d", "Release", "0.11.1+5f1c2a9d", "Debug", false)]
    [InlineData("0.11.0+5f1c2a9d", null, "0.11.0+0b7e3c41", "Release", true)]
    [InlineData("0.11.0+5f1c2a9d", null, "0.11.0+0b7e3c41", null, true)]
    public void The_commit_is_compared_only_between_two_release_builds_that_both_carry_one(
        string postgresStudio, string? studioConfiguration, string diagnostics, string? engineConfiguration, bool passes)
    {
        AssemblyBuilder studio = WithVersion(postgresStudio, studioConfiguration);
        AssemblyBuilder engine = WithVersion(diagnostics, engineConfiguration);
        bool bothRelease = PostgresStudioDiagnosticsVersionGuard.IsReleaseBuild(studio) && PostgresStudioDiagnosticsVersionGuard.IsReleaseBuild(engine);

        PostgresStudioDiagnosticsVersionGuard.SameBuild(postgresStudio, diagnostics, bothRelease).Should().Be(passes);

        Action ensure = () => PostgresStudioDiagnosticsVersionGuard.Ensure(studio, () => engine);

        if (passes)
        {
            ensure.Should().NotThrow();
        }
        else
        {
            ensure.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("needs PostgresStudio.Diagnostics");
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
        PostgresStudioDiagnosticsVersionGuard.IsReleaseBuild(WithVersion("0.11.0", configuration)).Should().Be(release);
    }

    [Fact]
    public void Two_commits_of_one_version_are_named_in_full()
    {
        FluentActions.Invoking(() => PostgresStudioDiagnosticsVersionGuard.Ensure(
                WithVersion("0.11.0+5f1c2a9d", "Release"), () => WithVersion("0.11.0+0b7e3c41", "Release")))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("PostgresStudio 0.11.0+5f1c2a9d needs PostgresStudio.Diagnostics 0.11.0+5f1c2a9d exactly, and found PostgresStudio.Diagnostics 0.11.0+0b7e3c41.")
            .And.Contain("must say 0.11.0,");
    }

    [Fact]
    public void Any_other_load_failure_is_not_dressed_up_as_a_version_problem()
    {
        var corrupt = new BadImageFormatException("not an assembly");

        FluentActions.Invoking(() => PostgresStudioDiagnosticsVersionGuard.Ensure(Studio, () => throw corrupt))
            .Should().Throw<BadImageFormatException>().Which.Should().BeSameAs(corrupt);
    }

    [Theory]
    [InlineData("0.11.0+5f1c2a9d", "0.11.0")]
    [InlineData("0.11.0-rc.1+5f1c2a9d", "0.11.0-rc.1")]
    [InlineData("0.11.0", "0.11.0")]
    [InlineData("0.11.0+", "0.11.0")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Only_the_version_before_the_build_metadata_is_compared(string? informational, string expected)
    {
        PostgresStudioDiagnosticsVersionGuard.ReleaseVersion(informational).Should().Be(expected);
    }

    /// <summary>
    /// The public <c>AddPostgresStudio</c> and the guard name no PostgresStudio.Diagnostics type or member - in a call, a
    /// field, a token or a local - so the JIT can compile them, and the guard can explain, when a newer engine whose members
    /// moved is loaded.
    /// </summary>
    [Fact]
    public void Neither_AddPostgresStudio_nor_the_guard_names_an_engine_member()
    {
        MethodBase[] methods =
        [
            AddPostgresStudio,
            .. GuardMethods(),
        ];

        methods.Should().HaveCountGreaterThan(3, "the public method, the guard's checks and its loader are all scanned");
        methods.SelectMany(EngineMembersIn).Should().BeEmpty(
            "an engine member here is resolved when the method is compiled - before the guard can run - and a deployment " +
            "without PostgresStudio.Diagnostics then fails with a bare FileNotFoundException instead of the guard's reason");
    }

    /// <summary>Anti-vacuity: the scan above does see engine members, in the method that is meant to hold them.</summary>
    [Fact]
    public void The_scan_finds_engine_members_where_they_are_meant_to_be()
    {
        MethodInfo services = typeof(PostgresStudioServiceCollectionExtensions).GetMethod("AddPostgresStudioServices", Declared)!;

        services.Should().NotBeNull();
        EngineMembersIn(services).Should().NotBeEmpty("AddPostgresStudioServices registers the engine's seams");
    }

    [Fact]
    public void What_names_the_engine_is_never_inlined_into_what_runs_before_the_guard()
    {
        MethodInfo services = typeof(PostgresStudioServiceCollectionExtensions).GetMethod("AddPostgresStudioServices", Declared)!;
        MethodInfo ensure = typeof(PostgresStudioDiagnosticsVersionGuard).GetMethod(nameof(PostgresStudioDiagnosticsVersionGuard.Ensure), Type.EmptyTypes)!;
        MethodInfo load = typeof(PostgresStudioDiagnosticsVersionGuard).GetMethod("LoadDiagnostics", Declared)!;

        foreach (MethodInfo method in new[] { services, ensure, load })
        {
            method.Should().NotBeNull();
            method.MethodImplementationFlags.HasFlag(MethodImplAttributes.NoInlining).Should().BeTrue(
                $"{method.Name} must be compiled on its own: inlined, its tokens would be resolved with its caller's");
        }
    }

    private static MethodInfo AddPostgresStudio =>
        typeof(PostgresStudioServiceCollectionExtensions).GetMethod(
            nameof(PostgresStudioServiceCollectionExtensions.AddPostgresStudio),
            [typeof(IServiceCollection), typeof(Action<PostgresStudioOptions>)])!;

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
        Type guard = typeof(PostgresStudioDiagnosticsVersionGuard);

        return new[] { guard }
            .Concat(guard.GetNestedTypes(Declared))
            .SelectMany(static type => type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            .Where(static method => method.GetMethodBody() is not null);
    }

    /// <summary>Each engine type or member a method's body names, as <c>opcode member</c> or <c>local type</c>.</summary>
    private static IEnumerable<string> EngineMembersIn(MethodBase method)
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

                if (TypesOf(member).Any(IsEngine))
                {
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

        foreach (LocalVariableInfo local in body.LocalVariables.Where(static x => Expand(x.LocalType).Any(IsEngine)))
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

    private static bool IsEngine(Type type) =>
        !type.IsGenericParameter
        && string.Equals(type.Assembly.GetName().Name, PostgresStudioDiagnosticsVersionGuard.DiagnosticsAssemblyName, StringComparison.Ordinal);
}
