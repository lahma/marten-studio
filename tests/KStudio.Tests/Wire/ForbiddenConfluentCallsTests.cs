using System.Reflection;
using System.Reflection.Emit;

using Confluent.Kafka;

namespace KStudio.Tests.Wire;

/// <summary>
/// AGENTS.md hard rule 17 at the IL, the Kafka analogue of <c>StorageEnsuringCallTests</c>: KStudio.Core never calls an API that creates,
/// alters, deletes, commits, subscribes, stores an offset, seeks, produces, lists every group, sends a single-topic metadata request, or
/// assigns without explicit offsets (decision 2.4). The compiled methods are read (<c>GetMethodBody()</c>, then
/// <c>Module.ResolveMethod</c> on each <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c> and <c>ldvirtftn</c> token, and
/// <c>Module.ResolveMember</c> on each <c>ldtoken</c>, which is how an expression tree such as <c>c =&gt; c.Subscribe("x")</c> names its
/// method), so a call in a lambda, a closure or an async state machine is found, and so is a method group converted to a delegate; a
/// comment that mentions a call is not. A token that cannot be resolved is counted and the test fails on one, never skips it. A producer
/// is recognised by the declaring type of the call (the type, its bases or its interfaces being a producer type), not by a substring of
/// a formatted name. <b>Reflection is closed by <c>KStudio_Core_calls_no_reflection_entry_point</c></b>: the engine calls none of
/// <c>MethodBase.Invoke</c>, <c>Delegate.DynamicInvoke</c>, <c>CreateDelegate</c>, <c>Type.InvokeMember</c>, <c>Type.GetMethod(s)</c>,
/// <c>LambdaExpression.Compile</c> or <c>Activator.CreateInstance</c>, so a forbidden method cannot be reached by a name this scan never
/// sees. <c>KStudioCoreFenceTests</c> checks assembly references, not reflection.
/// </summary>
public class ForbiddenConfluentCallsTests
{
    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.Ordinal)
    {
        "ListGroups", "ListGroup", "ListConsumerGroupsAsync",
        "CreateTopicsAsync", "DeleteTopicsAsync", "CreatePartitionsAsync", "AlterConfigsAsync", "IncrementalAlterConfigsAsync",
        "DeleteRecordsAsync", "CreateAclsAsync", "DeleteAclsAsync", "DeleteGroupsAsync", "DeleteConsumerGroupOffsetsAsync",
        "AlterConsumerGroupOffsetsAsync", "AlterUserScramCredentialsAsync", "ElectLeadersAsync",
        "Subscribe", "Unsubscribe", "Commit", "StoreOffset",

        // Committed is an OffsetFetch, which needs a group ACL (F10): the consumer is positioned by explicit offsets only.
        "Committed",

        // The producer's entry points; the producer types themselves are forbidden by IsProducerType.
        "Produce", "ProduceAsync", "InitTransactions", "BeginTransaction", "CommitTransaction", "AbortTransaction", "SendOffsetsToTransaction",

        // A seek on a partition in the out-of-range state throws (SPIKE S3d): the reader repositions with an unassign and an assign.
        "Seek",

        // The handlers the engine never sets (D37).
        "SetStatisticsHandler", "SetPartitionsAssignedHandler", "SetPartitionsRevokedHandler", "SetPartitionsLostHandler", "SetOffsetsCommittedHandler",
    };

    private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodes();

    [Fact]
    public void KStudio_Core_calls_no_forbidden_confluent_api()
    {
        Assembly core = typeof(KafkaEngineOptions).Assembly;
        List<string> found = [.. Offenders(core.GetTypes())];

        found.Should().BeEmpty("hard rule 17: a read never writes. Found: " + string.Join("; ", found));
    }

    [Fact]
    public void No_method_token_of_KStudio_Core_goes_unresolved()
    {
        List<string> unresolved = [];

        _ = CalledMethodsOf(typeof(KafkaEngineOptions).Assembly.GetTypes(), unresolved).ToList();

        unresolved.Should().BeEmpty("a token the scan could not resolve is a call it did not see");
    }

    [Fact]
    public void KStudio_Core_calls_no_reflection_entry_point()
    {
        List<string> found = [.. ReflectionOffenders(typeof(KafkaEngineOptions).Assembly.GetTypes())];

        found.Should().BeEmpty("a forbidden Confluent method could be reached through reflection, which the scan above cannot see. Found: " + string.Join("; ", found));
    }

    [Fact]
    public void The_reflection_scan_finds_each_entry_point_in_a_fixture()
    {
        List<string> found = [.. ReflectionOffenders([typeof(ReflectionFixture)])];

        foreach (string expected in new[]
        {
            "MethodBase.Invoke", "ConstructorInfo.Invoke", "Delegate.DynamicInvoke", "Delegate.CreateDelegate", "MethodInfo.CreateDelegate",
            "Type.InvokeMember", "Type.GetMethod ", "Type.GetMethods", "Expression`1.Compile", "Activator.CreateInstance",
        })
        {
            found.Should().Contain(x => x.StartsWith(expected, StringComparison.Ordinal), "the fixture calls " + expected);
        }
    }

    [Fact]
    public void The_scan_reads_the_methods_it_is_meant_to_read()
    {
        // Anti-vacuity: the scan sees at least the Confluent calls the engine really makes.
        List<string> calls = [.. CalledConfluentMethods(typeof(KafkaEngineOptions).Assembly.GetTypes()).Select(Format)];

        calls.Should().Contain(static x => x.Contains("IConsumer", StringComparison.Ordinal) && x.Contains("IncrementalAssign", StringComparison.Ordinal));
        calls.Should().Contain(static x => x.Contains("ListOffsetsAsync", StringComparison.Ordinal));
        calls.Should().Contain(static x => x.Contains("Consume", StringComparison.Ordinal));
        calls.Should().Contain(static x => x.Contains("DescribeConfigsAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void The_scan_finds_each_kind_of_forbidden_call_in_a_fixture()
    {
        List<string> found = [.. Offenders(typeof(Fixture).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).Append(typeof(Fixture)))];

        found.Should().Contain(static x => x.Contains("ProducerBuilder", StringComparison.Ordinal));
        found.Should().Contain(static x => x.Contains("Subscribe", StringComparison.Ordinal));
        found.Should().Contain(static x => x.Contains("GetMetadata(String, TimeSpan)", StringComparison.Ordinal));
        found.Should().Contain(static x => x.Contains("Assign(TopicPartition)", StringComparison.Ordinal));
        found.Should().Contain(static x => x.Contains("CreateTopicsAsync", StringComparison.Ordinal));
        found.Should().Contain(static x => x.Contains("Produce(", StringComparison.Ordinal));
        found.Should().Contain(static x => x.Contains("InitTransactions(", StringComparison.Ordinal));
        found.Should().Contain(static x => x.Contains("Committed(", StringComparison.Ordinal));
        found.Should().NotContain(static x => x.Contains("Assign(IEnumerable`1<TopicPartitionOffset>)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_method_group_alone_is_found()
    {
        // Only the delegate conversion reaches Subscribe here: the fixture never calls it.
        List<string> found = [.. Offenders([typeof(MethodGroupFixture)])];

        found.Should().Contain(static x => x.Contains("Subscribe(String)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_method_named_only_by_an_expression_tree_is_found()
    {
        // c => c.Subscribe("x") never calls Subscribe: it loads the method's handle with ldtoken (REVIEW-ENG-A-FIX 3).
        List<string> found = [.. Offenders([typeof(ExpressionFixture)])];

        found.Should().Contain(static x => x.Contains("Subscribe(String)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_producer_is_found_by_its_type_and_not_by_the_spelling_of_its_name()
    {
        List<string> found = [.. Offenders([typeof(ProducerFixture)])];

        found.Should().Contain(static x => x.Contains("Produce(", StringComparison.Ordinal));
        found.Should().Contain(static x => x.Contains("InitTransactions(", StringComparison.Ordinal));
    }

    private static IEnumerable<string> Offenders(IEnumerable<Type> types)
    {
        foreach (MethodBase call in CalledConfluentMethods(types))
        {
            if (IsForbidden(call))
            {
                yield return Format(call);
            }
        }
    }

    private static bool IsForbidden(MethodBase call)
    {
        string name = call.IsConstructor ? ".ctor" : call.Name;

        if (ForbiddenNames.Contains(name) || IsProducerType(call.DeclaringType!))
        {
            return true;
        }

        ParameterInfo[] parameters = call.GetParameters();

        // A single-topic metadata request is the one that can auto-create a topic.
        if (name == "GetMetadata" && parameters.Length > 0 && parameters[0].ParameterType == typeof(string))
        {
            return true;
        }

        // An assign without explicit offsets would let librdkafka fetch the committed ones, which needs a group ACL (F10).
        return name is "Assign" or "IncrementalAssign" && !parameters.Any(static x => IsOffsets(x.ParameterType));
    }

    private static bool IsOffsets(Type type) =>
        type == typeof(TopicPartitionOffset)
        || (type.IsGenericType && type.GetGenericArguments().Any(static x => x == typeof(TopicPartitionOffset)));

    /// <summary>A producer, a producer builder, or anything assignable to one: the declaring type, a base type or an interface of it.</summary>
    private static bool IsProducerType(Type type)
    {
        foreach (Type candidate in new[] { type }.Concat(type.GetInterfaces()).Concat(BaseTypes(type)))
        {
            string name = candidate.IsGenericType ? candidate.GetGenericTypeDefinition().Name : candidate.Name;

            if (candidate.Assembly.GetName().Name == "Confluent.Kafka"
                && (name.StartsWith("IProducer", StringComparison.Ordinal)
                    || name.StartsWith("Producer", StringComparison.Ordinal)
                    || name.StartsWith("DependentProducerBuilder", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Type> BaseTypes(Type type)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    /// <summary>Every method or constructor of Confluent.Kafka a type's methods call, take a delegate to, or name with <c>ldtoken</c>.</summary>
    private static IEnumerable<MethodBase> CalledConfluentMethods(IEnumerable<Type> types) =>
        CalledMethodsOf(types, []).Where(static x => x.DeclaringType?.Assembly.GetName().Name == "Confluent.Kafka");

    /// <summary>Every method or constructor any type's methods reference; a token that cannot be resolved is added to <paramref name="unresolved" />.</summary>
    private static IEnumerable<MethodBase> CalledMethodsOf(IEnumerable<Type> types, List<string> unresolved)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (Type type in types)
        {
            foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                foreach (MethodBase called in CalledMethods(method, unresolved))
                {
                    yield return called;
                }
            }
        }
    }

    /// <summary>The reflection entry points that could reach a method by a name the IL scan never sees.</summary>
    private static IEnumerable<string> ReflectionOffenders(IEnumerable<Type> types)
    {
        foreach (MethodBase call in CalledMethodsOf(types, []))
        {
            Type declaring = call.DeclaringType!;
            string name = call.Name;
            bool isReflection =
                (name == "Invoke" && typeof(MethodBase).IsAssignableFrom(declaring))
                || (name == "DynamicInvoke" && typeof(Delegate).IsAssignableFrom(declaring))
                || (name == "CreateDelegate" && (typeof(Delegate).IsAssignableFrom(declaring) || typeof(MethodInfo).IsAssignableFrom(declaring)))
                || (name == "InvokeMember" && typeof(Type).IsAssignableFrom(declaring))
                || (name is "GetMethod" or "GetMethods" && typeof(Type).IsAssignableFrom(declaring))
                || (name == "Compile" && IsLambdaExpression(declaring))
                || (name == "CreateInstance" && declaring == typeof(Activator));

            if (isReflection)
            {
                yield return declaring.Name + "." + name + " (" + Format(call) + ")";
            }
        }
    }

    private static bool IsLambdaExpression(Type type)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (current == typeof(System.Linq.Expressions.LambdaExpression))
            {
                return true;
            }
        }

        return false;
    }

    private static string Format(MethodBase method) =>
        method.DeclaringType!.Name + "." + (method.IsConstructor ? "ctor" : method.Name) + "(" + string.Join(", ", method.GetParameters().Select(static x => Name(x.ParameterType))) + ")";

    private static string Name(Type type) =>
        type.IsGenericType ? type.Name + "<" + string.Join(", ", type.GetGenericArguments().Select(Name)) + ">" : type.Name;

    private static IEnumerable<MethodBase> CalledMethods(MethodBase method, List<string> unresolved)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();

        if (il is null)
        {
            yield break;
        }

        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } ? method.DeclaringType.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int position = 0;

        while (position < il.Length)
        {
            OpCode opCode = il[position] == 0xFE
                ? OpCodesByValue[(short) (0xFE00 | il[position + 1])]
                : OpCodesByValue[il[position]];
            position += opCode.Size;

            int operandSize = opCode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
                _ => 4,
            };

            bool isCall = opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj || opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn;

            if (isCall || opCode == OpCodes.Ldtoken)
            {
                MethodBase? resolved = null;
                int token = BitConverter.ToInt32(il, position);

                try
                {
                    // An ldtoken names a type, a field or a method; only a method is a call this fence cares about.
                    resolved = isCall
                        ? method.Module.ResolveMethod(token, typeArguments, methodArguments)
                        : method.Module.ResolveMember(token, typeArguments, methodArguments) as MethodBase;
                }
                catch (ArgumentException)
                {
                    unresolved.Add(method.DeclaringType?.FullName + "." + method.Name + " token 0x" + token.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                }

                if (resolved is not null)
                {
                    yield return resolved;
                }
            }

            position += operandSize;
        }
    }

    private static Dictionary<short, OpCode> BuildOpCodes()
    {
        Dictionary<short, OpCode> result = [];

        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
            {
                result[opCode.Value] = opCode;
            }
        }

        return result;
    }

    /// <summary>What the scan has to find. Never called.</summary>
    private static class Fixture
    {
        public static void Calls(IConsumer<byte[], byte[]> consumer, IAdminClient admin, IProducer<Null, Null> producer, TopicPartition partition)
        {
            _ = new ProducerBuilder<string, string>(new ProducerConfig());
            consumer.Subscribe("topic");
            consumer.Assign(partition);
            _ = admin.GetMetadata("topic", TimeSpan.Zero);
            _ = admin.CreateTopicsAsync([], null);
            producer.Produce("topic", new Message<Null, Null>());
            producer.InitTransactions(TimeSpan.Zero);
            _ = consumer.Committed(TimeSpan.Zero);
            consumer.Assign([new TopicPartitionOffset(partition, Offset.Beginning)]);
        }
    }

    /// <summary>The producer an admin client's OAuth handler is handed, and calls on it. Never called.</summary>
    private static class ProducerFixture
    {
        public static void Calls(IProducer<Null, Null> producer)
        {
            producer.Produce("topic", new Message<Null, Null>());
            producer.InitTransactions(TimeSpan.Zero);
        }
    }

    /// <summary>Every reflection entry point the fence closes. Never called.</summary>
    private static class ReflectionFixture
    {
        public static void Calls(MethodInfo method, ConstructorInfo constructor, Delegate handler, Type type, System.Linq.Expressions.Expression<Action> expression)
        {
            _ = method.Invoke(null, null);
            _ = constructor.Invoke(null);
            _ = handler.DynamicInvoke();
            _ = Delegate.CreateDelegate(typeof(Action), method);
            _ = method.CreateDelegate<Action>();
            _ = type.InvokeMember("m", BindingFlags.InvokeMethod, null, null, null, System.Globalization.CultureInfo.InvariantCulture);
            _ = type.GetMethod("m");
            _ = type.GetMethods();
            _ = expression.Compile();
            _ = Activator.CreateInstance(type);
        }
    }

    /// <summary>A method named only by an expression tree, which loads its handle with ldtoken and never calls it. Never called.</summary>
    private static class ExpressionFixture
    {
        public static System.Linq.Expressions.Expression<Action<IConsumer<byte[], byte[]>>> Tree() => c => c.Subscribe("x");
    }

    /// <summary>A delegate to a forbidden method with no direct call to it. Never called.</summary>
    private static class MethodGroupFixture
    {
        public static Action<string> Group(IConsumer<byte[], byte[]> consumer)
        {
            Action<string> subscribe = consumer.Subscribe;
            return subscribe;
        }
    }
}
