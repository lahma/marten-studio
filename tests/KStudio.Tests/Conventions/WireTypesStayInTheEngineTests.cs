using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace KStudio.Tests.Conventions;

/// <summary>
/// D-ENGINE 10.4, now that <c>KStudio.dll</c> calls the engine: the wire record, the reader and the evaluator's context are internal to
/// <c>KStudio.Core</c> and are never returned from an API KStudio calls, so no facet, chip, export or view can be built from an unredacted record. The
/// scan reads the assembly's type references (<c>System.Reflection.Metadata</c>) and fails on any type of <c>KStudio.Core.Wire</c> that is not one of the
/// value types the catalog's own records carry, and on <c>KStudio.Core.Decoding.RecordEvaluationContext</c>.
/// </summary>
/// <remarks>
/// The catalog's records are the engine's API (D-ENGINE 10.4: "the catalog's records only"), and some of their fields are wire value types: a field is a
/// <c>KafkaField&lt;T&gt;</c> whose error is a <c>WireError</c>, a cluster overview holds a <c>WireClusterDescription</c>, a group overview a
/// <c>WireGroupDescription</c> and a lag a <c>WirePartition</c>, and a match says whether its timestamp is <c>CreateTime</c> or <c>LogAppendTime</c> in a <c>WireTimestampType</c> (UI-B draws it). Those carry metadata, never a record's key, headers or value; everything that could
/// (<c>WireRecord</c>, <c>WireHeader</c>, <c>IKafkaWire</c>, <c>IWireScanReader</c>, <c>WireReadOutcome</c>) is refused.
/// </remarks>
public class WireTypesStayInTheEngineTests
{
    /// <summary>The wire value types the catalog's records expose (a nested type is allowed with its outer: <c>WireError+NotAuthorized</c>).</summary>
    private static readonly string[] CatalogValueTypes =
    [
        "KStudio.Core.Wire.WireError",
        "KStudio.Core.Wire.WireClusterDescription",
        "KStudio.Core.Wire.WireNode",
        "KStudio.Core.Wire.WireGroupDescription",
        "KStudio.Core.Wire.WirePartition",
        "KStudio.Core.Wire.KafkaAclOperation",
        "KStudio.Core.Wire.KafkaResourceType",
        "KStudio.Core.Wire.WireTimestampType",
    ];

    [Fact]
    public void KStudio_refers_to_no_wire_record_reader_or_evaluation_context()
    {
        string[] offenders =
        [
            .. TypeReferences(typeof(KafkaStudioOptions).Assembly.Location)
                .Where(static x => x == "KStudio.Core.Decoding.RecordEvaluationContext"
                    || (x.StartsWith("KStudio.Core.Wire.", StringComparison.Ordinal) && !IsCatalogValueType(x)))
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty("the wire record and the reader never leave KStudio.Core (D-ENGINE 10.4): redaction runs in the engine before any sink");
    }

    [Fact]
    public void The_scan_sees_what_it_looks_for()
    {
        // Anti-vacuity: the test assembly builds wire records on purpose (the fake broker), and KStudio uses a catalog value type.
        TypeReferences(typeof(WireTypesStayInTheEngineTests).Assembly.Location).Should().Contain("KStudio.Core.Wire.WireRecord");
        TypeReferences(typeof(KafkaStudioOptions).Assembly.Location).Should().Contain(static x => x.StartsWith("KStudio.Core.Wire.WireError", StringComparison.Ordinal));
    }

    private static bool IsCatalogValueType(string name) =>
        CatalogValueTypes.Any(allowed => name.Equals(allowed, StringComparison.Ordinal) || name.StartsWith(allowed + "+", StringComparison.Ordinal));

    private static List<string> TypeReferences(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader pe = new(stream);
        MetadataReader reader = pe.GetMetadataReader();

        return [.. reader.TypeReferences.Select(handle => KStudioReadsNoRawSettingsTests.FullName(reader, handle))];
    }
}
