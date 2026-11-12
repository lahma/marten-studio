using System.Reflection;
using System.Reflection.Emit;

using KStudio.Internal;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio;

namespace KStudio.Tests.Registration;

/// <summary>
/// <c>AddKafkaStudio</c> refuses to start beside any PostgresStudio, PostgresStudio.Diagnostics or KStudio.Core but its own build, and says why. In
/// this test host all of them are always present and the same build, so each failure is driven through the guard's seam - the loaders are
/// parameters - and the property that lets the guard run at all when an assembly is missing, that neither it nor the public method calling it
/// names a PostgresStudio or KStudio.Core member, is checked on the IL (MartenStudio's <c>PostgresStudioVersionGuardTests</c> is this class's model).
/// </summary>
public class KStudioVersionGuardTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly Module = typeof(KafkaStudioOptions).Assembly;

    private static readonly Assembly Shared = typeof(PostgresStudioOptions).Assembly;

    private static readonly Assembly Engine = typeof(PostgresDiagnosticsOptions).Assembly;

    private static readonly Assembly Kafka = typeof(KafkaEngineOptions).Assembly;

    private static readonly Dictionary<short, OpCode> ByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(static field => (OpCode) field.GetValue(null)!)
        .GroupBy(static code => code.Value)
        .ToDictionary(static group => group.Key, static group => group.First());

    [Fact]
    public void Beside_the_builds_it_was_packed_with_the_guard_passes()
    {
        string required = KStudioVersionGuard.ReleaseVersion(Module);

        required.Should().NotBe("?");
        KStudioVersionGuard.InformationalVersion(Shared).Should().Be(KStudioVersionGuard.InformationalVersion(Module), "one build stamps all four");
        KStudioVersionGuard.InformationalVersion(Engine).Should().Be(KStudioVersionGuard.InformationalVersion(Module));
        KStudioVersionGuard.InformationalVersion(Kafka).Should().Be(KStudioVersionGuard.InformationalVersion(Module));

        FluentActions.Invoking(KStudioVersionGuard.Ensure).Should().NotThrow();
        FluentActions.Invoking(() => new ServiceCollection().AddKafkaStudio()).Should().NotThrow();
    }

    [Theory]
    [InlineData("PostgresStudio", "KStudio")]
    [InlineData("PostgresStudio.Diagnostics", "PostgresStudio")]
    [InlineData("KStudio.Core", "KStudio")]
    public void A_missing_assembly_is_refused_and_the_central_pin_is_named(string missing, string bringer)
    {
        FileNotFoundException notFound = new("Could not load file or assembly '" + missing + "'.", missing);
        string required = KStudioVersionGuard.ReleaseVersion(Module);

        InvalidOperationException refusal = FluentActions
            .Invoking(() => KStudioVersionGuard.Ensure(
                Module,
                () => missing == "PostgresStudio" ? throw notFound : Shared,
                () => missing == "PostgresStudio.Diagnostics" ? throw notFound : Engine,
                () => missing == "KStudio.Core" ? throw notFound : Kafka))
            .Should().Throw<InvalidOperationException>().Which;

        refusal.InnerException.Should().BeSameAs(notFound);
        refusal.Message.Should()
            .Contain($"KStudio {required} needs {missing} {required} exactly")
            .And.Contain("deployed without " + missing)
            .And.Contain("central PackageVersion for " + missing)
            .And.Contain("CentralPackageTransitivePinningEnabled")
            .And.Contain($"pin {missing} to {required}")
            .And.Contain("let " + bringer + " bring it");
    }

    [Theory]
    [InlineData("PostgresStudio")]
    [InlineData("PostgresStudio.Diagnostics")]
    [InlineData("KStudio.Core")]
    public void Another_version_is_refused_and_named(string other)
    {
        AssemblyBuilder module = WithVersion("0.5.0");

        FluentActions.Invoking(() => KStudioVersionGuard.Ensure(
                module,
                () => other == "PostgresStudio" ? WithVersion("0.5.1") : module,
                () => other == "PostgresStudio.Diagnostics" ? WithVersion("0.5.1") : module,
                () => other == "KStudio.Core" ? WithVersion("0.5.1") : module))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"KStudio 0.5.0 needs {other} 0.5.0 exactly, and found {other} 0.5.1.")
            .And.Contain("upgraded together")
            .And.Contain($"for {other} must say 0.5.0");
    }

    [Fact]
    public void Two_commits_of_one_version_are_named_in_full_between_two_release_builds()
    {
        AssemblyBuilder module = WithVersion("0.5.0+5f1c2a9d", "Release");

        FluentActions.Invoking(() => KStudioVersionGuard.Ensure(module, () => module, () => module, () => WithVersion("0.5.0+0b7e3c41", "Release")))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("KStudio 0.5.0+5f1c2a9d needs KStudio.Core 0.5.0+5f1c2a9d exactly, and found KStudio.Core 0.5.0+0b7e3c41.");
    }

    [Theory]
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.0+5f1c2a9d", "Release", true)]
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.0+0b7e3c41", "Release", false)]
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.0", "Release", true)]
    [InlineData("0.5.0", "Release", "0.5.1", "Release", false)]
    [InlineData("0.5.0+5f1c2a9d", "Debug", "0.5.0+0b7e3c41", "Debug", true)]
    [InlineData("0.5.0+5f1c2a9d", "Debug", "0.5.1+0b7e3c41", "Debug", false)]
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.0+0b7e3c41", "Debug", true)]
    [InlineData("0.5.0+5f1c2a9d", null, "0.5.0+0b7e3c41", null, true)]
    public void The_commit_is_compared_only_between_two_release_builds_that_both_carry_one(
        string kStudio, string? kStudioConfiguration, string other, string? otherConfiguration, bool passes)
    {
        AssemblyBuilder module = WithVersion(kStudio, kStudioConfiguration);
        AssemblyBuilder loaded = WithVersion(other, otherConfiguration);
        bool bothRelease = KStudioVersionGuard.IsReleaseBuild(module) && KStudioVersionGuard.IsReleaseBuild(loaded);

        KStudioVersionGuard.SameBuild(kStudio, other, bothRelease).Should().Be(passes);
    }

    [Fact]
    public void Any_other_load_failure_is_not_dressed_up_as_a_version_problem()
    {
        BadImageFormatException corrupt = new("not an assembly");

        FluentActions.Invoking(() => KStudioVersionGuard.Ensure(Module, () => throw corrupt, () => Engine, () => Kafka))
            .Should().Throw<BadImageFormatException>().Which.Should().BeSameAs(corrupt);
    }

    [Theory]
    [InlineData("0.5.0+5f1c2a9d", "0.5.0")]
    [InlineData("0.5.0-rc.1+5f1c2a9d", "0.5.0-rc.1")]
    [InlineData("0.5.0", "0.5.0")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Only_the_version_before_the_build_metadata_is_compared(string? informational, string expected)
    {
        KStudioVersionGuard.ReleaseVersion(informational).Should().Be(expected);
    }

    /// <summary>The public <c>AddKafkaStudio</c> and the guard name no PostgresStudio or KStudio.Core type or member, so the JIT can compile them when either is absent.</summary>
    [Fact]
    public void Neither_AddKafkaStudio_nor_the_guard_names_a_PostgresStudio_or_engine_member()
    {
        MethodBase[] methods = [AddKafkaStudio, .. GuardMethods()];

        methods.Should().HaveCountGreaterThan(4, "the public method, the guard's checks and its three loaders are all scanned");
        methods.SelectMany(ForbiddenMembersIn).Should().BeEmpty(
            "a PostgresStudio or KStudio.Core member here is resolved when the method is compiled - before the guard can run - and a deployment "
            + "without it then fails with a bare FileNotFoundException instead of the guard's reason");
    }

    /// <summary>Anti-vacuity: the scan does see these members, in the method that is meant to hold them.</summary>
    [Fact]
    public void The_scan_finds_the_members_where_they_are_meant_to_be()
    {
        MethodInfo services = typeof(KafkaStudioServiceCollectionExtensions).GetMethod("AddKafkaStudioServices", Declared)!;

        services.Should().NotBeNull();
        ForbiddenMembersIn(services).Should().Contain(static x => x.Contains("KStudioEngineRegistration", StringComparison.Ordinal));
        ForbiddenMembersIn(services).Should().Contain(static x => x.Contains("IStudioModule", StringComparison.Ordinal));
    }

    [Fact]
    public void What_names_the_other_assemblies_is_never_inlined_into_what_runs_before_the_guard()
    {
        MethodInfo services = typeof(KafkaStudioServiceCollectionExtensions).GetMethod("AddKafkaStudioServices", Declared)!;
        MethodInfo ensure = typeof(KStudioVersionGuard).GetMethod(nameof(KStudioVersionGuard.Ensure), Type.EmptyTypes)!;

        foreach (MethodInfo method in new[]
        {
            services,
            ensure,
            typeof(KStudioVersionGuard).GetMethod("LoadPostgresStudio", Declared)!,
            typeof(KStudioVersionGuard).GetMethod("LoadDiagnostics", Declared)!,
            typeof(KStudioVersionGuard).GetMethod("LoadEngine", Declared)!,
        })
        {
            method.MethodImplementationFlags.HasFlag(MethodImplAttributes.NoInlining).Should().BeTrue(
                $"{method.Name} must be compiled on its own: inlined, its tokens would be resolved with its caller's");
        }
    }

    private static MethodInfo AddKafkaStudio =>
        typeof(KafkaStudioServiceCollectionExtensions).GetMethod(
            nameof(KafkaStudioServiceCollectionExtensions.AddKafkaStudio),
            [typeof(IServiceCollection), typeof(Action<KafkaStudioOptions>)])!;

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

    private static IEnumerable<MethodBase> GuardMethods()
    {
        Type guard = typeof(KStudioVersionGuard);

        return new[] { guard }
            .Concat(guard.GetNestedTypes(Declared))
            .SelectMany(static type => type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            .Where(static method => method.GetMethodBody() is not null);
    }

    private static IEnumerable<string> ForbiddenMembersIn(MethodBase method)
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

                if (TypesOf(member).Any(IsForbidden))
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

        foreach (LocalVariableInfo local in body.LocalVariables.Where(static x => Expand(x.LocalType).Any(IsForbidden)))
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

    /// <summary>A type of PostgresStudio, PostgresStudio.Diagnostics or KStudio.Core: the three assemblies the guard checks.</summary>
    private static bool IsForbidden(Type type) =>
        !type.IsGenericParameter
        && type.Assembly.GetName().Name is KStudioVersionGuard.PostgresStudioAssemblyName
            or KStudioVersionGuard.DiagnosticsAssemblyName
            or KStudioVersionGuard.EngineAssemblyName;
}
