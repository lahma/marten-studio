using System.Reflection;
using System.Reflection.Emit;

using DataStudio.Kafka.Internal;

namespace DataStudio.Kafka.Wolverine.Tests.Conventions;

/// <summary>
/// <c>UseWolverineKafka</c> refuses to wire the adapter to any DataStudio.Kafka.Core but its own build, and says why (D43, D30's discipline). In this test host
/// the two are always the same build, so each failure is driven through the guard's seam - the loader is a parameter - and the property that lets the
/// guard run at all when DataStudio.Kafka.Core is skewed, that neither it nor the public method calling it names an internal member of DataStudio.Kafka.Core before it has
/// passed, is checked on the IL (<c>KafkaCoreVersionGuardTests</c> in DataStudio.Kafka.Tests is this class's model).
/// </summary>
public class KafkaWolverineVersionGuardTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly Adapter = typeof(KafkaWolverineExtensions).Assembly;

    private static readonly Assembly Engine = typeof(KafkaEngineOptions).Assembly;

    private static readonly Dictionary<short, OpCode> ByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(static field => (OpCode) field.GetValue(null)!)
        .GroupBy(static code => code.Value)
        .ToDictionary(static group => group.Key, static group => group.First());

    [Fact]
    public void Beside_the_build_it_was_packed_with_the_guard_passes_and_so_does_the_registration()
    {
        KafkaWolverineVersionGuard.InformationalVersion(Engine).Should().Be(KafkaWolverineVersionGuard.InformationalVersion(Adapter), "one build stamps both");

        FluentActions.Invoking(KafkaWolverineVersionGuard.Ensure).Should().NotThrow();
        FluentActions.Invoking(() => new KafkaEngineOptions().UseWolverineKafka()).Should().NotThrow();
    }

    [Fact]
    public void A_missing_Kafka_Core_is_refused_and_the_central_pin_is_named()
    {
        FileNotFoundException notFound = new("Could not load file or assembly 'DataStudio.Kafka.Core'.", "DataStudio.Kafka.Core");
        string required = KafkaWolverineVersionGuard.ReleaseVersion(KafkaWolverineVersionGuard.InformationalVersion(Adapter));

        InvalidOperationException refusal = FluentActions
            .Invoking(() => KafkaWolverineVersionGuard.Ensure(Adapter, () => throw notFound))
            .Should().Throw<InvalidOperationException>().Which;

        refusal.InnerException.Should().BeSameAs(notFound);
        refusal.Message.Should()
            .Contain($"DataStudio.Kafka.Wolverine {required} needs DataStudio.Kafka.Core {required} exactly")
            .And.Contain("deployed without DataStudio.Kafka.Core")
            .And.Contain($"pin DataStudio.Kafka.Core to {required}");
    }

    [Fact]
    public void Another_version_is_refused_and_named()
    {
        AssemblyBuilder adapter = WithVersion("0.5.0");

        FluentActions.Invoking(() => KafkaWolverineVersionGuard.Ensure(adapter, () => WithVersion("0.5.1")))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("DataStudio.Kafka.Wolverine 0.5.0 needs DataStudio.Kafka.Core 0.5.0 exactly, and found DataStudio.Kafka.Core 0.5.1.")
            .And.Contain("upgraded together");
    }

    /// <summary>The adapter and DataStudio.Kafka.Core are one version, a prerelease of the family included, so a correct install compares equal as it stands.</summary>
    [Theory]
    [InlineData("2.0.0-beta.1", "2.0.0-beta.1")]
    [InlineData("2.0.0-beta.1+5f1c2a9d", "2.0.0-beta.1+5f1c2a9d")]
    public void A_correct_prerelease_install_passes(string adapterVersion, string coreVersion)
    {
        AssemblyBuilder adapter = WithVersion(adapterVersion);

        FluentActions.Invoking(() => KafkaWolverineVersionGuard.Ensure(adapter, () => WithVersion(coreVersion))).Should().NotThrow();
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("2.0.0-beta.2")]
    [InlineData("2.0.1-beta.1")]
    public void A_Kafka_Core_that_is_not_the_adapters_own_build_is_refused(string core)
    {
        AssemblyBuilder adapter = WithVersion("2.0.0-beta.1");

        FluentActions.Invoking(() => KafkaWolverineVersionGuard.Ensure(adapter, () => WithVersion(core)))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain($"DataStudio.Kafka.Wolverine 2.0.0-beta.1 needs DataStudio.Kafka.Core 2.0.0-beta.1 exactly, and found DataStudio.Kafka.Core {core}.");
    }

    [Fact]
    public void Two_commits_of_one_version_are_named_in_full_between_two_release_builds()
    {
        AssemblyBuilder adapter = WithVersion("0.5.0+5f1c2a9d", "Release");

        FluentActions.Invoking(() => KafkaWolverineVersionGuard.Ensure(adapter, () => WithVersion("0.5.0+0b7e3c41", "Release")))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("DataStudio.Kafka.Wolverine 0.5.0+5f1c2a9d needs DataStudio.Kafka.Core 0.5.0+5f1c2a9d exactly, and found DataStudio.Kafka.Core 0.5.0+0b7e3c41.");
    }

    [Theory]
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.0+5f1c2a9d", "Release", true)]
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.0+0b7e3c41", "Release", false)]
    [InlineData("0.5.0+5f1c2a9d", "Debug", "0.5.0+0b7e3c41", "Debug", true)]
    [InlineData("0.5.0+5f1c2a9d", "Debug", "0.5.1+0b7e3c41", "Debug", false)]
    [InlineData("0.5.0+5f1c2a9d", "Release", "0.5.0+0b7e3c41", "Debug", true)]
    public void The_commit_is_compared_only_between_two_release_builds_that_both_carry_one(
        string adapter, string adapterConfiguration, string other, string otherConfiguration, bool passes)
    {
        bool bothRelease = KafkaWolverineVersionGuard.IsReleaseBuild(WithVersion(adapter, adapterConfiguration))
            && KafkaWolverineVersionGuard.IsReleaseBuild(WithVersion(other, otherConfiguration));

        KafkaWolverineVersionGuard.SameBuild(adapter, other, bothRelease).Should().Be(passes);
    }

    [Fact]
    public void Any_other_load_failure_is_not_dressed_up_as_a_version_problem()
    {
        BadImageFormatException corrupt = new("not an assembly");

        FluentActions.Invoking(() => KafkaWolverineVersionGuard.Ensure(Adapter, () => throw corrupt))
            .Should().Throw<BadImageFormatException>().Which.Should().BeSameAs(corrupt);
    }

    /// <summary>The public <c>UseWolverineKafka</c> and the guard name no internal member of DataStudio.Kafka.Core, so the JIT can compile them when it is skewed.</summary>
    [Fact]
    public void Neither_UseWolverineKafka_nor_the_guard_names_an_internal_engine_member()
    {
        MethodBase[] methods = [UseWolverineKafka, .. GuardMethods()];

        methods.Should().HaveCountGreaterThan(3, "the public method, the guard's checks and its loader are all scanned");
        methods.SelectMany(ForbiddenMembersIn).Should().BeEmpty(
            "an internal DataStudio.Kafka.Core member here is resolved when the method is compiled - before the guard can run - and a skewed deployment "
            + "then fails with a MissingMethodException instead of the guard's reason");
    }

    /// <summary>Anti-vacuity: the scan does see internal members, in the method that is meant to hold them.</summary>
    [Fact]
    public void The_scan_finds_the_internal_members_where_they_are_meant_to_be()
    {
        MethodInfo addSource = typeof(KafkaWolverineExtensions).GetMethod("AddSource", Declared)!;

        addSource.Should().NotBeNull();
        ForbiddenMembersIn(addSource).Should().Contain(static x => x.Contains("ClusterSources", StringComparison.Ordinal));
    }

    [Fact]
    public void What_names_the_internal_members_is_never_inlined_into_what_runs_before_the_guard()
    {
        MethodInfo addSource = typeof(KafkaWolverineExtensions).GetMethod("AddSource", Declared)!;
        MethodInfo ensure = typeof(KafkaWolverineVersionGuard).GetMethod(nameof(KafkaWolverineVersionGuard.Ensure), Type.EmptyTypes)!;
        MethodInfo load = typeof(KafkaWolverineVersionGuard).GetMethod("LoadEngine", Declared)!;

        foreach (MethodInfo method in new[] { addSource, ensure, load })
        {
            method.MethodImplementationFlags.HasFlag(MethodImplAttributes.NoInlining).Should().BeTrue(
                $"{method.Name} must be compiled on its own: inlined, its tokens would be resolved with its caller's");
        }
    }

    private static MethodInfo UseWolverineKafka =>
        typeof(KafkaWolverineExtensions).GetMethod(nameof(KafkaWolverineExtensions.UseWolverineKafka))!;

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
        Type guard = typeof(KafkaWolverineVersionGuard);

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

                if (IsInternalEngineMember(member) || TypesOf(member).Any(IsInternalEngineType))
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

        foreach (LocalVariableInfo local in body.LocalVariables.Where(static x => Expand(x.LocalType).Any(IsInternalEngineType)))
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

    /// <summary>A non-public type of DataStudio.Kafka.Core, or one nested in one.</summary>
    private static bool IsInternalEngineType(Type type) =>
        !type.IsGenericParameter && type.Assembly == Engine && !type.IsVisible;

    /// <summary>A member of DataStudio.Kafka.Core that is not public: <c>KafkaEngineOptions</c> is a public type with an internal property, and the guard must not read it either.</summary>
    private static bool IsInternalEngineMember(MemberInfo member) =>
        member.DeclaringType?.Assembly == Engine
        && member switch
        {
            MethodBase method => !method.IsPublic,
            FieldInfo field => !field.IsPublic,
            _ => false,
        };
}
