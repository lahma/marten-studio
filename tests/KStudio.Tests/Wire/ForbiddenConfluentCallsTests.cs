using System.Reflection;
using System.Reflection.Emit;

using Confluent.Kafka;

namespace KStudio.Tests.Wire;

/// <summary>
/// AGENTS.md hard rule 17 at the IL, the Kafka analogue of <c>StorageEnsuringCallTests</c>: KStudio.Core never calls an API that creates,
/// alters, deletes, commits, subscribes, stores an offset, seeks, produces, lists every group, sends a single-topic metadata request, or
/// assigns without explicit offsets (decision 2.4). The compiled methods are read (<c>GetMethodBody()</c>, then
/// <c>Module.ResolveMethod</c> on each <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c> and <c>ldvirtftn</c> token), so a call in a
/// lambda, a closure or an async state machine is found, and so is a method group converted to a delegate; a comment that mentions a call
/// is not. A producer is recognised by the declaring type of the call (the type, its bases or its interfaces being a producer type), not
/// by a substring of a formatted name. <b>Reflection is out of this fence's reach</b>: <c>MethodInfo.Invoke</c>,
/// <c>Delegate.DynamicInvoke</c> and a string-built method name are not seen, and <c>KStudioCoreFenceTests</c> is what keeps them out of
/// the sources.
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

    /// <summary>Every method or constructor of Confluent.Kafka a type's methods call or take a delegate to.</summary>
    private static IEnumerable<MethodBase> CalledConfluentMethods(IEnumerable<Type> types)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (Type type in types)
        {
            foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                foreach (MethodBase called in CalledMethods(method))
                {
                    if (called.DeclaringType?.Assembly.GetName().Name == "Confluent.Kafka")
                    {
                        yield return called;
                    }
                }
            }
        }
    }

    private static string Format(MethodBase method) =>
        method.DeclaringType!.Name + "." + (method.IsConstructor ? "ctor" : method.Name) + "(" + string.Join(", ", method.GetParameters().Select(static x => Name(x.ParameterType))) + ")";

    private static string Name(Type type) =>
        type.IsGenericType ? type.Name + "<" + string.Join(", ", type.GetGenericArguments().Select(Name)) + ">" : type.Name;

    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
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

            if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj || opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn)
            {
                MethodBase? resolved = null;

                try
                {
                    resolved = method.Module.ResolveMethod(BitConverter.ToInt32(il, position), typeArguments, methodArguments);
                }
                catch (ArgumentException)
                {
                    // A token that cannot be resolved with these generic arguments is skipped; the non-generic ones are what matters.
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
