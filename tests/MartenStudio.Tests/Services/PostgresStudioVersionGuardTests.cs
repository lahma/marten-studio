using System.Reflection;
using System.Reflection.Emit;

using MartenStudio.Internal;

using Microsoft.Extensions.DependencyInjection;

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

        FluentActions.Invoking(() => PostgresStudioVersionGuard.Ensure()).Should().NotThrow();
        FluentActions.Invoking(() => new ServiceCollection().AddMartenStudio()).Should().NotThrow();
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
        // Any assembly that is not this build will do: CoreLib's informational version is the runtime's.
        Assembly other = typeof(object).Assembly;
        string found = PostgresStudioVersionGuard.ReleaseVersion(other);
        string required = PostgresStudioVersionGuard.ReleaseVersion(Marten);

        found.Should().NotBe(required);

        FluentActions.Invoking(() => PostgresStudioVersionGuard.Ensure(Marten, () => other))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"MartenStudio {required} needs PostgresStudio {required} exactly, and found PostgresStudio {found}.")
            .And.Contain("upgraded together")
            .And.Contain("central PackageVersion");
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
    /// The public <c>AddMartenStudio</c> and the guard name no PostgresStudio type or member - in a call, a field, a
    /// token or a local - so the JIT can compile both, and the guard can explain, when PostgresStudio.dll is absent.
    /// </summary>
    [Fact]
    public void Neither_AddMartenStudio_nor_the_guard_names_a_PostgresStudio_member()
    {
        MethodBase[] methods =
        [
            AddMartenStudio,
            .. GuardMethods(),
        ];

        methods.Should().HaveCountGreaterThan(3, "the public method, the guard's checks and its loader are all scanned");
        methods.SelectMany(PostgresStudioMembersIn).Should().BeEmpty(
            "a PostgresStudio member here is resolved when the method is compiled - before the guard can run - and a " +
            "deployment without PostgresStudio then fails with a bare FileNotFoundException instead of the guard's reason");
    }

    /// <summary>Anti-vacuity: the scan above does see PostgresStudio members, in the method that is meant to hold them.</summary>
    [Fact]
    public void The_scan_finds_PostgresStudio_members_where_they_are_meant_to_be()
    {
        MethodInfo services = typeof(MartenStudioServiceCollectionExtensions).GetMethod("AddMartenStudioServices", Declared)!;

        services.Should().NotBeNull();
        PostgresStudioMembersIn(services).Should().Contain(static x => x.Contains(nameof(StudioCoreRegistration), StringComparison.Ordinal));
    }

    [Fact]
    public void What_names_PostgresStudio_is_never_inlined_into_what_runs_before_the_guard()
    {
        MethodInfo services = typeof(MartenStudioServiceCollectionExtensions).GetMethod("AddMartenStudioServices", Declared)!;
        MethodInfo ensure = typeof(PostgresStudioVersionGuard).GetMethod(nameof(PostgresStudioVersionGuard.Ensure), Type.EmptyTypes)!;
        MethodInfo load = typeof(PostgresStudioVersionGuard).GetMethod("LoadPostgresStudio", Declared)!;

        foreach (MethodInfo method in new[] { services, ensure, load })
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
                    yield return $"{method.Name}: {code.Name} {member.DeclaringType?.Name}.{member.Name}";
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
