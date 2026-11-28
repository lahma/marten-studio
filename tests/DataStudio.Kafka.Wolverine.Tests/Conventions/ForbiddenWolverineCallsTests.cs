using System.Reflection;
using System.Reflection.Emit;

using DataStudio.Kafka.Internal;

using Wolverine.Kafka;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime;

namespace DataStudio.Kafka.Wolverine.Tests.Conventions;

/// <summary>
/// D43, decision 5.9 at the IL, the analogue of DataStudio.Kafka.Tests' <c>ForbiddenConfluentCallsTests</c>: DataStudio.Kafka.Wolverine calls nothing of Wolverine's that
/// connects, creates, completes or inherits configuration, indexes the topic cache (which creates an endpoint), reads a message store, an envelope
/// table, a node table or a runtime agent, or reflects on anything but the three members of the tenant probe. The compiled methods are read
/// (<c>GetMethodBody()</c>, then <c>Module.ResolveMember</c> on each <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c>, <c>ldvirtftn</c> and
/// <c>ldtoken</c>), so a call in a lambda, a closure or an async state machine is found and a comment that mentions a call is not. A token that cannot
/// be resolved is counted and the test fails on one, never skips it.
/// </summary>
public class ForbiddenWolverineCallsTests
{
    private static readonly Assembly Adapter = typeof(KafkaWolverineExtensions).Assembly;

    /// <summary>Methods of Wolverine's that start something, connect, create a client, complete configuration, or write it.</summary>
    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.Ordinal)
    {
        "GetEffectiveConsumerConfig", "GetEffectiveProducerConfig",
        "CreateConsumer", "CreateProducer", "CreateAdminClient",
        "ConnectAsync", "InitializeAsync", "SetupAsync", "SetupOnAsync", "CheckAsync", "TeardownAsync", "BuildListenerAsync", "Compile", "ConfigureClient",
        "EnsureEnvelopeMapper", "BuildMapper", "StartSending", "BuildHealthCheck",

        // The builder callbacks and the internal collections: read nowhere, run nowhere.
        "get_ConfigureConsumerBuilders", "get_ConfigureProducerBuilders", "get_ConfigureAdminClientBuilders",
        "set_ConfigureConsumerBuilders", "set_ConfigureProducerBuilders", "set_ConfigureAdminClientBuilders",
        "get_TopicGroups", "get_Tenants", "get_CreateTopicFunc",
    };

    /// <summary>The cache's members that create or write an entry: the indexer, and the fill and set operations behind it.</summary>
    private static readonly HashSet<string> CacheWrites = new(StringComparer.Ordinal)
    {
        "get_Item", "set_Item", "FillDefault", "Fill", "Fill`1", "Store", "Remove", "Clear",
    };

    /// <summary>Namespaces and types of Wolverine's that hold a message store, an envelope table, a node record or a runtime agent.</summary>
    private static readonly string[] ForbiddenNamespaces =
    [
        "Wolverine.Persistence", "Wolverine.RDBMS", "Wolverine.Runtime.Agents", "Wolverine.Runtime.Handlers", "Wolverine.Logging",
    ];

    private static readonly string[] ForbiddenTypeNames =
    [
        "Wolverine.Runtime.IWolverineRuntime", "Wolverine.Runtime.WolverineRuntime", "Wolverine.Persistence.IMessageStore",
        "Wolverine.Persistence.Durability.IMessageStore",
    ];

    private static readonly HashSet<string> ReflectionLookups = new(StringComparer.Ordinal)
    {
        "GetProperty", "GetProperties", "GetField", "GetFields", "GetMethod", "GetMethods", "GetMember", "GetMembers", "GetConstructor", "GetConstructors",
        "GetEvent", "GetEvents", "GetNestedType", "GetNestedTypes", "GetInterface",
    };

    private static readonly HashSet<string> ReflectionInvocations = new(StringComparer.Ordinal)
    {
        "Invoke", "InvokeMember", "DynamicInvoke", "CreateDelegate", "CreateInstance", "SetValue", "Compile",
    };

    private static readonly string[] AllowedReflectedNames = ["Tenants", "TenantId", "Transport"];

    /// <summary>
    /// The three properties of Wolverine's Kafka transport whose types the shape check reads (Wolverine 7 changed them, #103). They are allowed to be looked up
    /// by name in exactly one method, <c>WolverineClusterSource.HasConfluentShape</c>, and only as a <c>GetProperty</c> lookup whose <c>PropertyType</c> is read.
    /// </summary>
    private static readonly string[] ShapeCheckNames = ["ConsumerConfig", "ProducerConfig", "AdminClientConfig"];

    /// <summary>The only reflective members the shape check may call: a lookup, the type of what it found, and the comparison of two types.</summary>
    private static readonly HashSet<string> ShapeCheckMembers = new(StringComparer.Ordinal)
    {
        "System.Type::GetProperty", "System.Reflection.PropertyInfo::get_PropertyType", "System.Type::GetTypeFromHandle", "System.Type::op_Equality",
    };

    private static bool IsShapeCheck(MethodBase caller) =>
        caller.DeclaringType == typeof(WolverineClusterSource) && caller.Name == nameof(WolverineClusterSource.HasConfluentShape);

    private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodes();

    [Fact]
    public void Kafka_Wolverine_calls_nothing_that_connects_creates_completes_or_writes()
    {
        List<string> found = [.. Offenders(Adapter.GetTypes())];

        found.Should().BeEmpty("D43: discovery starts, connects and writes nothing. Found: " + string.Join("; ", found));
    }

    [Fact]
    public void No_method_token_of_the_adapter_goes_unresolved()
    {
        List<string> unresolved = [];

        _ = Calls(Adapter.GetTypes(), unresolved).ToList();

        unresolved.Should().BeEmpty("a token the scan could not resolve is a call it did not see");
    }

    [Fact]
    public void Reflection_is_used_for_exactly_the_three_members_of_the_tenant_probe_and_the_shape_checks_one_lookup_of_three_types()
    {
        List<string> found = [.. ReflectionOffences(Adapter.GetTypes(), IsShapeCheck)];

        found.Should().BeEmpty("hard rule 17's reasoning: a forbidden member could be reached by a name the scan above never sees. Found: " + string.Join("; ", found));
    }

    [Fact]
    public void The_shape_check_looks_up_exactly_three_properties_by_literal_name_and_only_reads_their_types()
    {
        (MethodBase Caller, MemberInfo Member, string[] Strings)[] calls =
            [.. CallsWithStrings(Adapter.GetTypes()).Where(static x => IsShapeCheck(x.Caller))];

        // Anti-vacuity: the check is found, and it looks the three properties up and reads their types.
        calls.Where(static x => x.Member.Name == "GetProperty").Select(static x => x.Strings.SingleOrDefault()).Should().Equal(ShapeCheckNames);
        calls.Should().Contain(static x => x.Member.Name == "get_PropertyType");

        // Nothing else of reflection: no value read, no method, no invocation.
        List<string> reflective = [.. calls.Select(static x => x.Member).Where(IsReflective).Select(static x => x.DeclaringType!.FullName + "::" + x.Name).Distinct()];

        reflective.Should().BeSubsetOf(ShapeCheckMembers, "the shape check reads the types of three properties and never a value, a method or an invocation");
    }

    [Fact]
    public void The_scan_finds_the_shape_names_looked_up_anywhere_else_and_any_other_reflection_in_the_shape_check()
    {
        // The three names are allowed only in HasConfluentShape: the same lookup in another method is an offence.
        ReflectionOffences([typeof(ShapeNamesElsewhere)], IsShapeCheck).Should().ContainSingle().Which.Should().Contain(nameof(ShapeNamesElsewhere));

        // And in the shape check itself, a call of reflection beyond the allowed four is an offence (the fixture stands in for it).
        ReflectionOffences([typeof(ShapeCheckStandIn)], static caller => caller.DeclaringType == typeof(ShapeCheckStandIn))
            .Should().ContainSingle().Which.Should().Contain("GetValue");
    }

    [Fact]
    public void The_scan_reads_the_methods_it_is_meant_to_read()
    {
        // Anti-vacuity: it sees the reads the adapter really makes, and the reflection of the probe.
        List<string> calls = [.. Calls(Adapter.GetTypes(), []).Select(static x => x.DeclaringType?.Name + "::" + x.Name)];

        calls.Should().Contain("KafkaTransport::get_Topics");
        calls.Should().Contain("Endpoint::get_Subscriptions");
        calls.Should().Contain("Endpoint::get_IsListener");
        calls.Should().Contain("KafkaTopicGroup::get_TopicNames");
        calls.Should().Contain("KafkaTransport::get_DeadLetterQueueTopicName");
        calls.Should().Contain("WolverineOptions::get_Transports");
        calls.Should().Contain("Type::GetProperty");
        calls.Should().Contain("PropertyInfo::GetValue");
    }

    [Fact]
    public void The_scan_finds_each_kind_of_forbidden_call_in_a_fixture()
    {
        List<string> found = [.. Offenders([typeof(Fixture)])];

        found.Should().Contain(static x => x.Contains("CheckAsync", StringComparison.Ordinal));
        found.Should().Contain(static x => x.Contains("ConnectAsync", StringComparison.Ordinal));
        found.Should().Contain(static x => x.Contains("get_ConfigureConsumerBuilders", StringComparison.Ordinal));
        found.Should().Contain(static x => x.Contains("get_Item", StringComparison.Ordinal), "the topic cache's indexer creates an endpoint");
        found.Should().Contain(static x => x.Contains("IWolverineRuntime", StringComparison.Ordinal), "a runtime member is a read of the runtime");
    }

    [Fact]
    public void The_reflection_scan_finds_a_name_that_is_not_one_of_the_three()
    {
        List<string> found = [];

        foreach ((MethodBase caller, MemberInfo member, string[] strings) in CallsWithStrings([typeof(Fixture)]))
        {
            if (member.DeclaringType == typeof(Type) && ReflectionLookups.Contains(member.Name) && strings.Any(static x => !AllowedReflectedNames.Contains(x, StringComparer.Ordinal)))
            {
                found.Add(Describe(caller));
            }
        }

        found.Should().NotBeEmpty("the fixture reflects on a member the probe does not read");
    }

    /// <summary>
    /// What the reflection rule refuses in <paramref name="types" />: a lookup by a name that is not the tenant probe's three (or, in the shape check alone, the
    /// shape's three), a lookup with no literal name, any invocation, and in the shape check any reflective call but the four it is allowed.
    /// </summary>
    /// <param name="types">The types to scan.</param>
    /// <param name="isShapeCheck">Which method is the shape check.</param>
    private static IEnumerable<string> ReflectionOffences(IEnumerable<Type> types, Func<MethodBase, bool> isShapeCheck)
    {
        foreach ((MethodBase caller, MemberInfo member, string[] strings) in CallsWithStrings(types))
        {
            bool shape = isShapeCheck(caller);

            if (shape && IsReflective(member) && !ShapeCheckMembers.Contains(member.DeclaringType!.FullName + "::" + member.Name))
            {
                yield return $"{Describe(caller)} calls {member.DeclaringType?.Name}.{member.Name}, which the shape check may not";
            }
            else if (member.DeclaringType == typeof(Type) && ReflectionLookups.Contains(member.Name))
            {
                // The name is the string literal loaded just before the call; a name that is not a literal cannot be checked, so it is an offence too.
                string[] allowed = shape && member.Name == "GetProperty" ? ShapeCheckNames : AllowedReflectedNames;

                if (strings.Length == 0 || strings.Any(x => !allowed.Contains(x, StringComparer.Ordinal)))
                {
                    yield return $"{Describe(caller)} calls Type.{member.Name} with {string.Join(",", strings)}";
                }
            }
            else if (IsReflectionInvocation(member))
            {
                yield return $"{Describe(caller)} calls {member.DeclaringType?.Name}.{member.Name}";
            }
        }
    }

    /// <summary>A member of <see cref="Type" /> or of <c>System.Reflection</c>: reflection, whatever it is called.</summary>
    private static bool IsReflective(MemberInfo member) =>
        member.DeclaringType is { } owner && (owner == typeof(Type) || owner.Namespace == "System.Reflection");

    private static IEnumerable<string> Offenders(IEnumerable<Type> types)
    {
        foreach (MemberInfo call in Calls(types, []))
        {
            if (IsForbidden(call))
            {
                yield return call.DeclaringType?.FullName + "::" + call.Name;
            }
        }
    }

    private static bool IsForbidden(MemberInfo call)
    {
        Type? owner = call.DeclaringType;

        if (owner is null || owner.Assembly == Adapter || !IsWolverine(owner))
        {
            return false;
        }

        string name = call.Name;

        if (ForbiddenNames.Contains(name))
        {
            return true;
        }

        if (owner.IsGenericType && owner.GetGenericTypeDefinition().Name.StartsWith("Cache`", StringComparison.Ordinal) && CacheWrites.Contains(name))
        {
            return true;
        }

        string full = owner.FullName ?? string.Empty;

        return ForbiddenNamespaces.Any(x => full.StartsWith(x + ".", StringComparison.Ordinal))
            || ForbiddenTypeNames.Contains(full, StringComparer.Ordinal);
    }

    private static bool IsWolverine(Type type)
    {
        string? assembly = type.Assembly.GetName().Name;

        return assembly is "Wolverine" or "WolverineFx" or "Wolverine.Kafka" or "WolverineFx.Kafka" || assembly?.StartsWith("Wolverine", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("JasperFx.Core", StringComparison.Ordinal) == true;
    }

    private static bool IsReflectionInvocation(MemberInfo member) =>
        member.DeclaringType is { } owner
        && owner.Namespace is "System" or "System.Reflection" or "System.Linq.Expressions"
        && ReflectionInvocations.Contains(member.Name)
        && owner.Name is "MethodBase" or "MethodInfo" or "ConstructorInfo" or "Delegate" or "Type" or "Activator" or "PropertyInfo" or "FieldInfo" or "LambdaExpression"
            or "Expression`1";

    private static string Describe(MethodBase method) => method.DeclaringType?.FullName + "::" + method.Name;

    private static IEnumerable<MemberInfo> Calls(IEnumerable<Type> types, List<string> unresolved) =>
        CallsWithStrings(types, unresolved).Select(static x => x.Member);

    private static IEnumerable<(MethodBase Caller, MemberInfo Member, string[] Strings)> CallsWithStrings(IEnumerable<Type> types) =>
        CallsWithStrings(types, []);

    private static IEnumerable<(MethodBase Caller, MemberInfo Member, string[] Strings)> CallsWithStrings(IEnumerable<Type> types, List<string> unresolved)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (Type type in types)
        {
            foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                MethodBody? body = method.GetMethodBody();

                if (body is null)
                {
                    continue;
                }

                byte[] il = body.GetILAsByteArray()!;
                Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
                Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
                List<(int Token, OpCode Code, string? Literal)> tokens = [];
                string? lastString = null;
                int lastStringAt = int.MinValue;
                int index = 0;
                int position = 0;

                while (position < il.Length)
                {
                    short value = il[position] == 0xFE && position + 1 < il.Length ? unchecked((short) (0xFE00 | il[++position])) : il[position];
                    position++;

                    OpCode code = OpCodesByValue[value];
                    index++;

                    if (code.OperandType is OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineField or OperandType.InlineType)
                    {
                        tokens.Add((BitConverter.ToInt32(il, position), code, index - lastStringAt <= 3 ? lastString : null));
                    }
                    else if (code.OperandType == OperandType.InlineString)
                    {
                        lastString = method.Module.ResolveString(BitConverter.ToInt32(il, position));
                        lastStringAt = index;
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

                foreach ((int token, OpCode code, string? literal) in tokens)
                {
                    MemberInfo? member;

                    try
                    {
                        member = method.Module.ResolveMember(token, typeArguments, methodArguments);
                    }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException or BadImageFormatException or MissingMemberException)
                    {
                        unresolved.Add($"{Describe(method)}: {code.Name} {token:X8} ({e.GetType().Name})");
                        continue;
                    }

                    if (member is not null)
                    {
                        yield return (method, member, literal is null ? [] : [literal]);
                    }
                }
            }
        }
    }

    private static Dictionary<short, OpCode> BuildOpCodes() =>
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(static field => (OpCode) field.GetValue(null)!)
            .GroupBy(static code => code.Value)
            .ToDictionary(static group => group.Key, static group => group.First());

    /// <summary>The shape's three names looked up in a method that is not the shape check.</summary>
    private static class ShapeNamesElsewhere
    {
        public static bool Run(Type transport) => transport.GetProperty("ConsumerConfig", BindingFlags.Public | BindingFlags.Instance) is not null;
    }

    /// <summary>A shape check that also reads a value, which it may not.</summary>
    private static class ShapeCheckStandIn
    {
        public static object? Run(Type transport, object instance) =>
            transport.GetProperty("ConsumerConfig", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);
    }

    /// <summary>Every kind of call the scan must find, never run.</summary>
    private static class Fixture
    {
        public static void Run(KafkaTransport transport, KafkaTopic topic, IWolverineRuntime runtime)
        {
            _ = transport.ConfigureConsumerBuilders;
            _ = transport.Topics["created"];
            _ = topic.CheckAsync().AsTask();
            _ = transport.ConnectAsync(runtime).AsTask();
            _ = runtime.Options;
            _ = typeof(KafkaTransport).GetProperty("Something");
            _ = typeof(Fixture).GetMethods();
        }
    }
}
