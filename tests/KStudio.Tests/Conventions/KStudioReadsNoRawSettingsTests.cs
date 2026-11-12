using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace KStudio.Tests.Conventions;

/// <summary>
/// Spec B.6.2 and D33 for librdkafka: the cluster definition's raw settings never leave the engine's reader. <c>KStudio.dll</c> never refers to
/// <c>KafkaClusterDefinition.ConsumerSettings</c>, <c>AdminSettings</c> or <c>TokenRefresh</c> - a call, a delegate over them or a ldftn - so no page,
/// service, log line or audit entry can hold a secret value; the page reads <c>KafkaSettingsReader</c>'s view, which classifies every key. The
/// scan reads the assembly's member references (every call in IL goes through one) the way <c>PostgresStudioVersionGuardTests</c> reads IL.
/// </summary>
public class KStudioReadsNoRawSettingsTests
{
    private static readonly string[] RawGetters = ["get_ConsumerSettings", "get_AdminSettings", "get_TokenRefresh"];

    [Fact]
    public void KStudio_never_refers_to_the_definitions_raw_settings()
    {
        IReadOnlyList<string> offenders = MemberReferences(typeof(KafkaStudioOptions).Assembly.Location)
            .Where(static x => x.Type == "KStudio.Core.Clusters.KafkaClusterDefinition" && RawGetters.Contains(x.Member, StringComparer.Ordinal))
            .Select(static x => x.Type + "::" + x.Member)
            .ToList();

        offenders.Should().BeEmpty("the raw dictionaries of a cluster's configuration are read only inside KStudio.Core, by KafkaSettingsReader");
    }

    [Fact]
    public void The_scan_sees_what_it_looks_for()
    {
        // Anti-vacuity: the same scan finds the getter in the test assembly, whose ClusterRegistrationTests read the cloned dictionaries on purpose.
        MemberReferences(typeof(KStudioReadsNoRawSettingsTests).Assembly.Location)
            .Should().Contain(("KStudio.Core.Clusters.KafkaClusterDefinition", "get_ConsumerSettings"), "the scan resolves a getter on the definition when one is called");

        MemberReferences(typeof(KafkaStudioOptions).Assembly.Location)
            .Should().Contain(static x => x.Type == "KStudio.Core.Clusters.KafkaClusterDefinition", "KStudio does use the definition, for its declared topics");
    }

    private static List<(string Type, string Member)> MemberReferences(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader pe = new(stream);
        MetadataReader reader = pe.GetMetadataReader();
        List<(string Type, string Member)> result = [];

        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MemberReference member = reader.GetMemberReference(handle);

            if (member.Parent.Kind == HandleKind.TypeReference)
            {
                result.Add((FullName(reader, (TypeReferenceHandle) member.Parent), reader.GetString(member.Name)));
            }
        }

        return result;
    }

    /// <summary>The full name of a referenced type, a nested one as <c>Outer+Inner</c>.</summary>
    internal static string FullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string name = reader.GetString(type.Name);

        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return FullName(reader, (TypeReferenceHandle) type.ResolutionScope) + "+" + name;
        }

        string ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}
