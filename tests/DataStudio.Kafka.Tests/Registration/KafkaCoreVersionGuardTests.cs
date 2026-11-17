using System.Reflection;
using System.Reflection.Emit;

using DataStudio.Kafka.Internal;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Tests.Registration;

/// <summary>
/// <c>AddKafka</c> refuses to start beside any DataStudio (the shell) or DataStudio.Kafka.Core but its own build, and says why. In
/// this test host all of them are always present and the same build, so each failure is driven through the guard's seam - the loaders are
/// parameters - and the property that lets the guard run at all when an assembly is missing, that neither it nor the public method calling it
/// names a DataStudio or DataStudio.Kafka.Core member, is checked on the IL (the Marten module's <c>PostgresVersionGuardTests</c> is this class's model).
/// </summary>
public class KafkaCoreVersionGuardTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly Module = typeof(KafkaOptions).Assembly;

    private static readonly Assembly Shell = typeof(DataStudio.Components.StudioRoutes).Assembly;

    private static readonly Assembly Kafka = typeof(KafkaEngineOptions).Assembly;

    private static readonly Dictionary<short, OpCode> ByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(static field => (OpCode) field.GetValue(null)!)
        .GroupBy(static code => code.Value)
        .ToDictionary(static group => group.Key, static group => group.First());

    [Fact]
    public void Beside_the_builds_it_was_packed_with_the_guard_passes()
    {
        string required = KafkaCoreVersionGuard.ReleaseVersion(Module);

        required.Should().NotBe("?");

        // One version for the family: the shell and the engine carry exactly the Kafka module's own informational version.
        KafkaCoreVersionGuard.InformationalVersion(Shell).Should().Be(KafkaCoreVersionGuard.InformationalVersion(Module), "the shell beside the Kafka module is the family's version");
        KafkaCoreVersionGuard.InformationalVersion(Kafka).Should().Be(KafkaCoreVersionGuard.InformationalVersion(Module), "DataStudio.Kafka.Core is the Kafka module's own build");

        FluentActions.Invoking(KafkaCoreVersionGuard.Ensure).Should().NotThrow();
        FluentActions.Invoking(() => new ServiceCollection().AddDataStudio().AddKafka()).Should().NotThrow();
    }

    [Theory]
    [InlineData("DataStudio", "DataStudio.Kafka")]
    [InlineData("DataStudio.Kafka.Core", "DataStudio.Kafka")]
    public void A_missing_assembly_is_refused_and_the_central_pin_is_named(string missing, string bringer)
    {
        FileNotFoundException notFound = new("Could not load file or assembly '" + missing + "'.", missing);
        string self = KafkaCoreVersionGuard.ReleaseVersion(Module);
        string required = self;

        InvalidOperationException refusal = FluentActions
            .Invoking(() => KafkaCoreVersionGuard.Ensure(
                Module,
                () => missing == "DataStudio" ? throw notFound : Shell,
                () => missing == "DataStudio.Kafka.Core" ? throw notFound : Kafka))
            .Should().Throw<InvalidOperationException>().Which;

        refusal.InnerException.Should().BeSameAs(notFound);
        refusal.Message.Should()
            .Contain($"DataStudio.Kafka {self} needs {missing} {required} exactly")
            .And.Contain("deployed without " + missing)
            .And.Contain("central PackageVersion for " + missing)
            .And.Contain("CentralPackageTransitivePinningEnabled")
            .And.Contain($"pin {missing} to {required}")
            .And.Contain("let " + bringer + " bring it");
    }

    [Theory]
    [InlineData("DataStudio")]
    [InlineData("DataStudio.Kafka.Core")]
    public void Another_version_is_refused_and_named(string other)
    {
        AssemblyBuilder module = WithVersion("0.5.0");

        FluentActions.Invoking(() => KafkaCoreVersionGuard.Ensure(
                module,
                () => other == "DataStudio" ? WithVersion("0.5.1") : module,
                () => other == "DataStudio.Kafka.Core" ? WithVersion("0.5.1") : module))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"DataStudio.Kafka 0.5.0 needs {other} 0.5.0 exactly, and found {other} 0.5.1.")
            .And.Contain("upgraded together")
            .And.Contain($"for {other} must say 0.5.0");
    }

    [Fact]
    public void Two_commits_of_one_version_are_named_in_full_between_two_release_builds()
    {
        AssemblyBuilder module = WithVersion("0.5.0+5f1c2a9d", "Release");

        FluentActions.Invoking(() => KafkaCoreVersionGuard.Ensure(module, () => module, () => WithVersion("0.5.0+0b7e3c41", "Release")))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("DataStudio.Kafka 0.5.0+5f1c2a9d needs DataStudio.Kafka.Core 0.5.0+5f1c2a9d exactly, and found DataStudio.Kafka.Core 0.5.0+0b7e3c41.");
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
        string kafka, string? kafkaConfiguration, string other, string? otherConfiguration, bool passes)
    {
        AssemblyBuilder module = WithVersion(kafka, kafkaConfiguration);
        AssemblyBuilder loaded = WithVersion(other, otherConfiguration);
        bool bothRelease = KafkaCoreVersionGuard.IsReleaseBuild(module) && KafkaCoreVersionGuard.IsReleaseBuild(loaded);

        KafkaCoreVersionGuard.SameBuild(kafka, other, bothRelease).Should().Be(passes);
    }

    /// <summary>A prerelease of the family (2.0.0-beta.1) is one version for all seven packages: the three carry the same suffix as the four.</summary>
    [Theory]
    [InlineData("2.0.0-beta.1")]
    [InlineData("2.0.0-beta.1+5f1c2a9d")]
    [InlineData("2.0.0")]
    public void A_prerelease_family_at_one_version_passes(string version)
    {
        AssemblyBuilder module = WithVersion(version, "Release");

        FluentActions.Invoking(() => KafkaCoreVersionGuard.Ensure(module, () => WithVersion(version, "Release"), () => WithVersion(version, "Release")))
            .Should().NotThrow();
    }

    [Theory]
    [InlineData("DataStudio", "2.0.0-beta.2")]
    [InlineData("DataStudio", "2.0.0")]
    [InlineData("DataStudio.Kafka.Core", "2.0.0-beta.2")]
    [InlineData("DataStudio.Kafka.Core", "2.0.1-beta.1")]
    public void A_prerelease_family_with_another_suffix_or_version_is_refused(string other, string found)
    {
        AssemblyBuilder module = WithVersion("2.0.0-beta.1", "Release");

        FluentActions.Invoking(() => KafkaCoreVersionGuard.Ensure(
                module,
                () => other == "DataStudio" ? WithVersion(found) : module,
                () => other == "DataStudio.Kafka.Core" ? WithVersion(found) : module))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"DataStudio.Kafka 2.0.0-beta.1 needs {other} 2.0.0-beta.1 exactly, and found {other} {found}.")
            .And.Contain($"for {other} must say 2.0.0-beta.1");
    }

    [Fact]
    public void Any_other_load_failure_is_not_dressed_up_as_a_version_problem()
    {
        BadImageFormatException corrupt = new("not an assembly");

        FluentActions.Invoking(() => KafkaCoreVersionGuard.Ensure(Module, () => throw corrupt, () => Kafka))
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
        KafkaCoreVersionGuard.ReleaseVersion(informational).Should().Be(expected);
    }

    /// <summary>The public <c>AddKafka</c> and the guard name no DataStudio or DataStudio.Kafka.Core type or member (but the shell's <c>DataStudioBuilder</c>, its own first parameter, which the body only hands on), so the JIT can compile them when either is absent.</summary>
    [Fact]
    public void Neither_AddKafka_nor_the_guard_names_a_shell_or_engine_member()
    {
        MethodBase[] methods = [AddKafka, .. GuardMethods()];

        methods.Should().HaveCountGreaterThan(4, "the public method, the guard's checks and its two loaders are all scanned");
        methods.SelectMany(ForbiddenMembersIn).Should().BeEmpty(
            "a DataStudio or DataStudio.Kafka.Core member here is resolved when the method is compiled - before the guard can run - and a deployment "
            + "without it then fails with a bare FileNotFoundException instead of the guard's reason");
    }

    /// <summary>Anti-vacuity: the scan does see these members, in the method that is meant to hold them.</summary>
    [Fact]
    public void The_scan_finds_the_members_where_they_are_meant_to_be()
    {
        MethodInfo services = typeof(KafkaDataStudioBuilderExtensions).GetMethod("AddKafkaServices", Declared)!;

        services.Should().NotBeNull();
        ForbiddenMembersIn(services).Should().Contain(static x => x.Contains("KafkaEngineRegistration", StringComparison.Ordinal));
        ForbiddenMembersIn(services).Should().Contain(static x => x.Contains("IStudioModule", StringComparison.Ordinal));
    }

    [Fact]
    public void What_names_the_other_assemblies_is_never_inlined_into_what_runs_before_the_guard()
    {
        MethodInfo services = typeof(KafkaDataStudioBuilderExtensions).GetMethod("AddKafkaServices", Declared)!;
        MethodInfo ensure = typeof(KafkaCoreVersionGuard).GetMethod(nameof(KafkaCoreVersionGuard.Ensure), Type.EmptyTypes)!;

        foreach (MethodInfo method in new[]
        {
            services,
            ensure,
            typeof(KafkaCoreVersionGuard).GetMethod("LoadShell", Declared)!,
            typeof(KafkaCoreVersionGuard).GetMethod("LoadEngine", Declared)!,
        })
        {
            method.MethodImplementationFlags.HasFlag(MethodImplAttributes.NoInlining).Should().BeTrue(
                $"{method.Name} must be compiled on its own: inlined, its tokens would be resolved with its caller's");
        }
    }

    private static MethodInfo AddKafka =>
        typeof(KafkaDataStudioBuilderExtensions).GetMethod(
            nameof(KafkaDataStudioBuilderExtensions.AddKafka),
            [typeof(DataStudioBuilder), typeof(Action<KafkaOptions>)])!;

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
        Type guard = typeof(KafkaCoreVersionGuard);

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

    /// <summary>A type of DataStudio or DataStudio.Kafka.Core: the two assemblies the guard checks.</summary>
    private static bool IsForbidden(Type type) =>
        !type.IsGenericParameter
        && type != typeof(DataStudioBuilder)
        && type.Assembly.GetName().Name is KafkaCoreVersionGuard.ShellAssemblyName
            or KafkaCoreVersionGuard.EngineAssemblyName;
}
