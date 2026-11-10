using System.Reflection;
using System.Reflection.Emit;

using Confluent.Kafka;

namespace KStudio.Tests.Wire;

/// <summary>
/// AGENTS.md hard rule 17 at the IL, the Kafka analogue of <c>StorageEnsuringCallTests</c>: KStudio.Core never calls an API that creates,
/// alters, deletes, commits, subscribes, stores an offset, seeks, produces, lists every group, sends a single-topic metadata request, or
/// assigns without explicit offsets (decision 2.4). The compiled methods are read (<c>GetMethodBody()</c>, then
/// <c>Module.ResolveMethod</c> on each <c>call</c>, <c>callvirt</c> and <c>newobj</c> token), so a call in a lambda, a closure or an async
/// state machine is found too, and a comment that mentions one is not.
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
        List<string> calls = [.. CalledConfluentMethods(typeof(KafkaEngineOptions).Assembly.GetTypes())];

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
        found.Should().NotContain(static x => x.Contains("Assign(IEnumerable`1<TopicPartitionOffset>)", StringComparison.Ordinal));
    }

    private static IEnumerable<string> Offenders(IEnumerable<Type> types)
    {
        foreach (string call in CalledConfluentMethods(types))
        {
            if (IsForbidden(call))
            {
                yield return call;
            }
        }
    }

    private static bool IsForbidden(string call)
    {
        // "Declaring.Name(ParameterTypes)"
        string name = call[(call.LastIndexOf('.', call.IndexOf('(', StringComparison.Ordinal)) + 1)..call.IndexOf('(', StringComparison.Ordinal)];

        if (ForbiddenNames.Contains(name))
        {
            return true;
        }

        if (call.Contains("ProducerBuilder", StringComparison.Ordinal) || call.Contains(".IProducer", StringComparison.Ordinal) || call.Contains(".Producer`", StringComparison.Ordinal))
        {
            return true;
        }

        // A single-topic metadata request is the one that can auto-create a topic.
        if (name == "GetMetadata" && call.Contains("(String,", StringComparison.Ordinal))
        {
            return true;
        }

        // An assign without explicit offsets would let librdkafka fetch the committed ones, which needs a group ACL (F10).
        return name is "Assign" or "IncrementalAssign" && !call.Contains("TopicPartitionOffset", StringComparison.Ordinal);
    }

    /// <summary>Every method or constructor of Confluent.Kafka a type's methods call, as <c>Declaring.Name(Parameters)</c>.</summary>
    private static IEnumerable<string> CalledConfluentMethods(IEnumerable<Type> types)
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
                        yield return Format(called);
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

            if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj)
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
        public static void Calls(IConsumer<byte[], byte[]> consumer, IAdminClient admin, TopicPartition partition)
        {
            _ = new ProducerBuilder<string, string>(new ProducerConfig());
            consumer.Subscribe("topic");
            consumer.Assign(partition);
            _ = admin.GetMetadata("topic", TimeSpan.Zero);
            _ = admin.CreateTopicsAsync([], null);
            consumer.Assign([new TopicPartitionOffset(partition, Offset.Beginning)]);
        }
    }
}
